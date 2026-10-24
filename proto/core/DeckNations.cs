namespace Kards.Ui.Core;

/// <summary>
/// 构筑口径的国家标识归一 —— **唯一真源**。
///
/// <para>
/// 这里只做一件事：把**同一个国家的不同写法**归到一起。别的什么都不做。
/// <list type="bullet">
/// <item>同义写法：中文名与引擎 id，如 <c>苏联</c> ↔ <c>soviet</c>、<c>德国</c> ↔ <c>germany</c>；</item>
/// <item>大小写：<c>USG</c> ↔ <c>usg</c>（引擎 id 规范为全小写）。</item>
/// </list>
/// </para>
///
/// <para>
/// **每个卡池名都是一个独立的国家**，没有“新旧”之分：<c>av76</c> / <c>deran</c> / <c>USG</c> /
/// <c>UN</c> 各自就是自己的国家，彼此之间、与 <c>soviet</c> / <c>germany</c> / <c>usa</c> /
/// <c>britain</c> 之间都**不是同一个国家**。星盟的卡面注释写着「协约国 = USG + AV76」，
/// 已经把这种并列关系写在数据里了。
/// </para>
///
/// <para>
/// 这些国家在引擎侧走 <c>Faction.RegisterCustom</c> 注册为自定义主国（引擎 id 要求全小写 ASCII），
/// 注册点＝<c>OrcEngineHost.Initialize</c>。自建国（DIY）标识不在映射表内，原样返回。
/// </para>
///
/// <para>**约束**：任何“这两个国家是不是同一个”的判断都必须走 <see cref="Same"/>，
/// 任何“这张卡属于哪个国家”的判断都必须走 <see cref="Canonical"/>。</para>
/// </summary>
public static class DeckNations
{
    /// <summary>引擎内置阵营标识（规范小写；与 <c>Orc.Game.Cards.Faction.All</c> 同集）。</summary>
    private static readonly string[] Factions =
    [
        "germany", "soviet", "usa", "britain", "japan", "france", "italy", "poland", "finland", "anzac", "neutral",
    ];

    /// <summary>
    /// 内置阵营之外的独立国家（**书写形式**，即界面与存档里用的那个名字）。
    /// 它们与内置阵营地位相同，只是不在引擎的常量表里；引擎 id 由 <see cref="Canonical"/> 归一
    /// （引擎要求全小写 ASCII，故 <c>USG</c> / <c>UN</c> 的引擎 id 是 <c>usg</c> / <c>un</c>）。
    /// <c>NSF</c>＝星盟（卡池目录名是中文「星盟」，见 <see cref="Synonyms"/>）。
    /// </summary>
    public static IReadOnlyList<string> CustomNations { get; } = ["av76", "deran", "USG", "UN", "NSF"];

    /// <summary>同一个国家的另一种写法（中文名 → 引擎 id）。</summary>
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["德国"] = "germany",
        ["苏联"] = "soviet",
        ["美国"] = "usa",
        ["英国"] = "britain",
        ["日本"] = "japan",
        ["法国"] = "france",
        ["意大利"] = "italy",
        ["波兰"] = "poland",
        ["芬兰"] = "finland",
        ["澳新联军"] = "anzac",
        ["中立"] = "neutral",
        // 星盟＝NSF（同一个国家的两种写法）。卡池目录与卡面 `set` 用中文「星盟」，
        // 引擎 id 必须全小写 ASCII，故取 nsf。
        ["星盟"] = "nsf",
    };

    private static readonly Dictionary<string, string> CanonicalIds = BuildCanonical();

    private static Dictionary<string, string> BuildCanonical()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var faction in Factions) map[faction] = faction;
        // 书写形式 → 引擎 id（全小写；引擎 RegisterCustom 只收小写 ASCII）。
        foreach (var nation in CustomNations) map[nation] = nation.ToLowerInvariant();
        return map;
    }

    /// <summary>是否为内置阵营之外的独立国家（需引擎自定义主国注册）。</summary>
    public static bool IsCustomNation(string? id)
        => id is not null && CustomNations.Contains(id.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 规范国籍：同义写法归到引擎 id、大小写统一小写；未知（自建国 / 空 / null）原样返回。
    /// **不跨国家归并**——<c>av76</c> 归一后仍是 <c>av76</c>，不是 <c>soviet</c>。
    /// </summary>
    public static string Canonical(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var trimmed = id.Trim();
        if (Synonyms.TryGetValue(trimmed, out var synonym)) return synonym;
        return CanonicalIds.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    /// <summary>两个标识是否指向同一个国家（同义写法 / 大小写不同 = true）。</summary>
    public static bool Same(string? a, string? b)
        => string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);
}
