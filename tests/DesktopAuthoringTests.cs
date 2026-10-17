using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

public class DesktopAuthoringTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("部署：对所有攻击力大于2且防御力不大于5的敌方坦克造成3点伤害。")]
    [InlineData("对1个单位造成1点伤害。若其没有相邻单位，改为造成2点伤害。","order")]
    [InlineData("使1个友方单位获得：“友方回合结束时，若友方手牌数小于2，获得+1+1。”","order")]
    [InlineData("消灭一个单位及其相邻单位","order")]
    [InlineData("部署：抽2个单位，其每具有1个对战词条，使其与本单位获得+1+1。")]
    public void 完整原版解析_复杂句式保持结构(string text,string type="unit")
    {
        var graph=DesktopOrc.CompileGraph(text,type);
        Assert.True(graph.GetProperty("complete").GetBoolean(),graph.GetProperty("diagnostics").GetRawText());
        Assert.NotEmpty(graph.GetProperty("effects").EnumerateArray());
        var roundTrip=DesktopOrc.Request(new {method="roundtrip",value=graph.GetProperty("effects")});
        Assert.Equal(graph.GetProperty("effects").GetRawText(),roundTrip.GetRawText());
    }
    [Fact]
    public void 无损编辑_未知字段嵌套与卡面字段可保存重开导入()
    {
        const string json="""{"name":"无损试作","effects":[{"trigger":"behavior:game/attack:before","oncePerTurn":true,"future":{"v":[null,false,{"x":123}]},"targets":[],"actions":[{"op":"bindHQEvent","target":{"sel":"all","zone":"hq"},"effect":{"trigger":"turnEnd","actions":[{"op":"loop","times":3,"actions":[{"op":"drawOne"}]}]}}]}],"futureCardField":{"z":3}}""";
        using var input=JsonDocument.Parse(json);
        var draft=InspectorCardRecord.ApplyRecord(new DiyCardDraft(),input.RootElement);
        var directory=Path.Combine(Path.GetTempPath(),"orcksd-full-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,"diy.json");
        try
        {
            var library=new DiyCardLibrary(file);library.Save(draft);
            var reopened=Assert.Single(new DiyCardLibrary(file).Cards);
            Assert.Equal(draft.DesktopCardJson,reopened.DesktopCardJson);
            var imported=DiyCardLibrary.Import(DiyCardLibrary.Export(reopened));
            Assert.Equal(draft.OrcEffects[0].DesktopJson,imported.OrcEffects[0].DesktopJson);
            Assert.Equal(draft.DesktopCardJson,imported.DesktopCardJson);
            var native=DiyCardCompiler.Validate(draft,[]);Assert.False(native.Success);Assert.Contains(native.Errors,e=>e.Contains("OrC-KSD"));
        }
        finally{File.Delete(file);Directory.Delete(directory);}
    }
    [Fact]
    public void Godot完整目录_所有动作与参数可创建且未知数据可撤销()
    {
        Assert.Equal(161,InspectorVocabulary.Operations.Length);
        Assert.All(InspectorVocabulary.Operations,op=>Assert.True(InspectorPresentation.Operation(op).Any(c=>c is >= '\u4e00' and <= '\u9fff'),op));
        Assert.All(InspectorVocabulary.Operations,op=>Assert.NotEmpty(InspectorPresentation.Group(op)));
        Assert.Equal("value",InspectorPresentation.ParameterType(new(),"minCost"));
        Assert.Equal(System.Text.Json.JsonValueKind.Number,InspectorVocabulary.Initial("minCost")!.GetValueKind());
        foreach(var op in InspectorVocabulary.Operations)Assert.Equal(op,InspectorVocabulary.Action(op)["op"]!.ToString());
        var doc=new InspectorDocument(System.Text.Json.Nodes.JsonNode.Parse("{\"effects\":[{\"trigger\":\"deploy\",\"future\":{\"a/b~c\":[null,false,7]},\"actions\":[]}]}")!);
        var original=doc.Root.ToJsonString();
        doc.Append("/effects/0/actions",InspectorVocabulary.Action("loop"));
        doc.Set("/effects/0/actions/0/times",System.Text.Json.Nodes.JsonValue.Create(3));
        doc.Append("/effects/0/actions/0/actions",InspectorVocabulary.Action("drawOne"));
        doc.Duplicate("/effects/0/actions/0");doc.Move("/effects/0/actions/1",-1);
        Assert.Equal("7",doc.At("/effects/0/future/a~1b~0c/2")!.ToString());
        while(doc.CanUndo)doc.Undo();Assert.Equal(original,doc.Root.ToJsonString());
        while(doc.CanRedo)doc.Redo();Assert.Equal(2,doc.At("/effects/0/actions")!.AsArray().Count);
    }
    [Fact]
    public void 全语料_使用完整原版编译器而非缩减句式表()
    {
        var source=FindSource();var root=Path.GetFullPath(Path.Combine(source,"..","..",".."));
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests","fixtures","official-raw.json")));
        var rows=document.RootElement.GetProperty("cards").EnumerateArray().Select(c=>c.GetProperty("json"))
            .Where(c=>c.TryGetProperty("text",out var text)&&text.TryGetProperty("zh-Hans",out _)).Select(c=>new
            {id=c.GetProperty("id").GetString(),text=c.GetProperty("text").GetProperty("zh-Hans").GetString(),cardType=c.GetProperty("type").GetString() switch{"order"=>"order","countermeasure"=>"counter",_=>"unit"}}).ToArray();
        var result=DesktopOrc.Request(new {method="corpus",rows});Assert.Equal(1553,result.GetArrayLength());
        using var reference=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests","fixtures","desktop-orc-reference.json")));
        using var extensions=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests","fixtures","desktop-orc-hq-extensions.json")));
        var reviewed=extensions.RootElement.EnumerateArray().ToDictionary(e=>e.GetProperty("id").GetString()!,e=>e.Clone());
        var full=DesktopOrc.Request(new {method="parityCorpus",rows});
        var index=0;
        var changes=new List<string>();
        foreach(var expected in reference.RootElement.EnumerateArray())
        {
            Assert.Equal(expected.GetProperty("id").GetString(),rows[index].id);
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full[index++].GetRawText())));
            var id=rows[index-1].id!;
            if(reviewed.TryGetValue(id,out var extension))
            {
                Assert.Equal(extension.GetProperty("text").GetString(),rows[index-1].text);
                Assert.Equal(extension.GetProperty("sha256").GetString(),hash);
                Assert.NotEqual(expected.GetProperty("sha256").GetString(),hash);changes.Add(id);
            }
            else Assert.Equal(expected.GetProperty("sha256").GetString(),hash);
        }
        File.WriteAllText(Path.Combine(root,"artifacts","desktop-parser-deltas.json"),JsonSerializer.Serialize(changes));
        Assert.Equal(reviewed.Keys.Order(),changes.Order());Assert.Equal(13,changes.Count);
        var complete=result.EnumerateArray().Count(c=>c.GetProperty("complete").GetBoolean());
        output.WriteLine($"完整移植 OrC＋总部语义修复：{complete}/{rows.Length}，1540 项原始结果保持一致，13 项逐项审核扩展");Assert.Equal(698,complete);
        var report=Path.Combine(root,"artifacts","desktop-authoring");Directory.CreateDirectory(report);File.WriteAllText(Path.Combine(report,"full-corpus.json"),result.GetRawText());
    }
    [Fact]
    public void 卡牌声明_编辑单一字段不丢失卡面扩展与关键词参数()
    {
        using var record=JsonDocument.Parse("{\"name\":\"源卡\",\"effects\":[],\"kwMap\":{\"armor\":true},\"kwValues\":{\"armor\":3},\"future\":{\"x\":[null,false]}}");
        var draft=InspectorCardRecord.ApplyRecord(new DiyCardDraft(),record.RootElement);
        var doc=new InspectorDocument(InspectorCardRecord.FromDraft(draft));
        doc.Set("/name",System.Text.Json.Nodes.JsonValue.Create("新卡"));
        var edited=InspectorCardRecord.ApplyRecord(draft,doc.Element());
        Assert.Equal("新卡",edited.Name);Assert.Equal(3,edited.Armor);Assert.Contains("future",edited.DesktopCardJson!);
        doc.Undo();Assert.Equal("源卡",doc.At("/name")!.ToString());
        var legacy=new DiyCardDraft{Keywords=["blitz","armor"],Armor=3};
        var roundtrip=InspectorCardRecord.ApplyRecord(legacy,JsonSerializer.SerializeToElement(InspectorCardRecord.FromDraft(legacy)));
        Assert.Equal(legacy.Keywords,roundtrip.Keywords);Assert.Equal(3,roundtrip.Armor);
    }
    [Fact]
    public void 原版导入_CSV引用号与手写效果完整保留()
    {
        var rows=OrcImportService.ParseRows("卡名,类型,兵种,费用,攻击力,防御力,效果\n\"甲,乙\",单位,步兵,0,2,8,部署：抽1张牌");
        var row=Assert.Single(rows);Assert.Equal("甲,乙",row.Name);Assert.Equal(0,row.Cost);Assert.NotNull(row.DesktopJson);
        var outcome=Assert.Single(OrcImportService.ImportBatch(rows,[]));Assert.True(outcome.Complete,string.Join("；",outcome.Diagnostics));
        var manual=OrcImportService.ParseRows("""[{"id":"manual","name":"手写效果","cardType":"unit","unitType":"infantry","effects":[{"trigger":"deploy","actions":[{"op":"drawOne","side":"self"}]}],"text":"这句卡面文字不参与规则编译"}]""");
        var result=Assert.Single(OrcImportService.ImportBatch(manual,[]));Assert.True(result.Complete,string.Join("；",result.Diagnostics));
        Assert.Contains("drawOne",result.DesktopEffectsJson!);
    }
    [Fact]
    public void 完整解析_引擎缺口不冒充编译通过()
    {
        var compiled=OrcInspector.CompileText("test.full","使1个友方单位返回手牌。","order");
        Assert.True(compiled.ParseComplete);Assert.False(compiled.AllCompiled);Assert.NotEmpty(compiled.Problems);
        var draft=OrcEffectEditing.ToDraft(Assert.Single(compiled.Parse.Effects));Assert.NotNull(draft.DesktopJson);
    }
    [Theory]
    [InlineData("部署：使所有友方单位获得+1+1。")]
    [InlineData("部署：对所有攻击力大于2且防御力不大于5的敌方坦克造成3点伤害。")]
    [InlineData("部署：抽2张牌，使其获得+1+1。")]
    public void 完整声明_变量筛选与表达式编译到原生服务(string text)
    {
        var result=OrcInspector.CompileText("native.graph",text,"unit");
        Assert.True(result.ParseComplete,string.Join("；",result.Problems));
        Assert.True(result.AllCompiled,string.Join("；",result.Problems));
        var draft=new DiyCardDraft {Name="原生表达式",OrcEffects=result.Parse.Effects.Select(OrcEffectEditing.ToDraft).ToArray()};
        var native=DiyCardCompiler.Validate(draft,[]);Assert.True(native.Success,string.Join("；",native.Errors));
        output.WriteLine($"原生表达式通过：{text}");
    }
    [Fact]
    public async Task 原版文本_单一原生触发内按序抽两张()
    {
        var source=FindSource();var catalog=new CardCatalog();catalog.Load(source);
        var compiled=OrcInspector.CompileText("desktop.draw","部署：抽两张牌","unit");
        Assert.True(compiled.ParseComplete);Assert.True(compiled.AllCompiled,string.Join("；",compiled.Problems));
        var draft=new DiyCardDraft {Id="diy/desktop-draw",Name="原版解析实战",OrcEffects=compiled.Parse.Effects.Select(OrcEffectEditing.ToDraft).ToArray()};
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,20261016,"desktop-full","probe",openingHand:[draft.Id],sourceDirectory:source,customCards:[draft]);
        await session.SettleMulliganAsync(false);session.SettleProjection();
        var actor=session.View.SelfHand.Single(c=>c.CardId==draft.Id);
        for(var i=0;i<8&&session.View.SelfKredits<1;i++){Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new EndTurn()));session.SettleProjection();}
        var hand=session.View.SelfHand.Count;
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(actor.Uid)));session.SettleProjection();
        Assert.Equal(hand+1,session.View.SelfHand.Count);
    }
    [Fact]
    public async Task 原生声明选靶_取消不扣费且命中所选第二个单位()
    {
        var source=FindSource();var catalog=new CardCatalog();catalog.Load(source);
        var left=new DiyCardDraft {Id="diy/full-left",Name="目标甲",Cost=0,Attack=1,Defense=8};
        var right=left with {Id="diy/full-right",Name="目标乙"};
        var order=new DiyCardDraft {Id="diy/full-target",Name="完整指向指令",CardType="order",Cost=1,EffectText="对一个友方单位造成2点伤害。"};
        using var s=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,20261016,"desktop-targets","probe",sourceDirectory:source,openingHand:[left.Id,right.Id,order.Id],customCards:[left,right,order]);
        s.DriveOpponent=false;await s.SettleMulliganAsync(false);
        await s.Host.Match.ResourceManager.GainSlotsAsync(s.Host.Match.Players[0],10);
        await s.Host.Match.ResourceManager.GainPointsAsync(s.Host.Match.Players[0],10);
        foreach(var id in new[]{left.Id,right.Id}){var unit=s.View.SelfHand.First(c=>c.CardId==id);Assert.Equal(UiSubmitOutcome.Applied,await s.SubmitAsync(new PlayCard(unit.Uid)));s.SettleProjection();}
        var card=s.View.SelfHand.Single(c=>c.CardId==order.Id);var points=s.View.SelfKredits;var cancel=true;var prompts=0;
        s.TargetRequested+=(request,responder)=>
        {
            prompts++;
            if(cancel)Assert.True(responder.Cancel(request.RequestId));
            else Assert.True(responder.Complete(request.RequestId,new Dictionary<string,IReadOnlyList<string>>{{request.Slots[0].Name,[s.View.SelfLine.Single(c=>c.CardId==right.Id).Uid]}}));
        };
        Assert.Equal(UiSubmitOutcome.Rejected,await s.SubmitAsync(new PlayCard(card.Uid)));
        Assert.Equal(points,s.View.SelfKredits);Assert.Contains(s.View.SelfHand,c=>c.Uid==card.Uid);
        cancel=false;Assert.Equal(UiSubmitOutcome.Applied,await s.SubmitAsync(new PlayCard(card.Uid)));s.SettleProjection();
        Assert.Equal(points-1,s.View.SelfKredits);
        Assert.Equal(8,s.View.SelfLine.Single(c=>c.CardId==left.Id).EffectiveDefense);
        Assert.Equal(6,s.View.SelfLine.Single(c=>c.CardId==right.Id).EffectiveDefense);Assert.Equal(2,prompts);
    }
    private static string FindSource()
    {
        for(var d=AppContext.BaseDirectory;d is not null;d=Path.GetDirectoryName(d))
        {var p=Path.Combine(d,"proto","data","nations");if(Directory.Exists(p))return p;}
        throw new DirectoryNotFoundException();
    }
}
