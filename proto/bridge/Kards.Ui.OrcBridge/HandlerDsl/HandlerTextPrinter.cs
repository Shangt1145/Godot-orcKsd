using System.Globalization;
using System.Text;

namespace Kards.Ui.OrcBridge.HandlerDsl;

/// <summary>
/// 把一份 handler DSL 文档打印成「**像代码、词是游戏词**」的文本。
///
/// <para>
/// 存在的理由：积木画布适合"拼"，不适合"读"。作者写完一段效果，需要一眼看出
/// 「先干什么、在什么条件下、否则怎样」——那是语句的形态，不是块的形态。
/// 词表 ( <see cref="HandlerVocabulary"/> ) 里每个 op／引脚／算子都已有中文名，
/// 这里做的只是把它们按正常代码的语法排出来：
/// 触发→大括号包住整段；节点→<c>名称(引脚: 值)</c>；分支→<c>如果 … { } 否则 { }</c>。
/// </para>
///
/// <para>
/// **打印必须无副作用**：不能调用 <see cref="HandlerNode.Body"/>（它会建空列表），
/// 否则"看一眼代码"就把文档改了，撤销栈和 diff 全部失真。
/// </para>
///
/// <para>
/// 它不是新的真源：文档才是真源，这段文本是从文档算出来的视图。
/// 所以遇到目录里没有的 op、或作者手写的额外引脚，一律照实打印并标出来，
/// 不做"美化式省略"——隐藏掉的东西，等这段文本变成可编辑输入时就会变成数据丢失。
/// </para>
/// </summary>
public static class HandlerTextPrinter
{
    private const int IndentWidth = 2;

    private static string Indent(int depth) => new(' ', depth * IndentWidth);

    /// <summary>打印整段。触发时机属于预制体成员、不在 DSL 里，所以由调用方给。</summary>
    /// <param name="trigger">形如「部署时（本单位）」；为空则不打印触发行。</param>
    public static string Print(HandlerDocument document, HandlerCatalog catalog, string? trigger = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);

        var text = new StringBuilder();
        var wrapped = !string.IsNullOrWhiteSpace(trigger);

        if (wrapped)
            text.Append("当「").Append(trigger!.Trim()).Append("」时 {\n");

        if (document.Actions.Count == 0)
            text.Append(Indent(wrapped ? 1 : 0)).Append("// 还没有任何动作\n");
        else
            foreach (var node in document.Actions)
                PrintNode(text, node, catalog, wrapped ? 1 : 0);

