using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kards.Ui.OrcBridge.HandlerDsl;
using Orc.Game.Effects;
using Xunit;

namespace Kards.Ui.Tests;

public class InspectorCapabilityTests
{
    private static bool Chinese(string text) => Regex.IsMatch(text, "[\\u3400-\\u9fff]");

    [Fact]
    public void EveryEngineNodeAndPinIsPresentAndHasChinesePresentation()
    {
        var engine = new HandlerCatalogFacade().GetCatalog();
        var catalog = HandlerCatalog.Default;
        Assert.False(HandlerCatalog.DefaultIsFallback, HandlerCatalog.DefaultFallbackReason);
        var expected = engine.Nodes.Select(n => n.Name).Concat(engine.BuiltInOps.Select(n => n.Op)).Distinct().Order().ToArray();
        Assert.Equal(expected, catalog.Nodes.Select(n => n.Name).Distinct().Order().ToArray());
        foreach (var node in catalog.Nodes)
        {
            Assert.True(Chinese(node.Title), "Missing Chinese node title: " + node.Name);
            foreach (var pin in node.Inputs.Concat(node.Outputs))
            {
                Assert.True(Chinese(node.PinLabel(pin.Name)), "Missing Chinese pin: " + node.Name + "." + pin.Name);
                foreach (var value in pin.EnumValues ?? []) Assert.True(Chinese(HandlerVocabulary.LiteralLabel(value)), "Missing Chinese enum: " + value);
            }
        }
        Assert.Contains(catalog.Nodes, n => n.Name == "selectTargets");
        Assert.Contains(catalog.Nodes, n => n.Name == "headquartersOf");
    }

    [Theory]
    [InlineData("friendly", "one", false)]
    [InlineData("enemy", "random", false)]
    [InlineData("both", "all", false)]
    [InlineData("enemy", "all", true)]
    public void DirectTargetControlsBuildValidLosslessEngineDsl(string side, string mode, bool hq)
    {
        var action = new HandlerNode("damage").Set("amount", HandlerValue.Num(3));
        var doc = new HandlerDocument(); doc.Actions.AddRange(HandlerTargetAuthoring.SelectForAction(action, "target", side, mode, hq, 1));
        var catalog = HandlerCatalog.Default;
        Assert.Empty(HandlerDslValidator.Validate(doc, catalog, new Dictionary<string, HandlerViewField>()));
        var text = doc.ToJsonText(catalog);
        Assert.Equal(text, HandlerDocument.FromJson(text, catalog).ToJsonText(catalog));
        Assert.Equal("currentTarget_1", action.Value("target")!.Name);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BooleanInputsRoundTripAsBooleans(bool value)
    {
        var json = HandlerValue.Bool(value).ToJson();
        Assert.Equal(value, json["lit"]!.GetValue<bool>());
        Assert.Equal(value ? "是" : "否", HandlerValue.FromJson(json).Describe());
    }

    [Fact]
    public void EditingTargetRangeDoesNotNestSelectorsOrLoseAdvancedFilters()
    {
        var action = new HandlerNode("damage").Set("amount", HandlerValue.Num(3));
        var document = new HandlerDocument(); document.Actions.AddRange(HandlerTargetAuthoring.SelectForAction(action, "target", "enemy", "one", false, 1));
        document.Actions[0].Set("thresholdField", HandlerValue.Str("attack")).Set("thresholdValue", HandlerValue.Num(4));
        var selection = Assert.IsType<HandlerTargetAuthoring.SelectionContext>(HandlerTargetAuthoring.FindSelection(document, action, "target"));
        HandlerTargetAuthoring.UpdateSelection(selection, "friendly", "random", false, 2, "frontline");
        Assert.Equal(2, document.Actions.Count);
        Assert.Equal("random", document.Actions[0].Value("mode")!.Text);
        Assert.Equal(4, document.Actions[0].Value("thresholdValue")!.Number);
        Assert.Same(action, Assert.Single(document.Actions[1].Body("actions")));
    }

    [Fact]
    public void ChineseBindingNamesAreReadableAndCompileToValidIdentifiers()
    {
        var id = HandlerVocabulary.BindingId("选中的敌方单位");
        Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", id);
        Assert.Equal("选中的敌方单位", HandlerVocabulary.BindingLabel(id));
        var document = new HandlerDocument(); document.Actions.Add(new HandlerNode("let").SetText("name", id).Set("value", HandlerValue.Num(2)));
        document.Actions.Add(new HandlerNode("draw").Set("count", HandlerValue.Var(id)));
        Assert.Empty(HandlerDslValidator.Validate(document, HandlerCatalog.Default));
    }
}
