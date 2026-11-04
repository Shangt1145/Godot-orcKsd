using System.Collections.Generic;

namespace Kards.Ui.OrcBridge.HandlerDsl;

/// <summary>引脚在编辑面上的形态。目录只声明类型，这里把「怎么填」补上。</summary>
public enum HandlerPinKind
{
    /// <summary>表达式引脚：字面量／绑定／视图／读取／算子。</summary>
    Value,
    /// <summary>纯文案（ask.prompt、script.body）。</summary>
    Text,
    /// <summary>绑定名（ask.bind、iterate.as、let.name）。</summary>
    Bind,
    /// <summary>封闭取值（目录给了 EnumValues，或内建 op 的枚举）。</summary>
    Enum,
    /// <summary>视图字段名（write.field），候选来自预制体的视图声明。</summary>
    Field,
}

public sealed record HandlerPinSpec(
    string Name,
    string Type,
    bool Required,
    IReadOnlyList<string>? EnumValues = null,
    IReadOnlyList<string>? Subtypes = null,
    HandlerPinKind Kind = HandlerPinKind.Value)
{
    /// <summary>
    /// 文本型枚举引脚（<c>ask.kind</c>）：值是**裸字符串字段**，不是表达式。
    /// <para>
    /// 与「值引脚 ＋ 取值集」不同：<c>unitsOf.side</c> 在 DSL 里写成 <c>{"lit":"enemy"}</c>，
    /// 是表达式（见 gen 向量 010），所以它 <see cref="Kind"/> 是 <see cref="HandlerPinKind.Value"/>，
    /// 只是额外带 <see cref="EnumValues"/> 供界面做下拉。
    /// </para>
    /// </summary>
    public bool IsEnum => Kind == HandlerPinKind.Enum;
}

/// <summary>
/// 一个可调用原语：目录动作（引擎扫描得到）或内建 op（契约固定）。
/// <para>
/// 这是 <c>Orc.Game.Effects.HandlerCatalogNodeDto</c> 的镜像。**当前引擎检出尚未含 HandlerDsl 工程**，
/// 所以先用这份镜像跑通界面；引擎合并后由 <c>HandlerCatalogFacade</c> 提供同一形状的数据，
/// 界面层不需要改（见 docs/design/INSPECTOR_SELECTION_20261031.md §6 前置条件）。
/// </para>
/// </summary>
/// <summary>
/// 能力分区（SPEC §12.11／FRONTEND-GUIDE §5.5）：
/// <see cref="Alpha"/>＝这版真能跑；<see cref="Preview"/>＝目录里有、**现在跑不起来**。
/// <para>
/// 判据是**显式登记**，不是自动探针——`choice` 当年编译能过、运行才炸，按「编译一次」判会误判成 alpha。
/// 分区只影响排序与提示，**绝不裁剪**：藏起来会让作者以为这版没这个能力。
/// </para>
/// <para>
/// 2026-11-03（引擎 R38）起 `choice` 接通，契约的 `PreviewOps` 登记表清空：**当前没有任何预览态节点**。
/// 枚举与界面路径保留，因为「下一个跑不起来的 op」迟早还会出现。
/// </para>
/// </summary>
public enum HandlerNodeStage
{
    Alpha,
    Preview,
}

public sealed class HandlerNodeSpec
{
    public HandlerNodeSpec(string name, bool hasAwait, bool hasSideEffect,
        IReadOnlyList<HandlerPinSpec> inputs, IReadOnlyList<HandlerPinSpec> outputs,
        bool builtIn = false, IReadOnlyList<string>? bodies = null,
        HandlerNodeStage stage = HandlerNodeStage.Alpha, string? problem = null)
    {
        Name = name;
        HasAwait = hasAwait;
        HasSideEffect = hasSideEffect;
        Inputs = inputs;
        Outputs = outputs;
        BuiltIn = builtIn;
        Bodies = bodies ?? [];
        Stage = stage;
        Problem = problem;
    }

