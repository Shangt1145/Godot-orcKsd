using Kards.Ui.OrcBridge.HandlerDsl;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 代码视图的文本契约：正常代码的语法，游戏内的词。
///
/// <para>
/// 这套文本不是装饰——它是作者"从头读一遍"的唯一入口，后面还要变成可编辑输入。
/// 所以措辞和缩进都冻在这里：谁改了打印方式，这里先红。
/// </para>
/// </summary>
public sealed class HandlerTextPrinterTests
{
    private static readonly HandlerCatalog Catalog = HandlerCatalog.Snapshot;

    [Fact]
    public void 样例文档打印成带触发与嵌套分支的代码()
    {
        var text = HandlerTextPrinter.Print(HandlerDocument.Sample(), Catalog, "打出时（本单位）");

        Assert.Equal(
            "当「打出时（本单位）」时 {\n" +
            "  foes = 找出单位(哪一方: 敌方)\n" +
            "  遍历 foes 记作 f {\n" +
            "    如果 f 的 防御力 <= 2 {\n" +
            "      造成伤害(对谁: f, 打多少: 3)\n" +
            "    } 否则 {\n" +
            "      造成伤害(对谁: f, 打多少: 1)\n" +
            "    }\n" +
            "  }\n" +
            "}\n",
            text);
    }

    [Fact]
    public void 没有触发时机就不打印触发行()
    {
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("draw").Set("amount", HandlerValue.Num(1)));

        var text = HandlerTextPrinter.Print(doc, Catalog);

        Assert.DoesNotContain("当「", text, StringComparison.Ordinal);
        Assert.False(text.StartsWith("  ", StringComparison.Ordinal), "没有触发行时不该整体缩进：" + text);
        Assert.Contains("抽", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 空文档给出占位注释而不是空白()
    {
        var text = HandlerTextPrinter.Print(new HandlerDocument(), Catalog, "部署时");
        Assert.Contains("// 还没有任何动作", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 空的否则段整段省略()
    {
        var doc = new HandlerDocument();
        var branch = new HandlerNode("branch").Set("test", HandlerValue.Call("eq", HandlerValue.Num(1), HandlerValue.Num(2)));
        branch.Body("then").Add(new HandlerNode("draw"));
        doc.Actions.Add(branch);

        var text = HandlerTextPrinter.Print(doc, Catalog);

        Assert.Contains("如果 1 == 2 {", text, StringComparison.Ordinal);
        Assert.DoesNotContain("否则", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 抉择的每个分支打印成独立的标签段()
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

        var text = HandlerTextPrinter.Print(doc, Catalog, "部署时");

        Assert.Contains("选择 {", text, StringComparison.Ordinal);
        Assert.Contains("「拿一张牌」 {", text, StringComparison.Ordinal);
        Assert.Contains("「打一点伤害」 {", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 目录里没有的积木照实打印并标注()
    {
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("futureOp"));

        var text = HandlerTextPrinter.Print(doc, Catalog);

        Assert.Contains("futureOp()", text, StringComparison.Ordinal);
        Assert.Contains("目录里没有这个积木", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("是", true)]
    [InlineData("\"文字\"", false)]
    public void 字面量按类型打印(string expected, bool asBool)
    {
        var value = asBool ? HandlerValue.Bool(true) : HandlerValue.Str("文字");
        Assert.Equal(expected, HandlerTextPrinter.Value(value, Catalog));
    }

    [Fact]
    public void 运算符写成中缀而不是函数调用()
    {
        var value = HandlerValue.Call("and",
            HandlerValue.Call("ge", HandlerValue.Num(3), HandlerValue.Num(2)),
            HandlerValue.Call("not", HandlerValue.Bool(false)));

        Assert.Equal("3 >= 2 且 非 否", HandlerTextPrinter.Value(value, Catalog));
    }

    [Fact]
    public void 读取自己的属性打印成中文()
    {
        Assert.Equal("自己的攻击力", HandlerTextPrinter.Value(HandlerValue.Read("self", "attack"), Catalog));
        Assert.Equal("我方的防御力", HandlerTextPrinter.Value(HandlerValue.Read("viewer", "defense"), Catalog));
    }

    [Fact]
    public void 打印不会改动文档()
    {
        var doc = HandlerDocument.Sample();
        var before = doc.ToJsonText(Catalog);

        HandlerTextPrinter.Print(doc, Catalog, "打出时");

        Assert.Equal(before, doc.ToJsonText(Catalog));
    }
}
