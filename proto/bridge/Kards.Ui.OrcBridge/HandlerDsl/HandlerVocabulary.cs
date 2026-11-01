namespace Kards.Ui.OrcBridge.HandlerDsl;

/// <summary>
/// 目录里只有英文 op 名与英文引脚名（<c>HandlerCatalogNodeDto.Name</c>）。
/// 这一层是**界面侧词表**：中文名、分组、引脚说明都在这里，引擎不参与。
/// <para>
/// 为什么不写进 DSL：SPEC §0.4 规定未知节点名一律拒绝，别名不能进文件；
/// 为什么不写进引擎：见 docs/design/INSPECTOR_SELECTION_20261031.md §4.4 —— 中文名属于目录内容，
/// 放前端会漂移，理想解是引擎给 <c>title</c>（只升 catalogRevision）。在引擎给之前，这里兜底。
/// </para>
/// <para>
/// **用词原则**：作者是来设计卡牌的，不是来读规范的。凡是能用日常说法讲清的，就不要用术语
/// ——「绑定」→「起个名」，「表达式」→「值」，「引脚」→「格子」，「目录」→「引擎支持的积木」。
/// 术语只在**必须精确**的地方保留（比如错误原因里要指出是哪个名字冲突）。
/// </para>
/// </summary>
public static class HandlerVocabulary
{
    private static readonly Dictionary<string, (string Title, string Group)> Nodes = new(StringComparer.Ordinal)
    {
        // 控制流
        ["sequence"] = ("顺序", "控制流"),
        ["branch"] = ("如果", "控制流"),
        ["loop"] = ("重复几次", "控制流"),
        ["while"] = ("只要…就一直做", "控制流"),
        ["iterate"] = ("逐个处理", "控制流"),
        ["choice"] = ("让玩家选一个", "交互"),
        ["ask"] = ("让玩家挑目标", "交互"),
        // 查询（只读，不改世界）
        ["unitsOf"] = ("找出单位", "查询"),
        // 变量
        ["let"] = ("记下这个值", "变量"),
        ["write"] = ("改卡片上的属性", "变量"),
        ["script"] = ("脚本（自己写代码）", "高级"),
        // 造成与消灭
        ["damage"] = ("造成伤害", "造成与消灭"),
        ["kill"] = ("消灭", "造成与消灭"),
        ["death"] = ("判定死亡", "造成与消灭"),
        ["silence"] = ("沉默", "造成与消灭"),
        ["pin"] = ("压制", "造成与消灭"),
        // 属性与词条
        ["buff"] = ("改攻击和防御", "属性与词条"),
        ["grant"] = ("给予词条", "属性与词条"),
        ["revoke"] = ("移除词条", "属性与词条"),
        ["setKeywordValue"] = ("设置词条数值", "属性与词条"),
        ["costMod"] = ("改行动费", "属性与词条"),
        ["deployCostMod"] = ("改部署费", "属性与词条"),
        ["rerunAuras"] = ("重新计算加成", "属性与词条"),
        // 手牌与牌堆
        ["draw"] = ("抽牌", "手牌与牌堆"),
        ["addToHand"] = ("按名字拿牌", "手牌与牌堆"),
        ["shuffleIn"] = ("洗进牌堆", "手牌与牌堆"),
        ["fetchFromDeck"] = ("从牌堆里找牌", "手牌与牌堆"),
        ["pickHandCards"] = ("翻看手牌", "手牌与牌堆"),
        // 战场位置
        ["move"] = ("换个位置", "战场位置"),
        ["battle"] = ("发起交战", "战场位置"),
        ["upgrade"] = ("升级", "战场位置"),
        // 资源
        ["gainPoints"] = ("加点数", "资源"),
        ["losePoints"] = ("扣点数", "资源"),
        ["gainPointSlots"] = ("提高点数上限", "资源"),
        ["losePointSlots"] = ("降低点数上限", "资源"),
        // 流程
        ["stop"] = ("就此停下", "流程"),
        ["interrupt"] = ("打断后面所有", "流程"),
    };

    /// <summary>分组显示顺序。目录是方法名派生的，分组得我们自己排。</summary>
    public static readonly string[] GroupOrder =
        ["造成与消灭", "属性与词条", "手牌与牌堆", "战场位置", "资源", "交互", "查询", "控制流", "变量", "流程", "高级"];

