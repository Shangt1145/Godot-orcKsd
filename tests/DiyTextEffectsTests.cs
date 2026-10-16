using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// DIY 文本模式（自然文本→DSL）与 Inspector 的落地证据：
// 新建卡牌时规则文本真源编译、诚实诊断、inspector 中文标签、持久化往返、
// 以及文本卡在真对局中的部署与效果触发（手牌守恒断言）。
// ─────────────────────────────────────────────────────────────────────────────

public class DiyTextEffectsTests(ITestOutputHelper output)
{
    private static string Source
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
            {
                var candidate = Path.Combine(d, "proto", "data", "nations");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("找不到卡源目录");
        }
    }

    private static DiyCardDraft TextCard(string text, string? id = null) => new()
    {
        Id = id ?? "diy/text-" + Guid.NewGuid().ToString("N")[..8],
        Name = "文本试作卡", EffectText = text,
    };

    [Fact]
    public void 文本模式_部署抽牌编译入卡()
    {
        var draft = TextCard("部署：抽 1 张牌。");
        var validation = DiyCardCompiler.Validate(draft, []);
        Assert.True(validation.Success, string.Join("；", validation.Errors));
        Assert.NotNull(validation.EngineCardJson);
        Assert.Contains("effects", validation.EngineCardJson!);
        // 卡面文本＝真源原文（不是行编辑器生成句式）。
        Assert.Equal("部署：抽 1 张牌。", validation.Card!.Text);
    }

    [Fact]
    public void 文本模式_解析不了的句子诚实拒绝()
    {
        // 引擎真面外（返回手牌）：拒绝并给诊断，不静默降级空效果。
        var draft = TextCard("使 1 个友方单位返回手牌");
        var validation = DiyCardCompiler.Validate(draft, []);
        Assert.False(validation.Success);
        Assert.NotEmpty(validation.Errors);
    }

    [Fact]
    public void 文本模式_效果行不参与编译()
    {
        // 行编辑器里的非法行在文本模式下被忽略（真源＝文本），不应拖垮校验。
        var draft = TextCard("部署：抽 1 张牌。") with
        {
            Effects = [new DiyEffectDraft { Op = "unknown-op", Amount = 999 }],
        };
        var validation = DiyCardCompiler.Validate(draft, []);
        Assert.True(validation.Success, string.Join("；", validation.Errors));
    }

    [Fact]
    public void 文本模式_文本词条并入声明()
    {
        var draft = TextCard("部署：抽 1 张牌。") with { Keywords = ["blitz"] };
        var validation = DiyCardCompiler.Validate(draft, []);
        Assert.True(validation.Success, string.Join("；", validation.Errors));
        Assert.Contains("blitz", validation.Card!.Keywords);
    }

    [Fact]
    public void Inspector_中文标签树()
    {
        var compile = OrcInspector.CompileText("diy/inspect", "部署：抽 1 张牌。", "unit");
        Assert.True(compile.ParseComplete, string.Join("；", compile.Parse.Diagnostics));
        Assert.True(compile.AllCompiled, string.Join("；", compile.Problems));
        var nodes = OrcInspector.Describe(compile.Parse);
        var trigger = Assert.Single(nodes);
        Assert.Equal("部署时", trigger.Label);
        Assert.Contains(trigger.Children, c => c.Label == "抽牌" && c.Detail!.Contains("1 张"));
    }

    [Fact]
    public void Inspector_多效果与诊断面()
    {
        var compile = OrcInspector.CompileText("diy/inspect-multi",
            "部署：抽 1 张牌；获得 2 个指挥点。亡计：对 1 个敌方单位造成 2 点伤害。", "unit");
        Assert.True(compile.AllCompiled, string.Join("；", compile.Problems));
        var labels = OrcInspector.Describe(compile.Parse).Select(n => n.Label).ToArray();
        Assert.Contains("部署时", labels);
        Assert.Contains("亡计（被消灭时）", labels);
    }

    [Fact]
    public void EffectText_持久化往返()
    {
        var path = Path.Combine(Path.GetTempPath(), "diy-text-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var library = new DiyCardLibrary(path);
            var draft = TextCard("部署：抽 2 张牌。") with { Flavor = "测试风味" };
            library.Save(draft);
            var reloaded = new DiyCardLibrary(path);
            Assert.Null(reloaded.LoadError);
            var card = Assert.Single(reloaded.Cards);
            Assert.Equal("部署：抽 2 张牌。", card.EffectText);
        }
        finally { File.Delete(path); }
    }

    /// <summary>文本 DIY 卡走真对局：opening hand 直取，部署触发抽 1（手牌数守恒＝效果真的发生了）。</summary>
    [Fact]
    public async Task 文本DIY卡_真对局部署触发()
    {
        var draft = TextCard("部署：抽 1 张牌。", "diy/text-deploy-draw");
        var catalog = new CardCatalog();
        catalog.Load(Source);
        using var session = await OrcMatchSession.CreateFromCatalogAsync(
            catalog.Cards, 20261016, "diy-text-trial", "probe",
            openingHand: [draft.Id], sourceDirectory: Source, customCards: [draft]);
        await session.SettleMulliganAsync(false);
        session.SettleProjection();
        var actor = session.View.SelfHand.Single(c => c.CardId == draft.Id);
        for (var i = 0; i < 8 && session.View.SelfKredits < 1; i++)
        {
            Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new EndTurn()));
            session.SettleProjection();
        }
        var hand = session.View.SelfHand.Count;
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(actor.Uid)));
        session.SettleProjection();
        Assert.DoesNotContain(session.View.SelfHand, c => c.Uid == actor.Uid);
        // 部署触发抽了一张：出手 −1、抽牌 ＋1，手牌规模不变——效果没发生就会少一张。
        Assert.Equal(hand, session.View.SelfHand.Count);
        output.WriteLine($"文本 DIY 卡部署成功，手牌守恒 {hand}");
    }
}
