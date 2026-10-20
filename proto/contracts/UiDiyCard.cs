using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kards.Ui.Contracts;

/// <summary>Editable values only. Compilation, selection and effect execution belong to the bridge/engine.</summary>
public sealed record DiyEffectDraft
{
    [JsonExtensionData] public Dictionary<string,JsonElement>? ExtraFields {get;init;}
    public string Op { get; init; } = "draw";
    public string Target { get; init; } = "self";
    public int Amount { get; init; } = 1;
    public int Attack { get; init; } = 1;
    public int Defense { get; init; } = 1;
    public string Until { get; init; } = "";
}

public sealed record DiyCardDraft
{
    public string Id { get; init; } = "diy/" + Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "新卡牌";
    public string Nation { get; init; } = "av76";
    public string PackId { get; init; } = "";
    /// <summary>Local edit of a shipped card. Catalog and engine retain this original id.</summary>
    public string SourceCardId { get; init; } = "";
    public string RuntimeId => SourceCardId.Length > 0 ? SourceCardId : Id;
    public string Rarity { get; init; } = "iron";
    public string CardType { get; init; } = "unit";
    public string UnitType { get; init; } = "infantry";
    public int Cost { get; init; } = 1;
    public int OpCost { get; init; } = 1;
    public int Attack { get; init; } = 2;
    public int Defense { get; init; } = 3;
    public string[] Keywords { get; init; } = [];
    public Dictionary<string,int> KeywordValues {get;init;}=new();
    public int Armor { get; init; } = 1;
    /// <summary>旧存档兼容字段；Beta.1 不再提供现有卡图选择，新卡图只能导入。</summary>
    public string ArtCardId { get; init; } = "";
    /// <summary>
    /// 自导入卡图文件名（存于卡库旁 art/ 目录，内容经 CheckShape 校验的受限文件名）。
    /// 非空时优先于 <see cref="ArtCardId"/>（互斥口径：导入图覆盖素材引用）。
    /// </summary>
    public string ArtFile { get; init; } = "";
    public string ArtSourceFile { get; init; } = "";
    public string ArtImportMode { get; init; } = "illustration";
    public DiyArtRegion ArtRegion { get; init; } = new(0,0,1,1);
    /// <summary>自导入卡图的绘制缩放（百分比 50–300；仅对 ArtFile 生效，素材引用不缩放）。</summary>
    public int ArtScale { get; init; } = 100;
    public string Flavor { get; init; } = "";
    public DiyEffectDraft[] Effects { get; init; } = [];
    /// <summary>
    /// 规则文本（自然文本→DSL 的解析入口，兼作卡面规则文本的来源）。非空时解析结果落到
    /// <see cref="OrcEffects"/>；文本本身不随结构编辑回写（与 Electron 版口径相同：卡面文字
    /// 是给玩家看的说明，改结构不自动改文字）。
    /// </summary>
    public string EffectText { get; init; } = "";
    /// <summary>linked only for the backend's proven text round-trip subset; otherwise manual/detached.</summary>
    public string TextSyncMode {get;init;}="manual";
    /// <summary>
    /// OrC Inspector 编辑面：结构化效果（触发模板＋守卫＋op 序列），**编译真源**。
    /// 非空时效果由此编译（引擎 31 触发模板面），EffectText/Effects 行均不参与编译。
    /// 空且 EffectText 非空＝旧版文本模式（解析器演进后重编译自动跟随）。
    /// </summary>
    public OrcEffectDraft[] OrcEffects { get; init; } = [];
    /// <summary>An explicitly empty structured program must not fall back to reparsing display text.</summary>
    public bool UseStructuredEffects { get; init; }
    /// <summary>原版检查器卡面级扩展字段（含未知字段）；必须无损保存，准入时逐项校验。</summary>
    public string? DesktopCardJson { get; init; }
    public DiyCardDraft Snapshot() => this with
    {
        Keywords = Keywords.ToArray(), KeywordValues=new(KeywordValues), Effects = Effects.Select(e=>e with{ExtraFields=e.ExtraFields is null?null:new(e.ExtraFields)}).ToArray(),
        OrcEffects = OrcEffects.Select(e => e.Snapshot()).ToArray()
    };
}