    /// <summary>分组的一句话说明，工具箱的组标题用它，比只写组名好懂。</summary>
    public static string GroupHint(string group) => group switch
    {
        "造成与消灭" => "打伤害、消灭、判定死亡",
        "属性与词条" => "改数值、给词条、改费用",
        "手牌与牌堆" => "抽牌、找牌、洗手牌",
        "战场位置" => "挪位置、交战、升级",
        "资源" => "点数与上限",
        "交互" => "问玩家、让玩家选",
        "查询" => "只读，不改任何东西",
        "控制流" => "如果、循环、逐个处理",
        "变量" => "记下值、改属性、写代码",
        "流程" => "提前结束",
        "高级" => "不常用，改之前先想清楚",
        _ => "",
    };

    private static readonly Dictionary<string, string> Pins = new(StringComparer.Ordinal)
    {
        ["target"] = "对谁", ["source"] = "由谁发起", ["amount"] = "多少", ["count"] = "几张",
        ["cardName"] = "牌名", ["keyword"] = "词条", ["value"] = "数值", ["delta"] = "加减多少",
        ["duration"] = "持续多久", ["zone"] = "区域", ["attack"] = "攻击", ["defense"] = "防御",
        ["min"] = "最少", ["max"] = "最多", ["first"] = "第一个", ["second"] = "第二个",
        ["killer"] = "谁打死的", ["triggerCard"] = "触发它的牌", ["result"] = "结果",
        ["status"] = "结果状态", ["card"] = "这张牌", ["cards"] = "这些牌", ["outcome"] = "结果",
        // 内建
        ["test"] = "条件", ["times"] = "重复几次", ["limit"] = "最多几次", ["over"] = "处理这一组",
        ["as"] = "每个叫", ["name"] = "起个名", ["field"] = "改哪个属性", ["kind"] = "挑什么",
        ["side"] = "哪一方", ["prompt"] = "问玩家什么", ["bind"] = "存成名字", ["body"] = "脚本内容",
        ["actions"] = "动作", ["then"] = "那么", ["else"] = "不然", ["options"] = "选项",
        ["filter"] = "只看", ["fallback"] = "选不到时",
    };

    /// <summary>个别节点里同名引脚含义不同，按 (op, pin) 覆盖。</summary>
    private static readonly Dictionary<string, string> PinOverrides = new(StringComparer.Ordinal)
    {
        ["draw.count"] = "抽几张",
        ["addToHand.count"] = "加入几张",
        ["shuffleIn.count"] = "洗进几张",
        ["damage.amount"] = "打多少",
        ["gainPoints.amount"] = "加多少点",
        ["losePoints.amount"] = "扣多少点",
        ["gainPointSlots.amount"] = "加几个槽",
        ["losePointSlots.amount"] = "减几个槽",
        ["buff.attack"] = "攻击变多少",
        ["buff.defense"] = "防御变多少",
        ["setKeywordValue.value"] = "设成多少",
        ["grant.value"] = "数值",
        ["unitsOf.side"] = "哪一方",
        ["unitsOf.zone"] = "只在哪个区域",
        ["ask.fallback"] = "选不到时",
        ["ask.kind"] = "挑什么",
        ["ask.prompt"] = "问玩家什么",
    };

    /// <summary>可读项（<c>read.path</c>）的中文名。目录里是 <c>attack</c>／<c>kredits</c> 这种驼峰。</summary>
    private static readonly Dictionary<string, string> Readables = new(StringComparer.Ordinal)
    {
        ["attack"] = "攻击力",
        ["defense"] = "防御力",
        ["opCost"] = "行动费",
        ["deployCost"] = "部署费",
        ["zone"] = "所在区域",
        ["name"] = "名字",
        ["hqHealth"] = "总部血量",
        ["kredits"] = "点数",
        ["maxKredits"] = "点数上限",
    };

