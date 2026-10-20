using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge.OrcText;
using Orc.Cards;
using Orc.Game.EffectParsing.Dsl;

namespace Kards.Ui.OrcBridge;

// ─────────────────────────────────────────────────────────────────────────────
// OrC Inspector 编辑服务（对齐 Kards-Desktop 检查器的功能面，落在引擎强类型 DSL 上）：
//   · 目录＝唯一合法编辑面：12 个触发条目（模板＋守卫成对——damaged_basic 有自伤/监听
//     两种守卫形态，id 不唯一）× 17 个可加 op（解析器实际产出并编译往返的面；aura/csx/
//     nested 解析器不产出或不可持久化，编辑器不伪装）。
//   · 阈值过滤不给编辑：引擎 FilterDto 无 threshold 序列化维，JSON 往返必丢——
//     编辑器不产不能持久化的效果（解析器对阈值句同样拒绝，同一口径）。
//   · 编译＝OrC 导入同一通道（OrcImportService.CompileOrcEffect，逐 op 独立实例）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>触发条目：模板 id ＋ 钩子 ＋ 守卫成对（编辑器按条目整体切换，不拆开编辑）。</summary>
public sealed record OrcTemplateEntry(string Id, string Label, string Slot, string? Hook, OrcTriggerGuard Guard);

/// <summary>op 编辑面元数据：哪些参数字段对该 op 有效（UI 按此显隐；种子默认值也在此）。</summary>
public sealed record OrcOpInfo(
    string Id, string Label, string Group, bool Target, bool Amount, bool Count, bool AttackDefense,
    bool Keyword, bool Name, bool Until, bool Zone, int AmountMin, int AmountMax, string AmountLabel);

/// <summary>单条结构化效果的编译产物（Error 非空＝编译失败，Snapshots 为空）。</summary>
public sealed record OrcEditedEffect(
    OrcEffectDraft Draft, IReadOnlyList<EffectSnapshot> Snapshots, string? JsonText, string? Error);

public static class OrcEffectEditing
{
    // ───────────────────────── 目录 ─────────────────────────

    /// <summary>触发条目全集（解析器产出面；编辑器下拉按此成对切换模板＋守卫）。</summary>
    public static readonly IReadOnlyList<OrcTemplateEntry> Templates =
    [
        new("deploy_basic", "部署时", "on_deploy", "unit.deployed", OrcTriggerGuard.SelfUnit),
        new("played_basic", "打出时", "on_event", "card.played", OrcTriggerGuard.SelfCard),
        new("death_basic", "亡计（被消灭时）", "on_event", "card.died", OrcTriggerGuard.SelfCard),
        new("turn_start_basic", "友方回合开始时", "on_event", "turn.start", OrcTriggerGuard.OwnerSameByPlayer),
        new("turn_end_basic", "友方回合结束时", "on_event", "turn.end", OrcTriggerGuard.OwnerSameByPlayer),
        new("damaged_basic", "受到伤害时（本单位）", "on_event", "card.damaged", OrcTriggerGuard.SelfCard),
        new("damage_dealt_basic", "造成伤害时（本单位）", "on_event", "unit.damage.dealt", OrcTriggerGuard.SelfUnit),
        new("acted_basic", "攻击后（本单位）", "on_event", "unit.acted", OrcTriggerGuard.SelfUnit),
        new("attack_basic", "攻击时（特化模板）", "on_event", null, OrcTriggerGuard.None),
        new("deploy_listener", "友方单位部署时（监听）", "on_event", "unit.deployed", OrcTriggerGuard.OwnerSame),
        new("killed_basic", "友方单位被消灭时（监听）", "on_event", "card.died", OrcTriggerGuard.OwnerSame),
        new("damaged_basic", "友方单位受到伤害时（监听）", "on_event", "card.damaged", OrcTriggerGuard.OwnerSame),
    ];

    /// <summary>按（模板 id，守卫）取条目；找不到＝该组合不合法。</summary>
    public static OrcTemplateEntry? Template(string id, OrcTriggerGuard guard) =>
        Templates.FirstOrDefault(t => t.Id == id && t.Guard == guard);

