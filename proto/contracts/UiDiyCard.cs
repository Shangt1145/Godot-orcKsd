namespace Kards.Ui.Contracts;

/// <summary>Editable values only. Compilation, selection and effect execution belong to the bridge/engine.</summary>
public sealed record DiyEffectDraft
{
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
    public string Rarity { get; init; } = "iron";
    public string CardType { get; init; } = "unit";
    public string UnitType { get; init; } = "infantry";
    public int Cost { get; init; } = 1;
    public int OpCost { get; init; } = 1;
    public int Attack { get; init; } = 2;
    public int Defense { get; init; } = 3;
    public string[] Keywords { get; init; } = [];
    public int Armor { get; init; } = 1;
    public string ArtCardId { get; init; } = "";
    /// <summary>
    /// 自导入卡图文件名（存于卡库旁 art/ 目录，内容经 CheckShape 校验的受限文件名）。
    /// 非空时优先于 <see cref="ArtCardId"/>（互斥口径：导入图覆盖素材引用）。
    /// </summary>
    public string ArtFile { get; init; } = "";
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
    /// <summary>
    /// OrC Inspector 编辑面：结构化效果（触发模板＋守卫＋op 序列），**编译真源**。
    /// 非空时效果由此编译（引擎 31 触发模板面），EffectText/Effects 行均不参与编译。
    /// 空且 EffectText 非空＝旧版文本模式（解析器演进后重编译自动跟随）。
    /// </summary>
    public OrcEffectDraft[] OrcEffects { get; init; } = [];
    public DiyCardDraft Snapshot() => this with
    {
        Keywords = Keywords.ToArray(), Effects = Effects.ToArray(),
        OrcEffects = OrcEffects.Select(e => e.Snapshot()).ToArray()
    };
}

/// <summary>一条结构化效果：触发模板 ＋ 守卫 ＋ op 序列（编辑器持久化形态，纯 POCO）。</summary>
public sealed record OrcEffectDraft
{
    public string Template { get; init; } = "deploy_basic";
    /// <summary>触发钩子（如 unit.deployed）；null＝走引擎原版特化模板（attack_basic）。</summary>
    public string? Hook { get; init; }
    /// <summary>触发守卫（OrcTriggerGuard 枚举名）。模板与守卫成对合法，编辑器按目录成对给出。</summary>
    public string Guard { get; init; } = "SelfUnit";
    public OrcOpDraft[] Ops { get; init; } = [];
    public OrcEffectDraft Snapshot() => this with { Ops = Ops.ToArray() };
}

/// <summary>结构化 op（字段面按引擎 DslOp；编辑器只暴露编译面支持且可持久化的字段）。</summary>
public sealed record OrcOpDraft
{
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
}

/// <summary>结构化筛选。**故意不含 threshold 维**：引擎 FilterDto 无该序列化维，JSON 往返必丢——
/// 编辑器不产不能持久化的效果（解析器对阈值句同样拒绝）。</summary>
public sealed record OrcFilterDraft
{
    public string? UnitType { get; init; }
    public string? Keyword { get; init; }
    public string? Faction { get; init; }
    public bool ExcludeSelf { get; init; }
}

public sealed record UiDiyChoice(string Id, string Label);
public sealed record UiDiyValidation(UiCardDefinition? Card, string? EngineCardJson, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0 && Card is not null && EngineCardJson is not null;
}