    /// <summary>视图字段名（来自预制体，通常是 PascalCase 英文）。认识的翻中文，不认识的照原样。</summary>
    private static readonly Dictionary<string, string> ViewFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Card"] = "这张牌",
        ["Player"] = "玩家",
        ["Source"] = "来源",
        ["Target"] = "目标",
        ["Units"] = "场上的单位",
        ["Defense"] = "防御力",
        ["Attack"] = "攻击力",
        ["Name"] = "名字",
        ["Zone"] = "区域",
    };

    /// <summary>算子名 → 日常说法。算子表是封闭的（SPEC §5），所以这里可以穷举。</summary>
    private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
    {
        ["add"] = "加",
        ["sub"] = "减",
        ["mul"] = "乘",
        ["eq"] = "等于",
        ["ne"] = "不等于",
        ["lt"] = "小于",
        ["le"] = "小于或等于",
        ["gt"] = "大于",
        ["ge"] = "大于或等于",
        ["and"] = "并且",
        ["or"] = "或者",
        ["not"] = "反过来",
        ["count"] = "有几个",
        ["sum"] = "加起来",
        ["max"] = "最大的",
        ["min"] = "最小的",
        ["empty"] = "是不是空的",
    };

    /// <summary>13 个错误码 → 作者能看懂的一句话。码本身留在 tooltip 里，给排查的人看。</summary>
    private static readonly Dictionary<string, string> IssueTitles = new(StringComparer.Ordinal)
    {
        ["graph-shape"] = "结构不对",
        ["node-unknown"] = "引擎不认识这块",
        ["pin-unknown"] = "这里不该填东西",
        ["pin-missing"] = "还有地方没填",
        ["pin-type"] = "填的东西对不上",
        ["bind-scope"] = "名字用错了",
        ["write-forbidden"] = "这个属性不能改",
        ["loop-unbounded"] = "循环没写次数",
        ["ask-in-loop"] = "循环里不能问玩家",
        ["schema-version"] = "版本对不上",
        ["expr-op-unknown"] = "没有这种算法",
        ["expr-arity"] = "算法的参数个数不对",
        ["expr-type"] = "算法用错了东西",
    };

    public static string NodeTitle(string op) => Nodes.TryGetValue(op, out var v) ? v.Title : op;
    public static string NodeGroup(string op) => Nodes.TryGetValue(op, out var v) ? v.Group : "其他";

    public static string PinLabel(string op, string pin) =>
        PinOverrides.TryGetValue(op + "." + pin, out var over) ? over
        : Pins.TryGetValue(pin, out var label) ? label
        : pin;

    public static string ReadableLabel(string path) =>
        Readables.TryGetValue(path, out var label) ? label : path;

    public static string ViewFieldLabel(string name) =>
        ViewFields.TryGetValue(name, out var label) ? label : name;

    public static string OperatorLabel(string op) =>
        Operators.TryGetValue(op, out var label) ? label : op;

    /// <summary>
    /// 静态类型名 → 作者看得懂的说法。类型本身是契约概念（SPEC §7），
    /// 但「list&lt;entity&gt;」摆在下拉里没人知道是什么——这里只翻译，判定仍在校验器里。
    /// </summary>
    public static string TypeLabel(string type) => type switch
    {
        "entity" => "实体",
        "number" => "数字",
        "string" => "文字",
        "boolean" => "是/否",
        "list<entity>" => "一组实体",
        "list<number>" => "一组数字",
        "" or "unknown" => "不确定",
        _ => type,
    };

    public static string IssueTitle(string code) =>
        IssueTitles.TryGetValue(code, out var title) ? title : "有问题";

    /// <summary>没有中文名时如实显示英文并标一下，不要假装有。</summary>
    public static bool HasTitle(string op) => Nodes.ContainsKey(op);

    /// <summary>
    /// 把契约的节点路径（<c>actions[0].value.read.path</c>）翻成作者能读的位置
    /// ——「第1块 · 值 · 读取 · 属性」。原始路径放进 tooltip，排查时仍能拿到。
    /// </summary>
    public static string FriendlyPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "$") return "整段";

        var parts = new List<string>();
        foreach (var token in path.Split('.'))
        {
            var bracket = token.IndexOf('[', StringComparison.Ordinal);
            if (bracket < 0)
            {
                parts.Add(SegmentLabel(token));
                continue;
            }

            var name = token[..bracket];
            var index = token[(bracket + 1)..].TrimEnd(']');
            var n = int.TryParse(index, out var parsed) ? parsed + 1 : 1;
            parts.Add(name switch
            {
                "actions" => "第" + n + "块",
                "then" => "那么 第" + n + "块",
                "else" => "不然 第" + n + "块",
                "options" => "选项" + n,
                "args" => "第" + n + "个数",
                _ => SegmentLabel(name) + " " + n,
            });
        }
        return string.Join(" · ", parts);
    }

    private static string SegmentLabel(string name) => name switch
    {
        "actions" => "动作",
        "value" => "值",
        "read" => "读取",
        "path" => "属性",
        "of" => "读谁",
        "name" => "名字",
        "bind" => "存的名字",
        "field" => "属性",
        "times" => "次数",
        "limit" => "上限",
        "over" => "处理这一组",
        "as" => "每个叫",
        "test" => "条件",
        "kind" => "挑什么",
        "side" => "哪一方",
        "prompt" => "问的话",
        "amount" => "多少",
        "target" => "对谁",
        "label" => "选项文字",
        _ => Pins.TryGetValue(name, out var label) ? label : name,
    };
}