    public string Name { get; }
    public bool BuiltIn { get; }

    /// <summary>能力分区；缺省 <see cref="HandlerNodeStage.Alpha"/>。</summary>
    public HandlerNodeStage Stage { get; }

    /// <summary>人读原因（<see cref="HandlerNodeStage.Alpha"/> 恒为 null）。</summary>
    public string? Problem { get; }

    /// <summary>现在跑不起来（界面据此置灰＋角标）。</summary>
    public bool IsPreview => Stage == HandlerNodeStage.Preview;
    public bool HasAwait { get; }
    public bool HasSideEffect { get; }
    public IReadOnlyList<HandlerPinSpec> Inputs { get; }
    public IReadOnlyList<HandlerPinSpec> Outputs { get; }
    /// <summary>子动作数组的字段名：actions / then+else / options。</summary>
    public IReadOnlyList<string> Bodies { get; }

    public bool IsContainer => Bodies.Count > 0;
    public string Title => HandlerVocabulary.NodeTitle(Name);
    public string Group => HandlerVocabulary.NodeGroup(Name);
    public HandlerPinSpec? Pin(string name) => Inputs.FirstOrDefault(p => p.Name == name);
    public string PinLabel(string pin) => HandlerVocabulary.PinLabel(Name, pin);
}

/// <summary>
/// 节点目录。真实来源是引擎的 <c>HandlerCatalogFacade.GetCatalog()</c>；
/// 这里内置一份与 <c>contracts/handler-dsl/v1/catalog.snapshot.json</c> 对齐的快照，
/// 用于在引擎合并前把编辑面做出来，也是单元测试的夹具。
/// </summary>
public sealed class HandlerCatalog
{
    private readonly Dictionary<string, HandlerNodeSpec> _byName;

    public HandlerCatalog(int revision, IReadOnlyList<HandlerNodeSpec> nodes,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> readables)
    {
        Revision = revision;
        Nodes = nodes;
        Readables = readables;
        _byName = nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
    }

    public int Revision { get; }
    public IReadOnlyList<HandlerNodeSpec> Nodes { get; }
    /// <summary>
    /// 可读项：**实体种类**（<c>unit</c>／<c>hq</c>／<c>player</c>）→（<c>read.path</c> → 类型名）。
    /// <para>
    /// 2026-11-01 版契约把它从扁平表改成按种类分组：分组维度是「读的是哪一类实体」，
    /// 与 <c>read.of</c> 写成 <c>self</c>／<c>viewer</c> 还是实体表达式无关（SPEC §12.8）。
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Readables { get; }

    /// <summary>某个 path 在**这些种类**里声明的类型；任一种类声明了就取它。</summary>
    public string? ReadableType(IEnumerable<string> kinds, string path)
    {
        foreach (var kind in kinds)
            if (Readables.TryGetValue(kind, out var table) && table.TryGetValue(path, out var type))
                return type;
        return null;
    }

    /// <summary>path 是否在**任何**种类里声明过（用来区分「根本没有这个 path」与「这一类读不到」）。</summary>
    public bool KnowsReadable(string path) =>
        Readables.Values.Any(t => t.ContainsKey(path));

    /// <summary><c>self</c> 的宿主卡是单位还是总部静态未知，取并集（SPEC §12.8 折算表）。</summary>
    public static readonly IReadOnlyList<string> UnitOrHq = ["unit", "hq"];

    /// <summary>
    /// <c>read.of</c> 的保留字面量 → 目标种类集合。
    /// <para>实体表达式不走这里：它的种类由**静态子类型**决定，未知时同样退回 <see cref="UnitOrHq"/>。</para>
    /// </summary>
    public static IReadOnlyList<string> KindsForLiteral(string of) =>
        of == "viewer" ? ["player"] : UnitOrHq;

    public HandlerNodeSpec? Find(string op) => _byName.GetValueOrDefault(op);
    public bool Knows(string op) => _byName.ContainsKey(op);

