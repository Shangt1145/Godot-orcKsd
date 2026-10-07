using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game;
using Orc.Game.Cards;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class Stage6B1AdmissionTests
{
    private const string Source = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
    private static CardCatalog Catalog(string directory = Source) { var c = new CardCatalog(); c.Load(directory); return c; }

    [Fact]
    public void AdmissionSeparatesStructuralCoverageFromReviewedSemantics()
    {
        var catalog = Catalog(); var result = VerifiedCardPool.Compile(catalog.Cards, Source);
        Assert.Equal(315, result.Report.Total); Assert.Equal(9, result.Report.Verified);
        Assert.True(result.Report.StructurallyRepresentable > result.Report.Verified);
        Assert.Equal(result.Report.Verified, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal(CardCategory.Unit, e.Definition.Category));
        Assert.Contains(result.Report.Cards, r => r.CardId == "USG/units/_1" && r.Support == UiCardSupport.Unsupported);
        Assert.Contains(result.Report.Cards, r => r.CardId == "deran/units/_2" && r.Support == UiCardSupport.Unsupported);
        Assert.Contains(result.Report.Cards, r => r.Support == UiCardSupport.GeneratedOnly);
        Assert.Contains(result.Report.Cards, r => r.CardId == "USG/commands/27" && r.Support == UiCardSupport.Unsupported);
        var directory = Path.GetFullPath(Path.Combine(Source, "../../../artifacts")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stage6b1-card-pool.json"), JsonSerializer.Serialize(result.Report,
            new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
    }

    [Fact]
    public void ReviewedKeywordCardsKeepBlitzSmokeMobilizeAndArmorMagnitude()
    {
        var pool = VerifiedCardPool.Compile(Catalog().Cards, Source).Entries.ToDictionary(e => e.Id);
        Assert.Contains(pool["av76/units/15"].Definition.Keywords, k => k.Id == KeywordIds.Blitz);
        Assert.Contains(pool["deran/units/_14"].Definition.Keywords, k => k.Id == KeywordIds.Blitz);
        var mobile = pool["av76/units/16"].Definition.Keywords;
        Assert.Contains(mobile, k => k.Id == KeywordIds.SmokeScreen);
        Assert.Contains(mobile, k => k.Id == KeywordIds.Mobilize);
        var armor = Assert.Single(pool["av76/units/18"].Definition.Keywords);
        Assert.Equal(KeywordIds.Armor, armor.Id); Assert.Equal(1, armor.Value);
    }

    [Fact]
    public void ChangedUiDefinitionCannotBorrowAReviewedSourceIdentity()
    {
        var card = Catalog().Cards.Single(c => c.CardId == "USG/units/_4");
        var result = VerifiedCardPool.Compile([card with { BaseAttack = 99 }], Source);
        Assert.Empty(result.Entries); Assert.Equal(UiCardSupport.SourceChanged, result.Report.Cards[0].Support);
    }

    [Fact]
    public void SourceChangesRequireReviewEvenWhenTheUiFieldsDoNotChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ksd-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(Source, "USG.json")))!;
            root["cards"]!["USG/units/_4"]!["notes"] = "Changed source declaration";
            File.WriteAllText(Path.Combine(directory, "USG.json"), root.ToJsonString());
            var card = Catalog(directory).Cards.Single(c => c.CardId == "USG/units/_4");
            var result = VerifiedCardPool.Compile([card], directory);
            Assert.Empty(result.Entries); Assert.Equal(UiCardSupport.SourceChanged, result.Report.Cards[0].Support);
            root["cards"]!["USG/units/_4"]!["effects"] = JsonNode.Parse("[{\"trigger\":\"deployed\"}]");
            File.WriteAllText(Path.Combine(directory, "USG.json"), root.ToJsonString());
            result = VerifiedCardPool.Compile([card], directory);
            Assert.Empty(result.Entries); Assert.Equal(UiCardSupport.Unsupported, result.Report.Cards[0].Support);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ReviewedDeckIsExactDeterministicAndRequiresEnoughCapacity()
    {
        var pool = VerifiedCardPool.Compile(Catalog().Cards, Source).Entries;
        var deck = DeckBuilder.BuildVerified(pool, 7, VerifiedCardPool.ValidationDeckSize);
        Assert.Equal(24, deck.Count); Assert.Equal(deck, DeckBuilder.BuildVerified(pool, 7, 24));
        Assert.All(deck.GroupBy(id => id), g => Assert.InRange(g.Count(), 1, 3));
        Assert.Empty(DeckBuilder.Validate(deck, pool, 24)); // A command card is not mandatory.
        Assert.Throws<InvalidDataException>(() => DeckBuilder.BuildVerified(pool, 7));
        Assert.Throws<InvalidDataException>(() => DeckBuilder.BuildVerified([pool[0]], 7, 24));
    }

    [Fact]
    public void GeneratedCardsAreExcludedAndExplicitDecksAreRejected()
    {
        var pool = VerifiedCardPool.Compile(Catalog().Cards, Source).Entries;
        var token = new CardDefinitionEntry("generated", new CardDefinition("Token", 0, 0, 1, 1,
            faction: Faction.Germany, rarity: Rarity.Standard, tags: ["token"]));
        var augmented = pool.Append(token).ToArray();
        var deck = DeckBuilder.BuildVerified(augmented, 7, 24);
        Assert.DoesNotContain(token.Id, deck);
        Assert.Contains(DeckBuilder.Validate(deck.Take(23).Append(token.Id).ToArray(), augmented, 24), r => r.Contains("生成卡"));
    }

    [Fact]
    public async Task CatalogInitializationCannotSilentlyFallBackOrPinUnreviewedCards()
    {
        var card = Catalog().Cards.Single(c => c.CardId == "USG/commands/27");
        await Assert.ThrowsAsync<InvalidDataException>(() => OrcMatchSession.CreateFromCatalogAsync([card], 7, "rejected-catalog", "probe", sourceDirectory: Source));
        await Assert.ThrowsAsync<InvalidDataException>(() => OrcMatchSession.CreateFromCatalogAsync(Catalog().Cards, 7, "bad-pin", "probe",
            openingHand: ["av76/units/-4"], sourceDirectory: Source));
    }

    [Theory]
    [InlineData("av76/units/12")]
    [InlineData("av76/units/13")]
    [InlineData("av76/units/15")]
    [InlineData("av76/units/16")]
    [InlineData("av76/units/18")]
    [InlineData("deran/units/_14")]
    [InlineData("deran/units/_4")]
    [InlineData("deran/units/_6q")]
    [InlineData("USG/units/_4")]
    public async Task EveryReviewedDefinitionLoadsAndDeploysWithItsSourceStats(string id)
    {
        var cards = Catalog().Cards; var original = cards.Single(c => c.CardId == id);
        using var session = await OrcMatchSession.CreateFromCatalogAsync(cards, 7, "reviewed-" + id, "probe",
            openingHand: [id], sourceDirectory: Source);
        Assert.Equal(9, session.CardPoolReport!.Verified);
        await session.SettleMulliganAsync(false);
        session.SettleProjection();
        var actorUid = session.View.SelfHand.Single(c => c.CardId == id).Uid;
        for (var i = 0; i < 8 && session.View.SelfKredits < original.Cost; i++)
        {
            Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new EndTurn())); session.SettleProjection();
        }
        var actor = session.View.SelfHand.Single(c => c.Uid == actorUid);
        var points = session.View.SelfKredits;
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(actor.Uid, 0)));
        session.SettleProjection();
        var unit = session.View.SelfLine.Single(c => c.Uid == actor.Uid);
        Assert.Equal(points - original.Cost, session.View.SelfKredits);
        Assert.Equal(original.BaseAttack, unit.EffectiveAttack);
        Assert.Equal(original.BaseDefense, unit.EffectiveDefense);
        Assert.Equal(original.BaseOpCost, unit.EffectiveOpCost);
    }
}
