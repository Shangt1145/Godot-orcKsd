using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using System.Text.Json.Nodes;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyDiyLayout()
    {
        try{await VerifyDiyLayoutAsync();GetTree().Quit();}
        catch(Exception error){GD.PushError(error.ToString());GetTree().Quit(1);}
    }
    private async Task VerifyDiyLayoutAsync()
    {
        var baseline=OS.GetCmdlineUserArgs().Contains("--diy-layout-baseline");
        var width=GetWindow().Size.X;
        var directory=ProjectSettings.GlobalizePath($"res://artifacts/diy-layout/{(baseline?"before":"after")}/{width}");
        Directory.CreateDirectory(directory);
        async Task Settle(){await ToSignal(GetTree().CreateTimer(.65),SceneTreeTimer.SignalName.Timeout);}
        async Task Capture(string name)
        {await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);GetViewport().GetTexture().GetImage().SavePng(directory+"/"+name+".png");}
        void Check(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
        Show("editor");await MenuSettledAsync();
        var pack=new DiyPackDraft{Name="DIY 布局验收"};_workshopLibrary.Save(pack);RefreshDiy();_workshop.OpenPack(pack);
        var card=new DiyCardDraft{PackId=pack.Id,Name="战地纪念队",Cost=0,OrcEffects=[new(){Ops=[
            new(){Op="buff",Sel="self",Attack=1,Defense=1,Condition=OrcEffectEditing.OwnerControlsType,CondUnitTypes=["infantry","tank"],CondComparison=">=",CondValue=2},
            new(){Op="counter.add",Counter="阵亡数",Amount=1}]}]};
        _diyEditor.Load(card);await DiyClickAsync(_diyEditor.OpenEffectsButton);await Settle();
        Check(_active=="diyfx","Effect editor did not open");await Capture("01-structured");
        if(!baseline)
        {
            var op=_diyEffects.StructureRows[0].OpRows[0];
            op.ConditionNumber.Value=3;await Settle();
            Check(_diyEditor.Draft.OrcEffects[0].Ops[0].CondValue==3,"Condition edit did not reach card draft");
            await DeckClickAsync(_diyEffects.SaveButton);
            Check(_diy.Cards.Single(c=>c.Id==card.Id).OrcEffects[0].Ops[0].CondValue==3,"Condition edit did not persist");
            _diyEffects.Open(_diy.Cards.Single(c=>c.Id==card.Id));await Settle();
            Check(_diyEffects.StructureRows[0].OpRows[0].ConditionNumber.Value==3,"Reopening lost condition value");
        }
        var mixed=card with{Id="diy/layout-mixed",OrcEffects=[
            new(){DesktopJson="{\"trigger\":\"deploy\",\"actions\":[{\"op\":\"drawOne\",\"side\":\"self\"}],\"targets\":[]}"},
            new(){DesktopJson="{\"trigger\":\"deploy\",\"actions\":[],\"unsupportedFlag\":true}"}]};
        _diyEditor.Load(mixed);_diyEffects.Open(mixed);await Settle();await Capture("02-graphical");
        if(!baseline)
        {
            var pick=_diyEffects.NativeInspector.FindChildren("*","Button",true,false).OfType<Button>().Single(b=>b.Text.StartsWith("效果 2\n",StringComparison.Ordinal));
            await DeckClickAsync(pick);await Settle();
        }
        var blocked=_diyEffects.NativeInspector.FindChildren("*","Button",true,false).OfType<Button>().Last(b=>b.Text=="仅草稿"&&b.IsVisibleInTree());
        await DeckClickAsync(blocked);await Settle();await Capture("03-reason");
        if(!baseline)Check(blocked.GetGlobalRect().End.X<=GetViewportRect().Size.X,"Capability badge overflowed screen");
        var counter=card with{Id="diy/layout-counter",Name="计数支援队",OrcEffects=[new(){Ops=[new(){Op="counter.add",Counter="阵亡数",Amount=1},new(){Op="buff",Sel="self",Attack=1,Defense=1,Counter="阵亡数",AmountFromCounter=true}]}]};
        _diyEditor.Load(counter);_diyEffects.Open(counter);await Settle();await Capture("04-counters");
        if(!baseline)
        {
            _diyEffects.EditorTabs.CurrentTab=3;await Settle();await Capture("04a-counter-references");
            var name=_diyEffects.CounterReferences.FindChildren("*","LineEdit",true,false).OfType<LineEdit>().Single();name.Text="新累计";
            await DeckClickAsync(_diyEffects.CounterReferences.FindChildren("*","Button",true,false).OfType<Button>().Single());await Settle();
            Check(_diyEditor.Draft.OrcEffects.SelectMany(e=>e.Ops).All(o=>o.Counter=="新累计"),"Counter rename did not update every reference");
            _diyEffects.EditorTabs.CurrentTab=2;await Settle();await Capture("04b-compilation");
            Check(_diyEffects.CompilationReport.Text.Contains("counterVariables"),"Compilation tab did not reflect renamed counter");
        }
        // Native snapshots stay read-only; this verifies visibility without changing bridge policy.
        var compiled=OrcEffectEditing.Compile("layout-prefab",[new(){Ops=[new(){Op="draw",Count=1}]}]).Single();
        var raw=JsonNode.Parse(compiled.JsonText!)!.AsArray()[0]!.DeepClone();
        raw!["root"]!["injects"]=new JsonArray(new JsonObject{["target"]="部署触发器",["event"]="e1",["band"]="Counter"});
        var native=card with{Id="diy/layout-native",OrcEffects=[new(){Template="native-prefab",PrefabJson=raw.ToJsonString()}]};
        _diyEditor.Load(native);_diyEffects.Open(native);await Settle();await Capture("05-native-injection");
        if(!baseline)
        {
            Check(_diyEffects.GetGlobalRect().End.X<=GetViewportRect().Size.X+2,"Editor overflowed viewport");
            var unknown=card with{OrcEffects=[new(){Ops=[new(){Op="future.operation",Name="原始参数",CondUnitTypes=["infantry"],CondComparison=">",CondValue=6}]}]};
            _diyEditor.Load(unknown);_diyEffects.Open(unknown);await Settle();
            Check(_diyEffects.StructureRows[0].OpRows[0].Read().Op=="future.operation"&&_diyEffects.StructureRows[0].OpRows[0].Read().CondValue==6,"Unknown operation was rewritten by UI");
            await Capture("06-preserved-unknown");
        }
        GD.Print($"DIY_LAYOUT_VERIFY_OK size={width} baseline={baseline} source-data-preserved");
    }
}