    /// <summary>
    /// 内建 op 的能力分区（契约顶层 <c>builtInOps</c>，即 <c>HandlerCatalogDto.BuiltInOps</c>）。
    /// <para>
    /// 契约把它们单列一张表，是因为内建 op 由前端硬编码、目录里没有引脚条目，
    /// 而 <c>choice</c> 的运行时实现 <c>ChooseAsync</c> 在排除清单里、扫描器根本扫不到——
    /// 可用性无处可标，只能登记。**这里照抄，不自行推导。**
    /// </para>
    /// <para>
    /// **2026-11-03 起十项全为 <see cref="HandlerNodeStage.Alpha"/>**（引擎 R38）：<c>choice</c> 接通了
    /// 交互通道（有桥走 <c>target.submit</c>、无桥明确失败），契约的 <c>PreviewOps</c> 登记表随之清空。
    /// 注意两张表不是一回事：<c>BuiltInOps</c> 恒有十项（导出时由 <c>PreviewOps</c> 决定各自的 stage），
    /// 清空的是 <c>PreviewOps</c>。机制不拆——将来再出现「语法合法但跑不起来」的 op，登记后这里自然会标出来。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<(string Op, HandlerNodeStage Stage, string? Problem)> BuiltInOps =
    [
        ("sequence", HandlerNodeStage.Alpha, null),
        ("branch", HandlerNodeStage.Alpha, null),
        ("loop", HandlerNodeStage.Alpha, null),
        ("while", HandlerNodeStage.Alpha, null),
        ("iterate", HandlerNodeStage.Alpha, null),
        ("choice", HandlerNodeStage.Alpha, null),
        ("let", HandlerNodeStage.Alpha, null),
        ("write", HandlerNodeStage.Alpha, null),
        ("ask", HandlerNodeStage.Alpha, null),
        ("script", HandlerNodeStage.Alpha, null),
    ];

    /// <summary>op 的分区：先看节点自带标注，再看内建表，都没有＝alpha。</summary>
    public HandlerNodeStage StageOf(string op)
    {
        if (Find(op) is { } spec) return spec.Stage;
        foreach (var built in BuiltInOps)
            if (built.Op == op) return built.Stage;
        return HandlerNodeStage.Alpha;
    }

    /// <summary>op 的预览原因（alpha＝null）。</summary>
    public string? ProblemOf(string op)
    {
        if (Find(op) is { } spec) return spec.IsPreview ? spec.Problem : null;
        foreach (var built in BuiltInOps)
            if (built.Op == op) return built.Problem;
        return null;
    }

    private static HandlerCatalog? _snapshot;

    /// <summary>内置快照（40 节点 = 30 目录节点〔含 stop／interrupt〕+ 10 内建 op）。</summary>
    public static HandlerCatalog Snapshot => _snapshot ??= BuildSnapshot();

    private static HandlerCatalog? _default;

    /// <summary>
    /// 界面真正该用的目录：**引擎扫描结果优先**，扫描不可用才退回 <see cref="Snapshot"/>。
    /// <para>
    /// 为什么要退回：目录扫描依赖反射装配（`HandlerDslAssembly.Create()`），在裁剪过的运行期
    /// 或程序集缺失时会抛。界面因此空着比「用旧表」更糟——旧表至少能让作者看见有什么积木。
    /// 但**必须让人知道退回了**，所以有 <see cref="DefaultFallbackReason"/>。
    /// </para>
    /// </summary>
    public static HandlerCatalog Default => _default ??= BuildDefault();

    /// <summary>退回本地镜像的原因（null＝目录来自引擎）。</summary>
    public static string? DefaultFallbackReason { get; private set; }

    /// <summary>目录是不是本地镜像（true＝引擎目录不可用）。</summary>
    public static bool DefaultIsFallback => DefaultFallbackReason is not null;

