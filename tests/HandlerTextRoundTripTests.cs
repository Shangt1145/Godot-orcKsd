using Kards.Ui.OrcBridge.HandlerDsl;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 打印 → 解析 的往返契约。
///
/// <para>
/// 判据只有一条：<c>ToJsonText</c> 逐字相同。文本一旦要变成可编辑输入，
/// "读回来差不多"就等于静默改结构——所以这里比的是 JSON，不是"看着像"。
/// </para>
/// </summary>
public sealed class HandlerTextRoundTripTests
{
    private static readonly HandlerCatalog Catalog = HandlerCatalog.Snapshot;

    private static void AssertRoundTrips(HandlerDocument document, string? trigger = "打出时")
    {
        var printed = HandlerTextPrinter.Print(document, Catalog, trigger);
        var parsed = HandlerTextParser.Parse(printed, Catalog);
        Assert.Equal(document.ToJsonText(Catalog), parsed.ToJsonText(Catalog));

        // 再打印一次也必须一模一样（否则保存一次就会产生 diff 噪音）
        Assert.Equal(printed, HandlerTextPrinter.Print(parsed, Catalog, trigger));
    }

    [Fact]
    public void 样例文档往返一致()
    {
        AssertRoundTrips(HandlerDocument.Sample());
    }

    [Fact]
    public void 分支加否则往返一致()
    {
        var doc = new HandlerDocument();
        var branch = new HandlerNode("branch")
            .Set("test", HandlerValue.Call("gt", HandlerValue.Read("self", "attack"), HandlerValue.Num(2)));
        branch.Body("then").Add(new HandlerNode("damage").Set("amount", HandlerValue.Num(3)));
        branch.Body("else").Add(new HandlerNode("damage").Set("amount", HandlerValue.Num(1)));
        doc.Actions.Add(branch);

        AssertRoundTrips(doc);
    }

    [Fact]
    public void 抉择的多个分支往返一致()
    {
        var doc = new HandlerDocument();
        var choice = new HandlerNode("choice");
        var first = new HandlerOption("拿一张牌");
        first.Actions.Add(new HandlerNode("draw"));
        var second = new HandlerOption("打一点伤害");
        second.Actions.Add(new HandlerNode("damage").Set("amount", HandlerValue.Num(1)));
        choice.Options.Add(first);
        choice.Options.Add(second);
        doc.Actions.Add(choice);

        AssertRoundTrips(doc, "部署时");
    }

    [Fact]
    public void 遍历与嵌套动作往返一致()
    {
        var doc = new HandlerDocument();
        var iterate = new HandlerNode("iterate")
            .Set("over", HandlerValue.Call("unitsOf", HandlerValue.Str("enemy")))
            .SetText("as", "f");
        iterate.Body("actions").Add(new HandlerNode("damage")
            .Set("target", HandlerValue.Var("f"))
            .Set("amount", HandlerValue.Num(1)));
        doc.Actions.Add(iterate);

        AssertRoundTrips(doc);
    }

    [Fact]
    public void 目录里没有的动作也能读回来()
    {
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("futureOp"));

        AssertRoundTrips(doc);
    }

    [Fact]
    public void 运算优先级靠括号保住()
    {
        var doc = new HandlerDocument();
        var branch = new HandlerNode("branch").Set("test", HandlerValue.Call("eq",
            HandlerValue.Call("mul",
                HandlerValue.Call("add", HandlerValue.Num(2), HandlerValue.Num(3)),
                HandlerValue.Num(4)),
            HandlerValue.Num(20)));
        branch.Body("then").Add(new HandlerNode("draw"));
        doc.Actions.Add(branch);

        var printed = HandlerTextPrinter.Print(doc, Catalog, null);
        Assert.Contains("(2 + 3) * 4", printed, StringComparison.Ordinal);

        AssertRoundTrips(doc, null);
    }

    [Fact]
    public void 减法右侧的同级表达式必须加括号()
    {
        var doc = new HandlerDocument();
        var branch = new HandlerNode("branch").Set("test", HandlerValue.Call("eq",
            HandlerValue.Call("sub", HandlerValue.Num(10), HandlerValue.Call("sub", HandlerValue.Num(3), HandlerValue.Num(1))),
            HandlerValue.Num(1)));
        branch.Body("then").Add(new HandlerNode("draw"));
        doc.Actions.Add(branch);

        var printed = HandlerTextPrinter.Print(doc, Catalog, null);
        Assert.Contains("10 - (3 - 1)", printed, StringComparison.Ordinal);

        AssertRoundTrips(doc, null);
    }

    [Fact]
    public void 负数字面量不会被读成减法()
    {
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("damage").Set("amount", HandlerValue.Num(-2)));

        AssertRoundTrips(doc);
    }

    [Fact]
    public void 字符串里的引号与换行往返一致()
    {
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("draw").Set("note", HandlerValue.Str("他说\"抽一张\"\n然后结束")));

        AssertRoundTrips(doc);
    }

    [Fact]
    public void 读取自己与读取实体的属性往返一致()
    {
        var doc = new HandlerDocument();
        var branch = new HandlerNode("branch").Set("test", HandlerValue.Call("lt",
            HandlerValue.ReadFrom(HandlerValue.Var("t"), "defense"),
            HandlerValue.Read("viewer", "attack")));
        branch.Body("then").Add(new HandlerNode("draw"));
        doc.Actions.Add(branch);

        AssertRoundTrips(doc);
    }

    [Fact]
    public void 高级脚本的引脚声明也能往返()
    {
        // 不打印它们的话，纯文本往返会把作者写的 inputs/outputs 吃掉。
        var doc = new HandlerDocument();
        var script = HandlerNode.Script("return 1;");
        script.SetScriptPins("inputs", [new System.Text.Json.Nodes.JsonObject { ["name"] = "a", ["type"] = "number" }]);
        doc.Actions.Add(script);

        AssertRoundTrips(doc);
    }

    [Fact]
    public void 括号缺失时报错并指出行号()
    {
        const string broken = "当「部署时」时 {\n  造成伤害(打多少: 1)\n";
        var failure = Assert.Throws<HandlerTextParseException>(() => HandlerTextParser.Parse(broken, Catalog));
        Assert.Contains("行", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 不认识的属性直接报错而不是猜()
    {
        const string typo = "当「部署时」时 {\n  如果 t 的 不存在的属性 > 1 {\n    抽牌()\n  }\n}";
        var failure = Assert.Throws<HandlerTextParseException>(() => HandlerTextParser.Parse(typo, Catalog));
        Assert.Contains("不认识的属性", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 取值不能单独成句()
    {
        const string broken = "当「部署时」时 {\n  有几个(x)\n}";
        var failure = Assert.Throws<HandlerTextParseException>(() => HandlerTextParser.Parse(broken, Catalog));
        Assert.Contains("不是动作", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 触发时机不进入文档结构()
    {
        var printed = HandlerTextPrinter.Print(new HandlerDocument(), Catalog, "部署时");
        var parsed = HandlerTextParser.Parse(printed, Catalog);
        Assert.Empty(parsed.Actions);
    }
}
