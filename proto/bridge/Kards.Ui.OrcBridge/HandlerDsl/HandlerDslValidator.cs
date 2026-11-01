namespace Kards.Ui.OrcBridge.HandlerDsl;

/// <summary>预制体成员声明的视图字段。<c>Writable</c> 是 <c>write</c> 节点可写性的唯一依据。</summary>
public sealed record HandlerViewField(string Name, string Type, bool Writable);

public sealed record HandlerIssue(string Code, string NodePath, string Message, HandlerNode? Node = null, string? Pin = null)
{
    public override string ToString() => Code + " @" + NodePath + " · " + Message;
}

/// <summary>
/// 本地校验器（SPEC §14 的 13 个码）。
/// <para>
/// 为什么必须在前端做：作者改一次图就等一次后端，编辑体验会碎掉（FRONTEND-GUIDE §1）。
/// 后端装载期会再判一次，那是最终裁定；两边的码与节点路径必须一致，由契约包的 vectors/check 守护。
/// </para>
/// </summary>
public static class HandlerDslValidator
{
    private static readonly Dictionary<string, (int Arity, string Result)> Operators = new(StringComparer.Ordinal)
    {
        ["add"] = (2, "number"), ["sub"] = (2, "number"), ["mul"] = (2, "number"),
        ["eq"] = (2, "boolean"), ["ne"] = (2, "boolean"),
        ["lt"] = (2, "boolean"), ["le"] = (2, "boolean"), ["gt"] = (2, "boolean"), ["ge"] = (2, "boolean"),
        ["and"] = (2, "boolean"), ["or"] = (2, "boolean"), ["not"] = (1, "boolean"),
        ["count"] = (1, "number"), ["sum"] = (1, "number"), ["max"] = (1, "number"), ["min"] = (1, "number"),
        ["empty"] = (1, "boolean"),
    };

    /// <summary>算子表是封闭的；除法/取模/幂一律 expr-op-unknown（SPEC §5.1）。</summary>
    public static IReadOnlyCollection<string> OperatorNames => Operators.Keys;

    /// <summary>算子的结果类型名（只给界面分组用；判定仍以 <see cref="Validate"/> 为准）。</summary>
    public static string OperatorResult(string op) =>
        Operators.TryGetValue(op, out var info) ? info.Result : "unknown";

    /// <summary>算子的元数（界面插入算子时用来补默认参数）。</summary>
    public static int OperatorArity(string op) =>
        Operators.TryGetValue(op, out var info) ? info.Arity : 0;

    /// <summary>
    /// 词法作用域（SPEC §8）：名字 → 静态类型。顺序保留，用来判重名并按声明点报错。
    /// <para>
    /// 为什么要带类型：<c>iterate.as</c> 的元素类型、<c>ask</c> 结果的子类型都只能从声明处传下来，
    /// 光记名字的话 <c>pin-type</c> 就只能靠猜（SPEC §7.5）。
    /// </para>
    /// </summary>
    private sealed class Scope
    {
        private readonly List<string> _order = [];
        private readonly Dictionary<string, HandlerType> _types = new(StringComparer.Ordinal);

        public bool Contains(string name) => _types.ContainsKey(name);

        public HandlerType TypeOf(string name) =>
            _types.TryGetValue(name, out var type) ? type : HandlerType.Unknown;

        /// <summary>声明一个名字；已存在返回 false（调用方负责报 bind-scope）。</summary>
        public bool Declare(string name, HandlerType type)
        {
            if (!_types.TryAdd(name, type)) return false;
            _order.Add(name);
            return true;
        }

        public Scope Clone()
        {
            var copy = new Scope();
            foreach (var name in _order) copy.Declare(name, _types[name]);
            return copy;
        }
    }

