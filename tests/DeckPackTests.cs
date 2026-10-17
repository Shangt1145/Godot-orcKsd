using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

public class DeckPackTests
{
    private const string Source = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
    private static DeckConstructionService Service()
    {
        var catalog = new CardCatalog(); catalog.Load(Source); return new(catalog.Cards, Source);
    }
    [Fact]
    public void SelectionPersistsAndDoesNotAliasMutableArrays()
    {
        var deck = new ConstructedDeck { PackIds = ["source", "pack/builtin"], CardIds = ["av76/units/12"] };
        var snapshot = deck.Snapshot(); deck.PackIds[0] = "orc";
        Assert.Equal("source", snapshot.PackIds![0]);
        var imported = DeckLibrary.Import(DeckLibrary.Export(snapshot));
        Assert.Equal(snapshot.PackIds, imported.PackIds);
        var folder = Path.Combine(Path.GetTempPath(), "deck-packs-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        try { var path = Path.Combine(folder, "decks.json"); new DeckLibrary(path).Save(snapshot);
            Assert.Equal(snapshot.PackIds, Assert.Single(new DeckLibrary(path).Decks).PackIds); }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void LegacyDeckInfersOnlyItsOwnPacksAndEmptyExplicitSelectionIsInvalid()
    {
        var deck = new ConstructedDeck { CardIds = ["av76/units/12", "diy/one", "pack/builtin/card", "pack/exp-loyalty/other"] };
        Assert.Equal(["source", "diy", "pack/builtin", "pack/exp-loyalty"], DeckCardPacks.Selected(deck));
        Assert.False(DeckCardPacks.Allows(deck, "pack/exp-legions/hidden"));
        Assert.False(DeckCardPacks.ValidSelection([])); Assert.False(DeckCardPacks.ValidSelection(["source", "source"]));
        Assert.False(DeckCardPacks.ValidSelection(["pack/../../escape"]));
    }
    [Fact]
    public void PoolNeverContainsUnreviewedCardsAndOtherPacksCannotBeAdded()
    {
        var service = Service(); var deck = service.Starter() with { PackIds = ["source"] };
        Assert.Equal(24, service.Pool(deck).Count);
        Assert.All(service.Pool(deck), c => Assert.Contains(c.CardId, service.PlayableIds));
        var other = new UiCardDefinition { CardId = "pack/test/card", Name = "other", Rarity = "standard" };
        Assert.NotNull(ConstructionRules.CannotAdd(deck, other));
        Assert.Contains(service.Validate(deck with { PackIds = ["pack/test"] }), p => p.Contains("卡包"));
        Assert.Empty(service.Pool(deck with { PackIds = ["pack/test"] }));
    }
    [Fact]
    public async Task SelectedPackRestrictionIsAlsoEnforcedByNativeMatchFactory()
    {
        var catalog = new CardCatalog(); catalog.Load(Source); var service = new DeckConstructionService(catalog.Cards, Source);
        var illegal = service.Starter() with { PackIds = ["pack/builtin"] };
        await Assert.ThrowsAsync<InvalidDataException>(() => OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,
            31, "pack-scope-rejection", "unused", sourceDirectory: Source, constructedDeck: illegal));
    }
}
