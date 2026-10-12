using Kards.Ui.Contracts;
using Orc.Core;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// Converts official KARDS Chinese card text into effect drafts over the DIY op surface. The
/// grammar mirrors tools/build_builtin_packs.py's admission precheck (same template table, both
/// sides must move together). Unsupported text returns null — the generator never ships such a
/// card, so a null here means pack data and parser have drifted, which is a load-time error.
/// </summary>
public static class PackCardTextParser
{
    private static readonly string[] Targets =
    { "self", "friendlyUnits", "enemyUnits", "friendlyUnit", "enemyUnit", "anyUnit", "bothUnits" };

    /// <summary>Parses card text. Vanilla (empty) text → empty draft list. Unparsable → null.</summary>
    public static IReadOnlyList<DiyEffectDraft>? Parse(string? text, string cardType)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var body = text.Trim().TrimEnd('。').Trim();
        var hadDeployPrefix = body.StartsWith("部署：", StringComparison.Ordinal);
        if (hadDeployPrefix) body = body["部署：".Length..].Trim();
        if (cardType == "unit" && !hadDeployPrefix)
            return null; // plain unit text = passive/aura — not in the v1 surface
        var drafts = new List<DiyEffectDraft>();
        foreach (var raw in body.Split('；', ';'))
        {
            var sentence = raw.Trim().TrimEnd('。').Trim();
            if (sentence.Length == 0) continue;
            var draft = ParseSentence(sentence);
            if (draft is null) return null;
            drafts.Add(draft);
        }
        return drafts;
    }

    private static DiyEffectDraft? ParseSentence(string s) =>
        Num(@"^抽\s*(?<n>\d+)\s*张牌$", s, out var n) ? D("draw", "self", n)
        : Num(@"^(?:额外)?获得\s*(?<n>\d+)\s*个指挥点槽$", s, out n) ? D("gainSlot", "self", n)
        : Num(@"^获得\s*(?<n>\d+)\s*个指挥点$", s, out n) ? D("gainPoint", "self", n)
        : Two(@"^对\s*(?<t>__T__)\s*造成\s*(?<n>\d+)\s*点伤害$", s, out var t, out n) ? D("damage", t, n)
        : Target(@"^消灭\s*(?<t>__T__)$", s, out t) ? D("destroy", t, null)
        : Target(@"^压制\s*(?<t>__T__)(?:\s*1\s*回合)?$", s, out t) ? D("pin", t, 1)
        : Target(@"^抑制\s*(?<t>__T__)$", s, out t) ? D("silence", t, null)
        : Buff(@"^使\s*(?<t>__T__)\s*获得\s*(?<a>[+-]?\d+)\s*/\s*(?<d>[+-]?\d+)$", s, out t, out var a, out var d) ? D("buff", t, null, a, d)
        : Num(@"^使\s*(?<t>__T__)\s*获得\s*(?<n>[+-]?\d+)\s*行动花费$", s, out n, t: out t)
            ? Math.Abs(n) is > 0 and <= 20 ? D("costMod", t, n) : null
        : null;

    private const string __T = "(?:1\\s*个敌方(?:单位|目标)|1\\s*个友方单位|1\\s*个(?:单位|目标)|所有敌方单位|所有友方单位|所有(?:单位|目标))";

    private static string T(string pattern) => pattern.Replace("__T__", __T);

    private static DiyEffectDraft D(string op, string target, int? amount, int? attack = null, int? defense = null) =>
        new() { Op = op, Target = target, Amount = amount ?? 0, Attack = attack ?? 0, Defense = defense ?? 0, Until = "" };

    private static bool Num(string pattern, string s, out int n) => Num(pattern, s, out n, out _);

    private static bool Num(string pattern, string s, out int n, out string t)
    {
        n = 0; t = "";
        var m = System.Text.RegularExpressions.Regex.Match(s, T(pattern));
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["n"].Value, out n)) return false;
        if (m.Groups["t"].Success) t = Side(m.Groups["t"].Value);
        return true;
    }

    private static bool Two(string pattern, string s, out string t, out int n)
    {
        t = ""; n = 0;
        var m = System.Text.RegularExpressions.Regex.Match(s, T(pattern));
        if (!m.Success) return false;
        t = Side(m.Groups["t"].Value);
        return int.TryParse(m.Groups["n"].Value, out n);
    }

    private static bool Target(string pattern, string s, out string t)
    {
        t = "";
        var m = System.Text.RegularExpressions.Regex.Match(s, T(pattern));
        if (!m.Success) return false;
        t = Side(m.Groups["t"].Value);
        return true;
    }

    private static bool Buff(string pattern, string s, out string t, out int a, out int d)
    {
        t = ""; a = 0; d = 0;
        var m = System.Text.RegularExpressions.Regex.Match(s, T(pattern));
        if (!m.Success) return false;
        t = Side(m.Groups["t"].Value);
        return int.TryParse(m.Groups["a"].Value, out a) && int.TryParse(m.Groups["d"].Value, out d);
    }

    private static string Side(string text) => text switch
    {
        "所有敌方单位" => "enemyUnits",
        "所有友方单位" => "friendlyUnits",
        "所有单位" or "所有目标" => "bothUnits",
        "1 个敌方单位" or "1 个敌方目标" => "enemyUnit",
        "1 个友方单位" => "friendlyUnit",
        _ => "anyUnit"
    };
}
