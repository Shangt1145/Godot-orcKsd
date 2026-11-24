using System.Globalization;
using System.Text;

namespace Kards.Ui.OrcBridge.HandlerDsl;

/// <summary>文本读不回来：带行号，能直接指到作者看着的那一行。</summary>
public sealed class HandlerTextParseException(int line, string message) : Exception($"第 {line} 行：{message}")
{
    public int Line { get; } = line;
}

/// <summary>
/// <see cref="HandlerTextPrinter"/> 的反向：把"像代码、词是游戏词"的文本读回 DSL 文档。
///
/// <para>
/// 规矩只有一条：**认不出就报错，绝不猜**。保存是覆盖式的，一次"尽力而为"的猜测
/// 就会在作者不知情的情况下改掉结构——那比拒绝保存严重得多。
/// </para>
///
/// <para>
/// 只认自己打印出来的语法（括号、`引脚: 值`、`否则`、`记作`、「标签」）。
/// 不做通用语言，也不做模糊匹配：标题/引脚名/枚举词都走词表反查，查不到就报错。
/// 唯一的例外是**目录里没有的 op**——打印器会照实打出来（例如 <c>futureOp()</c>），
/// 解析时也照实读回，留给校验器去说"这个积木不存在"。
/// </para>
/// </summary>
public static class HandlerTextParser
{
    private enum Kind { Word, Number, String, Symbol, Comment, End }

    private readonly record struct Token(Kind Kind, string Text, int Line);

    /// <summary>右侧实参：要么是一棵表达式，要么是一个"裸词"（枚举词 / 绑定名 / 自由文本）。</summary>
    private readonly record struct Rhs(HandlerValue? Value, string? Bare);

    private sealed class Cursor(IReadOnlyList<Token> tokens)
    {
        public int Index { get; private set; }
        public Token Peek(int offset = 0) => tokens[Math.Min(Index + offset, tokens.Count - 1)];
        public Token Advance() => tokens[Math.Min(Index++, tokens.Count - 1)];
        public bool AtEnd => Peek().Kind == Kind.End;
        public bool PeekSymbol(string symbol) => Peek().Kind == Kind.Symbol && Peek().Text == symbol;
        public bool PeekWord(string word) => Peek().Kind == Kind.Word && Peek().Text == word;
        public bool TakeSymbol(string symbol)
        {
            if (!PeekSymbol(symbol)) return false;
            Index++;
            return true;
        }
        public int Line => Peek().Line;
    }

    public static bool TryParse(string text, HandlerCatalog catalog, out HandlerDocument? document, out string? error)
    {
        try
        {
            document = Parse(text, catalog);
            error = null;
            return true;
        }
        catch (HandlerTextParseException failure)
        {
            document = null;
            error = failure.Message;
            return false;
        }
    }

    /// <exception cref="HandlerTextParseException">文本不是这份语法。</exception>
    public static HandlerDocument Parse(string text, HandlerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(catalog);

        var cursor = new Cursor(Lex(text));
        var document = new HandlerDocument();

        // 触发行只描述"这段挂在哪"，DSL 里没有它：读的时候可以认，写回时不产生任何结构。
        if (cursor.PeekWord("当"))
        {
            cursor.Advance();
            if (cursor.Peek().Kind != Kind.String) throw Fail(cursor, "「当」后面应当跟着「触发时机」");
            cursor.Advance();
            ExpectWord(cursor, "时");
            ExpectSymbol(cursor, "{");
            ParseStatements(cursor, catalog, document.Actions, null);
            ExpectSymbol(cursor, "}");
        }
        else
        {
            ParseStatements(cursor, catalog, document.Actions, null);
        }

        while (cursor.Peek().Kind == Kind.Comment) cursor.Advance();
        if (!cursor.AtEnd) throw Fail(cursor, "多余的内容：" + Describe(cursor.Peek()));
        return document;
    }

    // ── 语句 ──────────────────────────────────────────────────────────────────