    /// <summary>可加 op 全集（17 个；解析器产出并编译往返的面）。</summary>
    public static readonly IReadOnlyList<OrcOpInfo> Ops =
    [
        new("damage", "造成伤害", "常用", true, true, false, false, false, false, false, false, 1, 99, "伤害"),
        new("draw", "抽牌", "常用", false, false, true, false, false, false, false, false, 1, 10, "张数"),
        new("buff", "属性增减", "常用", true, false, false, true, false, false, true, false, -20, 20, ""),
        new("destroy", "消灭", "常用", true, false, false, false, false, false, false, false, 0, 0, ""),
        new("pin", "压制", "常用", true, false, false, false, false, false, false, false, 0, 0, ""),
        new("silence", "抑制", "常用", true, false, false, false, false, false, false, false, 0, 0, ""),
        new("costMod", "行动花费修正", "资源", true, true, false, false, false, false, true, false, -20, 20, "行动费"),
        new("gainPoint", "获得指挥点", "资源", false, true, false, false, false, false, false, false, 1, 10, "点数"),
        new("losePoint", "失去指挥点", "资源", false, true, false, false, false, false, false, false, 1, 10, "点数"),
        new("gainSlot", "获得指挥点槽", "资源", false, true, false, false, false, false, false, false, 1, 10, "槽位"),
        new("loseSlot", "失去指挥点槽", "资源", false, true, false, false, false, false, false, false, 1, 10, "槽位"),
        new("grant", "获得词条", "词条", true, false, false, false, true, false, false, false, 0, 99, ""),
        new("addToHand", "加入手牌（指名卡）", "牌库", false, false, true, false, false, true, false, false, 0, 0, ""),
        new("shuffleIn", "洗入卡组（指名卡）", "牌库", false, false, false, false, false, true, false, false, 0, 0, ""),
        new("move", "移动", "牌库", true, false, false, false, false, false, false, true, 0, 0, ""),
        new("reveal", "揭示", "牌库", true, false, false, false, false, false, false, false, 0, 0, ""),
        new("counter.add","计数器增加","对局变量",false,true,false,false,false,false,false,false,0,1000000,"增加"),
        new("counter.sub","计数器减少","对局变量",false,true,false,false,false,false,false,false,0,1000000,"减少"),
        new("counter.set","计数器设值","对局变量",false,true,false,false,false,false,false,false,0,1000000,"设为"),
        new("upgrade", "升为老兵", "牌库", true, false, false, false, false, false, false, false, 0, 0, ""),
    ];

    public static OrcOpInfo? Op(string id) => Ops.FirstOrDefault(o => o.Id == id);

    /// <summary>词条选择面（grant 用）：引擎内建词条 id 即中文。</summary>
    public static readonly IReadOnlyList<UiDiyChoice> KeywordChoices = Orc.Game.Cards.KeywordIds.All
        .Select(id => new UiDiyChoice(id, id)).ToArray();

    /// <summary>选靶方式 / 阵营 / 区域 / 期限 / 条件词表（引擎 selector 与条件面的合法取值）。</summary>
    public static readonly IReadOnlyList<UiDiyChoice> Sels =
        [new("self", "自己"), new("all", "所有"), new("one", "单个"), new("random", "随机")];
    public static readonly IReadOnlyList<UiDiyChoice> Sides =
        [new("friendly", "友方"), new("enemy", "敌方"), new("both", "双方")];
    public static readonly IReadOnlyList<UiDiyChoice> SelZones =
        [new("", "不限区域"), new("frontline", "前线"), new("support", "支援阵线")];
    public static readonly IReadOnlyList<UiDiyChoice> UnitTypes =
        [new("", "不限兵种"), .. OrcText.Wordbook.EngineUnitTypes.GroupBy(p => p.Value)
            .Select(g => new UiDiyChoice(g.Key, g.Key == "artillery" ? "火炮" : g.First().Key))];
    public static readonly IReadOnlyList<UiDiyChoice> Factions =
        [new("", "不限阵营"), new("Soviet", "苏联"), new("Germany", "德国"), new("USA", "美国"), new("Britain", "英国")];
    public static readonly IReadOnlyList<UiDiyChoice> UntilChoices =
        [new("", "永久"), new(Orc.Game.EffectParsing.Dsl.Untils.TurnEnd, "本回合结束"),
         new(Orc.Game.EffectParsing.Dsl.Untils.NextOwnerTurnStart, "下个己方回合开始")];
    public static readonly IReadOnlyList<UiDiyChoice> Conditions =
        [new("", "无条件"), new(DslCondition.OwnerSame, "事件归属同主"), new(DslCondition.OwnerDifferent, "事件归属异主"),
         new(DslCondition.OwnerSameByPlayer, "事件玩家同主"), new(DslCondition.OwnerDifferentByPlayer, "事件玩家异主"),
         new(DslCondition.EventCardFilter, "事件卡过滤…"), new(DslCondition.OwnerControlsType,"宿主控制的兵种数量…")];
    public static readonly IReadOnlyList<UiDiyChoice> ConditionDims =
        [new("keyword", "词条"), new("tag", "子类别"), new("category", "卡类型"), new("faction", "阵营"), new("name", "卡名")];
    public static readonly IReadOnlyList<UiDiyChoice> OpZones =
        [new("frontline", "前线"), new("support", "支援阵线")];

