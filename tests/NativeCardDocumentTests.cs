using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

public class NativeCardDocumentTests
{
    private const string Source = """{"schemaVersion":1,"id":"probe","name":"旧名","future":{"x":42},"components":[{"component":"typeCategory","type":["infantry","future-type"]},{"component":"factionCost","faction":"Germany","kredits":2,"future":true},{"component":"battleStats","attack":1,"defense":3}]}""";
    [Fact]
    public void PropertyEditsAndTypeSwitchRetainUnknownDataAndIdentity()
    {
        var doc = new NativeCardDocument(Source); doc.Rename("新名"); doc.Set("factionCost", "faction", JsonValue.Create("自定义国家"));
        doc.SetType("counter", "infantry"); Assert.Equal("counter", doc.CardType); Assert.Contains("future-type", doc.Types);
        Assert.Equal(42, doc.Root["future"]!["x"]!.GetValue<int>()); Assert.True(doc.Component("factionCost")!["future"]!.GetValue<bool>());
        doc.SetType("unit", "tank"); Assert.Equal("tank", doc.Unit); Assert.Equal(3, doc.Number("battleStats", "defense")); Assert.Equal("probe", doc.Id);
        Assert.Equal("自定义国家", doc.String("factionCost", "faction"));
    }
    [Fact]
    public void UndoRedoAndBranchKeepWholeDocument()
    {
        var doc = new NativeCardDocument(Source); doc.Rename("A"); doc.SetType("order", "infantry"); doc.Undo();
        Assert.Equal("unit", doc.CardType); doc.Redo(); Assert.Equal("order", doc.CardType);
        doc.Undo(); doc.Rename("B"); Assert.False(doc.CanRedo); Assert.Equal("B", doc.Name); Assert.Equal(42, doc.Root["future"]!["x"]!.GetValue<int>());
    }
}
