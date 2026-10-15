using System.Text.Json;
using Kards.Ui.OrcBridge;
using Kards.Ui.OrcBridge.OrcText;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// OrC 批量导入端到端：语料行 → ImportBatch → 成功卡（引擎卡 JSON 可回读）/ 待核对卡（诊断）。
// ─────────────────────────────────────────────────────────────────────────────

public class OrcImportServiceTests(ITestOutputHelper output)
{
    private static string FixturePath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
            {
                var candidate = Path.Combine(d, "tests", "fixtures", "official-raw.json");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("找不到语料夹具");
        }
    }

    [Fact]
    public void 批量导入_成功与待核对分栏()
    {
        var rows = new[]
        {
            new OrcImportRow("t-deploy-draw", "测试卡甲", "infantry", 2, 1, 2, 3, "部署：抽 1 张牌。"),
            new OrcImportRow("t-damage", "测试卡乙", "order", 1, 0, 0, 0, "对 1 个敌方单位造成 2 点伤害"),
            new OrcImportRow("t-keyword-only", "测试卡丙", "tank", 3, 1, 3, 4, "闪击、重甲1"),
            new OrcImportRow("t-pending", "测试卡丁", "infantry", 2, 1, 2, 3, "使 1 个友方单位返回手牌"),
            new OrcImportRow("t-counter", "测试卡戊", "countermeasure", 1, 0, 0, 0, "抽 1 张牌"),
        };
        var outcomes = OrcImportService.ImportBatch(rows, []);

        var byId = outcomes.ToDictionary(o => o.Id);
        Assert.Equal(5, outcomes.Count);

        // 成功卡：编译产物可回读（引擎卡 JSON 往返），类型正确。
        var ok = byId["t-deploy-draw"];
        Assert.True(ok.Complete, string.Join("；", ok.Diagnostics));
        Assert.NotNull(ok.EngineCardJson);
        using (var doc = JsonDocument.Parse(ok.EngineCardJson!))
        {
            var text = doc.RootElement.GetRawText();
            Assert.Contains("effects", text); // effects 组件在场
        }
        Assert.True(byId["t-damage"].Complete, string.Join("；", byId["t-damage"].Diagnostics));
        Assert.True(byId["t-keyword-only"].Complete, string.Join("；", byId["t-keyword-only"].Diagnostics));

        // 待核对卡：诊断可读、原文保留。
        Assert.False(byId["t-pending"].Complete);
        Assert.NotEmpty(byId["t-pending"].Diagnostics);
        Assert.Equal("使 1 个友方单位返回手牌", byId["t-pending"].SourceText);
        Assert.False(byId["t-counter"].Complete); // 反制卡面外
    }

    [Fact]
    public void 全语料批量导入_成功卡全部编译通过()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var rows = doc.RootElement.GetProperty("cards").EnumerateArray()
            .Select(c => c.GetProperty("json"))
            .Where(c => c.TryGetProperty("text", out var t) && t.TryGetProperty("zh-Hans", out _))
            .Select(c => new OrcImportRow(
                Str(c, "id", "unknown"), Str(c, "name", "未命名"),
                Str(c, "type", "infantry"),
                Num(c, "cost", 1), Num(c, "opCost", 1), Num(c, "attack", 0), Num(c, "defense", 1),
                c.GetProperty("text").GetProperty("zh-Hans").GetString()!))
            .ToList();

        var outcomes = OrcImportService.ImportBatch(rows, []);
        var complete = outcomes.Where(o => o.Complete).ToList();
        output.WriteLine($"导入 {outcomes.Count} 行：成功 {complete.Count}，待核对 {outcomes.Count - complete.Count}");
        Assert.Equal(1553, outcomes.Count);

        // 成功卡必须有引擎卡 JSON；编译失败的行不得标成功。
        Assert.All(complete, o => Assert.NotNull(o.EngineCardJson));
        // 覆盖率口径与 OrcCorpusCoverageTests 一致（不严格相等——此处含编译阶段）。
        Assert.True(complete.Count >= 93,
            $"导入成功 {complete.Count} 低于基线 93——编译链发生退化");
    }

    private static string Str(JsonElement c, string key, string fallback) =>
        c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;

    private static int Num(JsonElement c, string key, int fallback) =>
        c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
}