    // ───────────────────────── 配方（从常见效果开始） ─────────────────────────

    /// <summary>种子配方（对齐 Electron 检查器的四个例子，落在引擎可编译面）。
    /// 单位卡插入时 deploy_basic 自动换 played_basic（UI 负责，见 DiyEditorScreen）。</summary>
    public static readonly IReadOnlyList<(string Name, string Hint, OrcEffectDraft Effect)> Recipes =
    [
        ("部署时抽两张牌", "可以把「2」改成想抽的张数。",
         new OrcEffectDraft { Ops = [new OrcOpDraft { Op = "draw", Count = 2 }] }),
        ("部署时让自己 +1/+1", "分别修改攻击力和防御力，数值可自行调整。",
         new OrcEffectDraft
         {
             Ops = [new OrcOpDraft { Op = "buff", Sel = "self", Attack = 1, Defense = 1 }]
         }),
        ("对单个敌方单位造成 2 点伤害", "打出卡牌前选目标；单位在部署时生效，指令在使用时生效。",
         new OrcEffectDraft
         {
             Ops = [new OrcOpDraft { Op = "damage", Sel = "one", Side = "enemy", Amount = 2 }]
         }),
        ("压制一个敌方单位", "被压制的单位不能行动，直到解除压制。",
         new OrcEffectDraft
         {
             Ops = [new OrcOpDraft { Op = "pin", Sel = "one", Side = "enemy" }]
         }),
    ];

    /// <summary>eventCard 过滤条件种类（引擎 DslCondition.EventCardFilter 的桥内转发，UI 装配禁引引擎类型）。</summary>
    public const string EventCardFilter = DslCondition.EventCardFilter;
    public const string OwnerControlsType = DslCondition.OwnerControlsType;
    public static IReadOnlyList<UiDiyChoice> Comparisons {get;} = new[]{">=",">","<=","<","=="}.Select(s=>new UiDiyChoice(s,s)).ToArray();

    /// <summary>grant 词条默认种子（引擎词条表首项）。</summary>
    public static string DefaultKeyword => Orc.Game.Cards.KeywordIds.All[0];