/// <summary>一条结构化效果：触发模板 ＋ 守卫 ＋ op 序列（编辑器持久化形态，纯 POCO）。</summary>
public sealed record OrcEffectDraft
{
    [JsonExtensionData] public Dictionary<string,JsonElement>? ExtraFields {get;init;}
    /// <summary>Desktop OrC/Inspector 的完整声明图。存在时为唯一效果真源；未知字段也必须保留。</summary>
    public string? DesktopJson { get; init; }
    /// <summary>Imported native effect snapshot, retained losslessly and displayed read-only.</summary>
    public string? PrefabJson { get; init; }
    /// <summary>Native text compiler source. Ops remain authoritative editable data; never reparse display text.</summary>
    public string? TextCardType {get;init;}
    public string Template { get; init; } = "deploy_basic";
    /// <summary>触发钩子（如 unit.deployed）；null＝走引擎原版特化模板（attack_basic）。</summary>
    public string? Hook { get; init; }
    /// <summary>触发守卫（OrcTriggerGuard 枚举名）。模板与守卫成对合法，编辑器按目录成对给出。</summary>
    public string Guard { get; init; } = "SelfUnit";
    public OrcOpDraft[] Ops { get; init; } = [];
    public OrcEffectDraft Snapshot() => this with { Ops = Ops.Select(o=>o.Snapshot()).ToArray(),ExtraFields=ExtraFields is null?null:new(ExtraFields) };
}

/// <summary>结构化 op（字段面按引擎 DslOp；编辑器只暴露编译面支持且可持久化的字段）。</summary>
public sealed record OrcOpDraft
{
    [JsonExtensionData] public Dictionary<string,JsonElement>? ExtraFields {get;init;}
    public string Op { get; init; } = "draw";
    // 作用对象（DslSelector）。
    public string? Sel { get; init; }
    public string? Side { get; init; }
    public string? SelZone { get; init; }
    public int? SelCount { get; init; }
    public OrcFilterDraft? Filter { get; init; }
    // op 参数。
    public int? Amount { get; init; }
    public int? Count { get; init; }
    public int? Attack { get; init; }
    public int? Defense { get; init; }
    public string? Keyword { get; init; }
    public int? Value { get; init; }
    /// <summary>op 级落点区域（move：frontline/support）。</summary>
    public string? OpZone { get; init; }
    /// <summary>卡牌引用名（addToHand/shuffleIn）。</summary>
    public string? Name { get; init; }
    public string? Until { get; init; }
    /// <summary>op 级条件种类（引擎 DslCondition.Kind；无＝null）。</summary>
    public string? Condition { get; init; }
    /// <summary>条件参数（eventCard.filter 的「维度:取值」；payload 自指类为视图属性名）。</summary>
    public string? ConditionRaw { get; init; }
    public string[]? CondUnitTypes { get; init; }
    public string? CondComparison { get; init; }
    public int? CondValue { get; init; }
    public string? Counter { get; init; }
    public bool AmountFromCounter { get; init; }
    public OrcOpDraft Snapshot()=>this with {CondUnitTypes=CondUnitTypes?.ToArray(),Filter=Filter?.Snapshot(),ExtraFields=ExtraFields is null?null:new(ExtraFields)};
}

/// <summary>结构化筛选。**故意不含 threshold 维**：引擎 FilterDto 无该序列化维，JSON 往返必丢——
/// 编辑器不产不能持久化的效果（解析器对阈值句同样拒绝）。</summary>
public sealed record OrcFilterDraft
{
    [JsonExtensionData] public Dictionary<string,JsonElement>? ExtraFields {get;init;}
    public string? UnitType { get; init; }
    public string? Keyword { get; init; }
    public string? Faction { get; init; }
    public bool ExcludeSelf { get; init; }
    public OrcFilterDraft Snapshot()=>this with{ExtraFields=ExtraFields is null?null:new(ExtraFields)};
}

public sealed record UiDiyChoice(string Id, string Label);
public sealed record UiDiyValidation(UiCardDefinition? Card, string? EngineCardJson, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0 && Card is not null && EngineCardJson is not null;
}