    public static IReadOnlyList<HandlerIssue> Validate(
        HandlerDocument doc,
        HandlerCatalog catalog,
        IReadOnlyDictionary<string, HandlerViewField>? view = null)
    {
        var issues = new List<HandlerIssue>();
        view ??= new Dictionary<string, HandlerViewField>(StringComparer.Ordinal);

        if (doc.SchemaVersion != HandlerDocument.CurrentSchemaVersion)
            issues.Add(new HandlerIssue("schema-version", "$",
                "这份图是 schemaVersion " + doc.SchemaVersion + " 画的，当前契约是 " + HandlerDocument.CurrentSchemaVersion));

        Walk(doc.Actions, "actions", new Scope(), 0, 1);

        return issues;

        void Walk(List<HandlerNode> list, string basePath, Scope scope, int loopDepth, int depth)
        {
            if (depth > 64)
            {
                issues.Add(new HandlerIssue("graph-shape", basePath, "嵌套超过 64 层"));
                return;
            }

            var local = scope.Clone();
            for (var i = 0; i < list.Count; i++)
            {
                var node = list[i];
                var path = basePath + "[" + i + "]";
                var spec = catalog.Find(node.Op);
                if (spec is null)
                {
                    issues.Add(new HandlerIssue("node-unknown", path, "未知节点 " + node.Op, node));
                    continue;
                }

                var pinTypes = CheckPins(node, spec, path, local, view);

                if ((node.Op == "ask" || node.Op == "choice") && loopDepth > 0)
                    issues.Add(new HandlerIssue("ask-in-loop", path, "选择类节点不能放在循环里", node));

                if (node.Op == "write")
                {
                    var field = node.Text("field") ?? "";
                    if (!view.TryGetValue(field, out var vf))
                        issues.Add(new HandlerIssue("write-forbidden", path + ".field",
                            "字段「" + field + "」不在视图声明里", node, "field"));
                    else if (!vf.Writable)
                        issues.Add(new HandlerIssue("write-forbidden", path + ".field",
                            "字段「" + field + "」是只读的", node, "field"));
                }

                if (node.Op == "loop") CheckLoopBound("times", "循环必须指定次数（number）");
                if (node.Op == "while") CheckLoopBound("limit", "循环必须指定上限（number）");

                void CheckLoopBound(string pin, string message)
                {
                    // SPEC §9.1：上限**缺失或不是 number** 都落 loop-unbounded，不落 pin-type
                    // （契约 check 向量 017 守这条）。所以 CheckPins 里跳过了这两个引脚的类型判定。
                    var value = node.Value(pin);
                    var type = pinTypes.GetValueOrDefault(pin, HandlerType.Unknown);
                    if (value is null || type.Known && type.Base != "number")
                        issues.Add(new HandlerIssue("loop-unbounded", path + "." + pin, message, node, pin));
                }

                if (node.Op == "branch")
                {
                    if (!node.Bodies.TryGetValue("then", out var then) || then.Count == 0)
                        issues.Add(new HandlerIssue("graph-shape", path + ".then", "「那么」分支不能为空", node, "then"));
                }

                if (node.Op == "choice")
                {
                    // options 是 [{ label, actions }]：每个选项的动作处在**同一作用域层级**（SPEC §3）
                    if (node.Options.Count == 0)
                        issues.Add(new HandlerIssue("pin-missing", path + ".options",
                            "至少要给一个选项", node, "options"));
                    for (var j = 0; j < node.Options.Count; j++)
                    {
                        var option = node.Options[j];
                        var optionPath = path + ".options[" + j + "]";
                        if (string.IsNullOrEmpty(option.Label))
                            issues.Add(new HandlerIssue("pin-missing", optionPath + ".label",
                                "选项上要写一句给玩家看的话", node, "options"));
                        Walk(option.Actions, optionPath + ".actions", local.Clone(), loopDepth + 1, depth + 1);
                    }
                }
                else
                {
                    // 子体：先按作用域规则把名字放进去，再往下走
                    foreach (var field in spec.Bodies)
                    {
                        if (!node.Bodies.TryGetValue(field, out var body)) continue;
                        var inner = local.Clone();
                        if (node.Op == "iterate" && !string.IsNullOrEmpty(node.Text("as")))
                            inner.Declare(node.Text("as")!, OverElement(pinTypes));
                        Walk(body, path + "." + field, inner, loopDepth + 1, depth + 1);
                    }
                }

                // 声明在「本节点之后」才对兄弟可见
                if (node.Op == "let" && !string.IsNullOrEmpty(node.Text("name")))
                    DeclareBinding(node.Text("name")!, path + ".name", "name",
                        pinTypes.GetValueOrDefault("value", HandlerType.Unknown));

                if (node.Op == "ask" && !string.IsNullOrEmpty(node.Text("bind")))
                    DeclareBinding(node.Text("bind")!, path + ".bind", "bind", AskResultType(node));

                // bind 是**节点级字段**，不是引脚（SPEC §3）。形态按 §8.9 判：
                // 无输出不能写；单输出必须是字符串；多输出必须是对象（v1 界面不编辑对象形态，这里如实报出来）。
                if (!spec.BuiltIn && node.Text("bind") is { Length: > 0 } outputBind)
                {
                    if (spec.Outputs.Count == 0)
                        issues.Add(new HandlerIssue("bind-scope", path + ".bind",
                            "节点 " + node.Op + " 没有返回值，不能写 bind（不写 bind ＝忽略返回值）", node, "bind"));
                    else if (spec.Outputs.Count > 1)
                        issues.Add(new HandlerIssue("bind-scope", path + ".bind",
                            "节点 " + node.Op + " 有 " + spec.Outputs.Count + " 个输出，bind 必须写成对象（键集＝输出名）",
                            node, "bind"));
                    else
                        DeclareBinding(outputBind, path + ".bind", "bind", new HandlerType(spec.Outputs[0].Type));
                }

                void DeclareBinding(string name, string bindPath, string pin, HandlerType type)
                {
                    if (!IsValidName(name))
                        issues.Add(new HandlerIssue("bind-scope", bindPath, "名字「" + name + "」不合法", node, pin));
                    else if (name.StartsWith("__", StringComparison.Ordinal))
                        issues.Add(new HandlerIssue("bind-scope", bindPath,
                            "不能以 __ 开头（生成器保留前缀）", node, pin));
                    else if (view.ContainsKey(name))
                        issues.Add(new HandlerIssue("bind-scope", bindPath,
                            "名字「" + name + "」与视图字段同名；绑定与视图字段不得重名", node, pin));
                    else if (!local.Declare(name, type))
                        issues.Add(new HandlerIssue("bind-scope", bindPath, "名字「" + name + "」已经存在", node, pin));
                }

                if (node.Op == "iterate")
                {
                    var name = node.Text("as") ?? "";
                    if (name.Length == 0 || !IsValidName(name))
                        issues.Add(new HandlerIssue("bind-scope", path + ".as", "元素名不合法", node, "as"));
                    else if (name.StartsWith("__", StringComparison.Ordinal))
                        issues.Add(new HandlerIssue("bind-scope", path + ".as", "不能以 __ 开头", node, "as"));
                    else if (local.Contains(name))
                        issues.Add(new HandlerIssue("bind-scope", path + ".as", "名字「" + name + "」已经存在", node, "as"));
                }
            }
        }

        Dictionary<string, HandlerType> CheckPins(HandlerNode node, HandlerNodeSpec spec, string path, Scope scope,
            IReadOnlyDictionary<string, HandlerViewField> viewFields)
        {
            var pinTypes = new Dictionary<string, HandlerType>(StringComparer.Ordinal);

            foreach (var pin in spec.Inputs)
            {
                var pinPath = path + "." + pin.Name;
                if (pin.Kind == HandlerPinKind.Value)
                {
                    var value = node.Value(pin.Name);
                    if (value is null)
                    {
                        if (pin.Required)
                            issues.Add(new HandlerIssue("pin-missing", pinPath,
                                "「" + spec.PinLabel(pin.Name) + "」还没有值", node, pin.Name));
                        continue;
                    }
                    var type = CheckExpr(value, pinPath, scope, viewFields);
                    pinTypes[pin.Name] = type;
                    // 循环上限走 loop-unbounded（SPEC §9.1 / 向量 017），不在这里报 pin-type
                    if (!IsLoopBoundPin(node.Op, pin.Name) && !IsAssignableTo(type, pin.Type, pin.Subtypes))
                        issues.Add(new HandlerIssue("pin-type", pinPath, PinTypeMessage(pin, type), node, pin.Name));
                }
                else if (pin.Required && string.IsNullOrEmpty(node.Text(pin.Name)))
                {
                    issues.Add(new HandlerIssue("pin-missing", pinPath,
                        "「" + spec.PinLabel(pin.Name) + "」还没有填", node, pin.Name));
                }
            }

            // 目录未声明的引脚。`bind` 是节点级字段（SPEC §3），不在 Inputs 里声明，别把它报成引脚。
            foreach (var key in node.Values.Keys)
                if (spec.Pin(key) is null)
                    issues.Add(new HandlerIssue("pin-unknown", path + "." + key,
                        "节点 " + node.Op + " 没有引脚 " + key, node, key));
            foreach (var key in node.Texts.Keys)
                if (key != "bind" && spec.Pin(key) is null)
                    issues.Add(new HandlerIssue("pin-unknown", path + "." + key,
                        "节点 " + node.Op + " 没有引脚 " + key, node, key));

            return pinTypes;
        }

        HandlerType CheckExpr(HandlerValue value, string path, Scope scope,
            IReadOnlyDictionary<string, HandlerViewField> viewFields)
        {
            switch (value.Kind)
            {
                case HandlerValueKind.Binding:
                    if (!scope.Contains(value.Name ?? ""))
                    {
                        issues.Add(new HandlerIssue("bind-scope", path,
                            "名字「" + value.Name + "」在这里不可见"));
                        return HandlerType.Unknown;
                    }
                    return scope.TypeOf(value.Name!);

                case HandlerValueKind.View:
                    if (!viewFields.TryGetValue(value.Name ?? "", out var field))
                    {
                        issues.Add(new HandlerIssue("pin-unknown", path,
                            "视图里没有字段「" + value.Name + "」"));
                        return HandlerType.Unknown;
                    }
                    // SPEC §7.5：视图字段的实体类型只知道是 entity，子类型未知。
                    return new HandlerType(field.Type);

                case HandlerValueKind.Read:
                {
                    // SPEC §4.5 / §12.8：of 两种形态恰取其一——保留字面量，或能推导出 entity 的表达式。
                    IReadOnlyList<string> kinds;
                    if (value.ReadTarget is { } target)
                    {
                        var targetType = CheckExpr(target, path + ".read.of", scope, viewFields);
                        if (targetType.Known && targetType.Base != "entity")
                        {
                            issues.Add(new HandlerIssue("pin-type", path + ".read.of",
                                "read.of 的实体表达式必须推导为 entity，当前是 " + targetType.Display));
                            return HandlerType.Unknown;
                        }
                        // 静态子类型决定能读哪一类；未知 ⇒ 退回 unit∪hq
                        kinds = targetType.Subtypes is { Count: > 0 } ? targetType.Subtypes : HandlerCatalog.UnitOrHq;
                    }
                    else if (value.ReadOf is "self" or "viewer")
                    {
                        kinds = HandlerCatalog.KindsForLiteral(value.ReadOf);
                    }
                    else
                    {
                        issues.Add(new HandlerIssue("pin-unknown", path + ".read.of",
                            "read.of 只接受 self／viewer 或实体表达式，当前是「" + value.ReadOf + "」"));
                        return HandlerType.Unknown;
                    }

                    var readPath = value.Name ?? "";
                    var readable = catalog.ReadableType(kinds, readPath);
                    if (readable is null)
                    {
                        // 复用 pin-unknown，不新增码；路径指向 .read.path（契约 check 向量 019／022）
                        issues.Add(new HandlerIssue("pin-unknown", path + ".read.path",
                            catalog.KnowsReadable(readPath)
                                ? "「" + readPath + "」在 " + string.Join("／", kinds) + " 上没有定义（读取目标种类不匹配）"
                                : "可读项里没有 " + readPath));
                        return HandlerType.Unknown;
                    }
                    return new HandlerType(readable);
                }

                case HandlerValueKind.Operator:
                    if (!Operators.TryGetValue(value.Name ?? "", out var op))
                    {
                        issues.Add(new HandlerIssue("expr-op-unknown", path, "算子 " + value.Name + " 不在封闭表里"));
                        return HandlerType.Unknown;
                    }
                    if (value.Args.Count != op.Arity)
                    {
                        issues.Add(new HandlerIssue("expr-arity", path,
                            "算子 " + value.Name + " 要 " + op.Arity + " 个参数，当前 " + value.Args.Count + " 个"));
                        return new HandlerType(op.Result);
                    }
                    for (var i = 0; i < value.Args.Count; i++)
                    {
                        var argType = CheckExpr(value.Args[i], path + ".args[" + i + "]", scope, viewFields);
                        if (!argType.Known) continue;
                        if (!ArgAccepted(value.Name!, op.Arity, i, argType.Base))
                            issues.Add(new HandlerIssue("expr-type", path + ".args[" + i + "]",
                                "算子 " + value.Name + " 不接受 " + argType.Display));
                    }
                    return new HandlerType(op.Result);

                default:
                    return value.InferType();
            }
        }
    }

