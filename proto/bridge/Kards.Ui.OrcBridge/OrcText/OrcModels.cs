using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge.OrcText;

// ─────────────────────────────────────────────────────────────────────────────
// OrC 词表与解析产物模型。词表真源＝引擎面：兵种＝UnitType.All（引擎 5 值），
// 词条＝KeywordIds.All（引擎内建 id 即中文）。KARDS 语料中引擎没有的词（巡地舰/
// 太空战机/工事 等）保留在 normalize 词表里（只管空白折叠），但 filter 映射时拒绝。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 解析词表（实例）：词条名表（引擎内建 + 卡包注入）＋ 可映射兵种表（引擎 5 值）。
/// normalize 词表另有静态全集（含引擎没有的 KARDS 兵种词——只管空白折叠）。
/// </summary>
internal sealed class Wordbook
{
    private Wordbook(IReadOnlyDictionary<string, string> keywordNames)
    {
        KeywordNames = keywordNames;
        MappableUnitTypes = EngineUnitTypes;
        NormalizeWords = BuildNormalizeWords(keywordNames);
    }

    /// <summary>词条中文名 → 引擎词条 id（内建 id 即中文；卡包词条经 packKeywords 注入）。</summary>
    public IReadOnlyDictionary<string, string> KeywordNames { get; }

    /// <summary>中文兵种 → 引擎 unitType（仅引擎有的 5 值；filter 可映射）。</summary>
    public IReadOnlyDictionary<string, string> MappableUnitTypes { get; }

    /// <summary>normalize 词表（语言词 + 全部兵种词 + 全部词条名）。</summary>
    public IReadOnlyCollection<string> NormalizeWords { get; }

    public string? LookupKeyword(string cn) => KeywordNames.TryGetValue(cn, out var id) ? id : null;

    public static Wordbook Create(IReadOnlyDictionary<string, string>? packKeywords)
    {
        Dictionary<string, string> table = new(StringComparer.Ordinal);
        foreach (var id in KeywordIds.All) table[id] = id; // 内建 id 即中文
        table["守护"] = KeywordIds.Guard; // js 版 aliases
        if (packKeywords != null)
            foreach (var pair in packKeywords) table[pair.Key] = pair.Value;
        return new Wordbook(table);
    }

    private static IReadOnlyCollection<string> BuildNormalizeWords(IReadOnlyDictionary<string, string> keywords)
    {
        var words = new List<string>(LanguageWords);
        words.AddRange(EngineUnitTypes.Keys);
        words.AddRange(ExtraUnitWords);
        words.AddRange(keywords.Keys);
        return words;
    }

    /// <summary>引擎兵种（filter 可映射）：中文 → 引擎 unitType 标识。</summary>
    public static readonly IReadOnlyDictionary<string, string> EngineUnitTypes = new Dictionary<string, string>
    {
        ["步兵"] = "infantry", ["坦克"] = "tank", ["火炮"] = "artillery", ["炮兵"] = "artillery",
        ["战斗机"] = "fighter", ["轰炸机"] = "bomber",
    };

    /// <summary>KARDS 语料中的其余兵种词（只进 normalize 词表；映射时拒绝并诊断）。</summary>
    public static readonly IReadOnlyCollection<string> ExtraUnitWords =
        ["太空战机", "巡地舰", "巡航舰", "工事", "建筑", "空军", "陆军", "地面单位", "地面", "太空单位", "太空"];

    /// <summary>语言词（normalize 折叠表固定成员）。</summary>
    public static readonly IReadOnlyCollection<string> LanguageWords =
        ["对战词条", "战斗词条", "相邻单位", "本单位", "此单位", "指挥点", "攻击力", "防御力"];

    /// <summary>属性词 → 视图属性名（js attributes 表）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Attributes = new Dictionary<string, string>
    {
        ["攻击力"] = "attack", ["防御力"] = "defense", ["生命值"] = "hq",
        ["行动花费"] = "opCost", ["操作费"] = "opCost",
        ["部署费用"] = "cost", ["费用"] = "cost", ["花费"] = "cost",
        ["剩余指挥点"] = "kredits", ["指挥点上限"] = "maxKredits",
    };

    /// <summary>国家词（语料口径）。</summary>
    public static readonly IReadOnlyCollection<string> Nations =
        ["英国", "美国", "德国", "苏联", "日本", "法国", "意大利", "波兰", "芬兰", "澳新军团"];
}

/// <summary>一条解析诊断（code 对齐 js 版：待核对列表按此归类）。</summary>
public sealed record OrcDiagnostic(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>解析出的单个效果：引擎触发模板 id ＋ 槽位名 ＋ 槽位 op 序列（可直交 EffectCompilationDriver.CompileDsl）。</summary>
/// <summary>
/// 解析出的单个效果：桥内合成触发模板所需的全量描述。Hook/Guard 由解析面按语义给定——
/// 引擎原版触发模板不可直接嵌入（deploy_basic 是 eventCard 面读 view.Card，而 unit.* 载荷键
/// 是 Unit，部署效果会静默空转；监听模板无归属守卫会泛触发），与 DIY 同一已验证语义：
/// effectHost 骨架 + op 级守卫。Guard＝null 的效果（attack_basic 特化视图）走引擎原模板。
/// </summary>
public sealed record OrcParsedEffect(string TemplateId, string Slot, IReadOnlyList<Orc.Game.EffectParsing.Dsl.DslOp> Ops,
    string? Hook = null, OrcTriggerGuard Guard = OrcTriggerGuard.None);

/// <summary>触发守卫口径（与引擎 DslCondition 常量一一对应）。</summary>
public enum OrcTriggerGuard
{
    None,
    /// <summary>载荷自指：unit.* 信号（view.Unit）。</summary>
    SelfUnit,
    /// <summary>载荷自指：card.* 信号（view.Card）。</summary>
    SelfCard,
    /// <summary>归属同主（事件载荷为卡/单位——监听句）。</summary>
    OwnerSame,
    /// <summary>归属同主（事件载荷为玩家——回合系信号）。</summary>
    OwnerSameByPlayer,
}

/// <summary>解析出的卡面词条声明（含参值）。</summary>
public sealed record OrcKeywordDecl(string Id, int Value, bool Quantified);

/// <summary>compile 产物：完整解析＝零诊断（与 js 版 complete 口径一致）。</summary>
public sealed record OrcCompileResult(
    string Source,
    IReadOnlyList<OrcParsedEffect> Effects,
    IReadOnlyList<OrcKeywordDecl> Keywords,
    IReadOnlyList<OrcDiagnostic> Diagnostics)
{
    /// <summary>文本与效果结构全部通过检查（不代表对战行为已验证——与 js 版口径相同）。</summary>
    public bool Complete => Diagnostics.Count == 0;
}

/// <summary>解析中途拒绝：携带用户可读原因，向上聚合为诊断（对应 js 版"未知句子/无效效果"）。</summary>
internal sealed class OrcReject(string reason) : Exception(reason);
