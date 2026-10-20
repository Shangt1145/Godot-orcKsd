using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

public class InspectorCompositionTests
{
    private static InspectorDocument Card()=>new(JsonNode.Parse("""{"effects":[{"trigger":"deploy","actions":[{"op":"loop","times":2,"actions":[]}],"future":{"a/b~c":[null,false,9]}}]}""")!);
    [Fact]
    public void PrivateEditsCancelWithoutTouchingSourceOrHistory()
    {
        var source=Card();var original=source.Root.ToJsonString();var events=0;source.Changed+=()=>events++;
        var session=new InspectorComposition(source,"/effects/0/actions/0/times",InspectorPart.Value);
        session.Switch("/value",InspectorPart.Value,"count");session.Draft.Set("/value/query/side",JsonValue.Create("enemy"));
        Assert.Equal(original,source.Root.ToJsonString());Assert.False(source.CanUndo);Assert.Equal(0,events);
    }
    [Fact]
    public void ApplyIsOneUndoAndPreservesUnknownData()
    {
        var source=Card();var original=source.Root.ToJsonString();var events=0;source.Changed+=()=>events++;
        var session=new InspectorComposition(source,"/effects/0/actions/0/times",InspectorPart.Value);
        session.Switch("/value",InspectorPart.Value,"count");
        session.Draft.Set("/value/query/side",JsonValue.Create("enemy"));session.Draft.Set("/value/query/zone",JsonValue.Create("hand"));
        Assert.True(session.Apply(out var error),error);Assert.Equal(1,events);
        Assert.Equal("9",source.At("/effects/0/future/a~1b~0c/2")!.ToString());
        source.Undo();Assert.Equal(original,source.Root.ToJsonString());Assert.False(source.CanUndo);
        source.Redo();Assert.Equal("count",source.At("/effects/0/actions/0/times/expr")!.ToString());
    }
    [Fact]
    public void UnfinishedConditionAndEmptyGroupsCannotReplaceValidSource()
    {
        var source=Card();var original=source.Root.ToJsonString();
        var session=new InspectorComposition(source,"/effects/0/condition",InspectorPart.Condition,true);
        foreach(var mode in new[]{"compare","exists","all","any","not"})
        {session.Switch("/value",InspectorPart.Condition,mode);Assert.False(session.Apply(out _));Assert.Equal(original,source.Root.ToJsonString());}
    }
    [Fact]
    public void SwitchingTypeBackRestoresParametersAndExtensions()
    {
        var source=Card();source.Set("/effects/0/actions/0/times",JsonNode.Parse("""{"expr":"read","of":"self","path":"defense","future":{"x":false}}"""));
        var session=new InspectorComposition(source,"/effects/0/actions/0/times",InspectorPart.Value);var before=session.Draft.Root.ToJsonString();
        session.Switch("/value",InspectorPart.Value,"math");session.Draft.Set("/value/operator",JsonValue.Create("multiply"));
        session.Switch("/value",InspectorPart.Value,"read");Assert.Equal(before,session.Draft.Root.ToJsonString());
        session.Switch("/value",InspectorPart.Value,"math");Assert.Equal("multiply",session.Draft.At("/value/operator")!.ToString());
    }
    [Fact]
    public void StaleSessionCannotOverwriteReorderedOrEditedSource()
    {
        var source=Card();var session=new InspectorComposition(source,"/effects/0/actions/0/times",InspectorPart.Value);
        session.Draft.Set("/value",JsonValue.Create(5));source.Set("/effects/0/future",JsonValue.Create("new"));
        Assert.False(session.Apply(out var error));Assert.Contains("变化",error);Assert.Equal("2",source.At("/effects/0/actions/0/times")!.ToString());
    }
    [Fact]
    public void WrapperAndUnknownConditionFieldsAreLossless()
    {
        var source=Card();source.Set("/effects/0/condition",JsonNode.Parse("""{"op":"predicate","test":{"test":"compare","left":2,"cmp":">=","right":1,"future":7},"wrapperFuture":false}"""));
        var session=new InspectorComposition(source,"/effects/0/condition",InspectorPart.Condition,true);
        session.Draft.Set("/value/right",JsonValue.Create(0));Assert.True(session.Apply(out _));
        Assert.Equal("false",source.At("/effects/0/condition/wrapperFuture")!.ToString());
        Assert.Equal("7",source.At("/effects/0/condition/test/future")!.ToString());
    }
    [Fact]
    public void UnseenCombinationUsesSameQueryValueAndConditionPrimitives()
    {
        var source=Card();var session=new InspectorComposition(source,"/effects/0/condition",InspectorPart.Condition,true);
        session.Draft.Set("/value",JsonNode.Parse("""{"test":"all","items":[{"test":"compare","left":{"expr":"count","query":{"sel":"all","side":"enemy","zone":"deck","where":{"test":"compare","left":{"expr":"read","of":"candidate","path":"cost"},"cmp":"<","right":4}}},"cmp":">=","right":2},{"test":"not","item":{"test":"compare","left":{"expr":"read","of":"self","path":"defense"},"cmp":"==","right":0}}]}"""));
        Assert.Empty(InspectorComposition.Issues(session.Draft.At("/value"),InspectorPart.Condition));Assert.True(session.Apply(out _));
        Assert.Contains("敌方",InspectorComposition.Describe(source.At("/effects/0/condition")));Assert.Contains("防御力",InspectorComposition.Describe(source.At("/effects/0/condition")));
    }
    [Fact]
    public void CompoundCommandsPublishOnceAndFailedCommandsRollback()
    {
        var source=Card();var original=source.Root.ToJsonString();var events=0;source.Changed+=()=>events++;
        Assert.Throws<InvalidOperationException>(()=>source.Transaction(()=>{source.Set("/name",JsonValue.Create("bad"));throw new InvalidOperationException();}));
        Assert.Equal(original,source.Root.ToJsonString());Assert.False(source.CanUndo);Assert.Equal(0,events);
        source.Transaction(()=>{source.Set("/name",JsonValue.Create("good"));source.Transaction(()=>source.Set("/cost",JsonValue.Create(3)));});
        Assert.Equal(1,events);source.Undo();Assert.Equal(original,source.Root.ToJsonString());
    }
    [Theory]
    [InlineData("{\"test\":\"compare\",\"left\":3,\"cmp\":\"contains\",\"right\":1}")]
    [InlineData("{\"test\":\"compare\",\"left\":3,\"cmp\":\">\",\"right\":\"abc\"}")]
    [InlineData("{\"test\":\"compare\",\"left\":true,\"cmp\":\">\",\"right\":false}")]
    public void TypedComparisonRejectsIncompatibleValues(string json)=>Assert.NotEmpty(InspectorComposition.Issues(JsonNode.Parse(json),InspectorPart.Condition));

    [Fact]
    public void ArithmeticRejectsTextInsteadOfCoercingItToZero()
    {
        var expression=JsonNode.Parse("{\"expr\":\"math\",\"operator\":\"add\",\"items\":[1,\"text\"]}");
        Assert.NotEmpty(InspectorComposition.Issues(expression,InspectorPart.Value));
    }
    [Fact]
    public void RemovingArrayItemDoesNotRestoreAnotherItemsCachedValues()
    {
        var source=Card();var session=new InspectorComposition(source,"/effects/0/actions/0/times",InspectorPart.Value);
        session.Draft.Set("/value",JsonNode.Parse("{\"expr\":\"math\",\"operator\":\"add\",\"items\":[7,9]}"));
        session.Switch("/value/items/0",InspectorPart.Value,"count");
        session.RemoveItem("/value/items/0");
        session.Switch("/value/items/0",InspectorPart.Value,"count");session.Switch("/value/items/0",InspectorPart.Value,"literal");
        Assert.Equal("9",session.Draft.At("/value/items/0")!.ToString());
    }
}
