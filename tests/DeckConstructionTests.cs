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
    // 旧卡池目录名（av76/deran/USG/UN）与引擎阵营标识指同一个国家。UI 若按裸字符串比较，
    // 就会放过「主国 av76 ＋ 盟国 soviet」这种组合，保存成功、开局时被引擎 fail-fast 拒绝。
    [Theory]
    [InlineData("av76", Faction.Soviet)] [InlineData("deran", Faction.Germany)]
    [InlineData("USG", Faction.USA)] [InlineData("UN", Faction.Britain)]
    public void LegacyPoolNamesNormalizeToTheSameEngineFaction(string pool, string faction)
    {
        Assert.True(DeckNations.IsLegacyPool(pool));
        Assert.Equal(faction, DeckNations.Canonical(pool));
        Assert.True(Faction.TryNormalize(DeckNations.Canonical(pool), out var normalized));
        Assert.Equal(faction, normalized);
    }
    [Theory]
    [InlineData("av76", "soviet")] [InlineData("soviet", "av76")]
    [InlineData("deran", "germany")] [InlineData("USG", "usa")] [InlineData("UN", "britain")]
    public void MainAndAllyInDifferentSpellingsOfOneNationAreRejected(string main, string ally)
    {
        var cards = new[] { Card("m", main), Card("a", ally) };
        var deck = Deck(["m", "m"], new DeckOptions { TotalCards = 3, MainNation = main, AllyNation = ally });
        Assert.Contains(ConstructionRules.Validate(deck, cards), s => s.Contains("同一个国家"));
        Assert.Contains(ConstructionRules.Validate(deck with { Options = deck.Options with { UnrestrictedNations = true } }, cards),
            s => s.Contains("同一个国家")); // 混编也要拦：引擎仍按主国＋盟国建配置。
    }
    [Fact]
    public void CardsBelongToNationByCanonicalFactionNotByCardPoolFolder()
    {
        var cards = new[] { Card("legacy", "av76"), Card("packed", "soviet"), Card("ally", Faction.Britain) };
        // 主国写 pack 口径（soviet），旧卡池 av76 的苏联卡同样是主国卡。
        var packMain = Deck(["legacy", "legacy", "packed", "packed", "ally"],
            new DeckOptions { TotalCards = 6, MainNation = "soviet", AllyNation = "britain" });
        Assert.Empty(ConstructionRules.Validate(packMain, cards));
        Assert.Null(ConstructionRules.CannotAdd(packMain with { CardIds = packMain.CardIds[..4] }, cards[0]));
        // 反过来：主国写旧卡池（av76），卡包口径的 soviet 卡也进得来。
        var poolMain = packMain with { Options = packMain.Options with { MainNation = "av76" } };
        Assert.Empty(ConstructionRules.Validate(poolMain, cards));
    }
    [Fact]
    public void AllyCopyLimitCountsBothSpellingsOfTheAllyNation()
    {
        var cards = Enumerable.Range(0, 4).Select(i => Card("a" + i, "av76"))
            .Concat(Enumerable.Range(0, 4).Select(i => Card("b" + i, "soviet"))).ToArray();
        var ids = cards.SelectMany(c => Enumerable.Repeat(c.CardId, 4)).Take(13).ToArray();
        var deck = Deck(ids, new DeckOptions { TotalCards = 14, MainNation = "germany", AllyNation = "soviet" });
        Assert.Contains(ConstructionRules.Validate(deck, cards), s => s.Contains("12 张"));
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
    public void CodesPreserveRulesAndCardsButImportGetsNewIdentity()    {
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
        using var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, 3, "constructed", "unused", sourceDirectory: Source, constructedDeck: deck, firstPlayerIndex: 0);
        session.DriveOpponent = false;
        await session.SettleMulliganAsync(interactive: false); session.Pump();
        var player = session.Host.Match.Players[0];
        var actual = player.Deck.Concat(session.View.SelfHand.Select(c => c.Definition.CardId)).OrderBy(id => id).ToArray();
        Assert.Equal(deck.CardIds.OrderBy(id => id), actual);
        Assert.Equal(size - session.View.SelfHand.Count, session.View.SelfDeckCount);
        var opponent = session.Host.Match.Players[1];
        Assert.Equal(opponent.Deck.Count, session.View.EnemyDeckCount);
        Assert.Equal(size, opponent.Deck.Count + opponent.Hand.Count);
        Assert.Equal(Faction.Soviet, player.MainFaction); Assert.Equal(Faction.Germany, player.AllyFaction);
        Assert.Equal(Faction.Soviet, session.Host.Match.Players[1].MainFaction);
        Assert.Equal(Faction.Germany, session.Host.Match.Players[1].AllyFaction);
        Assert.All(session.View.SelfHand, c => Assert.Contains(c.Definition.CardId, deck.CardIds));
    }
    [Fact]
    public async Task FactoryRejectsInvalidDeckInsteadOfFallingBackToRandomCards()
    {
        var c = Catalog(); var service = new DeckConstructionService(c.Cards, Source); var deck = service.Starter();
        await Assert.ThrowsAsync<InvalidDataException>(() => OrcMatchSession.CreateFromCatalogAsync(c.Cards, 1, "invalid", "unused", sourceDirectory: Source, constructedDeck: deck with { CardIds = ["missing"] }, firstPlayerIndex: 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => OrcMatchSession.CreateFromCatalogAsync(c.Cards, 1, "override", "unused", sourceDirectory: Source, constructedDeck: deck, openingHand: deck.CardIds[..4], firstPlayerIndex: 0));
        var edited = c.Cards.Select(card => card.CardId == deck.CardIds[0] ? card with { Text = card.Text + " changed" } : card).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => OrcMatchSession.CreateFromCatalogAsync(edited, 1, "changed", "unused", sourceDirectory: Source, constructedDeck: deck, firstPlayerIndex: 0));
    }
}
