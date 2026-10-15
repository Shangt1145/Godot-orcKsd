using System.Text;
using System.Text.RegularExpressions;

namespace Kards.Ui.OrcBridge.OrcText;

// ─────────────────────────────────────────────────────────────────────────────
// OrC 词法层（移植自 Kards-Desktop electron/game/js/orc.js 的 normalize/number/split/
// keyword/keywordLine——orc-4 语义）。只做"文本 → 可解析形态"，不做语义判定。
// 与 JS 版的差异：不移植 primitives.js 旧执行器回退——旧动作系统不在 OrC-KSD 引擎
// 编译面内，解析不了统一走诊断（待核对），符合 OrC 导入"未完整解析不入池"的哲学。
// ─────────────────────────────────────────────────────────────────────────────

internal static partial class OrcLexical
{
    /// <summary>中文数字 → int（"两"归"二"；支持 零〇一二三四五六七八九十百千 组合）。</summary>
    public static int? Number(string s)
    {
        s = s.Trim().Replace('两', '二');
        if (Regex.IsMatch(s, @"^[+-]?\d+$")) return int.Parse(s);
        if (!Regex.IsMatch(s, @"^[零〇一二三四五六七八九十百千]+$")) return null;
        var total = 0; var digit = 0;
        const string nums = "零一二三四五六七八九";
        foreach (var ch in s)
        {
            if (ch is '十' or '百' or '千')
            {
                total += (digit == 0 ? 1 : digit) * (ch switch { '十' => 10, '百' => 100, _ => 1000 });
                digit = 0;
            }
            else digit = ch == '〇' ? 0 : nums.IndexOf(ch);
        }
        return total + digit;
    }

    /// <summary>引号感知的逐字正规化：剥 BOM、统一换行、全角→半角、特殊符号归一。</summary>
    private static string NormalizeChars(string text)
    {
        var quote = '\0'; var pairs = new Dictionary<char, char> { ['“'] = '”', ['「'] = '」', ['『'] = '』', ['"'] = '"' };
        var outp = new StringBuilder();
        foreach (var ch in CrLf().Replace(Bom().Replace(text ?? "", ""), "\n"))
        {
            if (quote != '\0') { outp.Append(ch); if (ch == quote) quote = '\0'; continue; }
            if (pairs.TryGetValue(ch, out var closing)) { quote = closing; outp.Append(ch); continue; }
            outp.Append(ch switch
            {
                '\u00a0' or '\u3000' => ' ',
                '−' or '–' => '-',
                >= '０' and <= '９' => ((char)('0' + (ch - '０'))).ToString(),
                '＋' => "+",
                '／' => "/",
                _ => ch.ToString(),
            });
        }
        return outp.ToString();
    }

    /// <summary>
    /// 引号外的逐词空白折叠（js 版 words.reduce 的移植）：治「本单 位」排版噪声。
    /// 词表＝语言词 + 兵种词 + 词条中文名；引擎没有的词也进词表（normalize 只管折叠，不管合法性）。
    /// </summary>
    public static string Normalize(string text, IReadOnlyCollection<string> words)
    {
        var body = NormalizeChars(text);
        // 关键词行（顿号分隔的纯词条行）内空格→顿号；其余行折叠汉字间空格。
        var parts = QuoteSplit(body);
        for (var i = 0; i < parts.Count; i++)
        {
            if (i % 2 == 1) continue; // 引号段保持原样
            var segment = LiteralNewline().Replace(parts[i], "\n");
            var lines = segment.Split('\n');
            for (var l = 0; l < lines.Length; l++)
                lines[l] = IsKeywordLine(lines[l], words) ? SpaceRun().Replace(lines[l], "、") : lines[l];
            var joined = string.Join("\n", lines);
            joined = HanSpace().Replace(joined, ""); // 汉字/数字间空格塌缩（"或"两侧保留）
            parts[i] = words.Aggregate(joined, (s, word) => WordTolerant(word).Replace(s, word));
        }
        return string.Concat(parts).Trim();
    }

