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
    // 每个卡池名（av76 / deran / USG / UN）都是**独立国家**，不是苏/德/美/英的别名：
    // 星盟自己的卡面注释就写着「协约国 = USG + AV76」——两家并列，不是同一家。
    // 归一只能统一写法（USG → usg 的引擎 id），绝不能把两个国家折成一个。
    [Theory]
    [InlineData("av76", "av76")] [InlineData("deran", "deran")]
    [InlineData("USG", "usg")] [InlineData("UN", "un")]
    public void PoolNationsKeepTheirOwnIdentity(string pool, string engineId)
    {
        Assert.True(DeckNations.IsCustomNation(pool));
        Assert.Equal(engineId, DeckNations.Canonical(pool));
        Assert.False(DeckNations.Same(pool, Faction.Soviet));
        Assert.False(DeckNations.Same(pool, Faction.Germany));
        Assert.False(DeckNations.Same(pool, Faction.USA));
        Assert.False(DeckNations.Same(pool, Faction.Britain));
    }
    // 唯一该归并的是「同一个国家的同义写法」：中文名与引擎 id。
    [Theory]
    [InlineData("苏联", "soviet")] [InlineData("德国", "germany")]
    [InlineData("美国", "usa")] [InlineData("英国", "britain")]
    [InlineData("日本", "japan")] [InlineData("意大利", "italy")]
    public void SynonymsOfOneNationAreTheSameNation(string chinese, string engineId)
    {
        Assert.Equal(engineId, DeckNations.Canonical(chinese));
        Assert.True(DeckNations.Same(chinese, engineId));
        Assert.False(DeckNations.Same(chinese, "av76"));
    }
    [Fact]
    public void PoolNationsAreRegisteredAsEngineMajors()
    {
        OrcEngineHost.RegisterCustomNations();
        foreach (var nation in DeckNations.CustomNations)
        {
            var engineId = DeckNations.Canonical(nation);
            Assert.True(Faction.IsDefined(engineId), $"{nation} 必须以 {engineId} 注册为引擎阵营");
            Assert.True(FactionRules.IsMajorFaction(engineId), $"{nation} 必须能当主国");
            // 独立国家之间互为合法盟国（主国 av76 ＋ 盟国 soviet 是正常组合）。
            Assert.True(FactionRules.IsValidAlly(Faction.Soviet, engineId));
        }
    }
    [Fact]
    public void MainNationAv76AndAllySovietIsALegalDeck()
    {
        var cards = new[] { Card("m", "av76"), Card("a", "soviet") };
        var deck = Deck(["m", "m", "a", "a", "m"], new DeckOptions { TotalCards = 6, MainNation = "av76", AllyNation = "soviet" });
        Assert.Empty(ConstructionRules.Validate(deck, cards));
    }
    [Theory]
    [InlineData("USG", "usg")] [InlineData("usg", "USG")]
    public void DifferentSpellingsOfOneNationAreStillRejected(string main, string ally)
    {
        var cards = new[] { Card("m", main), Card("a", ally) };
        var deck = Deck(["m", "m"], new DeckOptions { TotalCards = 3, MainNation = main, AllyNation = ally });
        Assert.Contains(ConstructionRules.Validate(deck, cards), s => s.Contains("同一个国家"));
        Assert.Contains(ConstructionRules.Validate(deck with { Options = deck.Options with { UnrestrictedNations = true } }, cards),
            s => s.Contains("同一个国家")); // 混编也要拦：引擎仍按主国＋盟国建配置。
    }
    [Fact]
    public void CardsBelongToTheirOwnNationNotToAnotherNation()
    {
        var cards = new[] { Card("av", "av76"), Card("su", "soviet"), Card("br", Faction.Britain) };
        // 主国 soviet ＋ 盟国 britain：av76 的卡**不属于任何一个**，不能进这套卡组。
        var deck = Deck(["av", "su", "su", "br", "br"], new DeckOptions { TotalCards = 6, MainNation = "soviet", AllyNation = "britain" });
        Assert.Contains(ConstructionRules.Validate(deck, cards), s => s.Contains("不属于主国或盟国"));
        Assert.NotNull(ConstructionRules.CannotAdd(deck, cards[0]));
        // 换成主国 av76、盟国 soviet 就合法（只带这两国的卡）。
        var av76Main = Deck(["av", "av", "su", "su", "av"],
            new DeckOptions { TotalCards = 6, MainNation = "av76", AllyNation = "soviet" });
        Assert.Empty(ConstructionRules.Validate(av76Main, cards));
    }
    [Fact]
    public void AllyCopyLimitCountsTheAllyNationOnly()
    {
        var cards = Enumerable.Range(0, 4).Select(i => Card("a" + i, "soviet"))
            .Concat(Enumerable.Range(0, 4).Select(i => Card("b" + i, "av76"))).ToArray();
        // 12 张 soviet ＋ 1 张 av76：av76 不是盟国，不该把总数顶过 12。
        var twelve = new[] { "a0", "a0", "a0", "a0", "a1", "a1", "a1", "a1", "a2", "a2", "a2", "a2", "b0" };
        var deck = Deck(twelve, new DeckOptions { TotalCards = 14, MainNation = "germany", AllyNation = "soviet" });
        Assert.DoesNotContain(ConstructionRules.Validate(deck, cards), s => s.Contains("12 张"));
        // 再补一张 soviet 就到 13，超上限。
        var thirteen = twelve.Append("a3").ToArray();
        Assert.Contains(ConstructionRules.Validate(Deck(thirteen, deck.Options), cards), s => s.Contains("12 张"));
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
        // 主国按卡组自己的国家标识写入引擎：av76 是独立国家，不该被折成 soviet。
        Assert.Equal(DeckNations.Canonical(deck.Options.MainNation), player.MainFaction);
        Assert.Equal(DeckNations.Canonical(deck.Options.AllyNation), player.AllyFaction);
        Assert.Equal(DeckNations.Canonical(deck.Options.MainNation), session.Host.Match.Players[1].MainFaction);
        Assert.Equal(DeckNations.Canonical(deck.Options.AllyNation), session.Host.Match.Players[1].AllyFaction);
        Assert.All(session.View.SelfHand, c => Assert.Contains(c.Definition.CardId, deck.CardIds));
    }
    // 用户报的场景：主国 av76 ＋ 盟国 苏联，点出击必须真的能开一局。
    // 旧实现把 av76 折成 soviet → 引擎判「盟国＝主国」→ fail-fast 拒绝，构筑界面却放行。
    [Fact]
    public async Task Av76MainNationWithSovietAllyStartsARealMatch()
    {
        const string main = "av76", ally = "soviet";
        var catalog = Catalog(); var service = new DeckConstructionService(catalog.Cards, Source);
        var eligible = catalog.Cards.Where(c => service.PlayableIds.Contains(c.CardId) && c.Set == main).ToArray();
        Assert.True(eligible.Length >= 5, $"{main} 的可实战卡不足以组成卡组");
        var deck = new ConstructedDeck
        {
            Options = new DeckOptions { TotalCards = 17, MainNation = main, AllyNation = ally },
            CardIds = eligible.SelectMany(c => Enumerable.Repeat(c.CardId, ConstructionRules.CopyLimit(c.Rarity))).Take(16).ToArray(),
        };
        Assert.Empty(service.Validate(deck));
        using var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, 5, "constructed", "unused",
            sourceDirectory: Source, constructedDeck: deck, firstPlayerIndex: 0);
        session.DriveOpponent = false;
        await session.SettleMulliganAsync(interactive: false); session.Pump();
        Assert.Equal(DeckNations.Canonical(main), session.Host.Match.Players[0].MainFaction);
        Assert.Equal(DeckNations.Canonical(ally), session.Host.Match.Players[0].AllyFaction);
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
