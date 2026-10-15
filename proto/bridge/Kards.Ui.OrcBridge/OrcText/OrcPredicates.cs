using System.Text.RegularExpressions;
using Orc.Game.EffectParsing.Dsl;

namespace Kards.Ui.OrcBridge.OrcText;

// ─────────────────────────────────────────────────────────────────────────────
// OrC 谓词层（移植自 orc.js 的 value/predicate/unitPredicate/cardPredicate）。
// 产出直接对齐引擎 filter 面（DslFilter 五维：unitType/keyword/faction/excludeSelf/threshold）；
// 引擎面装不下的因子（多词条、多阈值、复合逻辑、动态值、稀有度/费用维度）→ OrcReject 待核对。
// 数值面：引擎 DSL 数值只有静态 int——动态表达式（N 倍/加减/属性读取/查询计数/随机数）一律拒绝。
// ─────────────────────────────────────────────────────────────────────────────

internal static partial class OrcPredicates
{
    /// <summary>
    /// 静态数值（js value 的可映射子集）：阿拉伯/中文数字。动态表达式（N 倍、加减、随机范围、
    /// 属性读取、查询计数）→ OrcReject（引擎数值面只有常量）。
    /// </summary>
    public static int Value(string text)
    {
        var s = text.Trim().TrimStart('等', '同', '于'); // "等同于" 前缀（js 只剥一次；这里剥修饰词）
        var n = OrcLexical.Number(s);
        if (n.HasValue) return n.Value;
        if (DynamicPattern().IsMatch(s))
            throw new OrcReject($"动态数值或属性读取不在引擎数值面内（引擎只支持常量）：“{s}”");
        throw new OrcReject($"无法解析数值：“{s}”");
    }

    /// <summary>是否"试图表达动态值"（用于诊断措辞）。</summary>
    public static bool IsDynamic(string text) => DynamicPattern().IsMatch(text.Trim());

    /// <summary>
    /// 单位谓词 → 引擎 DslFilter（五维）。识别：兵种、（非）国家、（不）具有词条、
    /// 属性阈值比较（单维）。其余（相邻引用、复合逻辑、多词条、extraTypes）→ OrcReject。
    /// </summary>
    public static DslFilter UnitFilter(string text, Wordbook wordbook)
    {
        var s = text.Trim().TrimEnd('的');
        string? unitType = null, keyword = null, faction = null;
        string? thresholdField = null, thresholdOp = null; int? thresholdValue = null;

        // 复合逻辑连接词 → 引擎 filter 无合取/析取，整句拒绝（诊断要指出原文）。
        if (s.Contains("或者") || s.Contains('或') && tailAfterUnit(s).Contains('或')
            || s.Contains("并且") || s.Contains("且"))
            throw new OrcReject($"复合条件（且/或）超出引擎过滤面（单维过滤）：“{text}”");

        // 尾部兵种：长词优先（js types 排序同款）。
        var types = wordbook.MappableUnitTypes.OrderByDescending(p => p.Key.Length).ToList();
        var type = types.FirstOrDefault(p => s.EndsWith(p.Key));
        if (type.Value != null)
        {
            unitType = type.Value;
            s = s[..^type.Key.Length];
        }
        else if (s.EndsWith("单位")) s = s[..^2];
        else if (s.Length > 0 && !s.EndsWith("牌")) { /* 兵种可省略（"敌方总部"等由调用方处理） */ }

        // 国家（非）X（可两个"和"连接——引擎 faction 单槽，两个直接拒绝）。
        var countries = NationPattern().Match(s);
        if (countries.Success)
        {
            if (countries.Groups[3].Success)
                throw new OrcReject($"多国家过滤超出引擎过滤面（单 faction 槽）：“{text}”");
            faction = FactionId(countries.Groups[2].Value);
            if (faction is null) throw new OrcReject($"未知国家“{countries.Groups[2].Value}”");
            if (countries.Groups[1].Success)
                throw new OrcReject($"否定式国家过滤（非 X 国）超出引擎过滤面：“{text}”");
            s = s[..countries.Index] + s[(countries.Index + countries.Value.Length)..];
        }
        s = s.TrimEnd('的');

        // 词条（不具有/没有/具有/拥有 X）。
        var kw = KeywordClause().Match(s);
        if (kw.Success)
        {
            var name = wordbook.LookupKeyword(kw.Groups[2].Value)
                ?? throw new OrcReject($"未知词条“{kw.Groups[2].Value}”");
            keyword = name;
            if (kw.Groups[1].Value is "不具有" or "没有")
                throw new OrcReject($"否定式词条过滤（不具有 X）超出引擎过滤面：“{text}”");
            s = s[..kw.Index] + s[(kw.Index + kw.Value.Length)..];
            s = s.TrimEnd('的');
        }

        // 属性阈值（攻击力/防御力/行动花费 不小于|大于|… N）——单槽。
        var th = ThresholdClause().Match(s);
        if (th.Success)
        {
            if (thresholdField != null)
                throw new OrcReject($"多重阈值超出引擎过滤面（单 threshold 槽）：“{text}”");
            (thresholdField, thresholdOp, thresholdValue) =
                (AttributeName(th.Groups[1].Value), CompareOp(th.Groups[2].Value), OrcLexical.Number(th.Groups[3].Value)
                    ?? throw new OrcReject($"阈值非静态数字：“{th.Groups[3].Value}”"));
            s = s[..th.Index] + s[(th.Index + th.Value.Length)..];
            s = s.TrimEnd('的');
        }

        // 残余文本 = 未消化的因子 → 拒绝（宁可待核对，不静默裁剪语义）。
        var rest = s.Trim();
        if (rest.Length > 0 && rest != "单位" && rest != "所有单位")
            throw new OrcReject($"过滤条件含引擎不支持的因素：“{rest}”（原句：{text}）");

        try
        {
            return new DslFilter(unitType, keyword, faction, excludeSelf: false,
                thresholdField, thresholdOp, thresholdValue);
        }
        catch (ArgumentException e) { throw new OrcReject("过滤条件为空或非法：" + e.Message); }
    }