    private static void ParseStatements(Cursor cursor, HandlerCatalog catalog, List<HandlerNode> sink, HandlerNode? owner)
    {
        var spec = owner is null ? null : catalog.Find(owner.Op);
        var target = sink;

        while (true)
        {
            // 注释里有一类**是有用的**：多子动作节点靠 `// then` / `// else` 分段。
            while (cursor.Peek().Kind == Kind.Comment)
            {
                var marker = cursor.Peek().Text.Trim();
                if (owner is not null && spec is not null && spec.Bodies.Contains(marker)) target = owner.Body(marker);
                cursor.Advance();
            }

            if (cursor.AtEnd) break;
            if (cursor.PeekSymbol("}")) break;
            target.Add(ParseStatement(cursor, catalog));
        }
    }

    private static HandlerNode ParseStatement(Cursor cursor, HandlerCatalog catalog)
    {
        if (cursor.PeekWord("如果")) return ParseBranch(cursor, catalog);
        if (cursor.PeekWord("遍历")) return ParseIterate(cursor, catalog);
        if (cursor.PeekWord("选择")) return ParseChoice(cursor, catalog);
        return ParseCallStatement(cursor, catalog);
    }

    private static HandlerNode ParseBranch(Cursor cursor, HandlerCatalog catalog)
    {
        cursor.Advance();   // 如果
        var node = new HandlerNode("branch").Set("test", ParseExpression(cursor, catalog));
        ExpectSymbol(cursor, "{");
        ParseStatements(cursor, catalog, node.Body("then"), node);
        ExpectSymbol(cursor, "}");

        if (cursor.PeekWord("否则"))
        {
            cursor.Advance();
            ExpectSymbol(cursor, "{");
            ParseStatements(cursor, catalog, node.Body("else"), node);
            ExpectSymbol(cursor, "}");
        }
        return node;
    }

    private static HandlerNode ParseIterate(Cursor cursor, HandlerCatalog catalog)
    {
        cursor.Advance();   // 遍历
        var node = new HandlerNode("iterate").Set("over", ParseExpression(cursor, catalog));
        if (cursor.PeekWord("记作"))
        {
            cursor.Advance();
            node.SetText("as", BindingName(TakeWord(cursor, "变量名")));
        }
        ExpectSymbol(cursor, "{");
        ParseStatements(cursor, catalog, node.Body("actions"), node);
        ExpectSymbol(cursor, "}");
        return node;
    }

    private static HandlerNode ParseChoice(Cursor cursor, HandlerCatalog catalog)
    {
        cursor.Advance();   // 选择
        var node = new HandlerNode("choice");
        ExpectSymbol(cursor, "{");
        while (cursor.Peek().Kind == Kind.String)
        {
            var option = new HandlerOption(cursor.Advance().Text);
            ExpectSymbol(cursor, "{");
            ParseStatements(cursor, catalog, option.Actions, null);
            ExpectSymbol(cursor, "}");
            node.Options.Add(option);
        }
        ExpectSymbol(cursor, "}");
        if (node.Options.Count == 0) throw Fail(cursor, "「选择」里至少要有一个分支");
        return node;
    }

