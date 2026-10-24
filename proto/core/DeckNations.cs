namespace Kards.Ui.Core;

/// <summary>
/// 构筑口径的国家标识归一 —— **唯一真源**。
///
/// <para>
/// 存在两套写法，指向同一个国家：
/// <list type="bullet">
/// <item>原始卡池（source）用目录名：<c>av76</c> / <c>deran</c> / <c>USG</c> / <c>UN</c>——它们分别是苏 / 德 / 美 / 英的旧卡池；</item>
/// <item>卡包（pack）用引擎阵营标识：<c>germany</c> / <c>soviet</c> / <c>usa</c> / <c>britain</c> / <c>japan</c> …</item>
/// </list>
/// 引擎侧在建立对局配置时早已把旧卡池折叠成所属国家（<c>OrcMatchSession</c> 的阵营映射）。
/// 所以 UI 若按裸字符串比较，就会出现「构筑界面认为主国 av76 与盟国 soviet 是两个国家、
/// 保存通过；引擎认为它们是同一个国家、拒绝开局」——即“主盟国一致时无法对局”。
/// </para>
///
/// <para>**约束**：任何“这两个国家是不是同一个”的判断都必须走 <see cref="Same"/>，
/// 任何“这张卡属于哪个国家”的判断都必须走 <see cref="Canonical"/>。
/// 自建国（DIY）标识不在映射表内，原样返回，不做大小写改写。</para>
/// </summary>
public static class DeckNations
{
    /// <summary>旧卡池目录名 → 所属国家（与引擎侧对局配置的阵营映射保持一致）。</summary>
    private static readonly Dictionary<string, string> LegacyPools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["av76"] = "soviet",
        ["deran"] = "germany",
        ["USG"] = "usa",
        ["UN"] = "britain",
    };

    /// <summary>引擎内置阵营标识（规范小写；与 <c>Orc.Game.Cards.Faction.All</c> 同集）。</summary>
    private static readonly string[] Factions =
    [
        "germany", "soviet", "usa", "britain", "japan", "france", "italy", "poland", "finland", "anzac", "neutral",
    ];

    /// <summary>是否为旧卡池伪国家标识（同一国家的另一套写法）。</summary>
    public static bool IsLegacyPool(string? id) => id is not null && LegacyPools.ContainsKey(id);

    /// <summary>
    /// 规范国籍：旧卡池折叠到所属国家、内置阵营统一小写、未知（自建国 / 空 / null）原样返回。
    /// </summary>
    public static string Canonical(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        if (LegacyPools.TryGetValue(id, out var faction)) return faction;
        foreach (var known in Factions)
            if (string.Equals(known, id, StringComparison.OrdinalIgnoreCase)) return known;
        return id;
    }

    /// <summary>两个标识是否指向同一个国家（旧卡池与卡包同国 = true）。</summary>
    public static bool Same(string? a, string? b)
        => string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);
}
