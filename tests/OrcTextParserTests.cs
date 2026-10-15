using Kards.Ui.OrcBridge.OrcText;
using Xunit;

namespace Kards.Ui.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// OrC 文本解析器（orc.js → C# 移植）对拍测试：每个引擎可映射形态一条断言，
// 每类面外语义一条拒绝断言。移植语义锚点＝js 版 orc-4；映射靶＝引擎 DSL 面
// （DslOpRegistry 21 op ＋ 触发模板 ＋ DslSelector）。
// ─────────────────────────────────────────────────────────────────────────────

public class OrcTextParserTests
{
    private static readonly Wordbook Book = Wordbook.Create(null);

    private static OrcCompileResult Compile(string text, string type = "unit") =>
        OrcParser.Compile(text, type, Book);

    private static Orc.Game.EffectParsing.Dsl.DslOp Single(string text, string type = "unit")
    {
        var r = Compile(text, type);
        Assert.Empty(r.Diagnostics);
        var e = Assert.Single(r.Effects);
        return Assert.Single(e.Ops);
    }

    // ── 触发面 ──

    [Theory]
    [InlineData("部署：抽 2 张牌", "deploy_basic", "on_deploy")]
    [InlineData("亡计：抽 1 张牌", "death_basic", "on_event")]
    [InlineData("遗言：抽 1 张牌", "death_basic", "on_event")]
    [InlineData("使用时：抽 1 张牌", "played_basic", "on_event")]
    [InlineData("己方回合开始时：抽 1 张牌", "turn_start_basic", "on_event")]
    [InlineData("己方回合结束时：抽 1 张牌", "turn_end_basic", "on_event")]
    public void 触发头映射到引擎模板(string text, string tpl, string slot)
    {
        var r = Compile(text);
        Assert.Empty(r.Diagnostics);
        var e = Assert.Single(r.Effects);
        Assert.Equal(tpl, e.TemplateId);
        Assert.Equal(slot, e.Slot);
    }

    [Fact]
    public void unit_无头行落部署触发() =>
        Assert.Equal("deploy_basic", Compile("抽 1 张牌").Effects.Single().TemplateId);

    [Fact]
    public void order_整行落使用触发() =>
        Assert.Equal("played_basic", Compile("抽 1 张牌", "order").Effects.Single().TemplateId);

    [Theory]
    [InlineData("攻击时，抽 1 张牌", "attack_basic")]
    [InlineData("受到伤害时，抽 1 张牌", "damaged_basic")]
    [InlineData("造成伤害时，抽 1 张牌", "damage_dealt_basic")]
    public void unit_自事件映射(string text, string tpl) =>
        Assert.Equal(tpl, Compile(text).Effects.Single().TemplateId);

    [Fact]
    public void 监听句_泛指单位映射_限定词拒绝()
    {
        Assert.Equal("deploy_listener", Compile("其他友方单位部署时，抽 1 张牌").Effects.Single().TemplateId);
        Assert.Contains(Compile("其他友方坦克部署时，抽 1 张牌").Diagnostics, d => d.Code == "unparsed-text");
        Assert.Contains(Compile("其他敌方单位部署时，抽 1 张牌").Diagnostics, d => d.Code == "unparsed-text");
    }

    // ── 词条行 ──

    [Fact]
    public void 词条行解析_含参值()
    {
        var r = Compile("闪击、重甲3");
        Assert.Empty(r.Diagnostics);
        Assert.Empty(r.Effects);
        Assert.Equal(2, r.Keywords.Count);
        Assert.Contains(r.Keywords, k => k.Id == "闪击" && !k.Quantified);
        Assert.Contains(r.Keywords, k => k.Id == "重甲" && k.Value == 3 && k.Quantified);
    }

    // ── 动作面：每种 op 一条 ──

    [Fact]
    public void 抽牌()
    {
        var op = Single("抽 2 张牌");
        Assert.Equal("draw", op.Op);
        Assert.Equal(2, op.Count);
    }

    [Fact]
    public void 中文数字抽牌_两归二()
    {
        var op = Single("抽两张牌");
        Assert.Equal(2, op.Count);
    }

    [Fact]
    public void 资源四态()
    {
        Assert.Equal("gainPoint", Single("获得 2 个指挥点").Op);
        Assert.Equal("losePoint", Single("失去 1 个指挥点").Op);
        Assert.Equal("gainSlot", Single("获得 1 个指挥点槽").Op);
        Assert.Equal("loseSlot", Single("失去 2 个指挥点槽").Op);
    }

    [Fact]
    public void 伤害_选靶与数值()
    {
        var op = Single("对 1 个敌方单位造成 3 点伤害");
        Assert.Equal("damage", op.Op);
        Assert.Equal(3, op.Amount);
        Assert.Equal("one", op.Target!.Sel);
        Assert.Equal("enemy", op.Target.Side);
    }

    [Fact]
    public void 伤害_全体友方带兵种过滤()
    {
        var op = Single("对所有友方坦克造成 1 点伤害");
        Assert.Equal("all", op.Target!.Sel);
        Assert.Equal("friendly", op.Target.Side);
        Assert.Equal("tank", op.Target.Filter!.UnitType);
    }