    /// <summary>op 摘要（行头 + 测试断言共用；中文、可读）。</summary>
    public static string Summarize(OrcOpDraft op)
    {
        var info = Op(op.Op);
        var parts = new List<string>();
        if(op.Op.StartsWith("counter.",StringComparison.Ordinal)||op.AmountFromCounter)parts.Add("计数器「"+(op.Counter??"未命名")+"」"+(op.AmountFromCounter?"作为数值":""));
        if (op.Sel is { } sel && sel != "self")
        {
            var head = Sels.FirstOrDefault(s => s.Id == sel)?.Label ?? sel;
            var side = Sides.FirstOrDefault(s => s.Id == op.Side)?.Label;
            parts.Add(head + (side ?? "") + (sel == "random" ? $" {op.SelCount ?? 1} 个" : ""));
        }
        if (op.Filter is { } f)
        {
            var dims = new List<string>();
            if (!string.IsNullOrEmpty(f.UnitType)) dims.Add("兵种＝" + (UnitTypes.FirstOrDefault(u => u.Id == f.UnitType)?.Label ?? f.UnitType));
            if (!string.IsNullOrEmpty(f.Keyword)) dims.Add("词条＝" + f.Keyword);
            if (!string.IsNullOrEmpty(f.Faction)) dims.Add("阵营＝" + (Factions.FirstOrDefault(x => x.Id == f.Faction)?.Label ?? f.Faction));
            if (f.ExcludeSelf) dims.Add("排除自身");
            if (dims.Count > 0) parts.Add("限 " + string.Join("，", dims));
        }
        if (op.Amount is { } amount) parts.Add((info?.AmountLabel is { Length: > 0 } label ? label : "数值") + " " + amount);
        if (op.Count is { } count) parts.Add(count + " 张");
        if (op.Attack is { } attack || op.Defense is { } defense)
            parts.Add($"攻 {Delta(op.Attack)}/防 {Delta(op.Defense)}");
        if (op.Keyword is { } keyword) parts.Add("词条 " + keyword + (op.Value is { } value && value != 0 ? $"（参值 {value}）" : ""));
        if (!string.IsNullOrEmpty(op.Name)) parts.Add("「" + op.Name + "」");
        if (op.OpZone is { } zone) parts.Add("落点 " + (zone == "frontline" ? "前线" : "支援阵线"));
        if (!string.IsNullOrEmpty(op.Until))
            parts.Add("期限 " + (op.Until == Untils.TurnEnd ? "本回合结束" : "下个己方回合开始"));
        if(op.Condition==OwnerControlsType) parts.Add("条件 宿主控制 "+(op.CondComparison??">=")+" "+(op.CondValue??0)+" 个"+string.Join("/",(op.CondUnitTypes??[]).Select(t=>UnitTypes.FirstOrDefault(u=>u.Id==t)?.Label??t)));
        else if (!string.IsNullOrEmpty(op.Condition)) parts.Add("条件 " + op.Condition + (op.ConditionRaw is { } raw ? "（" + raw + "）" : ""));
        return (info?.Label ?? op.Op) + (parts.Count > 0 ? "：" + string.Join("，", parts) : "");
    }

    private static string Delta(int? value) => value switch { null => "0", > 0 => "+" + value, _ => value.Value.ToString() };

    /// <summary>op 种子（对齐 Electron 的 seed：按目录给合法默认值）。</summary>
    public static OrcOpDraft Seed(string opId)
    {
        var info = Op(opId) ?? throw new InvalidDataException("未知动作：" + opId);
        var draft = new OrcOpDraft { Op = opId };
        if (info.Target) { draft = draft with { Sel = "one", Side = "enemy" }; }
        if (info.Amount) draft = draft with { Amount = Math.Max(1, info.AmountMin) };
        if (info.Count) draft = draft with { Count = 1 };
        if (info.AttackDefense) draft = draft with { Attack = 1, Defense = 1 };
        if (info.Keyword) draft = draft with { Keyword = Orc.Game.Cards.KeywordIds.All[0] };
        if (info.Name) draft = draft with { Name = "" };
        if (info.Zone) draft = draft with { OpZone = "frontline" };
        return draft;
    }

    // ───────────────────────── 转换：DTO ↔ 引擎 DSL ─────────────────────────

    /// <summary>编辑面 DTO → 解析产物（编译入口）。取值不合法抛 InvalidDataException（可读诊断）。</summary>
    public static OrcParsedEffect ToParsed(OrcEffectDraft draft)
    {
        if(draft.DesktopJson is {Length:>0} graph) return DesktopNativeLowering.Lower(graph);
        var entry = Template(draft.Template, ParseGuard(draft.Guard))
            ?? throw new InvalidDataException($"触发组合不合法：{draft.Template}（{draft.Guard}）——请重新选择触发时机");
        if (draft.Ops.Length == 0) throw new InvalidDataException($"{entry.Label}：至少需要一个动作");
        var ops = new List<DslOp>();
        foreach (var op in draft.Ops)
        {
            if (Op(op.Op) is null) throw new InvalidDataException($"{entry.Label}：未知动作「{op.Op}」");
            ops.Add(ToDslOp(op));
        }
        return new(entry.Id, entry.Slot, ops, entry.Hook, entry.Guard,NativeGenerated:draft.Ops.Any(o=>o.Op.StartsWith("counter.",StringComparison.Ordinal)||o.AmountFromCounter));
    }

