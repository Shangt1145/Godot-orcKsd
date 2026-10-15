using System.Text.Json;
using Kards.Ui.OrcBridge.OrcText;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// OrC 解析器 × 官方语料覆盖率：1553 条官方中文卡面文本全量解析，
// 锁定"零诊断（完整解析）"基线率。基线只许升不许降——解析面扩展后须先跑
// 诊断分布，把新可解析形态补进 OrcTextParserTests 再抬基线。
// 语料来源：Kards-Desktop tools/orc/corpus/official-raw.json（1646 卡，1553 有中文文本）。
// ─────────────────────────────────────────────────────────────────────────────

public class OrcCorpusCoverageTests(ITestOutputHelper output)
{
    // 基线（2026-10-15 首锁）：93/1553 = 6.0%。阈值过滤句（FilterDto 无 threshold 维）按引擎真实口径拒绝。剩余拒绝大头＝引擎真面外：
    // 总部目标（selector 无 hq）、返回手牌/撤退（无 returnUnit）、单位间战斗、
    // createUnit（卡入战场）、被攻击时监听（无 hook）、控制流（无 branch/loop/choice）、
    // 动态数值（引擎只有常量）、移除（无 leaveUnit）、set 语义（buff 仅增量）。
    // 只许升不许降；扩展解析面后先跑诊断分布补句级测试再抬此值。
    private const double BaselineCompleteRate = 0.059;

    private static string CardType(string corpusType) => corpusType switch
    {
        "order" => "order",
        "countermeasure" => "counter",
        _ => "unit", // infantry/tank/fighter/bomber/artillery
    };

    public static IEnumerable<object[]> CorpusRows()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var cards = doc.RootElement.GetProperty("cards").EnumerateArray()
            .Select(c => c.GetProperty("json"))
            .Where(c => c.TryGetProperty("text", out var t) && t.TryGetProperty("zh-Hans", out _))
            .ToList();
        foreach (var c in cards)
            yield return new object[]
            {
                c.GetProperty("id").GetString()!,
                c.GetProperty("type").GetString()!,
                c.GetProperty("text").GetProperty("zh-Hans").GetString()!,
            };
    }

    private static string FixturePath
    {
        get
        {
            // 测试运行目录向上找仓库 tests/fixtures/
            var dir = AppContext.BaseDirectory;
            for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
            {
                var candidate = Path.Combine(d, "tests", "fixtures", "official-raw.json");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("找不到语料夹具 official-raw.json");
        }
    }

    [Fact]
    public void 语料覆盖率锁定基线()
    {
        var book = Wordbook.Create(null);
        var rows = CorpusRows().Select(r => (Id: (string)r[0], Type: (string)r[1], Text: (string)r[2])).ToList();
        Assert.Equal(1553, rows.Count); // 夹具完整性

        var complete = 0;
        var byCode = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            var r = OrcParser.Compile(row.Text, CardType(row.Type), book);
            if (r.Diagnostics.Count == 0) complete++;
            else
            {
                var code = r.Diagnostics[0].Code;
                byCode[code] = byCode.TryGetValue(code, out var n) ? n + 1 : 1;
            }
        }

        var rate = (double)complete / rows.Count;
        output.WriteLine($"语料 {rows.Count} 条：完整解析 {complete}（{rate:P1}）");
        foreach (var pair in byCode.OrderByDescending(p => p.Value))
            output.WriteLine($"  {pair.Key}: {pair.Value}");

        Assert.True(rate >= BaselineCompleteRate,
            $"语料完整解析率 {rate:P1} 低于锁定基线 {BaselineCompleteRate:P1}——解析面发生退化");
    }
}