    /// <summary>
    /// 卡牌谓词（手牌/卡组筛选等场景，js cardPredicate）：引擎 filter 无稀有度/费用/名字维度，
    /// 仅 兵种/国家/词条 可映射；引号卡名、指令/反制类型、费用比较 → OrcReject。
    /// </summary>
    public static DslFilter CardFilter(string text, Wordbook wordbook)
    {
        var s = text.Trim().TrimStart('的').TrimEnd('的');
        if (Quoted().IsMatch(s)) throw new OrcReject($"卡名筛选超出引擎过滤面：{s}");
        if (s is "指令" or "反制" or "非单位") throw new OrcReject($"卡类型筛选（{s}）超出引擎过滤面");
        var cost = CostClause().Match(s);
        if (cost.Success) throw new OrcReject($"费用筛选超出引擎过滤面：{cost.Value}");
        var rarity = RarityClause().Match(s);
        if (rarity.Success) throw new OrcReject($"稀有度筛选超出引擎过滤面：{rarity.Value}");
        return UnitFilter(s, wordbook); // 其余按单位谓词走
    }

    public static string? FactionId(string cn) => cn switch
    {
        "英国" => "Britain", "美国" => "USA", "德国" => "Germany", "苏联" => "Soviet",
        _ => null, // 日本/法国/意大利/波兰/芬兰/澳新军团：引擎 Faction 四值，无映射
    };

    public static string AttributeName(string cn) => cn switch
    {
        "攻击力" => "attack", "防御力" => "defense", "生命值" => "hq",
        "行动花费" or "操作费" or "花费" or "费用" or "部署费用" => "opCost",
        _ => throw new OrcReject($"未知属性“{cn}”"),
    };

    public static string CompareOp(string cn) => cn switch
    {
        "不大于" or "小于等于" or "至多" or "<=" => "lte",
        "不小于" or "大于等于" or "至少" or ">=" => "gte",
        "大于" or ">" => "gt",
        "小于" or "<" => "lt",
        "等于" or "为" or "==" => "eq",
        _ => throw new OrcReject($"未知比较词“{cn}”"),
    };

    private static string tailAfterUnit(string s)
    {
        var i = s.LastIndexOf("单位", StringComparison.Ordinal);
        return i < 0 ? s : s[(i + 2)..];
    }

    [GeneratedRegex(@"^(.+?)([\d一二两三四五六七八九十百]+)倍$|指挥点|手牌|卡组|弃牌|随机|\d+-\d+$|的([\d一二两三四五六七八九十百]+)倍|攻击力|防御力|生命值|行动花费|操作费|花费|费用|剩余指挥点|指挥点上限|数量$|数$")]
    private static partial Regex DynamicPattern();
    [GeneratedRegex(@"(非)?(英国|美国|德国|苏联|日本|法国|意大利|波兰|芬兰|澳新军团)(?:和(英国|美国|德国|苏联|日本|法国|意大利|波兰|芬兰|澳新军团))?")]
    private static partial Regex NationPattern();
    [GeneratedRegex(@"(不具有|没有|具有|拥有)([^，。；；]+)")]
    private static partial Regex KeywordClause();
    [GeneratedRegex(@"(攻击力|防御力|行动花费|操作费|花费|生命值)(不大于|不小于|大于等于|小于等于|等于|大于|小于|至少|至多|为|>=|<=|==|>|<)([\d一二两三四五六七八九十百]+)")]
    private static partial Regex ThresholdClause();
    [GeneratedRegex(@"^[“「""].+[”」""]$")] private static partial Regex Quoted();
    [GeneratedRegex(@"^花费(不大于|不小于|大于|小于|等于|为)(\d+)")] private static partial Regex CostClause();
    [GeneratedRegex(@"^(精英|特殊|限定|普通)")] private static partial Regex RarityClause();
}