    private static HandlerNode ParseCallStatement(Cursor cursor, HandlerCatalog catalog)
    {
        string? binding = null;
        if (cursor.Peek().Kind == Kind.Word && cursor.Peek(1).Kind == Kind.Symbol && cursor.Peek(1).Text == "=")
        {
            binding = BindingName(cursor.Advance().Text);
            cursor.Advance();   // =
        }

        var name = TakeWord(cursor, "动作名");
        // 取值不能单独成句：先判，否则会被当成"参数缺冒号"报错，方向就带偏了。
        if (HandlerVocabulary.TryOperatorName(name, out _)) throw Fail(cursor, $"「{name}」是取值不是动作，不能单独成句");
        var (op, args) = ParseCall(cursor, catalog, name);

        var node = new HandlerNode(op);
        if (binding is not null) node.SetText("bind", binding);

        var spec = catalog.Find(op);
        foreach (var (key, rhs) in args)
        {
            // 高级脚本节点的引脚声明：打印器按 JSON 文本写出，这里原样读回。
            if (node.Op == "script" && key is "输入声明" or "输出声明")
            {
                var field = key == "输入声明" ? "inputs" : "outputs";
                var json = TextOf(rhs, cursor);
                try { node.SetScriptPins(field, System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray ?? throw new FormatException()); }
                catch (Exception failure) when (failure is System.Text.Json.JsonException or FormatException)
                { throw Fail(cursor, key + " 必须是 JSON 数组"); }
                continue;
            }

            var pinName = PinName(op, key, spec);
            var pin = spec?.Pin(pinName);
            if (pin is not null && pin.Kind != HandlerPinKind.Value) node.SetText(pinName, TextOf(rhs, cursor));
            else node.Set(pinName, ToValue(rhs, pin));
        }

        // 子动作：单子动作段省略标题，多段靠 `// 段名` 注释分段
        if (cursor.PeekSymbol("{"))
        {
            cursor.Advance();
            var fields = spec?.Bodies ?? [];
            var defaultField = fields.Count == 1 ? fields[0] : "actions";
            ParseStatements(cursor, catalog, node.Body(defaultField), node);
            ExpectSymbol(cursor, "}");
        }
        return node;
    }

    // ── 调用与实参 ────────────────────────────────────────────────────────────

    private static (string Op, List<(string Key, Rhs Value)> Args) ParseCall(Cursor cursor, HandlerCatalog catalog, string name)
    {
        ExpectSymbol(cursor, "(");
        var args = new List<(string Key, Rhs Value)>();
        if (!cursor.PeekSymbol(")"))
        {
            do
            {
                var key = TakeWord(cursor, "引脚名");
                ExpectSymbol(cursor, ":");
                args.Add((key, ParseRhs(cursor, catalog)));
            } while (cursor.TakeSymbol(","));
        }
        ExpectSymbol(cursor, ")");

        var candidates = HandlerVocabulary.OpsByTitle(name);
        var op = candidates.Count switch
        {
            1 => candidates[0],
            0 => name,   // 未知 op 照实保留，交给校验器
            _ => candidates.FirstOrDefault(c => ArgsMatch(c, args, catalog))
                 ?? throw Fail(cursor, $"「{name}」有 {candidates.Count} 个同名动作，" + string.Join("、", candidates) + "，引脚对不上任何一个"),
        };
        return (op, args);
    }

    /// <summary>同名标题靠引脚集合区分（历史上确实存在同标题的 op）。</summary>
    private static bool ArgsMatch(string op, List<(string Key, Rhs Value)> args, HandlerCatalog catalog)
    {
        var spec = catalog.Find(op);
        if (spec is null) return false;
        return args.All(arg => spec.Inputs.Any(pin => HandlerVocabulary.PinLabel(op, pin.Name) == arg.Key || pin.Name == arg.Key));
    }

    private static string PinName(string op, string key, HandlerNodeSpec? spec)
    {
        if (spec is null) return key;
        foreach (var pin in spec.Inputs)
            if (HandlerVocabulary.PinLabel(op, pin.Name) == key || pin.Name == key) return pin.Name;
        return key;   // 目录没声明的引脚：照原样保留（打印器也是照实打的）
    }

    /// <summary>实参的右侧：能当"裸词"就留着（枚举词/绑定名/自由文本），否则当表达式。</summary>
    private static Rhs ParseRhs(Cursor cursor, HandlerCatalog catalog)
    {
        if (cursor.Peek().Kind == Kind.Word)
        {
            var next = cursor.Peek(1);
            var continues = next.Kind == Kind.Symbol && next.Text is "(" or "的"
                || next.Kind == Kind.Word && next.Text is "且" or "或" or "的";
            var binary = next.Kind == Kind.Symbol && next.Text is "==" or "!=" or "<" or "<=" or ">" or ">=" or "+" or "-" or "*";
            if (!continues && !binary) return new Rhs(null, cursor.Advance().Text);
        }
        return new Rhs(ParseExpression(cursor, catalog), null);
    }

