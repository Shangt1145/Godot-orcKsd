using System.Text.Json;
using Kards.Ui.OrcBridge.OrcText;
using Orc.Cards;
using Orc.Game.EffectParsing.Dsl;

namespace Kards.Ui.OrcBridge;

// Text compiles through the complete Desktop syntax chain; declarations remain lossless.
// Authoring supports the full source vocabulary. Admission and execution still compile through
// OrC-KSD; unsupported mechanisms produce diagnostics without discarding their declarations.

/// <summary>单条效果的编译产物：Snapshots/JsonText 供效果组件内联（JsonText＝快照 JSON 数组）；
/// Error 非空＝该条编译失败（Snapshots 为空）。</summary>
public sealed record OrcCompiledEffect(
    OrcParsedEffect Effect, IReadOnlyList<EffectSnapshot> Snapshots, string? JsonText, string? Error);

/// <summary>规则文本整体编译产物。ParseComplete＝零解析诊断；AllCompiled＝逐条无 Error。</summary>
public sealed record OrcTextCompile(OrcCompileResult Parse, IReadOnlyList<OrcCompiledEffect> Effects)
{
    public bool ParseComplete => Parse.Complete;
    public IReadOnlyList<string> NativeProblems {get;init;}=[];
    public bool AllCompiled => NativeProblems.Count==0 && Effects.All(e => e.Error is null);
    /// <summary>用户可读的全部问题（解析诊断 + 逐条编译错误；空＝全绿）。</summary>
    public IReadOnlyList<string> Problems => Parse.Diagnostics.Select(d => d.ToString())
        .Concat(NativeProblems).Concat(Effects.Where(e => e.Error is not null)
            .Select(e => $"{OrcInspector.TriggerLabel(e.Effect.TemplateId)}：{e.Error}")).ToArray();
}

/// <summary>Inspector 树节点：Label＝中文标签，Detail＝参数摘要（可空），Children＝子节点。</summary>
public sealed record OrcInspectNode(string Label, string? Detail, IReadOnlyList<OrcInspectNode> Children)
{
    public static OrcInspectNode Leaf(string label, string? detail = null) => new(label, detail, []);
}

public static class OrcInspector
{
    /// <summary>规则文本 → 解析 + 逐 op 编译（每 op 独立实例，绕开同 handler 多 op 的
    /// var runtime 重复声明——与导入同一已验证语义）。解析有诊断时仍返回解析面供 Inspector 展示。</summary>
    public static OrcTextCompile CompileText(string cardId, string text, string cardType,IReadOnlyList<UiCardDefinition>? catalog=null)
    {
        OrcCompileResult parse;
        try { parse = DesktopOrc.Compile(text, cardType,catalog); }
        catch (Exception e) when (e is OrcReject or ArgumentException or FormatException or InvalidOperationException or InvalidDataException)
        { parse = new OrcCompileResult(text, [], [], [new OrcDiagnostic("parse", e.Message)]); }
        var effects = new List<OrcCompiledEffect>();
        if (parse.Complete)
        {
                foreach (var effect in parse.Effects)
            {
                string? error = null;
                var snapshots = new List<EffectSnapshot>();
                string? json = null;
                try
                {
                    // 与 ImportOne 相同：每 op 一个触发实例（独立 snapshot）后并入同一 effects 组件。
                    var jsons = new List<JsonElement>();
                    var native = effect.DesktopJson is {Length:>0} graph ? DesktopNativeLowering.Lower(graph) : effect;
                    foreach (var op in native.Ops)
                    {
                        var (one, oneJson) = OrcImportService.CompileOrcEffect(cardId, native with { Ops = [op] });
                        snapshots.Add(one);
                        using var doc = JsonDocument.Parse(oneJson);
                        jsons.Add(doc.RootElement.Clone());
                    }
                    // 快照 JSON 必须是**对象数组**（导入同款形态）；序列化成字符串数组会被
                    // effects 组件静默跳过——真对局部署空转（DiyTextEffectsTests 抓过）。
                    json = JsonSerializer.Serialize(jsons);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException
                    or JsonException or ArgumentException or InvalidOperationException or FormatException or OrcReject)
                { error = e.Message; snapshots.Clear(); }
                effects.Add(new(effect, snapshots, error is null ? json : null, error));
            }
        }
        var nativeProblems=new List<string>();
        if(parse.DesktopResultJson is { } full)
        {
            using var result=JsonDocument.Parse(full);
            if(result.RootElement.GetProperty("cardFields").EnumerateObject().Any())nativeProblems.Add("OrC-KSD 卡面机制字段尚无无损适配："+result.RootElement.GetProperty("cardFields").GetRawText());
        }
        return new(parse, effects) {NativeProblems=nativeProblems};
    }

    // ───────────────────────── Inspector：解析面 → 中文标签树 ─────────────────────────

