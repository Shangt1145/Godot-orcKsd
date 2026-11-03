using System.Text.Json.Nodes;

namespace Kards.Ui.OrcBridge.HandlerDsl;

public enum HandlerValueKind
{
    /// <summary>字面量（number / string / boolean）。</summary>
    Literal,
    /// <summary>绑定引用 { "var": "x" }。</summary>
    Binding,
    /// <summary>视图字段 { "view": "Defense" }。</summary>
    View,
    /// <summary>属性读取 { "read": { "of": "self"|"viewer", "path": "attack" } }。</summary>
    Read,
    /// <summary>算子应用 { "op": "ge", "args": [...] }。</summary>
    Operator,
}

/// <summary>
/// 表达式的静态类型（SPEC §7）。
/// <para>
/// <see cref="Base"/> 只取契约那六个文本形态：<c>entity</c>／<c>number</c>／<c>string</c>／<c>boolean</c>／
/// <c>list&lt;entity&gt;</c>／<c>list&lt;number&gt;</c>；空串表示「静态不可确定」。
/// </para>
/// <para>
/// <see cref="Subtypes"/> 是 entity 的静态子类型集（<c>unit</c>／<c>hq</c>／<c>card</c>），只用于
/// <c>pin-type</c> 的交集判定，**不进 JSON**——DSL 文本里没有子类型这个概念。
/// </para>
/// </summary>
public readonly record struct HandlerType(string Base, IReadOnlyList<string>? Subtypes = null)
{
    /// <summary>静态不可确定。按 SPEC §7.6，这种情况一律放行，留到运行期判。</summary>
    public static readonly HandlerType Unknown = new("");

    public bool Known => Base.Length > 0;
    public string Display => Known ? Base : "未知";

    public static HandlerType Entity(params string[] subtypes) =>
        new("entity", subtypes.Length == 0 ? null : subtypes);

    public static HandlerType ListOf(string element, IReadOnlyList<string>? subtypes = null) =>
        new("list<" + element + ">", subtypes);

    /// <summary><c>list&lt;T&gt;</c> 的元素类型；不是列表就是未知。</summary>
    public HandlerType Element() =>
        Base.StartsWith("list<", StringComparison.Ordinal)
            ? new HandlerType(Base[5..^1], Subtypes)
            : Unknown;

    public override string ToString() => Display;
}

/// <summary>
/// 引脚上的一棵表达式（SPEC §4 的五个变体，封闭）。
/// 值对象不可变：改一个引脚就换一个新实例，撤销栈才好做。
/// </summary>
public sealed class HandlerValue
{
    private HandlerValue(HandlerValueKind kind) => Kind = kind;

    public HandlerValueKind Kind { get; }
    public double Number { get; private init; }
    public string? Text { get; private init; }
    public bool Flag { get; private init; }
    /// <summary>绑定名 / 视图字段名 / read.path / 算子名。</summary>
    public string? Name { get; private init; }
    /// <summary>
    /// read.of 的**保留字面量**形态：self / viewer。
    /// <para>与 <see cref="ReadTarget"/> 恰取其一（SPEC §4.5「两种形态，恰取其一」）。</para>
    /// </summary>
    public string? ReadOf { get; private init; }
    /// <summary>
    /// read.of 的**实体表达式**形态：能推导出 <c>entity</c> 的值（典型是 <c>iterate</c> 的元素）。
    /// <para>这是「读被遍历的那个单位」的唯一入口，2026-11-01 版契约新增。</para>
    /// </summary>
    public HandlerValue? ReadTarget { get; private init; }
    public IReadOnlyList<HandlerValue> Args { get; private init; } = [];

    /// <summary>字面量的类型名：number / string / boolean。</summary>
    public string LiteralType { get; private init; } = "number";
    public static HandlerValue Num(double v) => new(HandlerValueKind.Literal) { Number = v, LiteralType = "number" };
    public static HandlerValue Str(string v) => new(HandlerValueKind.Literal) { Text = v, LiteralType = "string" };
    public static HandlerValue Bool(bool v) => new(HandlerValueKind.Literal) { Flag = v, LiteralType = "boolean" };
    public static HandlerValue Var(string name) => new(HandlerValueKind.Binding) { Name = name };
    public static HandlerValue ViewField(string field) => new(HandlerValueKind.View) { Name = field };
    public static HandlerValue Read(string of, string path) => new(HandlerValueKind.Read) { ReadOf = of, Name = path };

