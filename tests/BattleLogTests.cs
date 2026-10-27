using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 战报/弃牌堆/词条释义的数据面测试（P6）。
///
/// 这里测的是"引擎给的东西有没有被如实写成一句话"，不是排版：每条断言都能对上
/// 一个表现步骤或一个投影字段。凡是需要引擎真值的用例，一律用
/// <see cref="OrcDiscardVerification.RunAsync"/> 现跑一局，不用手捏的假数据冒充。
/// </summary>
public sealed class BattleLogTests
{
    private static UiCardView Card(string id, string name, string side = "self", string zone = "hand") => new()
    {
        Uid = id + "#1",
        Definition = new UiCardDefinition { CardId = id, Name = name },
        OwnerSide = side, Zone = zone, Health = 3,
        EffectiveAttack = 2, EffectiveDefense = 3, EffectiveCost = 2,
    };

    private static UiMatchView Match(string phase = "play", string active = "self") => new()
    {
        MatchId = "log-fixture", Phase = phase, ActivePlayerSide = active,
        Turn = 1, SelfKredits = 3, SelfMaxKredits = 3,
    };

    private static UiPresentationResolution Resolution(UiMatchView after, params UiPresentationStep[] steps) =>
        UiSnapshots.Freeze(new UiPresentationResolution(after.MatchId, steps, after));

    [Fact]
    public async Task RealEngineDiscardsLandInBothDiscardPiles()
    {
        var fixture = await OrcDiscardVerification.RunAsync();
        var log = new BattleLog();
        foreach (var resolution in fixture.Resolutions) log.Ingest(resolution);

        var own = Assert.Single(log.Discards("self"));
        Assert.Equal(BattleLogKind.Discard, own.Kind);
        Assert.Contains(fixture.Initial.SelfHand[0].Definition.Name, own.Text);

        // 对手的弃牌不带牌面：写"1 张牌"，不能出现"未知卡牌"这种既不像战报也不像提示的句子。
        var theirs = Assert.Single(log.Discards("enemy"));
        Assert.Contains("1 张牌", theirs.Text);
        Assert.Contains("对手手牌不可见", theirs.Text);
        Assert.DoesNotContain("未知", theirs.Text);
    }