        if (wrapped) text.Append("}\n");
        return text.ToString();
    }

    private static void PrintNode(StringBuilder text, HandlerNode node, HandlerCatalog catalog, int depth)
    {
        var spec = catalog.Find(node.Op);
        var indent = Indent(depth);

        switch (node.Op)
        {
            case "branch":
            {
                text.Append(indent).Append("如果 ").Append(Value(node.Value("test"), catalog)).Append(" {\n");
                PrintBody(text, node, "then", catalog, depth + 1);
                text.Append(indent).Append('}');
                if (Body(node, "else").Count > 0)
                {
                    text.Append(" 否则 {\n");
                    PrintBody(text, node, "else", catalog, depth + 1);
                    text.Append(indent).Append('}');
                }
                text.Append('\n');
                return;
            }
            case "iterate":
            {
                var over = Value(node.Value("over"), catalog);
                var asName = node.Text("as") ?? "";
                text.Append(indent).Append("遍历 ").Append(over);
                if (asName.Length > 0) text.Append(" 记作 ").Append(HandlerVocabulary.BindingLabel(asName));
                text.Append(" {\n");
                PrintBody(text, node, "actions", catalog, depth + 1);
                text.Append(indent).Append("}\n");
                return;
            }
            case "choice":
            {
                text.Append(indent).Append("选择 {\n");
                foreach (var option in node.Options)
                {
                    text.Append(Indent(depth + 1)).Append('「').Append(option.Label).Append("」 {\n");
                    foreach (var child in option.Actions) PrintNode(text, child, catalog, depth + 2);
                    text.Append(Indent(depth + 1)).Append("}\n");
                }
                text.Append(indent).Append("}\n");
                return;
            }
        }

        // 普通节点：绑定 = 名称(引脚: 值) { 子动作 }
        text.Append(indent);
        if (node.Text("bind") is { Length: > 0 } bind)
            text.Append(HandlerVocabulary.BindingLabel(bind)).Append(" = ");

        text.Append(spec is null ? node.Op : HandlerVocabulary.NodeTitle(node.Op));
        text.Append('(').Append(Arguments(node, spec, catalog)).Append(')');

        if (spec is null)
        {
            // 目录里没有的 op：仍然把子动作打出来，否则这一段会凭空消失
            if (node.Bodies.Count > 0)
            {
                text.Append(" {\n");
                foreach (var (field, children) in node.Bodies)
                {
                    if (children.Count == 0) continue;
                    text.Append(Indent(depth + 1)).Append("// ").Append(field).Append('\n');
                    foreach (var child in children) PrintNode(text, child, catalog, depth + 1);
                }
                text.Append(indent).Append('}');
            }
            text.Append("  // 目录里没有这个积木\n");
            return;
        }

        var bodies = spec.Bodies.Where(field => Body(node, field).Count > 0).ToArray();
        if (bodies.Length == 0)
        {
            text.Append('\n');
            return;
        }

        text.Append(" {\n");
        foreach (var field in bodies)
        {
            // 单子动作段（绝大多数 op 的 actions）不额外加标题，省一层噪音
            if (bodies.Length > 1 || field != "actions")
                text.Append(Indent(depth + 1)).Append("// ").Append(field).Append('\n');
            PrintBody(text, node, field, catalog, depth + 1);
        }
        text.Append(indent).Append("}\n");
    }

    private static void PrintBody(StringBuilder text, HandlerNode node, string field, HandlerCatalog catalog, int depth)
    {
        var children = Body(node, field);
        if (children.Count == 0)
        {
            text.Append(Indent(depth)).Append("// 这里是空的\n");
            return;
        }
        foreach (var child in children) PrintNode(text, child, catalog, depth);
    }

    /// <summary>只读取子动作：<see cref="HandlerNode.Body"/> 会建空列表，打印不能用它。</summary>
    private static IReadOnlyList<HandlerNode> Body(HandlerNode node, string field) =>
        node.Bodies.TryGetValue(field, out var list) ? list : [];

    private static string Arguments(HandlerNode node, HandlerNodeSpec? spec, HandlerCatalog catalog)
    {
        var parts = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal) { "bind" };

        if (spec is not null)
        {
            foreach (var pin in spec.Inputs)
            {
                if (pin.Name == "bind") continue;
                if (pin.Kind == HandlerPinKind.Value)
                {
                    if (node.Value(pin.Name) is { } value)
                        parts.Add(HandlerVocabulary.PinLabel(node.Op, pin.Name) + ": " + ValueForPin(value, pin, catalog));
                }
                else if (node.Text(pin.Name) is { Length: > 0 } raw)
                {
                    parts.Add(HandlerVocabulary.PinLabel(node.Op, pin.Name) + ": " + TextValue(raw, pin));
                }
                used.Add(pin.Name);
            }
        }

        // 目录没声明的字段照打：作者手写、或引擎加了新引脚而镜像没跟上。
        foreach (var (name, value) in node.Values)
        {
            if (used.Contains(name)) continue;
            parts.Add(name + ": " + Value(value, catalog));
            used.Add(name);
        }
        foreach (var (name, raw) in node.Texts)
        {
            if (used.Contains(name) || raw.Length == 0) continue;
            parts.Add(name + ": " + Quote(raw));
        }

        // 高级脚本节点的引脚声明：原样按 JSON 文本往返。
        // 不打印它们的话，纯文本往返会把作者的 inputs/outputs 吃掉——
        // 那是数据丢失，而这段文本马上就要变成可编辑输入了。
        if (node.Op == "script")
        {
            foreach (var (field, label) in new[] { ("inputs", "输入声明"), ("outputs", "输出声明") })
                if (node.ScriptPins.TryGetValue(field, out var pins))
                    parts.Add(label + ": " + Quote(pins.ToJsonString()));
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// 文本引脚：枚举值翻中文，自由文本（脚本正文等）加引号。
    /// <para>
    /// 判据用 <see cref="HandlerPinSpec.Kind"/> 而不是 <see cref="HandlerPinSpec.EnumValues"/>——
    /// 目录里枚举引脚的候选表可以是空的（取值面由引擎侧约束），拿候选表当判据会把
    /// <c>enemy</c> 打成带引号的英文，正好丢掉了「词是游戏词」这个唯一卖点。
    /// </para>
    /// </summary>
    private static string TextValue(string raw, HandlerPinSpec pin) =>
        pin.Kind == HandlerPinKind.Enum || pin.EnumValues is { Count: > 0 }
            ? HandlerVocabulary.LiteralLabel(raw)
            : Quote(raw);

    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        return "\"" + escaped + "\"";
    }

    /// <summary>
    /// 值引脚里的字符串：**词表认得的就按游戏词打印，不加引号**。
    /// <para>
    /// 引擎把枚举存成普通字符串字面量（<c>{"lit":"enemy"}</c>），引脚规格里不一定带候选表，
    /// 所以判据只能是"这个词表有没有中文名"。带引号的 <c>"enemy"</c> 对作者没有意义，
    /// <c>敌方</c> 才有——这正是这套文本存在的理由。
    /// </para>
    /// </summary>
    private static string ValueForPin(HandlerValue value, HandlerPinSpec pin, HandlerCatalog catalog)
    {
        if (value.Kind == HandlerValueKind.Literal && value.Text is { } raw
            && (pin.Kind == HandlerPinKind.Enum || pin.EnumValues is { Count: > 0 }
                || HandlerVocabulary.LiteralLabel(raw) != raw))
            return HandlerVocabulary.LiteralLabel(raw);
        return Value(value, catalog);
    }

    /// <summary>打印一个表达式。运算符按正常代码写成中缀，其余走「算子名(参数)」。</summary>
    public static string Value(HandlerValue? value, HandlerCatalog catalog)
    {
        if (value is null) return "?";
        return value.Kind switch
        {
            HandlerValueKind.Literal => value.Text is not null
                ? Quote(value.Text)
                : value.LiteralType == "boolean" ? (value.Flag ? "是" : "否")
                : value.Number.ToString("0.###", CultureInfo.InvariantCulture),
            HandlerValueKind.Binding => HandlerVocabulary.BindingLabel(value.Name ?? ""),
            HandlerValueKind.View => HandlerVocabulary.ViewFieldLabel(value.Name ?? ""),
            HandlerValueKind.Read => Read(value, catalog),
            HandlerValueKind.Operator => Operator(value, catalog),
            _ => "?",
        };
    }

    private static string Read(HandlerValue value, HandlerCatalog catalog)
    {
        var what = HandlerVocabulary.ReadableLabel(value.Name ?? "");
        if (value.ReadTarget is { } target) return Value(target, catalog) + " 的 " + what;
        return value.ReadOf == "self" ? "自己的" + what : "我方的" + what;
    }

    private static string Operator(HandlerValue value, HandlerCatalog catalog)
    {
        var name = value.Name ?? "";
        var args = value.Args.Select(arg => Value(arg, catalog)).ToArray();
        var mine = Precedence(name);

        // 二元中缀：左右各留一个空格，长表达式才数得清括号层次
        string? infix = name switch
        {
            "add" => "+", "sub" => "-", "mul" => "*",
            "eq" => "==", "ne" => "!=", "lt" => "<", "le" => "<=", "gt" => ">", "ge" => ">=",
            "and" => "且", "or" => "或",
            _ => null,
        };
        if (infix is not null && args.Length == 2)
            return Wrap(value.Args[0], args[0], mine, rightSide: false, catalog)
                + " " + infix + " "
                + Wrap(value.Args[1], args[1], mine, rightSide: true, catalog);
        if (name == "not" && args.Length == 1)
            return "非 " + Wrap(value.Args[0], args[0], mine + 1, rightSide: false, catalog);

        // 值位置也可能出现**动作**（查询类，如「找出单位」返回一组实体）：
        // 用标题 + 引脚名打印，和语句位置同一套词，读回来才认得。
        if (catalog.Find(name) is { } valueSpec && valueSpec.Inputs.Count > 0 && value.Args.Count > 0)
        {
            var parts = new List<string>();
            for (var i = 0; i < value.Args.Count; i++)
            {
                var pin = i < valueSpec.Inputs.Count ? valueSpec.Inputs[i] : null;
                parts.Add(pin is null
                    ? i.ToString(CultureInfo.InvariantCulture) + ": " + args[i]
                    : HandlerVocabulary.PinLabel(name, pin.Name) + ": " + ValueForPin(value.Args[i], pin, catalog));
            }
            return HandlerVocabulary.NodeTitle(name) + "(" + string.Join(", ", parts) + ")";
        }

        return HandlerVocabulary.OperatorLabel(name) + "(" + string.Join(", ", args) + ")";
    }

    /// <summary>
    /// 子表达式比父运算符更松（或同级且在右边）时补括号。
    /// <para>
    /// 不加这一层，<c>(a+b)*c</c> 会打成 <c>a + b * c</c>——**读回来是另一棵树**，
    /// 而这份文本马上要变成可编辑输入，一次静默改义就是一次数据损坏。
    /// </para>
    /// </summary>
    private static string Wrap(HandlerValue child, string rendered, int parentPrecedence, bool rightSide, HandlerCatalog catalog)
    {
        if (child.Kind != HandlerValueKind.Operator) return rendered;
        var childPrecedence = Precedence(child.Name ?? "");
        if (childPrecedence == 0) return rendered;   // 非中缀算子自带函数括号
        var needs = childPrecedence < parentPrecedence
            || (rightSide && childPrecedence == parentPrecedence && parentPrecedence <= 4);
        return needs ? "(" + rendered + ")" : rendered;
    }

    private static int Precedence(string op) => op switch
    {
        "or" => 1,
        "and" => 2,
        "eq" or "ne" or "lt" or "le" or "gt" or "ge" => 3,
        "add" or "sub" => 4,
        "mul" => 5,
        "not" => 6,
        _ => 0,
    };
}