    private static HandlerValue ToValue(Rhs rhs, HandlerPinSpec? pin)
    {
        if (rhs.Value is { } value) return value;
        var word = rhs.Bare ?? "";

        // 值引脚里的字符串：先当游戏词反查（敌方 → enemy），查不到再当绑定/视图。
        // 裸词落在值引脚上＝引用（绑定/视图），**不能**退化成字符串字面量——
        // 那会把 `对谁: f` 读成 "f"，绑定悄悄变成一句没意义的话。
        if (HandlerVocabulary.TryLiteralValue(word, out var literal) && HandlerVocabulary.LiteralLabel(literal) == word)
            return HandlerValue.Str(literal);
        if (HandlerVocabulary.TryBindingName(word, out var binding)) return HandlerValue.Var(binding);
        if (HandlerVocabulary.TryViewFieldName(word, out var field)) return HandlerValue.ViewField(field);
        if (word is "是" or "否") return HandlerValue.Bool(word == "是");
        return HandlerValue.Var(word);
    }

    private static string TextOf(Rhs rhs, Cursor cursor)
    {
        if (rhs.Bare is { } bare)
            return HandlerVocabulary.TryLiteralValue(bare, out var literal) && HandlerVocabulary.LiteralLabel(literal) == bare
                ? literal
                : bare;
        if (rhs.Value is { Kind: HandlerValueKind.Literal, Text: { } text }) return text;
        throw Fail(cursor, "这一格要填文字或选项，不是表达式");
    }

    // ── 表达式 ────────────────────────────────────────────────────────────────

    private static HandlerValue ParseExpression(Cursor cursor, HandlerCatalog catalog) => ParseBinary(cursor, catalog, 1);

    private static HandlerValue ParseBinary(Cursor cursor, HandlerCatalog catalog, int minPrecedence)
    {
        var left = ParseUnary(cursor, catalog);
        while (TryBinary(cursor, out var op, out var precedence) && precedence >= minPrecedence)
        {
            cursor.Advance();
            var right = ParseBinary(cursor, catalog, precedence + 1);
            left = HandlerValue.Call(op, left, right);
        }
        return left;
    }

    private static bool TryBinary(Cursor cursor, out string op, out int precedence)
    {
        var token = cursor.Peek();
        op = token.Text;
        precedence = 0;

        if (token.Kind == Kind.Symbol)
        {
            (op, precedence) = token.Text switch
            {
                "==" => ("eq", 3), "!=" => ("ne", 3), "<" => ("lt", 3), "<=" => ("le", 3),
                ">" => ("gt", 3), ">=" => ("ge", 3), "+" => ("add", 4), "-" => ("sub", 4), "*" => ("mul", 5),
                _ => ("", 0),
            };
            return precedence > 0;
        }
        if (token.Kind == Kind.Word)
        {
            (op, precedence) = token.Text switch { "且" => ("and", 2), "或" => ("or", 1), _ => ("", 0) };
            return precedence > 0;
        }
        return false;
    }

    private static HandlerValue ParseUnary(Cursor cursor, HandlerCatalog catalog)
    {
        if (cursor.PeekWord("非"))
        {
            cursor.Advance();
            return HandlerValue.Call("not", ParseUnary(cursor, catalog));
        }
        if (cursor.PeekSymbol("-"))
        {
            cursor.Advance();
            var operand = ParseUnary(cursor, catalog);
            // 负数字面量要读回 Num(-x) 而不是 sub(0, x)：否则往返之后 JSON 就变了。
            return operand is { Kind: HandlerValueKind.Literal, LiteralType: "number" }
                ? HandlerValue.Num(-operand.Number)
                : HandlerValue.Call("sub", HandlerValue.Num(0), operand);
        }
        return ParsePrimary(cursor, catalog);
    }

