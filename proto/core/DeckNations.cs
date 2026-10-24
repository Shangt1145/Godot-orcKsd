namespace Kards.Ui.Core;

/// <summary>
/// 构筑口径的国家标识归一 —— **唯一真源**。
///
/// <para>
/// 这里**只归一「同一个国家的不同写法」**（大小写 / 引擎规范 id），绝不把两个国家折成一个。
/// 原始卡池 <c>av76</c> / <c>deran</c> / <c>USG</c> / <c>UN</c> 是**独立国家**，
/// 与引擎内置的 <c>germany</c> / <c>soviet</c> / <c>usa</c> / <c>britain</c> 不是同一国：
/// 星盟的卡面注释自己就写着「协约国 = USG + AV76」——它们是并列的两家，不是别名。
/// </para>
///
/// <para>
/// 这些原始卡池在引擎侧走 <c>Faction.RegisterCustom</c> 注册为自定义主国
/// （引擎 id 要求全小写 ASCII，故 <c>USG</c> / <c>UN</c> 的规范 id 是 <c>usg</c> / <c>un</c>），
/// 注册点＝<c>OrcEngineHost.Initialize</c>；UI 与引擎因此对「哪些国家存在」看法一致。
/// </para>
///
/// <para>**约束**：任何“这两个国家是不是同一个”的判断都必须走 <see cref="Same"/>，
/// 任何“这张卡属于哪个国家”的判断都必须走 <see cref="Canonical"/>。
/// 自建国（DIY）标识不在映射表内，原样返回，不做改写。</para>
/// </summary>
public static class DeckNations
{
    /// <summary>引擎内置阵营标识（规范小写；与 <c>Orc.Game.Cards.Faction.All</c> 同集）。</summary>
    private static readonly string[] Factions =
    [
        "germany", "soviet", "usa", "britain", "japan", "france", "italy", "poland", "finland", "anzac", "neutral",
    ];

    /// <summary>
    /// 原始卡池国家（引擎侧的规范 id，全小写 ASCII）——**独立国家**，需要注册为自定义主国。
    /// 顺序即内置卡池的展示序。
    /// </summary>
    public static IReadOnlyList<string> SourceNations { get; } = ["av76", "deran", "usg", "un"];

    private static readonly Dictionary<string, string> CanonicalIds = BuildCanonical();

    private static Dictionary<string, string> BuildCanonical()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var faction in Factions) map[faction] = faction;
        foreach (var nation in SourceNations) map[nation] = nation;
        return map;
    }

    /// <summary>是否为原始卡池国家（独立国家，需引擎自定义主国注册）。</summary>
    public static bool IsSourceNation(string? id)
        => id is not null && SourceNations.Contains(id.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 规范国籍：已知国家统一成引擎 id（小写）；未知（自建国 / 空 / null）原样返回。
    /// **不跨国家折叠**——<c>av76</c> 归一后仍是 <c>av76</c>，不是 <c>soviet</c>。
    /// </summary>
    public static string Canonical(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var trimmed = id.Trim();
        return CanonicalIds.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    /// <summary>两个标识是否指向同一个国家（仅等同一种写法的不同大小写）。</summary>
    public static bool Same(string? a, string? b)
        => string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);
}
