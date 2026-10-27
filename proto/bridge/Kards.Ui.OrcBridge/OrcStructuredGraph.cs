using System.Text.Json.Nodes;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 结构化 op → 声明图（<see cref="DesktopNativeLowering"/> 的入口形态）。
///
/// <para>
/// 为什么需要这一层：结构化效果原本逐 op 交给 op 模板编译，而 op 模板在 <c>sel=one</c> 时走
/// <c>runtime.SelectAsync</c>——**无头**选靶，直接取候选第一个。玩家看到的是"指向的效果随机命中"，
/// 实际是确定性取第一个（2026-10-26 实测：场上两个稻草人，部署后"乙方"掉血、"甲方"没事，选靶面板 0 次）。
/// </para>
///
/// <para>
/// 声明图是唯一能交互的通道：<c>targets</c> 里声明一个单目标，引擎在**预打出**阶段把它交给
/// <see cref="DesktopTargetContext"/>（<c>AuthoringTargetContext</c>）捕获，执行时
/// <c>SelectAsync</c> 直接取回玩家点的那一个，不再问第二遍。文本模式一直是这条通道，
/// 所以只有结构化通道有这个缺陷。
/// </para>
///
/// <para>
/// 边界（诚实降级，不硬造）：
/// ① 只有 <c>deploy_basic</c> / <c>played_basic</c> 有"打出前"这个时机，其余触发（回合开始、受伤、亡计…）
///    没有玩家应答的窗口，继续走 op 模板的无头选靶；
/// ② 筛选条件只支持声明面能无损表达的部分（兵种、词条）；阵营筛选与"排除自身"没有对应字段，
///    继续走 op 模板，而不是悄悄丢掉条件；
/// ③ 期限只支持永久／本回合结束；"下个己方回合开始"没有对应值，交回 op 模板。
/// </para>
/// </summary>
internal static class OrcStructuredGraph
{
    /// <summary>声明图里的目标 id（单效果单目标，固定即可——每 op 一张图，作用域按图哈希隔离）。</summary>
    private const string TargetId = "orcTarget1";

    /// <summary>这个 op 是否需要玩家亲自选目标（<c>sel=one</c>；随机/全体/自己都不需要）。</summary>
    public static bool NeedsChoice(OrcOpDraft op) =>
        op.Sel == "one"
        && !op.AmountFromCounter
        && !op.Op.StartsWith("counter.", StringComparison.Ordinal);

    /// <summary>单 op 的声明图；返回 null＝这个 op 不该走声明图（保持 op 模板的原行为）。</summary>
    public static string? Graph(OrcEffectDraft draft, OrcOpDraft op)
    {
        // 已有完整声明图的效果以 DesktopJson 为唯一真源，不在这里重造。
        if (draft.DesktopJson is { Length: > 0 }) return null;
        if (!NeedsChoice(op)) return null;
        if (Trigger(draft) is not { } trigger) return null;
        if (Target(op) is not { } target) return null;
        if (Actions(op) is not { } actions) return null;
        return new JsonObject
        {
            ["trigger"] = trigger,
            ["actions"] = actions,
            ["targets"] = new JsonArray(target),
        }.ToJsonString();
    }

    /// <summary>一整组效果里所有需要声明图的目标（选靶声明装配用；与编译侧同一函数，保证作用域哈希一致）。</summary>
    public static IReadOnlyList<string> Graphs(IEnumerable<OrcEffectDraft> effects)
    {
        var graphs = new List<string>();
        foreach (var effect in effects)
            foreach (var op in effect.Ops)
                if (Graph(effect, op) is { } graph) graphs.Add(graph);
        return graphs;
    }

    /// <summary>声明图的触发名；null＝这个触发时机没有"打出前"，玩家无从应答。</summary>
    private static string? Trigger(OrcEffectDraft draft) => draft.Template switch
    {
        "deploy_basic" => "deploy",
        // 指令与"打出时"共用 played_basic：引擎侧两者都落到 card.played，声明图口径也一样。
        "played_basic" => "play",
        _ => null,
    };