    private static HandlerCatalog BuildDefault()
    {
        try
        {
            var catalog = FromEngine(new Orc.Game.Effects.HandlerCatalogFacade().GetCatalog());
            DefaultFallbackReason = null;
            return catalog;
        }
        catch (Exception error)
        {
            // 这里必须宽：装配失败的花样（反射找不到类型、程序集被裁、Roslyn 缺编译器）都只在运行期现形，
            // 而「编辑面打不开」对作者来说是一样的。原因记下来，界面照实说。
            DefaultFallbackReason = error.GetType().Name + "：" + error.Message;
            return Snapshot;
        }
    }

    /// <summary>
    /// 引擎全量目录 → 编辑面目录（**单向投影，不推导**）。
    /// <para>
    /// 三处刻意的不对称：
    /// <list type="bullet">
    ///   <item><b>内建 op 的引脚仍取本地表</b>：引擎的 <c>BuiltInOps</c> 只报能力分区，没有引脚
    ///     （内建 op 由前端硬编码，目录扫描根本扫不到它们）。所以「引脚＝本地、stage＝引擎」。</item>
    ///   <item><b>扫描节点的引脚一律 <see cref="HandlerPinKind.Value"/></b>：引擎给的 <c>EnumValues</c>
    ///     是**取值建议**，不是「写成裸字符串」的授权。`unitsOf.side` 在 DSL 里是 <c>{"lit":"enemy"}</c>
    ///     （gen 向量 010），标成文本枚举会把产物写成 <c>"enemy"</c>，摊平期才炸。</item>
    ///   <item><b>子类型集照抄</b>：引擎已把列表引脚的约束挂在元素上，这里不再展开一次。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">dto 为 null。</exception>
    public static HandlerCatalog FromEngine(Orc.Game.Effects.HandlerCatalogDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // 内建 op：引脚与容器形态取本地表（引擎没有这张表），能力分区取引擎。
        var builtIns = Snapshot.Nodes.Where(node => node.BuiltIn).ToArray();
        var stages = dto.BuiltInOps.ToDictionary(
            op => op.Op,
            op => (Stage: op.Stage == "preview" ? HandlerNodeStage.Preview : HandlerNodeStage.Alpha, op.Problem),
            StringComparer.Ordinal);

        var merged = new List<HandlerNodeSpec>(builtIns.Length + dto.Nodes.Count);
        foreach (var spec in builtIns)
        {
            if (stages.TryGetValue(spec.Name, out var stage))
            {
                merged.Add(new HandlerNodeSpec(spec.Name, spec.HasAwait, spec.HasSideEffect,
                    spec.Inputs, spec.Outputs, builtIn: true, bodies: spec.Bodies, stage.Stage, stage.Problem));
            }
            else
            {
                // 引擎的 builtInOps 里没有它＝引擎不认这个 op（契约漂了）。照旧收着，但标成预览态，
                // 免得作者搭出一段引擎装不下的图。
                merged.Add(new HandlerNodeSpec(spec.Name, spec.HasAwait, spec.HasSideEffect,
                    spec.Inputs, spec.Outputs, builtIn: true, bodies: spec.Bodies,
                    HandlerNodeStage.Preview, "引擎目录里没有这个内建 op（契约可能已变更）。"));
            }
        }

        foreach (var node in dto.Nodes)
        {
            merged.Add(new HandlerNodeSpec(
                node.Name, node.HasAwait, node.HasSideEffect,
                node.Inputs.Select(Pin).ToArray(),
                node.Outputs.Select(Pin).ToArray(),
                builtIn: false,
                bodies: null,
                node.Stage == "preview" ? HandlerNodeStage.Preview : HandlerNodeStage.Alpha,
                node.Problem));
        }

        return new HandlerCatalog(dto.CatalogRevision, merged, dto.Readables);
    }