    /// <summary>解析产物 → 编辑面 DTO（解析替换/追加用；未知模板拒绝——编辑器不伪装能编辑一切）。</summary>
    public static OrcEffectDraft ToDraft(OrcParsedEffect effect)
    {
        if(effect.DesktopJson is {Length:>0} graph) return new() {Template=effect.TemplateId,DesktopJson=graph};
        var entry = Templates.FirstOrDefault(t => t.Id == effect.TemplateId && t.Guard == effect.Guard)
            ?? throw new InvalidDataException($"触发「{OrcInspector.TriggerLabel(effect.TemplateId)}」不在编辑面内，无法转为结构编辑");
        return new OrcEffectDraft
        {
            Template = entry.Id, Hook = entry.Hook, Guard = entry.Guard.ToString(),
            Ops = effect.Ops.Select(ToOpDraft).ToArray(),
        };
    }

    private static OrcTriggerGuard ParseGuard(string name) =>
        Enum.TryParse<OrcTriggerGuard>(name, ignoreCase: false, out var guard) ? guard
            : throw new InvalidDataException("触发守卫不合法：" + name);

    private static DslOp ToDslOp(OrcOpDraft op)
    {
        var selector = BuildSelector(op);
        var condition = BuildCondition(op);
        if(op.Op.StartsWith("counter.",StringComparison.Ordinal)||op.AmountFromCounter)return OrcCounterCodegen.Generate(op,condition);
        return new DslOp(op.Op,
            target: selector,
            amount: op.Amount,
            count: op.Count,
            attack: op.Attack,
            defense: op.Defense,
            keyword: NullIfEmpty(op.Keyword),
            zone: NullIfEmpty(op.OpZone),
            name: NullIfEmpty(op.Name),
            condition: condition,
            until: NullIfEmpty(op.Until),
            value: op.Value);
    }

    private static DslSelector? BuildSelector(OrcOpDraft op)
    {
        if (NullIfEmpty(op.Sel) is not { } sel) return null;
        if (sel == "self") return new DslSelector("self");
        // 引擎口径（解析器同款）：one + 双方 → any。
        var side = sel == "one" && op.Side == "both" ? "any" : NullIfEmpty(op.Side) ?? "friendly";
        var filter = BuildFilter(op.Filter);
        return new DslSelector(sel, side, NullIfEmpty(op.SelZone), filter,
            sel == "random" ? op.SelCount ?? 1 : null);
    }

    private static DslFilter? BuildFilter(OrcFilterDraft? filter)
    {
        if (filter is null) return null;
        if (string.IsNullOrWhiteSpace(filter.UnitType) && string.IsNullOrWhiteSpace(filter.Keyword)
            && string.IsNullOrWhiteSpace(filter.Faction) && !filter.ExcludeSelf) return null;
        return new DslFilter(NullIfEmpty(filter.UnitType), NullIfEmpty(filter.Keyword),
            NullIfEmpty(filter.Faction), filter.ExcludeSelf);
    }

    private static DslCondition? BuildCondition(OrcOpDraft op)
    {
        if (NullIfEmpty(op.Condition) is not { } kind) return null;
        if(!Conditions.Any(c=>c.Id==kind))throw new InvalidDataException("未知条件："+kind);
        if(kind==OwnerControlsType)
        {
            if(op.CondUnitTypes is not {Length:>0} types || types.Any(t=>t.Length==0||!UnitTypes.Any(u=>u.Id==t)))throw new InvalidDataException("条件：至少选择一个有效兵种");
            if(!Comparisons.Any(c=>c.Id==(op.CondComparison??">="))||op.CondValue is <0 or >20)throw new InvalidDataException("条件：比较运算或数量无效");
            return new DslCondition(kind,unitTypes:types.ToArray(),comparison:op.CondComparison??">=",value:op.CondValue??0);
        }
        return kind == DslCondition.EventCardFilter
            ? new DslCondition(kind, string.IsNullOrWhiteSpace(op.ConditionRaw) ? "keyword:" : op.ConditionRaw)
            : new DslCondition(kind);
    }