    private static JsonObject? Target(OrcOpDraft op)
    {
        // 阵营与"排除自身"在选靶声明里没有无损字段（AuthoringTargetContext.ValidateTarget 只收
        // id/kind/side/zone/prompt/filter/reuse，filter 只认兵种/词条/阈值）。与其丢掉条件让它
        // 变成"任意单位"，不如留在 op 模板里——宁可没交互，也不能把限定条件悄悄放宽。
        if (op.Filter is { } filter && (!string.IsNullOrWhiteSpace(filter.Faction) || filter.ExcludeSelf)) return null;
        var target = new JsonObject
        {
            ["id"] = TargetId,
            ["kind"] = "unit",
            // 阵营口径必须与 op 模板的 BuildSelector 完全一致：one+both→any、空→friendly。
            // 这里若擅自把空当成 any，同一张卡换通道就换了目标池。
            ["side"] = op.Side switch { "enemy" => "enemy", "both" => "any", _ => "friendly" },
            // 引擎选靶声明只认 field/frontline/support/hq；空＝整片战场。
            ["zone"] = op.SelZone is "frontline" or "support" ? op.SelZone : "field",
            ["prompt"] = "选择一个单位",
        };
        if (op.Filter is { } declared)
        {
            var conditions = new JsonObject();
            if (!string.IsNullOrWhiteSpace(declared.UnitType)) conditions["unitType"] = declared.UnitType;
            if (!string.IsNullOrWhiteSpace(declared.Keyword)) conditions["keyword"] = declared.Keyword;
            if (conditions.Count > 0) target["filter"] = conditions;
        }
        return target;
    }

    private static JsonArray? Actions(OrcOpDraft op)
    {
        var duration = Duration(op.Until);
        switch (op.Op)
        {
            case "damage":
            {
                var one = Action("dealDamage");
                one["amount"] = op.Amount ?? 1;
                return new JsonArray(one);
            }
            case "buff":
            {
                if (duration is null) return null;
                var actions = new JsonArray();
                if (op.Attack is { } attack && attack != 0)
                    actions.Add(Attribute("attack", attack, duration));
                if (op.Defense is { } defense && defense != 0)
                    actions.Add(Attribute("defense", defense, duration));
                return actions.Count > 0 ? actions : null;
            }
            case "destroy":
                return new JsonArray(Action("destroyUnit"));
            case "pin":
            {
                // 结构化面只放行 1 回合（CheckEffect 已拦其它值），与原生压制模板同口径。
                var one = Action("setPinned");
                one["enabled"] = true;
                one["turns"] = 1;
                return new JsonArray(one);
            }
            case "silence":
                return new JsonArray(Action("suppressUnit"));
            case "costMod":
            {
                if (duration is null) return null;
                return new JsonArray(Attribute("opCost", op.Amount ?? 1, duration));
            }
            case "grant":
            {
                if (string.IsNullOrWhiteSpace(op.Keyword)) return null;
                var one = Action("setKeyword");
                one["keyword"] = op.Keyword;
                one["enabled"] = true;
                one["value"] = op.Value ?? 1;
                return new JsonArray(one);
            }
            case "move":
            {
                if (op.OpZone is not ("frontline" or "support")) return null;
                var one = Action("moveUnit");
                one["to"] = op.OpZone;
                return new JsonArray(one);
            }
            case "reveal":
                return new JsonArray(Action("reveal"));
            case "upgrade":
                return new JsonArray(Action("upgradeSelf"));
            default:
                return null;
        }
    }

    /// <summary>指向声明目标的动作骨架（每次新建，JsonNode 不能挂在两个父节点下）。</summary>
    private static JsonObject Action(string op) => new()
    {
        ["op"] = op,
        ["target"] = new JsonObject { ["sel"] = "refAll", ["ref"] = TargetId },
    };

    private static JsonObject Attribute(string property, int value, string duration)
    {
        var one = Action("changeAttribute");
        one["property"] = property;
        one["mode"] = "add";
        one["value"] = value;
        one["duration"] = duration;
        return one;
    }

    /// <summary>op 期限 → 声明图期限；null＝声明图没有对应值，交回 op 模板。</summary>
    private static string? Duration(string? until) => until switch
    {
        null or "" => "permanent",
        Orc.Game.EffectParsing.Dsl.Untils.TurnEnd => "turn",
        _ => null,
    };
}
