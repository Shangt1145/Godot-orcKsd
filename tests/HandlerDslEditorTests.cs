using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge.HandlerDsl;
using Xunit;

namespace Kards.Ui.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 积木式 Inspector 的桥接层证据：目录快照、文档导出（含 else 省略与引脚顺序）、
// 13 码校验的判定与节点路径。界面不生成 csx（契约硬禁令），这里也不碰生成器。
// ─────────────────────────────────────────────────────────────────────────────

public class HandlerDslEditorTests
{
    private static readonly HandlerCatalog Catalog = HandlerCatalog.Snapshot;

    [Fact]
    public void 目录快照与契约一致()
    {
        Assert.Equal(37, Catalog.Nodes.Count);                       // 10 内建 op + 27 目录节点
        Assert.Equal(27, Catalog.Nodes.Count(n => !n.BuiltIn));      // 25 动作 + stop/interrupt
        Assert.Contains("stop", Catalog.Nodes.Select(n => n.Name));
        Assert.Contains("interrupt", Catalog.Nodes.Select(n => n.Name));

        var damage = Catalog.Find("damage");
        Assert.NotNull(damage);
        Assert.True(damage!.HasAwait);
        Assert.True(damage.HasSideEffect);
        // bind 是**节点级字段**，不是引脚（SPEC §3）——目录里不该有它
        Assert.Equal(["target", "amount", "source"], damage.Inputs.Select(p => p.Name));
        Assert.Null(damage.Pin("bind"));
        Assert.True(damage.Pin("target")!.Required);
        Assert.False(damage.Pin("source")!.Required);

        // 内建 op 也要在目录里，否则界面上加不出控制流
        Assert.True(Catalog.Find("branch")!.IsContainer);
        Assert.True(Catalog.Find("iterate")!.IsContainer);
        Assert.Equal(["then", "else"], Catalog.Find("branch")!.Bodies);
    }

    [Fact]
    public void 样例导出为契约形状()
    {
        var doc = HandlerDocument.Sample();
        var json = doc.ToJson(Catalog);
        var expected = JsonNode.Parse("""
        {
          "schemaVersion": 1,
          "actions": [
            { "op": "unitsOf", "side": { "lit": "enemy" }, "bind": "foes" },
            { "op": "iterate", "over": { "var": "foes" }, "as": "f", "actions": [
              { "op": "branch",
                "test": { "op": "le", "args": [
                  { "read": { "of": { "var": "f" }, "path": "defense" } }, { "lit": 2 } ] },
                "then": [ { "op": "damage", "target": { "var": "f" }, "amount": { "lit": 3 } } ],
                "else": [ { "op": "damage", "target": { "var": "f" }, "amount": { "lit": 1 } } ] }
            ] }
          ]
        }
        """);
        Assert.True(JsonNode.DeepEquals(expected, json), json.ToJsonString());
    }

    [Fact]
    public void 空的否则分支整段省略()
    {
        var branch = new HandlerNode("branch")
            .Set("test", HandlerValue.Bool(true));
        branch.Body("then").Add(new HandlerNode("draw").Set("count", HandlerValue.Num(1)));
        branch.Body("else");   // 界面上点了「添加否则」但还没填

        var doc = new HandlerDocument();
        doc.Actions.Add(branch);
        var json = doc.ToJson(Catalog);

        var branchJson = json["actions"]!.AsArray()[0]!.AsObject();
        Assert.False(branchJson.ContainsKey("else"));
        Assert.True(branchJson.ContainsKey("then"));
    }

    [Fact]
    public void 样例本身没有校验问题()
    {
        var issues = HandlerDslValidator.Validate(HandlerDocument.Sample(), Catalog);
        Assert.Empty(issues);
    }

