using Godot;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

internal partial class NativeEffectInspector
{
    private void FriendlyNativeEffect(VBoxContainer parent,string effectPath,JsonObject envelope)
    {
        var path=effectPath+"/"+InspectorCardRecord.NativeKey;var native=envelope[InspectorCardRecord.NativeKey]!.AsObject();
        if(_playerAuthoring&&native["PrefabJson"] is not null){parent.AddChild(Note("这条导入效果会保持原样，目前不能可靠地转换为图形规则。"));return;}
        var timing=Card(parent);timing.AddChild(MenuStyles.Label("① 何时发生",18,MenuStyles.Gold));
        CompositionSlot(timing,path,native,InspectorPart.NativeEffect,native["PrefabJson"] is null?"事件与范围":"原生预制体",0);
        if(native["PrefabJson"] is not null)return;
        var actions=Card(parent);actions.AddChild(MenuStyles.Label("② 做什么，作用于谁",18,MenuStyles.Gold));
        var ops=native["Ops"] as JsonArray??new();
        for(var i=0;i<ops.Count;i++)
        {
            var at=path+"/Ops/"+i;var row=Card(actions);var head=new HFlowContainer();row.AddChild(head);
            var op=ops[i];head.AddChild(MenuStyles.Label((i+1)+". "+InspectorComposition.Describe(op),17,MenuStyles.Gold));
            head.AddChild(MenuStyles.Button("↑",()=>Structural(()=>Document.Move(at,-1))));head.AddChild(MenuStyles.Button("↓",()=>Structural(()=>Document.Move(at,1))));
            head.AddChild(MenuStyles.Button("复制",()=>Structural(()=>Document.Duplicate(at))));head.AddChild(MenuStyles.Button("移除",()=>Structural(()=>Document.Remove(at))));
            CompositionSlot(row,at,op,InspectorPart.NativeOperation,"对象、数值与条件",0,false,"native-op");
        }
        var add=MenuStyles.Button("＋ 添加或整理动作",()=>Compose(path+"/Ops",InspectorPart.List,"依次执行的动作",false,"native-ops"));
        actions.AddChild(add);FriendlyControls[path+"/Ops#compose"]=add;
    }
}