    /// <summary>把解析产物渲染成 Inspector 树：每条效果一个节点（触发 + 守卫 + op 序列），附词条声明。</summary>
    public static IReadOnlyList<OrcInspectNode> Describe(OrcCompileResult parse)
    {
        var nodes = new List<OrcInspectNode>();
        foreach (var effect in parse.Effects)
        {
            if(effect.DesktopJson is { } graph)
            {
                try
                {
                    var native=DesktopNativeLowering.Lower(graph);
                    // Show the complete original graph alongside the native status; it may contain arbitrary nested data.
                    using var raw=JsonDocument.Parse(graph);
                    var childrenRaw=raw.RootElement.GetProperty("actions").EnumerateArray().Select(a=>
                        a.GetProperty("op").GetString()=="drawOne"?OrcInspectNode.Leaf("抽牌","1 张"):OrcInspectNode.Leaf(a.GetProperty("op").GetString()!,a.GetRawText())).ToArray();
                    nodes.Add(new(TriggerLabel(native.TemplateId),null,childrenRaw));
                }
                catch(InvalidDataException){nodes.Add(new(effect.TemplateId,null,[OrcInspectNode.Leaf("完整声明",graph)]));}
                continue;
            }
            var children = new List<OrcInspectNode>();
            if (effect.Guard != OrcTriggerGuard.None)
                children.Add(OrcInspectNode.Leaf("触发守卫", GuardLabel(effect.Guard)));
            foreach (var op in effect.Ops) children.Add(DescribeOp(op));
            nodes.Add(new(TriggerLabel(effect.TemplateId), null, children));
        }
        foreach (var keyword in parse.Keywords)
            nodes.Add(OrcInspectNode.Leaf("词条声明", keyword.Quantified
                ? $"{keyword.Id}（参值 {keyword.Value}）" : keyword.Id));
        if (nodes.Count == 0 && parse.Diagnostics.Count > 0)
            nodes.Add(new("未能解析出效果", null, parse.Diagnostics
                .Select(d => OrcInspectNode.Leaf(d.ToString())).ToArray()));
        return nodes;
    }

    /// <summary>单个 op → 标签节点（op 中文名 + 参数摘要 + 子结构）。</summary>
    public static OrcInspectNode DescribeOp(DslOp op)
    {
        var children = new List<OrcInspectNode>();
        var detail = new List<string>();
        if (op.Target is { } target) children.Add(OrcInspectNode.Leaf("作用对象", SelectorLabel(target)));
        if (op.Filter is { } filter) children.Add(OrcInspectNode.Leaf("筛选", FilterLabel(filter)));
        if (op.Condition is { } condition) children.Add(OrcInspectNode.Leaf("条件", ConditionLabel(condition)));
        if (op.Count is { } count) detail.Add($"{count} 张");
        if (op.Amount is { } amount) detail.Add($"{amount} 点");
        if (op.Attack is { } attack) detail.Add($"攻击 {(attack >= 0 ? "+" : "")}{attack}");
        if (op.Defense is { } defense) detail.Add($"防御 {(defense >= 0 ? "+" : "")}{defense}");
        if (op.Keyword is { } keyword) detail.Add("词条 " + keyword + (op.Value is { } v ? $"（参值 {v}）" : ""));
        if (op.Name is { } name) detail.Add("「" + name + "」");
        if (op.Zone is { } zone) detail.Add("落点 " + ZoneLabel(zone));
        if (op.Field is { } field) detail.Add("属性 " + FieldLabel(field));
        if (op.Until is { } until) detail.Add("直到" + UntilLabel(until));
        if (op.Script is { } script) detail.Add(script.Length > 60 ? script[..60] + "…" : script);
        if (op.Nested is { } nested)
            foreach (var nestedEffect in nested)
                foreach (var nestedOp in nestedEffect.Fills.Values.SelectMany(f => f.Ops))
                    children.Add(DescribeOp(nestedOp));
        var label = OpLabel(op.Op);
        return new(label, detail.Count > 0 ? string.Join("，", detail) : null, children);
    }

    private static readonly IReadOnlyDictionary<string, string> OpNames = new Dictionary<string, string>
    {
        ["damage"] = "造成伤害", ["draw"] = "抽牌", ["buff"] = "属性增减", ["grant"] = "获得词条",
        ["move"] = "移动", ["destroy"] = "消灭", ["pin"] = "压制", ["silence"] = "抑制",
        ["costMod"] = "行动花费修正", ["addToHand"] = "加入手牌", ["shuffleIn"] = "洗入卡组",
        ["gainSlot"] = "获得指挥点槽", ["loseSlot"] = "失去指挥点槽", ["gainPoint"] = "获得指挥点",
        ["losePoint"] = "失去指挥点", ["aura"] = "光环", ["upgrade"] = "升为老兵", ["reveal"] = "揭示",
        ["csx"] = "内联脚本（危险）", ["needsCsx"] = "需要脚本实现（引擎表达面外）", ["nested"] = "内嵌效果",
    };