    [Fact]
    public void 动词四态()
    {
        var destroy = Single("消灭 1 个敌方单位");
        Assert.Equal("destroy", destroy.Op);
        Assert.Equal("enemy", destroy.Target!.Side);

        Assert.Equal("pin", Single("压制 1 个敌方单位").Op);
        Assert.Equal("silence", Single("抑制 1 个友方单位").Op);
    }

    [Fact]
    public void 增益复合与期限()
    {
        var op = Single("使 1 个友方单位获得 +2/+1，直到回合结束");
        Assert.Equal("buff", op.Op);
        Assert.Equal(2, op.Attack);
        Assert.Equal(1, op.Defense);
        Assert.Equal(Orc.Game.EffectParsing.Dsl.Untils.TurnEnd, op.Until);
    }

    [Fact]
    public void 增益单维_数字在属性词前()
    {
        var op = Single("使 1 个友方单位获得 +2 攻击力");
        Assert.Equal("buff", op.Op);
        Assert.Equal(2, op.Attack);
        Assert.Null(op.Defense);
    }

    [Fact]
    public void 行动费两种句式()
    {
        var a = Single("使 1 个敌方单位行动费 +1");
        Assert.Equal("costMod", a.Op);
        Assert.Equal(1, a.Amount);
        var b = Single("使 1 个敌方单位获得 2 行动花费");
        Assert.Equal("costMod", b.Op);
        Assert.Equal(2, b.Amount);
    }

    [Fact]
    public void 授予词条含参值()
    {
        var op = Single("使 1 个友方单位获得 重甲2");
        Assert.Equal("grant", op.Op);
        Assert.Equal("重甲", op.Keyword);
        Assert.Equal(2, op.Value);
    }

    [Fact]
    public void 移动与升格()
    {
        Assert.Equal("move", Single("将本单位移至前线").Op);
        Assert.Equal("upgrade", Single("本单位升为老兵").Op);
    }

    [Fact]
    public void 卡牌生成_引号名()
    {
        Assert.Equal("addToHand", Single("将“侦察连”加入手牌").Op);
        Assert.Equal("shuffleIn", Single("将“侦察连”洗入卡组").Op);
    }

    [Fact]
    public void 嵌套引号内词条_按js口径拒绝()
    {
        // js 口径：引号内若解析出词条/卡面字段而非纯效果 → return null（拒绝）。
        // "闪击"是词条不是效果 → 内层无效果 → 整句待核对。
        var r = Compile("使 1 个友方单位获得：“闪击”");
        Assert.NotEmpty(r.Diagnostics);
    }

    [Fact]
    public void 嵌套引号内动作效果()
    {
        var op = Single("使 1 个友方单位获得：“亡计：抽 1 张牌”");
        Assert.Equal("nested", op.Op);
        var inner = Assert.Single(op.Nested!);
        Assert.Equal("death_basic", inner.Template);
    }

    // ── 面外拒绝（宁可待核对，不静默裁剪）──

    [Theory]
    [InlineData("随机消灭 1 个敌方单位")] // 随机可映射，但整句"随机+动词"词序未支持 → 待核对
    [InlineData("使敌方玩家抽 1 张牌")]   // draw 无 side
    [InlineData("使对手获得 2 个指挥点")] // 资源无 side
    [InlineData("将 1 个友方单位返回手牌")] // returnUnit 面外
    [InlineData("本单位无法被压制")]       // 被动句面外
    [InlineData("对敌方总部造成 2 点伤害")] // HQ 目标面外
    [InlineData("对所有其他友方单位造成 1 点伤害")] // excludeSelf 不在 selector 面
    [InlineData("如果其已被压制，将其消灭")] // 控制流面外
    [InlineData("对 1 个敌方单位造成等同于其攻击力的伤害")] // 动态数值
    public void 面外语义产生待核对诊断(string text)
    {
        var r = Compile(text);
        Assert.NotEmpty(r.Diagnostics);
        Assert.All(r.Effects, e => Assert.Contains(e.Ops, op => op.Op == "needsCsx"));
    }

    [Fact]
    public void 多子句拆分_逗号连接()
    {
        var r = Compile("抽 1 张牌，获得 1 个指挥点");
        Assert.Empty(r.Diagnostics);
        var e = Assert.Single(r.Effects);
        Assert.Equal(2, e.Ops.Count);
        Assert.Equal("draw", e.Ops[0].Op);
        Assert.Equal("gainPoint", e.Ops[1].Op);
    }

    [Fact]
    public void 全角归一_引号保护()
    {
        // 全角数字/加号归一；引号内卡名保持原样
        var op = Single("对 １ 个敌方单位造成 ２ 点伤害");
        Assert.Equal(2, op.Amount);
    }

    [Fact]
    public void 空文本零效果零诊断()
    {
        var r = Compile("");
        Assert.Empty(r.Diagnostics);
        Assert.Empty(r.Effects);
    }

    [Fact]
    public void 非空零效果报待核对()
    {
        var r = Compile("？？？？");
        Assert.Contains(r.Diagnostics, d => d.Code is "unparsed-text" or "empty-effect");
    }
}