    private static HandlerValue ParsePrimary(Cursor cursor, HandlerCatalog catalog)
    {
        var token = cursor.Peek();

        if (token.Kind == Kind.Number)
        {
            cursor.Advance();
            return HandlerValue.Num(double.Parse(token.Text, CultureInfo.InvariantCulture));
        }
        if (token.Kind == Kind.String)
        {
            cursor.Advance();
            return HandlerValue.Str(Unescape(token.Text));
        }
        if (cursor.TakeSymbol("("))
        {
            var inner = ParseExpression(cursor, catalog);
            ExpectSymbol(cursor, ")");
            return inner;
        }
        if (token.Kind != Kind.Word) throw Fail(cursor, "这里应当是一个值");

        cursor.Advance();
        var word = token.Text;

        if (word is "是" or "否") return HandlerValue.Bool(word == "是");
        if (ReadPrefix(word, "自己的", out var selfPath))
            return HandlerValue.Read("self", ReadablePath(selfPath, cursor));
        if (ReadPrefix(word, "我方的", out var viewerPath))
            return HandlerValue.Read("viewer", ReadablePath(viewerPath, cursor));

        if (cursor.PeekSymbol("(")) return ParseOperatorCall(cursor, catalog, word);

        // 「X 的 属性」：读某个实体自己的字段
        if (cursor.PeekWord("的"))
        {
            cursor.Advance();
            var path = ReadablePath(TakeWord(cursor, "属性名"), cursor);
            return HandlerValue.ReadFrom(BindingOrView(word), path);
        }

        return BindingOrView(word);
    }

    private static HandlerValue ParseOperatorCall(Cursor cursor, HandlerCatalog catalog, string label)
    {
        ExpectSymbol(cursor, "(");
        var positional = new List<HandlerValue>();
        var named = new List<(string Key, Rhs Value)>();
        if (!cursor.PeekSymbol(")"))
        {
            do
            {
                if (cursor.Peek().Kind == Kind.Word && cursor.Peek(1).Kind == Kind.Symbol && cursor.Peek(1).Text == ":")
                {
                    var key = cursor.Advance().Text;
                    cursor.Advance();
                    named.Add((key, ParseRhs(cursor, catalog)));
                }
                else positional.Add(ParseExpression(cursor, catalog));
            } while (cursor.TakeSymbol(","));
        }
        ExpectSymbol(cursor, ")");

        if (HandlerVocabulary.TryOperatorName(label, out var op))
        {
            if (named.Count > 0) throw Fail(cursor, $"「{label}」是运算，参数不写名字");
            return HandlerValue.Call(op, positional.ToArray());
        }

        // 值位置也可能出现**动作**（查询类如「找出单位」返回一组实体）：按标题反查，
        // 参数按目录顺序对应引脚——打印器就是按引脚顺序写的。
        var candidates = HandlerVocabulary.OpsByTitle(label);
        var resolved = candidates.Count switch
        {
            1 => candidates[0],
            0 => catalog.Knows(label) ? label : null,
            _ => candidates.FirstOrDefault(c => catalog.Find(c)?.Inputs.Count == positional.Count + named.Count),
        };
        if (resolved is null) throw Fail(cursor, $"「{label}(…)」既不是已知运算，也不是已知动作");

        var spec = catalog.Find(resolved);
        var args = new List<HandlerValue>();
        if (named.Count > 0 && spec is not null)
        {
            // 命名参数：按目录的引脚顺序重排回位置参数（Call 是位置形态）。
            // 打印器只按引脚顺序写，所以"中间有洞"不会出现；真出现了也会在这里丢形，宁可报错。
            foreach (var pin in spec.Inputs)
            {
                var match = named.Where(n => PinName(resolved, n.Key, spec) == pin.Name).ToArray();
                if (match.Length == 0) continue;
                var pinSpec = spec.Pin(pin.Name);
                args.Add(pinSpec is not null && pinSpec.Kind != HandlerPinKind.Value
                    ? HandlerValue.Str(TextOf(match[0].Value, cursor))
                    : ToValue(match[0].Value, pinSpec));
            }
        }
        else args.AddRange(positional);

        return HandlerValue.Call(resolved, args.ToArray());
    }

