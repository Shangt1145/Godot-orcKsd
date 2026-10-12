using System.Text;
using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Data.Components;
using Xunit;

namespace Kards.Ui.Tests;

public class DiyCardTests
{
    private const string Source = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
    private static CardCatalog Catalog() { var c = new CardCatalog(); c.Load(Source); return c; }
    private static DiyCardDraft Unit(string id = "diy/test") => new() { Id = id, Name = "测试步兵", Cost = 0, OpCost = 0 };
    private static Task<OrcMatchSession> Session(params DiyCardDraft[] drafts)
        => OrcMatchSession.CreateFromCatalogAsync(Catalog().Cards, 20261011, Guid.NewGuid().ToString(), "unused",
            openingHand: drafts.Select(d => d.Id).ToArray(), sourceDirectory: Source, customCards: drafts);

    [Fact]
    public void CompilesNativeCardAndSelfScopedPrefabWithoutEditingSourcePool()
    {
        var catalog = Catalog();
        var card = Unit() with { Keywords = ["blitz", "armor"], Armor = 3,
            Effects = [new() { Op = "draw", Amount = 2 }] };
        var result = DiyCardCompiler.Validate(card, catalog.Cards);
        Assert.True(result.Success, string.Join(";", result.Errors));
        var body = CardDataJson.Deserialize(result.EngineCardJson!, out var warnings);
        Assert.Empty(warnings); Assert.Equal(card.Id, body.Id);
        var effects = Assert.Single(body.Components.OfType<EffectsDefinition>());
        Assert.Single(effects.Inline); Assert.Empty(effects.Prefabs);
        Assert.Contains("object.ReferenceEquals(view.Unit, self)", result.EngineCardJson!);
        var service = new DeckConstructionService(catalog.Cards, Source); service.UpdateCustom([card]);
        Assert.Contains(card.Id, service.PlayableIds); Assert.Equal(21, service.Report.Verified);
        Assert.Equal(316, service.Cards.Count);
    }
    [Theory]
    [InlineData("damage", "self", 0, "")]
    [InlineData("gainSlot", "enemyUnits", 1, "")]
    [InlineData("notAnOp", "self", 1, "")]
    [InlineData("draw", "self", 1, "turnEnd")]
    [InlineData("costMod", "self", 0, "")]
    [InlineData("buff", "enemyHq", 1, "")]
    public void RejectsUnsupportedOrIncompleteEffects(string op, string target, int amount, string until)
    {
        var result = DiyCardCompiler.Validate(Unit() with
        { Effects = [new() { Op = op, Target = target, Amount = amount, Until = until }] }, Catalog().Cards);
        Assert.False(result.Success); Assert.NotEmpty(result.Errors);
    }
    [Fact]
    public void RejectsUnknownKeywordsCommandsWithoutEffectsAndOriginalIds()
    {
        var catalog = Catalog();
        Assert.False(DiyCardCompiler.Validate(Unit() with { Keywords = ["invented"] }, catalog.Cards).Success);
        Assert.False(DiyCardCompiler.Validate(Unit() with { CardType = "order" }, catalog.Cards).Success);
        Assert.False(DiyCardCompiler.Validate(Unit("USG/units/_4"), catalog.Cards).Success);
    }
    [Fact]
    public async Task DeploymentDrawsThroughNativeEngineAndDoesNotListenToOtherUnits()
    {
        var first = Unit("diy/draw") with { Keywords = ["blitz"], Effects = [new() { Op = "draw", Amount = 2 }] };
        var second = Unit("diy/plain");
        using var session = await Session(first, second); session.DriveOpponent = false;
        await session.SettleMulliganAsync(false);
        var count = session.View.SelfHand.Count; var deck = session.Host.Match.Players[0].Deck.Count;
        var card = session.View.SelfHand.Single(c => c.CardId == first.Id);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(card.Uid, 0)));
        session.Pump();
        Assert.Equal(count + 1, session.View.SelfHand.Count);
        Assert.Equal(deck - 2, session.Host.Match.Players[0].Deck.Count);
        Assert.Equal(first.Name, Assert.Single(session.View.SelfLine).Definition.Name);
        var plain = session.View.SelfHand.Single(c => c.CardId == second.Id);
        var before = session.View.SelfHand.Count;
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(plain.Uid, 1)));
        Assert.Equal(before - 1, session.View.SelfHand.Count);
        Assert.Equal(deck - 2, session.Host.Match.Players[0].Deck.Count);
    }
    [Fact]
    public async Task NativeOrderRunsEffectsInDeclaredOrderAndTemporaryBuffExpires()
    {
        var unit = Unit();
        var order = Unit("diy/order") with { Name = "战地补给", CardType = "order",
            Effects = [new() { Op = "draw", Amount = 1 }, new() { Op = "buff", Target = "friendlyUnits", Attack = 2, Defense = 3, Until = "turnEnd" }] };
        using var session = await Session(unit, order); session.DriveOpponent = false;
        await session.SettleMulliganAsync(false);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(session.View.SelfHand.Single(c => c.CardId == unit.Id).Uid, 0)));
        var resolutions = new List<UiPresentationResolution>(); session.Host.PresentationReady += (r, _) => resolutions.Add(r);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(session.View.SelfHand.Single(c => c.CardId == order.Id).Uid)));
        session.Pump();
        var live = Assert.Single(session.View.SelfLine); Assert.Equal(4, live.EffectiveAttack); Assert.Equal(6, live.Health);
        var steps = resolutions.SelectMany(r => r.Steps).ToArray();
        Assert.Contains(steps, s => s is UiDrawPresentation); Assert.Contains(steps, s => s is UiStatusPresentation { Status: UiStatusKind.Buff });
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new EndTurn()));
        Assert.Equal(2, Assert.Single(session.View.SelfLine).EffectiveAttack);
    }
    [Fact]
    public async Task NativeResourceSuppressionInhibitAndCostOperationsApplyAndExpire()
    {
        var unit = Unit() with { OpCost = 2, Keywords = ["blitz", "armor"], Armor = 1 };
        var order = Unit("diy/mechanisms") with { CardType = "order", Effects =
        [
            new() { Op = "gainSlot", Amount = 2 }, new() { Op = "gainPoint", Amount = 2 },
            new() { Op = "silence", Target = "friendlyUnits" }, new() { Op = "pin", Target = "friendlyUnits" },
            new() { Op = "costMod", Target = "friendlyUnits", Amount = -1, Until = "turnEnd" }
        ] };
        using var session = await Session(unit, order); session.DriveOpponent = false;
        await session.SettleMulliganAsync(false);
        await session.SubmitAsync(new PlayCard(session.View.SelfHand.Single(c => c.CardId == unit.Id).Uid));
        var points = session.View.SelfKredits; var slots = session.View.SelfMaxKredits;
        var errors = new List<string>(); session.Host.ErrorRaised += errors.Add;
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(session.View.SelfHand.Single(c => c.CardId == order.Id).Uid)));
        session.Pump(); Assert.Empty(errors);
        Assert.Equal(points + 2, session.View.SelfKredits); Assert.Equal(slots + 2, session.View.SelfMaxKredits);
        var live = Assert.Single(session.View.SelfLine);
        Assert.True(live.IsSuppressed); Assert.True(live.IsSilenced); Assert.Equal(1, live.EffectiveOpCost);
        Assert.DoesNotContain("blitz", live.Keywords); Assert.DoesNotContain("armor", live.Keywords);
        await session.SubmitAsync(new EndTurn());
        Assert.Equal(2, Assert.Single(session.View.SelfLine).EffectiveOpCost);
    }
    [Fact]
    public async Task DamageSelectsEnemyUnitsAndEffectOrderCanChangeLethality()
    {
        var plain = Unit();
        var attack = Unit("diy/damage") with { CardType = "order", Effects = [new() { Op = "damage", Target = "enemyUnits", Amount = 2 }] };
        var before = Unit("diy/order-before") with { CardType = "order", Effects =
            [new() { Op = "buff", Target = "friendlyUnits", Attack = 0, Defense = 2 }, new() { Op = "damage", Target = "friendlyUnits", Amount = 4 }] };
        using var session = await Session(plain, attack, before); session.DriveOpponent = false;
        await session.SettleMulliganAsync(false);
        var uid = session.View.SelfHand.Single(c => c.CardId == plain.Id).Uid;
        await session.SubmitAsync(new PlayCard(uid));
        await session.SubmitAsync(new PlayCard(session.View.SelfHand.Single(c => c.CardId == before.Id).Uid));
        Assert.Equal(1, Assert.Single(session.View.SelfLine).Health); // Buff first survives; reverse order would destroy it.
        var match = session.Host.Match;
        var enemy = match.CardLibrary.Instantiate(plain.Id); await enemy.LoadAsync(match.Players[1]); match.Players[1].Hand.Add(enemy);
        await session.SubmitAsync(new EndTurn()); session.SelectViewer(1);
        var enemyUid = session.View.SelfHand.Single(c => c.CardId == plain.Id).Uid;
        await session.SubmitAsync(new PlayCard(enemyUid)); await session.SubmitAsync(new EndTurn()); session.SelectViewer(0);
        var errors = new List<string>(); session.Host.ErrorRaised += errors.Add;
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(session.View.SelfHand.Single(c => c.CardId == attack.Id).Uid)));
        session.Pump(); Assert.Empty(errors);
        Assert.Equal(1, Assert.Single(session.View.EnemyLine).Health);
        Assert.Equal(1, Assert.Single(session.View.SelfLine).Health);
    }
    [Fact]
    public async Task ExistingMatchKeepsDefinitionSnapshotAfterDraftIsEdited()
    {
        var draft = Unit() with { Attack = 7, Keywords = ["armor"], Armor = 2 };
        using var session = await Session(draft); session.DriveOpponent = false;
        draft.Keywords[0] = "blitz";
        var service = new DeckConstructionService(Catalog().Cards, Source);
        service.UpdateCustom([draft with { Attack = 20 }]);
        await session.SettleMulliganAsync(false);
        var card = session.View.SelfHand.Single(c => c.CardId == draft.Id);
        Assert.Equal(7, card.EffectiveAttack); Assert.Contains("armor", card.Keywords);
        await session.SubmitAsync(new PlayCard(card.Uid));
        Assert.Equal(7, Assert.Single(session.View.SelfLine).EffectiveAttack);
    }
    [Fact]
    public void StorageRoundTripsSnapshotsImportIsNewIdentityAndFailedWriteRollsBack()
    {
        var folder = Path.Combine(Path.GetTempPath(), "diy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "cards.json"); var library = new DiyCardLibrary(path);
            var draft = Unit() with { Keywords = ["armor"], Effects = [new() { Op = "draw", Amount = 2 }] };
            library.Save(draft); draft.Keywords[0] = "changed";
            var saved = Assert.Single(new DiyCardLibrary(path).Cards); Assert.Equal("armor", saved.Keywords[0]);
            var imported = DiyCardLibrary.Import(DiyCardLibrary.Export(saved)); Assert.NotEqual(saved.Id, imported.Id);
            Assert.Equal(saved.Name, imported.Name); Assert.Equal(saved.Effects, imported.Effects);
            var broken = new DiyCardLibrary(folder);
            var failure = Record.Exception(() => broken.Save(saved));
            Assert.True(failure is IOException or UnauthorizedAccessException); Assert.Empty(broken.Cards);
            library.Delete(saved.Id); Assert.Empty(new DiyCardLibrary(path).Cards);
        }
        finally { Directory.Delete(folder, true); if (File.Exists(folder + ".tmp")) File.Delete(folder + ".tmp"); }
    }
    [Fact]
    public void ImportRejectsHiddenExecutableFieldsAndCorruptStorageIsPreserved()
    {
        var json = JsonSerializer.Serialize(Unit());
        json = json[..^1] + ",\"Script\":\"arbitrary()\"}";
        Assert.Throws<InvalidDataException>(() => DiyCardLibrary.Import("ORCDIY1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))));
        var path = Path.Combine(Path.GetTempPath(), "diy-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{bad");
        try
        {
            var library = new DiyCardLibrary(path); Assert.NotNull(library.LoadError);
            Assert.Throws<IOException>(() => library.Save(Unit())); Assert.Equal("{bad", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void DeletedAndInvalidCustomCardsNeverRemainAdmittedToDecks()
    {
        var service = new DeckConstructionService(Catalog().Cards, Source); var card = Unit();
        service.UpdateCustom([card]);
        var starter = service.Starter(5);
        var deck = starter with { CardIds = starter.CardIds.Skip(1).Append(card.Id).ToArray() };
        Assert.Empty(service.Validate(deck));
        service.UpdateCustom([card with { Effects = [new() { Op = "unknown" }] }]);
        Assert.DoesNotContain(card.Id, service.PlayableIds); Assert.NotEmpty(service.CustomErrors);
        Assert.NotEmpty(service.Validate(deck));
        service.UpdateCustom([]); Assert.NotEmpty(service.Validate(deck)); Assert.Equal(21, service.Report.Verified);
    }
}