    [Fact]
    public void EverySuppliedStepTypeIsIngestedWithoutThrowing()
    {
        var card = Card("a", "甲");
        var log = new BattleLog();
        log.Ingest(Resolution(Match(),
            new UiTurnPresentation("self", 2),
            new UiMulliganPresentation("self", 2, []),
            new UiDeploymentPresentation(card, Match()),
            new UiOrderPresentation(card, []),
            new UiDrawPresentation("self", 0),
            new UiBoardImpactsPresentation([new UiOrderImpact(card, card with { Health = 1 }, 2, null)]),
            new UiRemovalPresentation(card),
            new UiDiscardPresentation("enemy", null, UiDiscardKind.Burn),
            new UiCounterPresentation("enemy", UiCounterStage.Triggered, card, card),
            new UiShufflePresentation("enemy"),
            new UiStatusPresentation(card, card, UiStatusKind.Suppressed),
            new UiResourcePresentation("self", UiResourceCause.Gain, 1, 2, 3, 3)));
        Assert.True(log.Entries.Count >= 10, $"entries={log.Entries.Count}");
        Assert.All(log.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Text)));
    }

    [Fact]
    public void TurnStepsAdvanceTheCounterAndNeverMoveItBackwards()
    {
        var log = new BattleLog();
        log.Ingest(Resolution(Match(), new UiTurnPresentation("enemy", 3)));
        Assert.Equal(3, log.Turn);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(BattleLogKind.Turn, entry.Kind);
        Assert.Equal("对手", entry.SideLabel);
        Assert.Contains("第 3 回合", entry.Text);

        // 迟到的旧步骤不能把回合数拉回去，否则战报会从第 3 回合跳回第 2 回合。
        log.Ingest(Resolution(Match(), new UiTurnPresentation("self", 2)));
        Assert.Equal(3, log.Turn);
    }

    [Fact]
    public void FatigueIsLoggedAsDamageWithItsAmount()
    {
        var log = new BattleLog();
        log.Ingest(Resolution(Match(), new UiTurnPresentation("self", 5, UiTurnKind.Fatigue, 3)));
        var entry = Assert.Single(log.Entries);
        Assert.Equal(BattleLogKind.Damage, entry.Kind);
        Assert.Equal(3, entry.Amount);
        Assert.Contains("疲劳", entry.Text);
        Assert.Contains("3 点", entry.Text);
    }

    [Fact]
    public void DrawsAreMergedIntoOneLinePerSide()
    {
        var log = new BattleLog();
        log.Ingest(Resolution(Match(),
            new UiDrawPresentation("self", 0), new UiDrawPresentation("self", 1), new UiDrawPresentation("enemy", 0)));
        Assert.Equal(2, log.Entries.Count);
        Assert.Contains(log.Entries, e => e.Side == "self" && e.Text == "抽牌 2 张" && e.Amount == 2);
        Assert.Contains(log.Entries, e => e.Side == "enemy" && e.Text == "抽牌 1 张");
    }

    [Fact]
    public void BoardImpactsCarryDamageAndTheAttackerThatDealtIt()
    {
        var target = Card("a", "甲");
        var attacker = Card("b", "乙", "self", "frontline");
        var log = new BattleLog();
        log.Ingest(Resolution(Match(), new UiBoardImpactsPresentation([
            new UiOrderImpact(target, target with { Health = 1 }, 2, attacker),
            new UiOrderImpact(Card("c", "丙"), null, 5),
        ])));

        var hit = Assert.Single(log.Entries, e => e.Kind == BattleLogKind.Damage);
        Assert.Contains("甲 受到 2 点伤害", hit.Text);
        Assert.Contains("（乙）", hit.Text);
        Assert.Equal(2, hit.Amount);
        var death = Assert.Single(log.Entries, e => e.Kind == BattleLogKind.Death);
        Assert.Contains("丙 阵亡", death.Text);
    }

    [Fact]
    public void ADamagedStatusIsNotLoggedTwiceButOtherStatusesAre()
    {
        var card = Card("a", "甲");
        var log = new BattleLog();
        // 同一次伤害已经由 board-impacts 记过；Damaged 再记一条就是同一件事写两遍。
        log.Ingest(Resolution(Match(), new UiStatusPresentation(card, card with { Health = 1 }, UiStatusKind.Damaged)));
        Assert.Empty(log.Entries);
        log.Ingest(Resolution(Match(), new UiStatusPresentation(card, card, UiStatusKind.Suppressed)));
        var entry = Assert.Single(log.Entries);
        Assert.Equal(BattleLogKind.Status, entry.Kind);
        Assert.Contains("被压制", entry.Text);
    }

    [Fact]
    public void ResourceStepsAreDeliberatelyLeftOut()
    {
        var log = new BattleLog();
        log.Ingest(Resolution(Match(), new UiResourcePresentation("self", UiResourceCause.Gain, 1, 2, 3, 3)));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void AnUnknownDeploymentCostIsOmittedRatherThanPrintedAsAQuestionMark()
    {
        var log = new BattleLog();
        log.Ingest(Resolution(Match(), new UiDeploymentPresentation(Card("a", "甲") with { EffectiveCost = null }, Match())));
        Assert.Equal("部署 甲", Assert.Single(log.Entries).Text);
        log.Ingest(Resolution(Match(), new UiDeploymentPresentation(Card("b", "乙"), Match())));
        Assert.Equal("部署 乙（2 费）", log.Entries[1].Text);
    }

    [Fact]
    public void TheResultLineAppearsOnlyOnceTheMatchIsOver()
    {
        var log = new BattleLog();
        log.Ingest(Resolution(Match()));
        Assert.DoesNotContain(log.Entries, e => e.Kind == BattleLogKind.Result);

        log.Ingest(Resolution(Match("over") with { ResultTitle = "胜利", FinalTurn = 9 }));
        var result = Assert.Single(log.Entries, e => e.Kind == BattleLogKind.Result);
        Assert.Contains("胜利", result.Text);
        Assert.Contains("第 9 回合", result.Text);
    }

    [Fact]
    public void AMissingResultTitleIsReportedAsMissing()
    {
        var log = new BattleLog();
        log.Ingest(Resolution(Match("over")));
        var result = Assert.Single(log.Entries, e => e.Kind == BattleLogKind.Result);
        Assert.Contains("结果未提供", result.Text);
    }

    [Fact]
    public void CapacityDropsTheOldestEntriesAndSaysHowMany()
    {
        var log = new BattleLog();
        for (var i = 0; i < BattleLog.Capacity + 7; i++) log.Note(BattleLogKind.Move, "self", "移动");
        Assert.Equal(BattleLog.Capacity, log.Entries.Count);
        Assert.Equal(7, log.Dropped);
    }

    [Fact]
    public void ResetClearsEntriesTurnAndDropCount()
    {
        var log = new BattleLog();
        log.Ingest(Resolution(Match(), new UiTurnPresentation("self", 4)));
        log.Reset();
        Assert.Empty(log.Entries);
        Assert.Equal(0, log.Turn);
        Assert.Equal(0, log.Dropped);
    }

    [Fact]
    public void ObservedTurnsOnlyMoveForwardAndStampLaterEntries()
    {
        var log = new BattleLog();
        log.Note(BattleLogKind.Move, "self", "移动");
        Assert.Equal(0, log.Entries[0].Turn);

        // 投影给出回合号后，条目必须落在那一回合上——界面不该出现"0 回合"。
        log.Observe(4);
        log.Note(BattleLogKind.Attack, "self", "进攻");
        Assert.Equal(4, log.Entries[1].Turn);
        log.Observe(2);
        Assert.Equal(4, log.Turn);
    }

    [Fact]
    public void IngestTakesTheTurnFromTheSettledProjection()
    {
        var log = new BattleLog();
        log.Ingest(Resolution(Match() with { Turn = 7 },
            new UiDeploymentPresentation(Card("a", "甲"), Match())));
        Assert.Equal(7, log.Turn);
        Assert.Equal(7, Assert.Single(log.Entries).Turn);
    }

    [Fact]
    public void BlankTextIsNeverStored()
    {
        var log = new BattleLog();
        log.Note(BattleLogKind.Move, "self", "   ");
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void GlossaryExplainsOnlyVerifiedKeywordsAndSaysNothingOtherwise()
    {
        Assert.NotEmpty(KeywordGlossary.BuiltIn);
        Assert.All(KeywordGlossary.BuiltIn, pair =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"词条 {pair.Key} 的释义是空的");
            Assert.Equal(pair.Key, pair.Key.Trim());
        });
        Assert.NotNull(KeywordGlossary.Describe("armor"));
        // 引擎的词条 id 是小写；大小写不同即未核对过，宁可返回 null 让界面说"未提供"。
        Assert.Null(KeywordGlossary.Describe("Armor"));
        Assert.Null(KeywordGlossary.Describe("not-a-keyword"));
    }
}