    private static HandlerValue BindingOrView(string word)
    {
        if (HandlerVocabulary.TryBindingName(word, out var binding)) return HandlerValue.Var(binding);
        if (HandlerVocabulary.TryViewFieldName(word, out var field)) return HandlerValue.ViewField(field);
        return HandlerValue.Var(word);
    }

    private static bool ReadPrefix(string word, string prefix, out string rest)
    {
        rest = word.StartsWith(prefix, StringComparison.Ordinal) ? word[prefix.Length..] : "";
        return rest.Length > 0;
    }

    private static string ReadablePath(string label, Cursor cursor) =>
        HandlerVocabulary.TryReadablePath(label, out var path) ? path : throw Fail(cursor, $"不认识的属性：{label}");

    private static string BindingName(string word)
    {
        // 打印器会把 t/x/picked 翻成「当前对象」这类说法，读回来要还原成原名，
        // 否则绑定名一改，后面的 read.of 就指向别的东西了。
        return HandlerVocabulary.TryBindingName(word, out var name) ? name : HandlerVocabulary.BindingId(word);
    }

    // ── 词法 ──────────────────────────────────────────────────────────────────

    private static List<Token> Lex(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        var line = 1;

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var start = i + 2;
                while (i < text.Length && text[i] != '\n') i++;
                tokens.Add(new Token(Kind.Comment, text[start..i], line));
                continue;
            }

            if (c == '"')
            {
                var start = ++i;
                var builder = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { builder.Append(text[i]).Append(text[i + 1]); i += 2; continue; }
                    builder.Append(text[i]); i++;
                }
                if (i >= text.Length) throw new HandlerTextParseException(line, "字符串没有闭合");
                i++;
                tokens.Add(new Token(Kind.String, builder.ToString(), line));
                continue;
            }

            if (c == '「')
            {
                var start = ++i;
                while (i < text.Length && text[i] != '」') { if (text[i] == '\n') line++; i++; }
                if (i >= text.Length) throw new HandlerTextParseException(line, "「」没有闭合");
                var raw = text[start..i];
                i++;
                tokens.Add(new Token(Kind.String, raw, line));
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(new Token(Kind.Number, text[start..i], line));
                continue;
            }

            var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
            if (two is "==" or "!=" or "<=" or ">=")
            {
                tokens.Add(new Token(Kind.Symbol, two, line));
                i += 2;
                continue;
            }
            if ("{}():,=<>+-*".Contains(c))
            {
                tokens.Add(new Token(Kind.Symbol, c.ToString(), line));
                i++;
                continue;
            }

            var wordStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && !"{}():,=<>+-*\"「」/".Contains(text[i])) i++;
            if (i == wordStart) throw new HandlerTextParseException(line, $"看不懂的字符：{c}");
            tokens.Add(new Token(Kind.Word, text[wordStart..i], line));
        }

        tokens.Add(new Token(Kind.End, "", line));
        return tokens;
    }

    // ── 小工具 ────────────────────────────────────────────────────────────────

    private static HandlerTextParseException Fail(Cursor cursor, string message) =>
        new(cursor.Line, message + "（读到：" + Describe(cursor.Peek()) + "）");

    private static string Describe(Token token) => token.Kind switch
    {
        Kind.End => "文末",
        Kind.Comment => "注释",
        _ => "「" + token.Text + "」",
    };

    private static void ExpectSymbol(Cursor cursor, string symbol)
    {
        if (!cursor.TakeSymbol(symbol)) throw Fail(cursor, "这里应当有 " + symbol);
    }

    private static void ExpectWord(Cursor cursor, string word)
    {
        if (!cursor.PeekWord(word)) throw Fail(cursor, "这里应当有「" + word + "」");
        cursor.Advance();
    }

    private static string TakeWord(Cursor cursor, string what)
    {
        if (cursor.Peek().Kind != Kind.Word) throw Fail(cursor, "这里应当是" + what);
        return cursor.Advance().Text;
    }

    private static string Unescape(string value) =>
        value.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
}
