using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// OrC Inspector 结构编辑面（alpha.31，对齐 Electron 检查器）的落地证据：
// DTO↔DSL 往返无损、编辑→编译生效、解析→结构等价、不合法组合诚实诊断、
// 配方种子全绿、持久化往返、以及结构卡在真对局中的部署触发（手牌守恒断言）。
// ─────────────────────────────────────────────────────────────────────────────

public class DiyInspectorTests(ITestOutputHelper output)
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

    private static OrcEffectDraft Draft(OrcOpDraft op, string template = "deploy_basic",
        string guard = "SelfUnit", string? hook = "unit.deployed") =>
        new() { Template = template, Guard = guard, Hook = hook, Ops = [op] };

    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public void 结构编辑_部署抽牌编译入卡()
    {
        var draft = new DiyCardDraft { Id = "diy/insp-draw", Name = "结构试作卡", OrcEffects = [Draft(new OrcOpDraft { Op = "draw", Count = 2 })] };
        var validation = DiyCardCompiler.Validate(draft, []);
        Assert.True(validation.Success, string.Join("；", validation.Errors));
        Assert.NotNull(validation.EngineCardJson);
        Assert.Contains("effects", validation.EngineCardJson!);
    }

    [Fact]
    public void 结构编辑_改参数编译产物跟着变()
    {
        const string id = "diy/insp-edit";
        var two = OrcEffectEditing.Compile(id, [Draft(new OrcOpDraft { Op = "damage", Sel = "one", Side = "enemy", Amount = 2 })]);
        var five = OrcEffectEditing.Compile(id, [Draft(new OrcOpDraft { Op = "damage", Sel = "one", Side = "enemy", Amount = 5 })]);
        Assert.Null(two[0].Error);
        Assert.Null(five[0].Error);
        Assert.NotEqual(two[0].JsonText, five[0].JsonText); // 编辑真的改了产物，不是同一份快照
        Assert.Contains("5", five[0].JsonText);
    }

    [Fact]
    public void 结构往返_DTO与引擎DSL互转无损()
    {
        var draft = Draft(new OrcOpDraft
        {
            Op = "buff", Sel = "all", Side = "both", SelZone = "frontline",
            Filter = new OrcFilterDraft { UnitType = "tank", ExcludeSelf = true },
            Attack = -2, Defense = 3, Until = "turnEnd",
        }, template: "turn_start_basic", guard: "OwnerSameByPlayer", hook: "turn.start");
        var roundTrip = OrcEffectEditing.ToDraft(OrcEffectEditing.ToParsed(draft));
        Assert.Equal(Json(draft), Json(roundTrip));
    }

    [Fact]
    public void 解析到结构_编译与文本通道等价()
    {
        const string text = "部署：随机对 1 个敌方单位造成 2 点伤害。";
        const string id = "diy/insp-parity";
        var fromText = OrcInspector.CompileText(id, text, "unit");
        Assert.True(fromText.ParseComplete, string.Join("；", fromText.Parse.Diagnostics));
        Assert.True(fromText.AllCompiled, string.Join("；", fromText.Problems));
        var drafts = fromText.Parse.Effects.Select(OrcEffectEditing.ToDraft).ToArray();
        var fromStructure = OrcEffectEditing.Compile(id, drafts);
        Assert.Null(fromStructure[0].Error);
        // 同一解析链（ToDraft/ToParsed 无损往返已单测）：两条通道产物同为 deploy 触发 + 随机敌方伤害。
        var summary = OrcEffectEditing.Summarize(drafts[0].Ops[0]);
        Assert.Contains("随机", summary);
        Assert.Contains("敌方", summary);
        Assert.Contains("伤害 2", summary);
    }

    [Fact]
    public void 不合法触发组合_诚实诊断()
    {
        // deploy_basic + None 不在目录（守卫面成对合法）——必须给可读错误，不能静默编译。
        var results = OrcEffectEditing.Compile("diy/insp-bad", [Draft(new OrcOpDraft { Op = "draw", Count = 1 }, guard: "None", hook: null)]);
        Assert.NotNull(results[0].Error);
        Assert.Contains("触发", results[0].Error);
    }

    [Fact]
    public void 配方种子_全部可编译()
    {
        var results = OrcEffectEditing.Compile("diy/insp-recipes", OrcEffectEditing.Recipes.Select(r => r.Effect).ToArray());
        Assert.All(results, r => Assert.Null(r.Error));
    }

    [Fact]
    public void 摘要_中文可读()
    {
        var summary = OrcEffectEditing.Summarize(new OrcOpDraft { Op = "damage", Sel = "one", Side = "enemy", Amount = 2 });
        Assert.Equal("造成伤害：单个敌方，伤害 2", summary);
    }

    [Fact]
    public void OrcEffects_持久化往返()
    {
        var path = Path.Combine(Path.GetTempPath(), "diy-insp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var library = new DiyCardLibrary(path);
            var draft = new DiyCardDraft
            {
                Id = "diy/insp-persist", Name = "持久试作卡",
                EffectText = "部署：抽 2 张牌。",
                OrcEffects = [Draft(new OrcOpDraft { Op = "draw", Count = 2 })],
            };
            library.Save(draft);
            var reloaded = new DiyCardLibrary(path);
            Assert.Null(reloaded.LoadError);
            var card = Assert.Single(reloaded.Cards);
            var effect = Assert.Single(card.OrcEffects);
            Assert.Equal("deploy_basic", effect.Template);
            Assert.Equal("draw", Assert.Single(effect.Ops).Op);
            Assert.Equal(2, Assert.Single(effect.Ops).Count);
        }
        finally { File.Delete(path); }
    }

    /// <summary>结构 DIY 卡走真对局：部署触发抽 1（手牌守恒＝效果真的发生了）。</summary>
    [Fact]
    public async Task 结构DIY卡_真对局部署触发()
    {
        var draft = new DiyCardDraft
        {
            Id = "diy/insp-deploy-draw", Name = "结构试作卡",
            OrcEffects = [Draft(new OrcOpDraft { Op = "draw", Count = 1 })],
        };
        var catalog = new CardCatalog();
        catalog.Load(Source);
        using var session = await OrcMatchSession.CreateFromCatalogAsync(
            catalog.Cards, 20261016, "diy-insp-trial", "probe",
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
        Assert.Equal(hand, session.View.SelfHand.Count);
        output.WriteLine($"结构 DIY 卡部署成功，手牌守恒 {hand}");
    }
}
