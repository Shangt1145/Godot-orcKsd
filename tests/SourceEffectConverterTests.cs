using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Orc.Game.Cards.Data.Components;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 批次 1 准入：非交互源效果经 <see cref="SourceEffectConverter"/> 翻译成 DIY DSL，
/// 走 DiyCardCompiler 的原生编译链（编译 + 实例化全过才准入）。
/// 断言面＝公开的 VerifiedCardPool.Compile / OrcMatchSession.CreateFromCatalogAsync。
/// </summary>
public class SourceEffectConverterTests
{
    private const string Source = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
    static SourceEffectConverterTests() => OrcEngineHost.WarmUp(); // 内置词条注册面必须先于任何卡定义解析
    private static CardCatalog Catalog() { var c = new CardCatalog(); c.Load(Source); return c; }
    private static (IReadOnlyList<CardDefinitionEntry> Entries, UiCardPoolReport Report) Compile()
    {
        var (entries, report, _) = VerifiedCardPool.Compile(Catalog().Cards, Source);
        return (entries, report);
    }

    [Fact]
    public void ConvertedNonInteractiveEffectsAreAdmittedWithNativeEffectComponents()
    {
        var (entries, report) = Compile();
        foreach (var id in (string[])["UN/unit/_1", "av76/command/-20", "deran/command/_1"])
        {
            var row = Assert.Single(report.Cards, r => r.CardId == id);
            Assert.Equal(UiCardSupport.Verified, row.Support);
            Assert.Contains("Converted non-interactive", row.Reason);
            var entry = Assert.Single(entries, e => e.Id == id);
            var effects = Assert.Single(entry.Definition.Components.OfType<EffectsDefinition>());
            Assert.Single(effects.Inline);
            Assert.Empty(effects.Prefabs);
        }
        Assert.Equal(24, report.Verified); // 21（6B2 基线）+ 3 转换（白板卡还缺 unitType 数据，见 Unsupported）
    }

    [Theory]
    [InlineData("av76/command/-6", "op 'discard' has no non-interactive conversion")]
    [InlineData("USG/commands/_18", "outside the self/all surface")] // random 目标
    public void ShapesBeyondTheNonInteractiveSurfaceStayExplicitlyUnsupported(string id, string reasonPart)
    {
        var (_, report) = Compile();
        var row = Assert.Single(report.Cards, r => r.CardId == id);
        Assert.Equal(UiCardSupport.Unsupported, row.Support);
        Assert.Contains("adapter", row.Reason);
        Assert.Contains(reasonPart, row.Reason);
    }

    [Fact]
    public void DeclarationsThatDivergeFromTheirPrintedTextStayUnreviewed()
    {
        // deran/command/_16（文本"下一回合"vs 声明立即）与 deran/command/26（声明缺伤害）：
        // 形状可转，但审核未放行——准入必须卡在审核门，而不是绕过语义审核。
        var (_, report) = Compile();
        Assert.All(["deran/command/_16", "deran/command/26"],
            id => Assert.Equal(UiCardSupport.Unreviewed, Assert.Single(report.Cards, r => r.CardId == id).Support));
    }