    /// <summary>引号切段：奇数下标＝引号内原文（保护卡名与字面量）。</summary>
    public static List<string> QuoteSplit(string text)
    {
        var list = new List<string>();
        var buffer = new StringBuilder();
        var quote = '\0';
        var pairs = new Dictionary<char, char> { ['“'] = '”', ['「'] = '」', ['『'] = '』', ['"'] = '"' };
        foreach (var ch in text)
        {
            if (quote != '\0')
            {
                buffer.Append(ch);
                if (ch == quote) { quote = '\0'; list.Add(buffer.ToString()); buffer.Clear(); }
                continue;
            }
            if (pairs.TryGetValue(ch, out _))
            {
                if (buffer.Length > 0) { list.Add(buffer.ToString()); buffer.Clear(); }
                quote = pairs[ch];
                buffer.Append(ch);
                continue;
            }
            buffer.Append(ch);
        }
        if (buffer.Length > 0) list.Add(buffer.ToString());
        return list;
    }

    /// <summary>引号与括号感知的切分（js split 的移植）：按分隔符切句，括号深度保护。</summary>
    public static List<string> Split(string text, string separators)
    {
        var parts = new List<string>();
        var start = 0; var quote = '\0'; var depth = 0;
        var pairs = new Dictionary<char, char> { ['“'] = '”', ['「'] = '」', ['『'] = '』', ['"'] = '"' };
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (pairs.ContainsKey(c)) { quote = pairs[c]; continue; }
            if (c is '（' or '(' or '[') depth++;
            else if (c is '）' or ')' or ']') depth--;
            else if (depth == 0 && separators.Contains(c)) { parts.Add(text[start..i]); start = i + 1; }
        }
        parts.Add(text[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>词条 token："重甲3"／"闪击" → (id, 参值, 是否带参)。</summary>
    public static (string Id, int Value, bool Quantified)? Keyword(string s,
        IReadOnlyDictionary<string, string> keywordNames)
    {
        var m = KeywordTail().Match(s.Trim());
        if (!m.Success) return null;
        if (!keywordNames.TryGetValue(m.Groups[1].Value.Trim(), out var id)) return null;
        var quantified = m.Groups[2].Success;
        return (id, quantified ? Number(m.Groups[2].Value) ?? 1 : 1, quantified);
    }

    /// <summary>词条行：整行都是顿号/空格分隔的词条 token。</summary>
    public static List<(string Id, int Value, bool Quantified)>? KeywordLine(string s,
        IReadOnlyDictionary<string, string> keywordNames, IReadOnlyCollection<string> words)
    {
        var items = Split(s, "、,，;；").SelectMany(x => x.Split(' ', '\t'))
            .Where(x => x.Length > 0).Select(x => Keyword(x, keywordNames)).ToList();
        return items.Count > 0 && items.All(i => i.HasValue)
            ? items.Select(i => i!.Value).ToList() : null;
    }

    private static bool IsKeywordLine(string line, IReadOnlyCollection<string> words)
    {
        if (line.Length == 0 || line.Contains('：') || line.Contains(':')) return false;
        var table = words.ToDictionary(w => w, w => w, StringComparer.Ordinal);
        var items = Split(line, "、,，;；").SelectMany(x => x.Split(' ', '\t'))
            .Where(x => x.Length > 0).Select(x => Keyword(x, table)).ToList();
        return items.Count > 0 && items.All(i => i.HasValue);
    }

    [GeneratedRegex(@"^\uFEFF")] private static partial Regex Bom();
    [GeneratedRegex(@"\r\n?")] private static partial Regex CrLf();
    [GeneratedRegex(@"\\r\\n|\\n")] private static partial Regex LiteralNewline();
    [GeneratedRegex(@"[ \t]+")] private static partial Regex SpaceRun();
    [GeneratedRegex(@"(?<!或)(?<=[\p{IsCJKUnifiedIdeographs}\d+\-])[ \t]+(?=[\p{IsCJKUnifiedIdeographs}\d+\-])(?!或)")]
    private static partial Regex HanSpace();
    [GeneratedRegex(@"^(.+?)(?:\s*[xX×]?\s*([\d零〇一二两三四五六七八九十百]+))?$")]
    private static partial Regex KeywordTail();

    private static Regex WordTolerant(string word) => new(
        string.Join("[ \\t]*\\n?[ \\t]*", word.Select(c => Regex.Escape(c.ToString()))), RegexOptions.Compiled);
}