    /// <summary>读**某个实体**自身的属性：<c>{ "read": { "of": { "var": "t" }, "path": "defense" } }</c>。</summary>
    public static HandlerValue ReadFrom(HandlerValue target, string path) =>
        new(HandlerValueKind.Read) { ReadTarget = target, Name = path };
    public static HandlerValue Call(string op, params HandlerValue[] args) =>
        new(HandlerValueKind.Operator) { Name = op, Args = args };

    public bool IsLiteral => Kind == HandlerValueKind.Literal;

    /// <summary>
    /// SPEC §7：类型只由节点本身决定、与上下文无关，不做双向推断。
    /// 绑定／视图／读取要查作用域与目录，由校验器解析；这里只管字面量与算子。
    /// </summary>
    public HandlerType InferType()
    {
        switch (Kind)
        {
            case HandlerValueKind.Literal: return new HandlerType(LiteralType);
            case HandlerValueKind.Operator:
                return Name switch
                {
                    "ge" or "gt" or "le" or "lt" or "eq" or "ne" or "and" or "or" or "not" or "empty" => new("boolean"),
                    "add" or "sub" or "mul" or "count" or "sum" or "max" or "min" => new("number"),
                    _ => HandlerType.Unknown,
                };
            default: return HandlerType.Unknown;
        }
    }

    public HandlerValue WithArg(int index, HandlerValue arg)
    {
        if (Kind != HandlerValueKind.Operator) return this;
        var args = Args.ToArray();
        if (index < 0 || index >= args.Length) return this;
        args[index] = arg;
        return new HandlerValue(HandlerValueKind.Operator) { Name = Name, Args = args, LiteralType = "number" };
    }

    public string Describe()
    {
        switch (Kind)
        {
            case HandlerValueKind.Literal:
                if (Text is not null) return "\"" + Text + "\"";
                return Flag ? "true" : Number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            case HandlerValueKind.Binding: return "‹" + Name + "›";
            case HandlerValueKind.View: return HandlerVocabulary.ViewFieldLabel(Name ?? "");
            case HandlerValueKind.Read:
                // 界面文字：属性名翻中文（attack → 攻击力），否则作者读到的是一串驼峰英文。
                var what = HandlerVocabulary.ReadableLabel(Name ?? "");
                return ReadTarget is null
                    ? (ReadOf == "self" ? "自己的" : "我方的") + what
                    : ReadTarget.Describe() + "的" + what;
            case HandlerValueKind.Operator:
                return Name + "(" + string.Join(", ", Args.Select(a => a.Describe())) + ")";
            default: return "?";
        }
    }

    public JsonNode ToJson()
    {
        switch (Kind)
        {
            case HandlerValueKind.Literal:
                if (Text is not null) return new JsonObject { ["lit"] = Text };
                if (Flag) return new JsonObject { ["lit"] = true };
                return new JsonObject { ["lit"] = Number };
            case HandlerValueKind.Binding:
                return new JsonObject { ["var"] = Name };
            case HandlerValueKind.View:
                return new JsonObject { ["view"] = Name };
            case HandlerValueKind.Read:
                return new JsonObject
                {
                    ["read"] = new JsonObject
                    {
                        ["of"] = ReadTarget is null ? JsonValue.Create(ReadOf) : ReadTarget.ToJson(),
                        ["path"] = Name,
                    },
                };
            default:
                var args = new JsonArray();
                foreach (var a in Args) args.Add(a.ToJson());
                return new JsonObject { ["op"] = Name, ["args"] = args };
        }
    }