    /// <summary>打一张转换指令：打出后抽一张，手牌数应当不变（−1 出手 ＋1 抽牌）。</summary>
    [Fact]
    public async Task ConvertedOrderDrawsWhenPlayedInARealMatch()
    {
        using var session = await OrcMatchSession.CreateFromCatalogAsync(Catalog().Cards, 20261014,
            "converted-order-draw", "probe", openingHand: ["deran/command/_1"], sourceDirectory: Source);
        Assert.Equal(24, session.CardPoolReport!.Verified);
        await session.SettleMulliganAsync(false);
        session.SettleProjection();
        var actor = session.View.SelfHand.Single(c => c.CardId == "deran/command/_1");
        for (var i = 0; i < 8 && session.View.SelfKredits < 1; i++)
        {
            Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new EndTurn()));
            session.SettleProjection();
        }
        var hand = session.View.SelfHand.Count;
        var kredits = session.View.SelfKredits;
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(actor.Uid)));
        session.SettleProjection();
        Assert.DoesNotContain(session.View.SelfHand, c => c.Uid == actor.Uid);
        Assert.Equal(kredits - 1, session.View.SelfKredits); // 打出扣费
        // 出手 −1、抽牌 ＋1：手牌规模不变——抽牌没发生就会少一张。
        Assert.Equal(hand, session.View.SelfHand.Count);
    }

    /// <summary>块级 condition／ref 选靶／动态数值——公开准入面被结构门抢先的形状，直测转换器拒绝。</summary>
    [Fact]
    public void ConverterRejectsBlockConditionsRefsAndDynamicAmounts()
    {
        var blocked = JsonDocument.Parse("""[{"trigger":"deploy","condition":{"op":"handHasCard"},"actions":[{"op":"gainKredits","amount":2,"side":"self"}]}]""").RootElement;
        Assert.False(SourceEffectConverter.TryConvert(blocked, "unit", out _, out var blockedReason));
        Assert.Contains("unsupported field 'condition'", blockedReason);

        var refs = JsonDocument.Parse("""[{"trigger":"order","actions":[{"op":"damage","amount":3,"target":"t1"}]}]""").RootElement;
        Assert.False(SourceEffectConverter.TryConvert(refs, "order", out _, out var refReason));
        Assert.Contains("outside the self/all surface", refReason);

        var dynamic = JsonDocument.Parse("""[{"trigger":"order","actions":[{"op":"gainKredits","amount":{"stat":"lastDiscardedDefense"},"side":"self"}]}]""").RootElement;
        Assert.False(SourceEffectConverter.TryConvert(dynamic, "order", out _, out var dynamicReason));
        Assert.Contains("literal", dynamicReason);

        var interactive = JsonDocument.Parse("""[{"trigger":"order","targets":[{"id":"t1"}],"actions":[{"op":"draw","count":1}]}]""").RootElement;
        Assert.False(SourceEffectConverter.TryConvert(interactive, "order", out _, out var interactiveReason));
        Assert.Contains("batch 2", interactiveReason);
    }

    /// <summary>合法面全谱：九种 op 在 self/all 目标上的翻译结果（单位／指令分开验——触发面不同）。</summary>
    [Fact]
    public void ConverterTranslatesTheFullNonInteractiveVocabulary()
    {
        var orderBlock = JsonDocument.Parse(
            """[{"trigger":"order","actions":[{"op":"draw","count":2},{"op":"damage","amount":2,"target":{"sel":"all","side":"both"}},{"op":"pin","target":{"sel":"all","side":"both"}}]}]""").RootElement;
        Assert.True(SourceEffectConverter.TryConvert(orderBlock, "order", out var orderDrafts, out var orderError), orderError);
        Assert.Equal(
            [("draw", "self", 2), ("damage", "bothUnits", 2), ("pin", "bothUnits", 1)],
            orderDrafts.Select(d => (d.Op, d.Target, d.Amount)).ToArray());

        var deployBlock = JsonDocument.Parse(
            """[{"trigger":"deploy","actions":[{"op":"buff","attack":1,"defense":2,"target":{"sel":"all","side":"self"},"duration":"turn"},{"op":"opCostMod","amount":-1,"target":{"sel":"all","side":"friendly"}},{"op":"destroy","target":{"sel":"all","side":"enemy"}},{"op":"silence","target":{"sel":"all","side":"enemy"}},{"op":"gainKredits","amount":2,"side":"self"},{"op":"gainKreditSlot","amount":1,"side":"self"}]}]""").RootElement;
        Assert.True(SourceEffectConverter.TryConvert(deployBlock, "unit", out var unitDrafts, out var unitError), unitError);
        Assert.False(SourceEffectConverter.TryConvert(deployBlock, "order", out _, out _)); // 触发面不匹配
        Assert.Equal(
            [("buff", "self", 1), ("costMod", "friendlyUnits", -1), ("destroy", "enemyUnits", 1),
             ("silence", "enemyUnits", 1), ("gainPoint", "self", 2), ("gainSlot", "self", 1)],
            unitDrafts.Select(d => (d.Op, d.Target,
                d.Op is "buff" ? d.Attack : d.Amount)).ToArray());
        Assert.Equal("turnEnd", unitDrafts.Single(d => d.Op == "buff").Until);
        Assert.Equal("", unitDrafts.Single(d => d.Op == "costMod").Until); // 未声明 duration＝永久
    }

    /// <summary>打一张转换单位：部署触发抽牌，单位以源身材落地。</summary>
    [Fact]
    public async Task ConvertedUnitDeployDrawsAndLandsWithSourceStats()
    {
        var original = Catalog().Cards.Single(c => c.CardId == "UN/unit/_1");
        using var session = await OrcMatchSession.CreateFromCatalogAsync(Catalog().Cards, 20261014,
            "converted-unit-deploy", "probe", openingHand: ["UN/unit/_1"], sourceDirectory: Source);
        await session.SettleMulliganAsync(false);
        session.SettleProjection();
        var actor = session.View.SelfHand.Single(c => c.CardId == "UN/unit/_1");
        for (var i = 0; i < 8 && session.View.SelfKredits < original.Cost; i++)
        {
            Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new EndTurn()));
            session.SettleProjection();
        }
        var hand = session.View.SelfHand.Count;
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(actor.Uid, 0)));
        session.SettleProjection();
        var unit = session.View.SelfLine.Single(c => c.Uid == actor.Uid);
        Assert.Equal(original.BaseAttack, unit.EffectiveAttack);
        Assert.Equal(original.BaseDefense, unit.EffectiveDefense);
        // 部署触发抽了一张：出手 −1、抽牌 ＋1，手牌规模不变。
        Assert.Equal(hand, session.View.SelfHand.Count);
    }
}