    /// <summary>SPEC §7.5：<c>ask</c> 的结果恒为 <c>list&lt;entity&gt;</c>，静态子类型就是它的 <c>kind</c>。</summary>
    private static HandlerType AskResultType(HandlerNode node)
    {
        var kind = node.Text("kind");
        return kind is "unit" or "hq" or "card"
            ? HandlerType.ListOf("entity", [kind])
            : HandlerType.ListOf("entity");
    }

    /// <summary><c>iterate.as</c> 的类型＝<c>over</c> 的元素类型；<c>over</c> 静态不可确定时也就不可确定。</summary>
    private static HandlerType OverElement(Dictionary<string, HandlerType> pinTypes) =>
        pinTypes.TryGetValue("over", out var over) ? over.Element() : HandlerType.Unknown;

    /// <summary>循环上限的两个引脚由 <c>loop-unbounded</c> 统一报，不在引脚层报 <c>pin-type</c>。</summary>
    private static bool IsLoopBoundPin(string op, string pin) =>
        (op == "loop" && pin == "times") || (op == "while" && pin == "limit");

    /// <summary>引脚类型不符时的提示。列表接单实体是最常见的一种，单独说清楚该怎么改。</summary>
    private static string PinTypeMessage(HandlerPinSpec pin, HandlerType actual)
    {
        if (pin.Type == "entity" && actual.Base == "list<entity>")
            return "实体引脚只收单个实体，这里给的是 list<entity>；要遍历请先用「逐个处理」";
        return "需要 " + pin.Type + "，当前是 " + actual.Display;
    }