    /// <summary>
    /// 表达式反序列化（SPEC §4 的五个变体）。判别键是 <c>lit</c>／<c>var</c>／<c>view</c>／<c>read</c>／<c>op</c>，
    /// 恰有一个——这正是 <see cref="ToJson"/> 写出去的形态。
    /// </summary>
    /// <exception cref="FormatException">不是五个变体之一。</exception>
    public static HandlerValue FromJson(JsonNode? node)
    {
        if (node is not JsonObject json)
            throw new FormatException("表达式必须是对象：" + (node?.ToJsonString() ?? "null"));

        if (json["lit"] is { } literal)
        {
            switch (literal.GetValueKind())
            {
                case System.Text.Json.JsonValueKind.String: return Str(literal.GetValue<string>());
                case System.Text.Json.JsonValueKind.True: return Bool(true);
                case System.Text.Json.JsonValueKind.False: return Bool(false);
                case System.Text.Json.JsonValueKind.Number: return Num(literal.GetValue<double>());
                default: throw new FormatException("lit 只能是字符串、数字或布尔：" + literal.ToJsonString());
            }
        }

        if (json["var"] is { } binding) return Var(binding.GetValue<string>());
        if (json["view"] is { } view) return ViewField(view.GetValue<string>());

        if (json["read"] is JsonObject read)
        {
            var path = read["path"]?.GetValue<string>()
                ?? throw new FormatException("read 缺少 path：" + json.ToJsonString());
            // of 有两种形态：保留字面量（self／viewer）或实体表达式（读被遍历的那个单位）。
            return read["of"] is JsonObject target
                ? ReadFrom(FromJson(target), path)
                : Read(read["of"]?.GetValue<string>() ?? "self", path);
        }

        if (json["op"] is { } op)
        {
            var args = json["args"] is JsonArray array ? array.Select(FromJson).ToArray() : [];
            return Call(op.GetValue<string>(), args);
        }

        throw new FormatException("表达式缺少判别键（lit／var／view／read／op）：" + json.ToJsonString());
    }
}

/// <summary>
/// <c>choice</c> 的一个选项：一句给玩家看的话 ＋ 选它之后做的一串动作。
/// <para>
/// 它**不是**普通的动作数组，所以不能塞进 <see cref="HandlerNode.Bodies"/>——
/// 契约里 <c>options</c> 是 <c>[{ label, actions }]</c>，硬塞会导出错误结构（SPEC §3）。
/// </para>
/// </summary>
public sealed class HandlerOption
{
    public HandlerOption(string label) => Label = label;

    public string Label { get; set; }
    public List<HandlerNode> Actions { get; } = [];

    public HandlerOption Clone()
    {
        var copy = new HandlerOption(Label);
        foreach (var node in Actions) copy.Actions.Add(node.Clone());
        return copy;
    }
}

/// <summary>
/// 一个动作节点。字段分三类存放：值引脚、文本/枚举引脚、子动作数组。
/// 这样「加一个节点」只是照着目录建一张表，不需要为每个 op 写一遍界面。
/// </summary>
public sealed class HandlerNode
{
    public HandlerNode(string op) => Op = op;