    /// <summary>触发模板 id → 中文（解析器产出面：11 个模板 id；未识别回退原 id）。</summary>
    public static string TriggerLabel(string templateId) => templateId switch
    {
        "deploy_basic" => "部署时", "played_basic" => "打出时", "death_basic" => "亡计（被消灭时）",
        "killed_basic" => "消灭（击杀）时", "damaged_basic" => "受到伤害时", "damage_dealt_basic" => "造成伤害时",
        "acted_basic" => "攻击后", "attack_basic" => "攻击时", "turn_start_basic" => "友方回合开始时",
        "turn_end_basic" => "友方回合结束时", "counter_basic" => "反制",
        _ => templateId,
    };

    private static string OpLabel(string op) => OpNames.TryGetValue(op, out var label) ? label : op;

    private static string GuardLabel(OrcTriggerGuard guard) => guard switch
    {
        OrcTriggerGuard.SelfUnit => "仅本单位（事件载荷自指）",
        OrcTriggerGuard.SelfCard => "仅本卡（事件载荷自指）",
        OrcTriggerGuard.OwnerSame => "仅事件归属与本卡相同",
        OrcTriggerGuard.OwnerSameByPlayer => "仅事件归属玩家与本卡相同",
        _ => guard.ToString(),
    };

    private static string SelectorLabel(DslSelector selector)
    {
        var side = selector.Side switch
        {
            "friendly" => "友方", "enemy" => "敌方", "both" => "双方", _ => "",
        };
        var head = selector.Sel switch
        {
            "self" => "自己", "all" => "所有", "one" => "1 个", "random" => $"随机 {selector.Count ?? 1} 个",
            "ref" => "引用目标", _ => selector.Sel,
        };
        if (selector.Sel == "self") return "自己";
        return string.Join("", head, side, selector.Zone is { } zone ? ZoneLabel(zone) : "单位");
    }

    private static string FilterLabel(DslFilter filter)
    {
        var parts = new List<string>();
        if (filter.UnitType is { } unitType)
            parts.Add("兵种＝" + (Wordbook.EngineUnitTypes.FirstOrDefault(p => p.Value == unitType).Key ?? unitType));
        if (filter.Keyword is { } keyword) parts.Add("词条＝" + keyword);
        if (filter.Faction is { } faction) parts.Add("阵营＝" + faction);
        if (filter.ExcludeSelf) parts.Add("排除自身");
        if (filter.ThresholdField is { } field)
            parts.Add($"{FieldLabel(field)} {ThresholdOpLabel(filter.ThresholdOp)} {filter.ThresholdValue}");
        return string.Join("，", parts);
    }

    private static string ThresholdOpLabel(string? op) => op switch
    {
        "gte" => "≥", "lte" => "≤", "gt" => ">", "lt" => "<", "eq" => "=", _ => op ?? "",
    };

    private static string ConditionLabel(DslCondition condition) => condition.Kind switch
    {
        DslCondition.OwnerSame => "事件归属与本卡相同",
        DslCondition.OwnerDifferent => "事件归属与本卡不同",
        DslCondition.OwnerSameByPlayer => "事件归属玩家与本卡相同",
        DslCondition.OwnerDifferentByPlayer => "事件归属玩家与本卡不同",
        DslCondition.PayloadSelf => $"载荷自指（{condition.Raw}）",
        DslCondition.PayloadIsHq => $"载荷是总部（{condition.Raw}）",
        DslCondition.PayloadOwnerSame => $"载荷归属同主（{condition.Raw}）",
        DslCondition.PayloadOwnerDifferent => $"载荷归属异主（{condition.Raw}）",
        DslCondition.EventCardFilter => "事件卡过滤：" + condition.Raw,
        DslCondition.Compare => "数值比较：" + condition.Raw,
        DslCondition.TurnOwner => "回合归属：" + condition.Raw,
        DslCondition.AllKind => "同时满足（" + (condition.All?.Count ?? 0) + " 项）",
        _ => condition.Raw ?? condition.Kind,
    };

    private static string ZoneLabel(string zone) => zone switch
    {
        "frontline" => "前线", "support" => "支援阵线", "hand" => "手牌", "deck" => "卡组", "discard" => "弃牌区",
        "hq" => "总部", _ => zone,
    };

    private static string FieldLabel(string field) => field switch
    {
        "attack" => "攻击力", "defense" => "防御力", "opCost" => "行动花费", "deployCost" => "部署花费",
        _ => field,
    };

    private static string UntilLabel(string until) => until switch
    {
        Untils.TurnEnd => "本回合结束", Untils.NextOwnerTurnStart => "下个己方回合开始", _ => until,
    };
}