    private static bool ArgAccepted(string op, int arity, int index, string type)
    {
        switch (op)
        {
            case "add": case "sub": case "mul":
            case "lt": case "le": case "gt": case "ge":
                return type == "number";
            case "and": case "or": case "not":
                return type == "boolean";
            case "eq": case "ne":
                return type is "entity" or "number" or "string" or "boolean";
            case "count": case "empty":
                return type.StartsWith("list<", StringComparison.Ordinal);
            case "sum": case "max": case "min":
                return type == "list<number>";
            default:
                return true;
        }
    }

    /// <summary>
    /// SPEC §7.6 的 <c>IsAssignableTo</c>：基类型必须**完全相同**（<c>list&lt;T&gt;</c> 比到元素）；
    /// <c>entity</c> 两侧都有子类型集且交集为空＝不接受；任一侧未知＝放行（留到运行期判）。
    /// </summary>
    private static bool IsAssignableTo(HandlerType actual, string expectedBase, IReadOnlyList<string>? expectedSubtypes)
    {
        if (!actual.Known) return true;
        if (actual.Base != expectedBase) return false;
        if (expectedBase is not ("entity" or "list<entity>")) return true;

        if (actual.Subtypes is not { Count: > 0 }) return true;
        if (expectedSubtypes is not { Count: > 0 }) return true;
        return actual.Subtypes.Any(expectedSubtypes.Contains);
    }

    private static bool IsValidName(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_');
}