    public string Op { get; set; }
    public Dictionary<string, HandlerValue> Values { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<HandlerNode>> Bodies { get; } = new(StringComparer.Ordinal);
    /// <summary>只有 <c>choice</c> 用；其余节点为空。</summary>
    public List<HandlerOption> Options { get; } = [];

    public List<HandlerNode> Body(string field)
    {
        if (!Bodies.TryGetValue(field, out var list))
            Bodies[field] = list = [];
        return list;
    }

    public HandlerValue? Value(string pin) => Values.GetValueOrDefault(pin);
    public string? Text(string pin) => Texts.GetValueOrDefault(pin);

    public HandlerNode Set(string pin, HandlerValue? value)
    {
        if (value is null) Values.Remove(pin);
        else Values[pin] = value;
        return this;
    }

    public HandlerNode SetText(string pin, string? text)
    {
        if (string.IsNullOrEmpty(text)) Texts.Remove(pin);
        else Texts[pin] = text;
        return this;
    }

    public HandlerNode Clone()
    {
        var copy = new HandlerNode(Op);
        foreach (var (k, v) in Values) copy.Values[k] = v;
        foreach (var (k, v) in Texts) copy.Texts[k] = v;
        foreach (var (k, v) in Bodies) copy.Bodies[k] = v.Select(n => n.Clone()).ToList();
        foreach (var option in Options) copy.Options.Add(option.Clone());
        return copy;
    }

    /// <summary>把节点自己写进 JSON。引脚顺序按目录声明，保证同一结构产出同一段文本。</summary>
    public JsonObject ToJson(HandlerCatalog catalog)
    {
        var spec = catalog.Find(Op);
        var json = new JsonObject { ["op"] = Op };

        // script 的 inputs／outputs 在契约（SPEC §3 字段表）里是**必填数组**，即使为空也要写出来。
        // v1 界面不编辑它们，所以按契约顺序先放空数组占位——漏掉会被后端按 pin-missing 拒绝。
        if (Op == "script")
        {
            json["inputs"] = new JsonArray();
            json["outputs"] = new JsonArray();
        }

        if (spec is not null)
        {
            foreach (var pin in spec.Inputs)
            {
                if (pin.Kind == HandlerPinKind.Value)
                {
                    if (Values.TryGetValue(pin.Name, out var v)) json[pin.Name] = v.ToJson();
                }
                else if (Texts.TryGetValue(pin.Name, out var t) && t.Length > 0)
                {
                    json[pin.Name] = t;
                }
            }
            // 目录里没声明的字段也照写（避免编辑器把作者的额外声明吃掉）
            foreach (var (k, v) in Values) if (spec.Pin(k) is null) json[k] = v.ToJson();
            foreach (var (k, v) in Texts) if (spec.Pin(k) is null && v.Length > 0) json[k] = v;
        }
        else
        {
            foreach (var (k, v) in Values) json[k] = v.ToJson();
            foreach (var (k, v) in Texts) if (v.Length > 0) json[k] = v;
        }

        // choice 的 options 是 [{ label, actions }]，不是普通动作数组，单独写（SPEC §3）
        if (Op == "choice")
        {
            var options = new JsonArray();
            foreach (var option in Options)
            {
                var item = new JsonObject { ["label"] = option.Label };
                var acts = new JsonArray();
                foreach (var child in option.Actions) acts.Add(child.ToJson(catalog));
                item["actions"] = acts;
                options.Add(item);
            }
            json["options"] = options;
            return json;
        }

        foreach (var field in spec?.Bodies ?? ["actions", "then", "else"])
        {
            if (!Bodies.TryGetValue(field, out var list)) continue;
            // else 为空必须整段省略：空数组与「没有 else」语义相同却产出两种文本（SPEC §3）
            if (field == "else" && list.Count == 0) continue;
            var arr = new JsonArray();
            foreach (var child in list) arr.Add(child.ToJson(catalog));
            json[field] = arr;
        }
        return json;
    }

    /// <summary>
    /// 动作节点反序列化（<see cref="ToJson"/> 的逆）。
    /// <para>
    /// 三类字段靠 JSON 形状区分，不靠目录：数组＝子动作、对象＝值引脚、字符串＝文本引脚。
    /// 目录只用来判断哪个数组是子动作——所以目录里没声明的字段也不会被吃掉（与
    /// <see cref="ToJson"/> 的「额外声明照写」对称）。
    /// </para>
    /// </summary>
    /// <exception cref="FormatException">不是动作对象／缺少 op。</exception>
    public static HandlerNode FromJson(JsonNode? node, HandlerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (node is not JsonObject json)
            throw new FormatException("动作必须是对象：" + (node?.ToJsonString() ?? "null"));

        var op = json["op"]?.GetValue<string>()
            ?? throw new FormatException("动作缺少 op：" + json.ToJsonString());
        var result = new HandlerNode(op);

        foreach (var (key, value) in json)
        {
            if (key == "op") continue;

            // choice 的 options 是 [{ label, actions }]，不是普通动作数组（SPEC §3）。
            if (op == "choice" && key == "options")
            {
                if (value is not JsonArray options) continue;
                foreach (var item in options)
                {
                    if (item is not JsonObject option) continue;
                    var choice = new HandlerOption(option["label"]?.GetValue<string>() ?? "");
                    if (option["actions"] is JsonArray acts)
                        foreach (var child in acts) choice.Actions.Add(FromJson(child, catalog));
                    result.Options.Add(choice);
                }
                continue;
            }

            if (value is JsonArray array)
            {
                var body = result.Body(key);
                foreach (var child in array) body.Add(FromJson(child, catalog));
                continue;
            }

            if (value is JsonObject expression)
            {
                result.Set(key, HandlerValue.FromJson(expression));
                continue;
            }

            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                result.SetText(key, text);
        }

        return result;
    }
}

/// <summary>一份 handler DSL 文档：<c>{ schemaVersion, catalogRevision?, actions[] }</c>。</summary>
public sealed class HandlerDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int? CatalogRevision { get; set; }
    public List<HandlerNode> Actions { get; } = [];

