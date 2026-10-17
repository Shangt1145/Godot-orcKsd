using System.Text.Json;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

public class CardCoverageTests(ITestOutputHelper output)
{
    [Fact]
    public void ReportCatalogSyntaxAndNativeCoverageSeparately()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "project.godot"))) directory = directory.Parent;
        var root = directory!.FullName;
        var source = Path.Combine(root, "proto/data/nations");
        var catalog = new CardCatalog(); catalog.Load(source);
        var (_, reviewed, _) = VerifiedCardPool.Compile(catalog.Cards, source);
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests/fixtures/official-raw.json")));
        var official = fixture.RootElement.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("json")).ToArray();
        var rows = new List<object>(); var parsed = 0; var native = 0; var total = 0;
        foreach (var card in official)
        {
            if (!card.TryGetProperty("text", out var texts) || !texts.TryGetProperty("zh-Hans", out var text)) continue;
            total++;
            var type = card.GetProperty("type").GetString() switch { "order" => "order", "countermeasure" => "counter", _ => "unit" };
            var result = OrcInspector.CompileText(card.GetProperty("id").GetString()!, text.GetString()!, type);
            if (result.ParseComplete) parsed++;
            if (result.ParseComplete && result.AllCompiled) native++;
            rows.Add(new { id = card.GetProperty("id").GetString(), parsed = result.ParseComplete,
                nativeTextCompiled = result.ParseComplete && result.AllCompiled, problems = result.Problems });
        }
        var report = new { officialArchive = official.Length, chineseTextRows = total, parsed, nativeTextCompiled = native,
            referenceCatalog = reviewed.Total, structurallyRepresentable = reviewed.StructurallyRepresentable,
            reviewedReferencePlayable = reviewed.Verified,
            shippedOfficialCards = Directory.GetFiles(Path.Combine(root, "proto/data/kards/packs"), "*.card.json", SearchOption.AllDirectories).Length,
            note = "Native text compilation is not complete card semantic verification. Empty reference pool, matching source parity conditions.", rows };
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        File.WriteAllText(Path.Combine(root, "artifacts/beta1-card-coverage.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"official={official.Length} chinese={total} parsed={parsed} nativeTextCompiled={native} reference={reviewed.Total} structural={reviewed.StructurallyRepresentable} reviewed={reviewed.Verified}");
        Assert.Equal(1553, total); Assert.Equal(698, parsed); Assert.InRange(native, 1, parsed);
    }
}
