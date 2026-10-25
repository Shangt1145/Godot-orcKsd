using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

public class DiyAuthoringCoverageTests(ITestOutputHelper output)
{
    [Fact]
    public void AuditNativeAdmissionAcrossTheOfficialCorpusAndInspector()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Kards.Ui.csproj"))) root = Path.GetDirectoryName(root)!;
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests/fixtures/official-raw.json")));
        var rows = source.RootElement.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("json"))
            .Where(c => c.TryGetProperty("text", out var t) && t.TryGetProperty("zh-Hans", out _)).Select(c => new {
                id = c.GetProperty("id").GetString(), text = c.GetProperty("text").GetProperty("zh-Hans").GetString(),
                cardType = c.GetProperty("type").GetString() switch { "order" => "order", "countermeasure" => "counter", _ => "unit" }
            }).ToArray();
        var reports = new List<object>(); var gaps = new Dictionary<string, int>(); int parsed = 0, admitted = 0;
        foreach (var batch in rows.Chunk(25))
        {
            var graphs = DesktopOrc.Request(new { method = "parityCorpus", rows = batch });
            for (int i = 0; i < batch.Length; i++)
            {
                var graph = graphs[i]; var errors = new List<string>(); bool complete = graph.GetProperty("complete").GetBoolean();
                if (complete)
                {
                    parsed++;
                    foreach (var effect in graph.GetProperty("effects").EnumerateArray())
                        try { DesktopNativeLowering.Lower(effect.GetRawText()); }
                        catch (Exception error) { errors.Add(error.Message); gaps[error.Message] = gaps.GetValueOrDefault(error.Message) + 1; }
                    if (errors.Count == 0) admitted++;
                }
                var sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(graph.GetRawText())));
                reports.Add(new { batch[i].id, batch[i].text, complete, errors, graph, sha256 });
            }
        }
        var actions = InspectorVocabulary.Operations.Select(op => {
            var graph = new JsonObject { ["trigger"] = "deploy", ["actions"] = new JsonArray(InspectorVocabulary.Action(op)) };
            try { DesktopNativeLowering.Lower(graph.ToJsonString()); return new { op, supported = true, error = "" }; }
            catch (Exception error) { return new { op, supported = false, error = error.Message }; }
        }).ToArray();
        var report = new { parsed, admitted, inspectorSupported = actions.Count(a => a.supported), actions, gaps = gaps.OrderByDescending(p => p.Value), cards = reports };
        Directory.CreateDirectory(Path.Combine(root, "artifacts/diy-authoring"));
        File.WriteAllText(Path.Combine(root, "artifacts/diy-authoring/coverage.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"parsed={parsed}; admitted={admitted}; inspector={actions.Count(a => a.supported)}/{actions.Length}");
        foreach (var gap in gaps.OrderByDescending(p => p.Value).Take(20)) output.WriteLine($"{gap.Value}: {gap.Key}");
        Assert.Equal(1553, rows.Length);
    }
}