    [Fact]
    public void 循环里的选靶被拒绝并给出节点路径()
    {
        var doc = new HandlerDocument();
        var loop = new HandlerNode("loop").Set("times", HandlerValue.Num(2));
        loop.Body("actions").Add(new HandlerNode("ask").SetText("kind", "unit").SetText("prompt", "选一个"));
        doc.Actions.Add(loop);

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog), i => i.Code == "ask-in-loop");
        Assert.Equal("actions[0].actions[0]", issue.NodePath);
    }

    [Fact]
    public void 引用不可见的名字是绑定作用域错误()
    {
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("damage")
            .Set("target", HandlerValue.Var("nobody"))
            .Set("amount", HandlerValue.Num(1)));

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog), i => i.Code == "bind-scope");
        Assert.Equal("actions[0].target", issue.NodePath);
    }

    [Fact]
    public void 必填引脚缺失按引脚路径报告()
    {
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("damage").Set("amount", HandlerValue.Num(2)));

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog), i => i.Code == "pin-missing");
        Assert.Equal("actions[0].target", issue.NodePath);
    }

    [Fact]
    public void 写只读视图字段被拒绝()
    {
        var view = new Dictionary<string, HandlerViewField>(StringComparer.Ordinal)
        {
            ["Card"] = new("Card", "entity", false),
            ["Defense"] = new("Defense", "number", true),
        };

        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("write").SetText("field", "Card").Set("value", HandlerValue.Num(1)));

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog, view), i => i.Code == "write-forbidden");
        Assert.Equal("actions[0].field", issue.NodePath);

        // 可写字段则通过
        doc.Actions[0].SetText("field", "Defense");
        Assert.DoesNotContain(HandlerDslValidator.Validate(doc, Catalog, view), i => i.Code == "write-forbidden");
    }

    [Fact]
    public void 算子表封闭且元数与类型都查()
    {
        HandlerDocument Doc(HandlerValue v)
        {
            var d = new HandlerDocument();
            d.Actions.Add(new HandlerNode("damage").Set("target", HandlerValue.Var("t")).Set("amount", v));
            return d;
        }

        Assert.Contains(HandlerDslValidator.Validate(Doc(HandlerValue.Call("div", HandlerValue.Num(4), HandlerValue.Num(2))), Catalog),
            i => i.Code == "expr-op-unknown");

        Assert.Contains(HandlerDslValidator.Validate(Doc(HandlerValue.Call("add", HandlerValue.Num(4))), Catalog),
            i => i.Code == "expr-arity");

        // 加法吃字符串＝expr-type
        Assert.Contains(HandlerDslValidator.Validate(Doc(HandlerValue.Call("add", HandlerValue.Str("x"), HandlerValue.Num(1))), Catalog),
            i => i.Code == "expr-type");
    }

    [Fact]
    public void 版本不匹配在根路径报告()
    {
        var doc = HandlerDocument.Sample();
        doc.SchemaVersion = 2;
        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog), i => i.Code == "schema-version");
        Assert.Equal("$", issue.NodePath);
    }

    [Fact]
    public void 目录里没有的节点是未知节点()
    {
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("explode"));
        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog));
        Assert.Equal("node-unknown", issue.Code);
        Assert.Equal("actions[0]", issue.NodePath);
    }

    // ── 静态类型：ask/iterate 把子类型带下来，列表与单实体不能混用（SPEC §7.5、§8.2）──

    [Fact]
    public void 列表接到单实体引脚时提示改用逐个处理()
    {
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("ask")
            .SetText("kind", "unit").SetText("prompt", "选单位").SetText("bind", "units"));
        // units 是 list<entity>，damage.target 只收单个 entity
        doc.Actions.Add(new HandlerNode("damage")
            .Set("target", HandlerValue.Var("units")).Set("amount", HandlerValue.Num(1)));

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog), i => i.Code == "pin-type");
        Assert.Equal("actions[1].target", issue.NodePath);
        Assert.Contains("逐个处理", issue.Message);
    }

    [Fact]
    public void 逐个处理只接受列表()
    {
        var doc = new HandlerDocument();
        var iterate = new HandlerNode("iterate").Set("over", HandlerValue.Num(3)).SetText("as", "x");
        iterate.Body("actions").Add(new HandlerNode("draw").Set("count", HandlerValue.Num(1)));
        doc.Actions.Add(iterate);

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog), i => i.Code == "pin-type");
        Assert.Equal("actions[0].over", issue.NodePath);
    }

    [Fact]
    public void 实体子类型交集为空是引脚类型错误()
    {
        // 目录当前没有声明 Subtypes 的引脚；这里造一个只收 hq 的，验证交集判定真的在跑。
        var hqOnly = new HandlerNodeSpec("killHq", true, true,
            [new HandlerPinSpec("target", "entity", true, Subtypes: ["hq"])], []);
        var catalog = new HandlerCatalog(Catalog.Revision, [.. Catalog.Nodes, hqOnly], Catalog.Readables);

        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("ask")
            .SetText("kind", "unit").SetText("prompt", "选单位").SetText("bind", "units"));
        var iterate = new HandlerNode("iterate").Set("over", HandlerValue.Var("units")).SetText("as", "u");
        iterate.Body("actions").Add(new HandlerNode("killHq").Set("target", HandlerValue.Var("u")));
        doc.Actions.Add(iterate);

        // ask.kind=unit → u 是 entity(unit)，与 {hq} 交集为空
        var issue = Assert.Single(HandlerDslValidator.Validate(doc, catalog), i => i.Code == "pin-type");
        Assert.Equal("actions[1].actions[0].target", issue.NodePath);

        // 同一份文档，把 kind 换成 hq 就不再冲突
        doc.Actions[0].SetText("kind", "hq");
        Assert.DoesNotContain(HandlerDslValidator.Validate(doc, catalog), i => i.Code == "pin-type");
    }

    [Fact]
    public void 绑定类型沿声明点传下去()
    {
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("let").SetText("name", "n").Set("value", HandlerValue.Num(1)));
        // n 是 number，cardName 要 string
        doc.Actions.Add(new HandlerNode("addToHand")
            .Set("cardName", HandlerValue.Var("n")).Set("count", HandlerValue.Num(1)));

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog), i => i.Code == "pin-type");
        Assert.Equal("actions[1].cardName", issue.NodePath);
    }

    // ── 对齐契约自带的 check 向量（015 / 017）与 SPEC §3 的 bind ──

    [Fact]
    public void 循环上限不是数字时仍报循环无界()
    {
        // 契约 check 向量 017：times 给了字符串，期望 loop-unbounded，**不是** pin-type
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("loop").Set("times", HandlerValue.Str("3")));

        var issues = HandlerDslValidator.Validate(doc, Catalog);
        var issue = Assert.Single(issues, i => i.Code == "loop-unbounded");
        Assert.Equal("actions[0].times", issue.NodePath);
        Assert.DoesNotContain(issues, i => i.Code == "pin-type");
    }

    [Fact]
    public void 绑定名与视图字段同名被拒绝()
    {
        // 契约 check 向量 015：draw.bind 用了视图字段名 Defense
        var view = new Dictionary<string, HandlerViewField>(StringComparer.Ordinal)
        {
            ["Defense"] = new("Defense", "number", true),
        };
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("draw")
            .Set("count", HandlerValue.Num(1)).SetText("bind", "Defense"));

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog, view), i => i.Code == "bind-scope");
        Assert.Equal("actions[0].bind", issue.NodePath);
    }

    [Fact]
    public void 目录节点可以带bind且结果可被后续引用()
    {
        // SPEC §3：目录节点的 `bind?` 是合法字段，不能被报成 pin-unknown
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("draw").Set("count", HandlerValue.Num(2)).SetText("bind", "n"));
        // draw 的输出是 number，可以当 count 用
        doc.Actions.Add(new HandlerNode("addToHand")
            .Set("cardName", HandlerValue.Str("Strike")).Set("count", HandlerValue.Var("n")));

        Assert.Empty(HandlerDslValidator.Validate(doc, Catalog));

        // 导出也要带上 bind
        var draw = doc.ToJson(Catalog)["actions"]!.AsArray()[0]!.AsObject();
        Assert.Equal("n", draw["bind"]!.GetValue<string>());
    }

    [Fact]
    public void script导出补上空的输入输出数组()
    {
        // 契约（SPEC §3 字段表）里 script 的 inputs／outputs 是必填数组，空也得写。
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("script").SetText("body", "return;"));
        var json = doc.ToJson(Catalog);

        var script = json["actions"]!.AsArray()[0]!.AsObject();
        Assert.True(script.ContainsKey("inputs"));
        Assert.True(script.ContainsKey("outputs"));
        Assert.Empty(script["inputs"]!.AsArray());
        Assert.Empty(script["outputs"]!.AsArray());
        Assert.Equal("return;", script["body"]!.GetValue<string>());

        // 键序也要按契约来：op → inputs → outputs → body
        Assert.Equal(["op", "inputs", "outputs", "body"], script.Select(kv => kv.Key));

        // 导出物自己要通过本地校验
        Assert.Empty(HandlerDslValidator.Validate(doc, Catalog));
    }

    // ── 2026-11-01 版契约（check 019~024 ／ gen 009~010）─────────────────────

    [Fact]
    public void read的path不存在是pin_unknown且路径指向read_path()
    {
        // check 向量 019：复用 pin-unknown，不新增码
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("write").SetText("field", "Defense")
            .Set("value", HandlerValue.Read("self", "nonexistent")));
        var view = new Dictionary<string, HandlerViewField>(StringComparer.Ordinal)
        {
            ["Defense"] = new("Defense", "number", true),
        };

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog, view));
        Assert.Equal("pin-unknown", issue.Code);
        Assert.Equal("actions[0].value.read.path", issue.NodePath);
    }

    [Fact]
    public void read的目标与读取项不匹配是pin_unknown()
    {
        // check 向量 022：self 读 kredits —— 单位没有「点数」
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("write").SetText("field", "Field")
            .Set("value", HandlerValue.Read("self", "kredits")));
        var view = new Dictionary<string, HandlerViewField>(StringComparer.Ordinal)
        {
            ["Field"] = new("Field", "number", true),
        };

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog, view));
        Assert.Equal("pin-unknown", issue.Code);
        Assert.Equal("actions[0].value.read.path", issue.NodePath);

        // 反过来 viewer 读 attack 同样不成立
        doc.Actions[0].Set("value", HandlerValue.Read("viewer", "attack"));
        Assert.Equal("actions[0].value.read.path",
            Assert.Single(HandlerDslValidator.Validate(doc, Catalog, view)).NodePath);
    }

    [Fact]
    public void read可以读被遍历的那个实体()
    {
        // gen 向量 009：iterate 之后读 t.defense —— 11-01 版契约前完全写不出来
        var doc = new HandlerDocument();
        var iterate = new HandlerNode("iterate")
            .Set("over", HandlerValue.Var("units")).SetText("as", "u");
        var branch = new HandlerNode("branch")
            .Set("test", HandlerValue.Call("le",
                HandlerValue.ReadFrom(HandlerValue.Var("u"), "defense"), HandlerValue.Num(2)));
        branch.Body("then").Add(new HandlerNode("draw").Set("count", HandlerValue.Num(1)));
        iterate.Body("actions").Add(branch);
        doc.Actions.Add(new HandlerNode("ask").SetText("kind", "unit")
            .SetText("prompt", "选单位").SetText("bind", "units"));
        doc.Actions.Add(iterate);

        Assert.Empty(HandlerDslValidator.Validate(doc, Catalog));

        // 导出成契约要的形态：of 是对象
        var json = doc.ToJson(Catalog)["actions"]!.AsArray()[1]!.AsObject();
        var read = json["actions"]!.AsArray()[0]!.AsObject()["test"]!.AsObject()["args"]!.AsArray()[0]!.AsObject();
        Assert.Equal("u", read["read"]!.AsObject()["of"]!.AsObject()["var"]!.GetValue<string>());
        Assert.Equal("defense", read["read"]!.AsObject()["path"]!.GetValue<string>());
    }

    [Fact]
    public void read的实体表达式必须是entity()
    {
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("let").SetText("name", "n").Set("value", HandlerValue.Num(1)));
        doc.Actions.Add(new HandlerNode("write").SetText("field", "Defense")
            .Set("value", HandlerValue.ReadFrom(HandlerValue.Var("n"), "defense")));
        var view = new Dictionary<string, HandlerViewField>(StringComparer.Ordinal)
        {
            ["Defense"] = new("Defense", "number", true),
        };

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog, view));
        Assert.Equal("pin-type", issue.Code);
        Assert.Equal("actions[1].value.read.of", issue.NodePath);
    }

    [Fact]
    public void sum只收数值列表()
    {
        // check 向量 020：list<entity> 的数值聚合没有定义
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("damage")
            .Set("target", HandlerValue.ViewField("Target"))
            .Set("amount", HandlerValue.Call("sum", HandlerValue.ViewField("Units"))));
        var view = new Dictionary<string, HandlerViewField>(StringComparer.Ordinal)
        {
            ["Target"] = new("Target", "entity", false),
            ["Units"] = new("Units", "list<entity>", false),
        };

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog, view));
        Assert.Equal("expr-type", issue.Code);
        Assert.Equal("actions[0].amount.args[0]", issue.NodePath);
    }

    [Fact]
    public void unitsOf是只读查询并给出list_entity()
    {
        // gen 向量 010：unitsOf → iterate → damage
        var units = Catalog.Find("unitsOf");
        Assert.NotNull(units);
        Assert.True(units!.HasAwait);          // 读世界状态，会挂起
        Assert.False(units.HasSideEffect);     // 但**不改世界**（[HandlerDslPure]）
        Assert.Equal(["side", "zone"], units.Inputs.Select(p => p.Name));
        Assert.False(units.Pin("zone")!.Required);
        Assert.Equal(["self", "enemy", "any"], units.Pin("side")!.EnumValues);
        Assert.Equal("list<entity>", units.Outputs[0].Type);

        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("unitsOf")
            .Set("side", HandlerValue.Str("enemy")).SetText("bind", "foes"));
        var iterate = new HandlerNode("iterate").Set("over", HandlerValue.Var("foes")).SetText("as", "f");
        iterate.Body("actions").Add(new HandlerNode("damage")
            .Set("target", HandlerValue.Var("f")).Set("amount", HandlerValue.Num(1)));
        doc.Actions.Add(iterate);

        Assert.Empty(HandlerDslValidator.Validate(doc, Catalog));
    }

    [Fact]
    public void bind是节点级字段不是引脚()
    {
        // 没有返回值：不能写 bind（后端 HandlerDslReader.ParseBind 同判）
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("stop").SetText("bind", "x"));
        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog));
        Assert.Equal("bind-scope", issue.Code);
        Assert.Equal("actions[0].bind", issue.NodePath);

        // 多输出：必须是对象形态，v1 界面写不出来 → 如实报错而不是默默放过
        var multi = new HandlerDocument();
        multi.Actions.Add(new HandlerNode("fetchFromDeck")
            .Set("target", HandlerValue.ViewField("Target")).SetText("bind", "c"));
        var view = new Dictionary<string, HandlerViewField>(StringComparer.Ordinal)
        {
            ["Target"] = new("Target", "entity", false),
        };
        var second = Assert.Single(HandlerDslValidator.Validate(multi, Catalog, view));
        Assert.Equal("bind-scope", second.Code);
        Assert.Equal("actions[0].bind", second.NodePath);
    }

    [Fact]
    public void ask的zone与filter与fallback都是合法字段()
    {
        // 之前目录漏声明这三个，会把合法文档报成 pin-unknown
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("ask")
            .SetText("kind", "unit").SetText("prompt", "选一个")
            .SetText("zone", "frontline").SetText("filter", "armor")
            .SetText("fallback", "first").SetText("bind", "picked"));

        Assert.Empty(HandlerDslValidator.Validate(doc, Catalog));
    }

    [Fact]
    public void choice的选项是label加actions而不是动作数组()
    {
        // SPEC §3：options 是 [{ label, actions }]。写成普通动作数组后端会按 graph-shape 拒。
        var doc = new HandlerDocument();
        var choice = new HandlerNode("choice").SetText("prompt", "选一个");
        var yes = new HandlerOption("打它");
        yes.Actions.Add(new HandlerNode("draw").Set("count", HandlerValue.Num(1)));
        choice.Options.Add(yes);
        choice.Options.Add(new HandlerOption("算了"));
        doc.Actions.Add(choice);

        var json = doc.ToJson(Catalog)["actions"]!.AsArray()[0]!.AsObject();
        var options = json["options"]!.AsArray();
        Assert.Equal(2, options.Count);
        Assert.Equal(["label", "actions"], options[0]!.AsObject().Select(kv => kv.Key));
        Assert.Equal("打它", options[0]!["label"]!.GetValue<string>());
        Assert.Single(options[0]!["actions"]!.AsArray());
        // 空选项也要导出空数组，不能整段省略（与 else 相反：契约里 actions 必填）
        Assert.Empty(options[1]!["actions"]!.AsArray());

        Assert.Empty(HandlerDslValidator.Validate(doc, Catalog));
    }

    [Fact]
    public void choice没有选项或选项没写字都要报出来()
    {
        var empty = new HandlerDocument();
        empty.Actions.Add(new HandlerNode("choice").SetText("prompt", "选一个"));
        var issue = Assert.Single(HandlerDslValidator.Validate(empty, Catalog));
        Assert.Equal("pin-missing", issue.Code);
        Assert.Equal("actions[0].options", issue.NodePath);

        // 选项文字是给玩家看的，空着必须拦——这正是新插入的 choice 默认留空的原因。
        // 码是 pin-unknown（SPEC §14.1／check 向量 025），**不是** pin-missing：
        // 字段在、只是值不合法；13 个码封闭，不为这个新增第 14 个。
        var blank = new HandlerDocument();
        var choice = new HandlerNode("choice").SetText("prompt", "选一个");
        choice.Options.Add(new HandlerOption(""));
        blank.Actions.Add(choice);
        var second = Assert.Single(HandlerDslValidator.Validate(blank, Catalog));
        Assert.Equal("pin-unknown", second.Code);
        Assert.Equal("actions[0].options[0].label", second.NodePath);
    }

    [Fact]
    public void choice的选项文案纯空白与运行期同口径()
    {
        // 引擎 Reader 用 IsNullOrWhiteSpace 拒（向量 027）——只拒 "" 会把纯空白留在同一条失败路径上。
        var doc = new HandlerDocument();
        var choice = new HandlerNode("choice").SetText("prompt", "选一个");
        choice.Options.Add(new HandlerOption("   "));
        doc.Actions.Add(choice);

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog));
        Assert.Equal("pin-unknown", issue.Code);
        Assert.Equal("actions[0].options[0].label", issue.NodePath);
    }

    [Fact]
    public void choice的选项文案重复要指出跟谁撞了()
    {
        // 交互通道按**文案**回填被选项（标识集＝labels）：重复＝IndexOf 恒命中第一个＝生效到错误分支上
        // （check 向量 026）。报错必须指到「跟第几个撞了」，否则作者得自己一项项比。
        var doc = new HandlerDocument();
        var choice = new HandlerNode("choice").SetText("prompt", "选一个");
        choice.Options.Add(new HandlerOption("抽 1 张"));
        choice.Options.Add(new HandlerOption("抽 1 张"));
        doc.Actions.Add(choice);

        var issue = Assert.Single(HandlerDslValidator.Validate(doc, Catalog));
        Assert.Equal("pin-unknown", issue.Code);
        Assert.Equal("actions[0].options[1].label", issue.NodePath);
        Assert.Contains("options[0].label", issue.Message);
    }

    [Fact]
    public void choice的选项文案互不相同就放行()
    {
        var doc = new HandlerDocument();
        var choice = new HandlerNode("choice").SetText("prompt", "选一个");
        choice.Options.Add(new HandlerOption("抽 1 张"));
        choice.Options.Add(new HandlerOption("抽 2 张"));
        doc.Actions.Add(choice);

        Assert.Empty(HandlerDslValidator.Validate(doc, Catalog));
    }

    // ── 能力分区（SPEC §12.11／FRONTEND §5.5）─────────────────────────────────

    [Fact]
    public void 今天没有预览态节点但机制留着()
    {
        // 2026-11-03（引擎 R38）：choice 接通了交互通道（有桥走 target.submit、无桥明确失败），
        // 契约的 PreviewOps 登记表清空——**注意清空的是登记表，不是 BuiltInOps**：
        // 后者恒有十项，导出时按 PreviewOps 决定各自的 stage。
        Assert.Equal(10, HandlerCatalog.BuiltInOps.Count);
        Assert.All(HandlerCatalog.BuiltInOps, b => Assert.Equal(HandlerNodeStage.Alpha, b.Stage));
        Assert.All(HandlerCatalog.BuiltInOps, b => Assert.Null(b.Problem));

        Assert.Empty(Catalog.Nodes.Where(n => n.IsPreview).Select(n => n.Name));
        Assert.Equal(HandlerNodeStage.Alpha, Catalog.StageOf("choice"));
        Assert.Null(Catalog.ProblemOf("choice"));
    }

    [Fact]
    public void 内建op表与目录里的十个内建节点一一对应()
    {
        // 顺序也照契约（HandlerDslContract.BuiltinOps）：前端不自行推导这张表。
        Assert.Equal(
            ["sequence", "branch", "loop", "while", "iterate", "choice", "let", "write", "ask", "script"],
            HandlerCatalog.BuiltInOps.Select(b => b.Op));

        // 表里的每个 op 都必须是目录里真实存在的内建节点，反之亦然——
        // 两边错一个就会静默失效（引擎那边也有一条守护测试专门校验这个）。
        Assert.Equal(
            HandlerCatalog.BuiltInOps.Select(b => b.Op).OrderBy(n => n, StringComparer.Ordinal),
            Catalog.Nodes.Where(n => n.BuiltIn).Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void 分区查询对未知op退回alpha()
    {
        Assert.Equal(HandlerNodeStage.Alpha, Catalog.StageOf("damage"));
        Assert.Null(Catalog.ProblemOf("damage"));

        // 未知 op 不编造预览态：它该走 node-unknown，不是「还没接通」。
        Assert.Equal(HandlerNodeStage.Alpha, Catalog.StageOf("没有这个op"));
        Assert.Null(Catalog.ProblemOf("没有这个op"));
    }
}
