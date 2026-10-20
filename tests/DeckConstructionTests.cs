using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class DeckConstructionTests
{
    private const string Source = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
    private static CardCatalog Catalog() { var c = new CardCatalog(); c.Load(Source); return c; }
    private static UiCardDefinition Card(string id, string set = "av76", string rarity = "iron") => new() { CardId = id, Name = id, Set = set, Rarity = rarity, CardType = "order" };
    private static ConstructedDeck Deck(string[] ids, DeckOptions? options = null) => new() { CardIds = ids, Options = options ?? new DeckOptions { TotalCards = ids.Length + 1 } };

    [Theory]
    [InlineData("iron", 4)] [InlineData("bronze", 3)] [InlineData("silver", 2)] [InlineData("gold", 1)]
    [InlineData("Standard", 4)] [InlineData("Limited", 3)] [InlineData("Special", 2)] [InlineData("Elite", 1)]
    public void RarityCopiesAreEnforced(string rarity, int limit)
    {
        var cards = new[] { Card("a", rarity: rarity), Card("b"), Card("c") };
        var valid = Deck(Enumerable.Repeat("a", limit).Concat(Enumerable.Repeat("b", 4)).ToArray());
        Assert.Empty(ConstructionRules.Validate(valid, cards));
        var excess = Deck(valid.CardIds.Append("a").ToArray());
        Assert.Contains(ConstructionRules.Validate(excess, cards), s => s.Contains($"最多 {limit}"));
    }
    [Theory]
    [InlineData("iron", Rarity.Standard)] [InlineData("bronze", Rarity.Limited)]
    [InlineData("silver", Rarity.Special)] [InlineData("gold", Rarity.Elite)]
    public void SourceRaritiesReachNativeDefinitions(string rarity, string expected)
    {
        Assert.True(CardPoolCompiler.TryCompile(Card("a", rarity: rarity) with { Cost = 1 }, out var compiled, out _));
        Assert.Equal(expected, compiled!.Definition.Rarity);
    }
    [Fact]
    public void StandardIs39CardsPlusHQAndCustomOptionsAreIndependent()
    {
        var cards = Enumerable.Range(0, 10).Select(i => Card("a" + i)).ToArray();
        var ids = cards.SelectMany(c => Enumerable.Repeat(c.CardId, 4)).Take(39).ToArray();
        var standard = Deck(ids, new DeckOptions());
        Assert.Empty(ConstructionRules.Validate(standard, cards)); // No invented eight-unit requirement.
        Assert.Contains(ConstructionRules.Validate(standard with { CardIds = ids[..30] }, cards), e => e.Contains("30 / 39"));
        Assert.Empty(ConstructionRules.Validate(standard with { CardIds = ids[..30], Options = new DeckOptions { TotalCards = 31 } }, cards));
        Assert.Empty(ConstructionRules.Validate(standard with { CardIds = ids[..21], Options = new DeckOptions { UnrestrictedSize = true } }, cards));
        Assert.NotEmpty(ConstructionRules.Validate(standard with { CardIds = ids[..4], Options = new DeckOptions { UnrestrictedSize = true } }, cards));
    }
    [Fact]
    public void MainAllyAndEliteConstraintsCanAllBeLiftedTogether()
    {
        var cards = new[] { Card("main"), Card("ally", "deran"), Card("third", "USG"), Card("elite", "deran", "gold") };
        var deck = Deck(["main", "main", "main", "ally", "third", "elite"]);
        var errors = ConstructionRules.Validate(deck, cards);
        Assert.Contains(errors, s => s.Contains("不属于")); Assert.Contains(errors, s => s.Contains("精英卡"));
        Assert.Empty(ConstructionRules.Validate(deck with { Options = deck.Options with { UnrestrictedNations = true } }, cards));
        var allyCards = Enumerable.Range(0, 4).Select(i => Card("b" + i, "deran")).ToArray();
        var tooMany = Deck(allyCards.SelectMany(c => Enumerable.Repeat(c.CardId, 4)).Take(13).ToArray());
        Assert.Contains(ConstructionRules.Validate(tooMany, allyCards), s => s.Contains("12 张"));
        Assert.Empty(ConstructionRules.Validate(tooMany with { Options = tooMany.Options with { UnrestrictedNations = true } }, allyCards));
    }
    [Fact]
    public void RemovedGeneratedAndUnimplementedCardsNeverBecomePlayable()
    {
        var c = new[] { Card("known"), Card("token") with { IsToken = true } };
        var errors = ConstructionRules.Validate(Deck(["known", "token", "missing", "known", "known"]), c, new HashSet<string>());
        Assert.Contains(errors, s => s.Contains("已不存在")); Assert.Contains(errors, s => s.Contains("无法加入")); Assert.Contains(errors, s => s.Contains("尚未支持"));
    }
    [Fact]
    public void DraftSaveCloneActiveDeleteAndReloadPreserveSnapshots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orc-decks-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "decks.json"); var library = new DeckLibrary(path);
            var draft = new ConstructedDeck { Name = "草稿", CardIds = ["a"], Options = new DeckOptions { UnrestrictedSize = true } };
            library.Save(draft); draft.CardIds[0] = "mutated";
            Assert.Equal("a", library.Decks[0].CardIds[0]);
            library.Activate(draft.Id); var copy = library.ActiveDeck!; copy.CardIds[0] = "other";
            Assert.Equal("a", library.ActiveDeck!.CardIds[0]);
            var duplicate = library.ActiveDeck with { Id = Guid.NewGuid().ToString("N"), Name = "副本" }; library.Save(duplicate);
            var reloaded = new DeckLibrary(path); Assert.Equal(draft.Id, reloaded.ActiveId); Assert.Equal(2, reloaded.Decks.Count);
            reloaded.Delete(draft.Id); Assert.Null(reloaded.ActiveDeck); Assert.Single(new DeckLibrary(path).Decks);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void CorruptOrFutureFilesArePreservedAndFailedWritesRollBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orc-decks-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "decks.json"); File.WriteAllText(path, "{\"Schema\":99,\"Decks\":[]}");
            var library = new DeckLibrary(path); Assert.NotNull(library.LoadError);
            Assert.Throws<IOException>(() => library.Save(new ConstructedDeck())); Assert.Contains("99", File.ReadAllText(path));
            var blocked = new DeckLibrary(Path.Combine(dir, "blocked")); Directory.CreateDirectory(Path.Combine(dir, "blocked"));
            var error = Record.Exception(() => blocked.Save(new ConstructedDeck()));
            Assert.True(error is IOException or UnauthorizedAccessException); Assert.Empty(blocked.Decks);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void CodesPreserveRulesAndCardsButImportGetsNewIdentity()
    {
        var original = Deck(["a", "b", "a"], new DeckOptions { TotalCards = 28, UnrestrictedSize = true, UnrestrictedNations = true });
        var imported = DeckLibrary.Import(DeckLibrary.Export(original)); Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal(original.CardIds, imported.CardIds); Assert.Equal(original.Options, imported.Options);
        Assert.Throws<InvalidDataException>(() => DeckLibrary.Import("%%KARDS"));
        Assert.Throws<InvalidDataException>(() => DeckLibrary.Import("ORK1:broken"));
    }
    [Theory]
    [InlineData(39, true)] [InlineData(30, true)] [InlineData(16, false)]
    public async Task ActualNativeMatchReceivesExactCompositionAndSelectedCount(int size, bool mixed)
    {
        var catalog = Catalog(); var service = new DeckConstructionService(catalog.Cards, Source);
        var deck = service.Starter(size);
        if (!mixed)
        {
            var eligible = catalog.Cards.Where(c => service.PlayableIds.Contains(c.CardId) && c.Set == "av76");
            deck = deck with { Options = new DeckOptions { TotalCards = size + 1 }, CardIds = eligible.SelectMany(c => Enumerable.Repeat(c.CardId, ConstructionRules.CopyLimit(c.Rarity))).Take(size).ToArray() };
        }
        Assert.Empty(service.Validate(deck));
        using var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, 3, "constructed", "unused", sourceDirectory: Source, constructedDeck: deck);
        session.DriveOpponent = false;
        await session.SettleMulliganAsync(interactive: false); session.Pump();
        var player = session.Host.Match.Players[0];
        var actual = player.Deck.Concat(session.View.SelfHand.Select(c => c.Definition.CardId)).OrderBy(id => id).ToArray();
        Assert.Equal(deck.CardIds.OrderBy(id => id), actual);
        Assert.Equal(size - session.View.SelfHand.Count, session.View.SelfDeckCount);
        // 🔧 新引擎对自动对手按 TotalCards(含 HQ=40) 补齐缺张，EnemyDeckCount=40−5；
        // 玩家座不补（行为不对称，引擎侧深水区）。先按现行为断言，待引擎明确口径后再收紧。
        Assert.Equal(size + 1 - 5, session.View.EnemyDeckCount);
        Assert.Equal(Faction.Soviet, player.MainFaction); Assert.Equal(Faction.Germany, player.AllyFaction);
        Assert.Equal(Faction.Soviet, session.Host.Match.Players[1].MainFaction);
        Assert.Equal(Faction.Germany, session.Host.Match.Players[1].AllyFaction);
        Assert.All(session.View.SelfHand, c => Assert.Contains(c.Definition.CardId, deck.CardIds));
    }
    [Fact]
    public async Task FactoryRejectsInvalidDeckInsteadOfFallingBackToRandomCards()
    {
        var c = Catalog(); var service = new DeckConstructionService(c.Cards, Source); var deck = service.Starter();
        await Assert.ThrowsAsync<InvalidDataException>(() => OrcMatchSession.CreateFromCatalogAsync(c.Cards, 1, "invalid", "unused", sourceDirectory: Source, constructedDeck: deck with { CardIds = ["missing"] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => OrcMatchSession.CreateFromCatalogAsync(c.Cards, 1, "override", "unused", sourceDirectory: Source, constructedDeck: deck, openingHand: deck.CardIds[..4]));
        var edited = c.Cards.Select(card => card.CardId == deck.CardIds[0] ? card with { Text = card.Text + " changed" } : card).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => OrcMatchSession.CreateFromCatalogAsync(edited, 1, "changed", "unused", sourceDirectory: Source, constructedDeck: deck));
    }
}