    private static HandlerPinSpec Pin(Orc.Game.Effects.HandlerCatalogPinDto pin) =>
        new(pin.Name, pin.Type, pin.Required,
            pin.EnumValues.Count > 0 ? pin.EnumValues : null,
            pin.Subtypes.Count > 0 ? pin.Subtypes : null);

    private static HandlerCatalog BuildSnapshot()
    {
        // ── 内建 op（契约固定，不由扫描产生）─────────────────────────────
        HandlerPinSpec V(string name, string type, bool required = true) => new(name, type, required);
        HandlerPinSpec E(string name, string type, params string[] values) =>
            new(name, type, true, values, null, HandlerPinKind.Enum);
        HandlerPinSpec B(string name, string type = "string") => new(name, type, false, null, null, HandlerPinKind.Bind);
        HandlerPinSpec T(string name, bool required = true) => new(name, "string", required, null, null, HandlerPinKind.Text);

        var builtIns = new HandlerNodeSpec[]
        {
            new("sequence", false, false, [], [], true, ["actions"]),
            new("branch", false, false, [V("test", "boolean")], [], true, ["then", "else"]),
            new("loop", false, false, [V("times", "number")], [], true, ["actions"]),
            new("while", false, false, [V("test", "boolean"), V("limit", "number")], [], true, ["actions"]),
            new("iterate", false, false, [V("over", "list<entity>"), B("as")], [], true, ["actions"]),
            // choice 自 2026-11-03（引擎 R38）起是 **alpha**：复用 targeter 提交通道
            // （option 选择器，标识集＝选项文案），有桥即呈现 prompt ＋ N 个选项。
            // 无桥／取消／非法选择＝抛 InvalidOperationException（消息以 choice-failed[...] 开头），
            // 整条效果中断——所以校验期必须保证文案非空且唯一（见 HandlerDslValidator）。
            new("choice", true, true, [T("prompt")], [], true, ["options"]),
            new("let", false, false, [B("name"), V("value", "number")], [], true),
            new("write", false, true,
                [new("field", "string", true, null, null, HandlerPinKind.Field), V("value", "number")], [], true),
            new("ask", true, true,
                [E("kind", "string", "unit", "hq", "card"),
                 // side／zone／filter／fallback 都可省略（SPEC §3 字段表）
                 new("side", "string", false, ["enemy", "friendly", "any"], null, HandlerPinKind.Enum),
                 T("prompt"),
                 V("zone", "string", false),
                 V("filter", "string", false),
                 // fallback 可省略，缺省 empty（SPEC §3 字段表）
                 new("fallback", "string", false, ["empty", "first"], null, HandlerPinKind.Enum),
                 B("bind", "list<entity>")], [], true),
            new("script", true, true, [T("body")], [], true),
        };

        // ── 目录动作（扫描结果快照；顺序与 catalog.snapshot.json 一致）─────
        var actions = new HandlerNodeSpec[]
        {
            new("death", true, true,
                [new("target", "entity", true), new("killer", "entity", false), new("triggerCard", "entity", false)],
                [new("result", "boolean", true)]),
            // 只读查询原语（SPEC §12.10）：不是合成节点，由门面上的 UnitsOfAsync ＋ [HandlerDslPure] 扫出来。
            // hasAwait=true → 折叠规则判它不可复制（复制一份等于在两个时刻各查一次），要共享就写 let。
            new("unitsOf", true, false,
                // side 在 DSL 里是**表达式**（{"lit":"enemy"}，见 gen 向量 010），
                // 只是带取值集供界面做下拉——所以是 Value 引脚，不是文本枚举。
                [new("side", "string", true, ["self", "enemy", "any"]), new("zone", "string", false)],
                [new("result", "list<entity>", true)]),
            new("addToHand", true, true,
                [new("cardName", "string", true), new("count", "number", true), new("source", "entity", false)],
                [new("result", "number", true)]),
            new("attackByEffect", true, true,
                [new("attacker", "entity", true), new("target", "entity", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            // 纯查询：不产生副作用，hasAwait=false → 折叠规则可以安全复制（与 unitsOf 相反，那边是异步查询）。
            new("attackCandidates", false, false,
                [new("attacker", "entity", true)],
                [new("result", "list<entity>", true)]),
            new("battle", true, true,
                [new("first", "entity", true), new("second", "entity", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("buff", true, true,
                [new("target", "entity", true), new("attack", "number", true), new("defense", "number", true),
                 new("duration", "string", false, ["Permanent", "TurnEnd", "NextOwnerTurnStart"]),
                 new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("costMod", true, true,
                [new("target", "entity", true), new("delta", "number", true),
                 new("duration", "string", false, ["Permanent", "TurnEnd", "NextOwnerTurnStart"])],
                [new("result", "boolean", true)]),
            new("damage", true, true,
                [new("target", "entity", true), new("amount", "number", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("deployCostMod", true, true,
                [new("target", "entity", true), new("delta", "number", true)],
                [new("result", "boolean", true)]),
            new("draw", true, true,
                [new("count", "number", true), new("source", "entity", false)],
                [new("result", "number", true)]),
            new("exchange", true, true,
                // source 在引擎里是**必填**（交换的发起方决定归属判定），不是可选。
                [new("first", "entity", true), new("second", "entity", true), new("source", "entity", true)],
                []),
            new("fetchFromDeck", true, true,
                [new("target", "entity", true), new("source", "entity", false)],
                [new("status", "string", true), new("card", "entity", true)]),
            new("gainPointSlots", true, true,
                [new("amount", "number", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("gainPoints", true, true,
                [new("amount", "number", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("grant", true, true,
                [new("target", "entity", true), new("keyword", "string", true),
                 new("source", "entity", false), new("value", "number", false)],
                [new("result", "boolean", true)]),
            new("kill", true, true,
                [new("target", "entity", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("losePointSlots", true, true,
                [new("amount", "number", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("losePoints", true, true,
                [new("amount", "number", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("move", true, true,
                [new("target", "entity", true), new("zone", "string", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("pickHandCards", true, true,
                [new("min", "number", true), new("max", "number", true)],
                [new("status", "string", true), new("cards", "list<entity>", true)]),
            new("pin", true, true,
                [new("target", "entity", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("rerunAuras", true, true, [], [new("result", "boolean", true)]),
            new("revoke", true, true,
                [new("target", "entity", true), new("keyword", "string", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("setKeywordValue", true, true,
                [new("target", "entity", true), new("keyword", "string", true),
                 new("value", "number", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("shuffleIn", true, true,
                [new("cardName", "string", true), new("count", "number", true), new("source", "entity", false)],
                [new("result", "number", true)]),
            new("silence", true, true,
                [new("target", "entity", true), new("source", "entity", false)],
                [new("result", "boolean", true)]),
            new("upgrade", true, true,
                [new("target", "entity", true), new("source", "entity", false)],
                [new("outcome", "string", true)]),
            // 合成节点：由 ctx 提供，没有动作面方法，不接受 bind
            new("stop", false, true, [], []),
            new("interrupt", false, true, [], []),
        };

        // 注意：**不要把 bind 造成引脚**。SPEC §3 的 `"bind"?: "x" | {…}` 是**节点级字段**，
        // 不是动作面参数，快照里当然不会出现；把它塞进 Inputs 会让导出的目录与快照不一致，
        // 还会让多输出节点的对象形态 bind 被当成值引脚去比类型。校验器单独判它（§8.9）。

        var readables = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["unit"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attack"] = "number", ["defense"] = "number", ["opCost"] = "number",
                ["deployCost"] = "number", ["name"] = "string", ["zone"] = "string",
            },
            ["hq"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hqHealth"] = "number", ["name"] = "string",
            },
            ["player"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kredits"] = "number", ["maxKredits"] = "number", ["hqHealth"] = "number",
            },
        };

        return new HandlerCatalog(0, [.. builtIns, .. actions], readables);
    }
}