    public HandlerDocument Clone()
    {
        var copy = new HandlerDocument { SchemaVersion = SchemaVersion, CatalogRevision = CatalogRevision };
        foreach (var n in Actions) copy.Actions.Add(n.Clone());
        return copy;
    }

    public JsonObject ToJson(HandlerCatalog catalog)
    {
        var json = new JsonObject { ["schemaVersion"] = SchemaVersion };
        if (CatalogRevision is { } rev) json["catalogRevision"] = rev;
        var arr = new JsonArray();
        foreach (var n in Actions) arr.Add(n.ToJson(catalog));
        json["actions"] = arr;
        return json;
    }

    public string ToJsonText(HandlerCatalog catalog) =>
        ToJson(catalog).ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <summary>
    /// 把一份 DSL 文本读回文档（<see cref="ToJson"/> 的逆）。
    /// <para>
    /// 为什么必须有：覆盖一个已有效果时，画布要显示作者**上次搭的图**。没有这条，
    /// 「改一个效果」就退化成「用一份空白文档覆盖它」——那是数据丢失，不是编辑。
    /// </para>
    /// <para>
    /// 不认识的东西一律抛 <see cref="FormatException"/>，不做「尽力而为」的猜测：
    /// 猜错的图会让作者在不知情的情况下把结构改掉，而保存是覆盖式的。
    /// </para>
    /// </summary>
    /// <exception cref="FormatException">不是合法的 DSL 文档。</exception>
    public static HandlerDocument FromJson(string text, HandlerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("DSL 文档的根必须是对象");
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new FormatException("DSL 文档不是合法 JSON：" + error.Message, error);
        }

        var document = new HandlerDocument
        {
            SchemaVersion = root["schemaVersion"] is JsonValue version && version.TryGetValue<int>(out var schema)
                ? schema
                : CurrentSchemaVersion,
            CatalogRevision = root["catalogRevision"] is JsonValue revision && revision.TryGetValue<int>(out var rev)
                ? rev
                : null,
        };

        if (root["actions"] is JsonArray actions)
        {
            foreach (var node in actions) document.Actions.Add(HandlerNode.FromJson(node, catalog));
        }

        return document;
    }

    /// <summary>把「我在这份文档里改了什么」做成可撤销的一步。</summary>
    /// <summary>
    /// 样例：找出敌方单位 → 逐个处理 → 若它防御 ≤ 2 就打 3，否则打 1。
    /// <para>
    /// 刻意用上 2026-11-01 版契约才有的两件事：只读查询 <c>unitsOf</c>、
    /// 以及 <c>read.of</c> 接受实体表达式（读**被遍历的那个单位**自己的防御）。
    /// 这两条在旧版契约下都写不出来——所以样例本身就是契约能力的证据。
    /// </para>
    /// </summary>
    public static HandlerDocument Sample()
    {
        var doc = new HandlerDocument();

        doc.Actions.Add(new HandlerNode("unitsOf")
            .Set("side", HandlerValue.Str("enemy"))
            .SetText("bind", "foes"));

        var iterate = new HandlerNode("iterate")
            .Set("over", HandlerValue.Var("foes"))
            .SetText("as", "f");

        var branch = new HandlerNode("branch")
            .Set("test", HandlerValue.Call("le",
                HandlerValue.ReadFrom(HandlerValue.Var("f"), "defense"), HandlerValue.Num(2)));
        branch.Body("then").Add(new HandlerNode("damage")
            .Set("target", HandlerValue.Var("f")).Set("amount", HandlerValue.Num(3)));
        branch.Body("else").Add(new HandlerNode("damage")
            .Set("target", HandlerValue.Var("f")).Set("amount", HandlerValue.Num(1)));

        iterate.Body("actions").Add(branch);
        doc.Actions.Add(iterate);
        return doc;
    }
}
