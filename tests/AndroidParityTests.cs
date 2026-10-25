using System.Text.Json;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class AndroidParityTests
{
    [Theory]
    [InlineData("部署：抽2张牌。", "unit")]
    [InlineData("对1个单位造成1点伤害。若其没有相邻单位，改为造成2点伤害。", "order")]
    [InlineData("部署：对所有攻击力大于2且防御力不大于5的敌方坦克造成3点伤害。", "unit")]
    public void ManagedCompilerPreservesDesktopGraphs(string text, string type)
    {
        var request = new { method = "compile", text, card = new { cardType = type }, pool = Array.Empty<object>() };
        Assert.Equal(DesktopOrc.Request(request).GetRawText(), DesktopOrc.RequestManaged(request).GetRawText());
    }
    [Fact]
    public void ManagedCatalogContainsEveryDesktopTool()
    {
        Assert.Equal(DesktopOrc.Request(new { method = "catalog" }).GetRawText(), DesktopOrc.RequestManaged(new { method = "catalog" }).GetRawText());
    }
    [Fact]
    public void ManagedCompilerMatchesDesktopForTheWholeOfficialCorpus()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "tests", "fixtures", "official-raw.json")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("官方语料不存在。");
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "official-raw.json")));
        var rows = corpus.RootElement.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("json"))
            .Where(c => c.TryGetProperty("text", out var text) && text.TryGetProperty("zh-Hans", out _)).Select(c => new {
                id = c.GetProperty("id").GetString(), text = c.GetProperty("text").GetProperty("zh-Hans").GetString(),
                cardType = c.GetProperty("type").GetString() switch { "order" => "order", "countermeasure" => "counter", _ => "unit" }
            }).ToArray();
        Assert.Equal(1553, rows.Length);
        foreach (var batch in rows.Chunk(25))
        {
            var request = new { method = "parityCorpus", rows = batch };
            Assert.Equal(DesktopOrc.Request(request).GetRawText(), DesktopOrc.RequestManaged(request).GetRawText());
        }
    }
}