    private static OrcOpDraft ToOpDraft(DslOp op)
    {
        return new OrcOpDraft
        {
            Op = op.Op,
            Sel = op.Target?.Sel,
            Side = op.Target?.Side == "any" ? "both" : op.Target?.Side,
            SelZone = op.Target?.Zone,
            SelCount = op.Target?.Count,
            Filter = op.Target?.Filter is { } f
                ? new OrcFilterDraft { UnitType = f.UnitType, Keyword = f.Keyword, Faction = f.Faction, ExcludeSelf = f.ExcludeSelf }
                : null,
            Amount = op.Amount, Count = op.Count, Attack = op.Attack, Defense = op.Defense,
            Keyword = op.Keyword, Value = op.Value, OpZone = op.Zone, Name = op.Name, Until = op.Until,
            Condition = op.Condition?.Kind,
            CondUnitTypes=op.Condition?.UnitTypes?.ToArray(),CondComparison=op.Condition?.Comparison,CondValue=op.Condition?.Value,
            ConditionRaw = op.Condition?.Kind == DslCondition.EventCardFilter ? op.Condition.Raw : op.Condition?.Raw,
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ───────────────────────── 编译 ─────────────────────────

    /// <summary>逐条编译（每效果每 op 独立实例——与导入同一已验证语义）。单条失败不拖垮其余条目。</summary>
    public static IReadOnlyList<OrcEditedEffect> Compile(string cardId, IReadOnlyList<OrcEffectDraft> drafts)
    {
        var results = new List<OrcEditedEffect>();
        for(var effectIndex=0;effectIndex<drafts.Count;effectIndex++)
        {
            var draft=drafts[effectIndex];
            string? error = null;
            var snapshots = new List<EffectSnapshot>();
            var jsons = new List<System.Text.Json.JsonElement>();
            try
            {
                if(draft.ExtraFields is {Count:>0}||draft.Ops.Any(o=>o.ExtraFields is {Count:>0}||o.Filter?.ExtraFields is {Count:>0}))
                    throw new InvalidDataException("原生效果含尚未适配的扩展参数；原始值已保留，仅可保存草稿。");
                if(draft.TextCardType is not null)
                {
                    var snapshot=BackendTextAuthoring.Compile(cardId+".e"+effectIndex,draft);snapshots.Add(snapshot);
                    using var raw=System.Text.Json.JsonDocument.Parse(PrefabJson.SerializeForDisk(snapshot));jsons.Add(raw.RootElement.Clone());
                }
                else if(draft.PrefabJson is { } prefabJson)
                {
                    var errors=DiyInjectionCatalog.Read(prefabJson).Where(i=>i.Error is not null).Select(i=>i.Error).ToArray();
                    if(errors.Length>0)throw new InvalidDataException(string.Join("；",errors));
                    var snapshot=PrefabJson.Deserialize(prefabJson);snapshots.Add(snapshot);
                    using var raw=System.Text.Json.JsonDocument.Parse(prefabJson);jsons.Add(raw.RootElement.Clone());
                }
                else
                {
                var parsed = ToParsed(draft);
                for(var opIndex=0;opIndex<parsed.Ops.Count;opIndex++)
                {
                    var op=parsed.Ops[opIndex];
                    var (one, oneJson) = OrcImportService.CompileOrcEffect(cardId+".e"+effectIndex+".op"+opIndex, parsed with { Ops = [op] });
                    snapshots.Add(one);
                    using var doc = System.Text.Json.JsonDocument.Parse(oneJson);
                    jsons.Add(doc.RootElement.Clone());
                }
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or ArgumentException or InvalidOperationException
                or FormatException or OrcText.OrcReject)
            {
                error = e.Message; snapshots.Clear(); jsons.Clear();
            }
            results.Add(new(draft, snapshots, error is null ? System.Text.Json.JsonSerializer.Serialize(jsons) : null, error));
        }
        return results;
    }

    /// <summary>全部条目的用户可读问题（空＝全绿）。</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<OrcEditedEffect> compiled) => compiled
        .Where(e => e.Error is not null)
        .Select(e => $"效果（{TemplateLabel(e.Draft)}）：{e.Error}").ToArray();

    private static string TemplateLabel(OrcEffectDraft draft) =>
        Template(draft.Template, Enum.TryParse<OrcTriggerGuard>(draft.Guard, out var g) ? g : OrcTriggerGuard.None)?.Label ?? draft.Template;
}
