using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using P=Kards.Ui.OrcBridge.InspectorPresentation;

namespace Kards.Ui;

internal partial class InspectorComposer
{
    private void Set(string path,JsonNode? value)=>_session!.Draft.Set(path,value);
    private void Record(string path,JsonNode? value,string schema)
    {
        if(value is not JsonObject record)
        {
            Hint("还没有设置。开始后可逐项填写，应用前不会改动原效果。");
            _body.AddChild(MenuStyles.Button("开始设置",()=>Change(()=>Set(path,new JsonObject()))));return;
        }
        if(schema is "action" or "condition-library" or "native-op")
        {
            var key=schema=="native-op"?"Op":"op";
            var choices=schema switch{"native-op"=>InspectorSchema.Choices("native-operation"),"action"=>InspectorVocabulary.Operations,_=>InspectorVocabulary.Conditions};
            Pick(path+"/"+key,schema=="condition-library"?"已有条件类型":"执行什么",record[key]?.ToString(),choices,id=>schema=="native-op"?InspectorSchema.ChoiceLabel("native-operation",id):schema=="action"?P.Operation(id):P.Condition(id),id=>Change(()=>
            {
                var seed=schema=="action"?InspectorVocabulary.Action(id):new JsonObject{[key]=id};
                if(schema=="native-op")
                    foreach(var field in InspectorSchema.Fields(schema,seed).Where(f=>!f.Optional&&f.Key!=key))
                        seed[field.Key]=field.Type switch{"integer"=>JsonValue.Create(1),"native-selection"=>JsonValue.Create("self"),_=>InspectorSchema.Initial(field)};
                _session!.SelectRecordKind(path,schema,key,id,seed);
            }));
        }
        var fields=InspectorSchema.Fields(schema,record);var covered=fields.Select(f=>f.Key).Append("op").ToHashSet();
        foreach(var field in fields)
        {
            if(field.Key=="Op"&&schema=="native-op")continue;
            if(record[field.Key] is null&&field.Optional)continue;
            RenderField(path,record,field);
        }
        var missing=fields.Where(f=>f.Optional&&record[f.Key] is null).ToArray();
        if(missing.Length>0)
        {
            var chosen=missing[0];Pick(path+"#optional","按需添加参数",null,missing.Select(f=>f.Key).ToArray(),id=>missing.First(f=>f.Key==id).Label,id=>chosen=missing.First(f=>f.Key==id));
            var add=MenuStyles.Button("＋ 添加选中的参数",()=>Change(()=>Set(InspectorDocument.Child(path,chosen.Key),InspectorSchema.Initial(chosen))));_body.AddChild(add);Controls[path+"#addField"]=add;
        }
        Extensions(record,covered.ToArray());
        if(fields.Count==0)Hint("未识别的结构保留原文，不推测字段含义；可在高级工具中检查。");
    }
    private void RenderField(string path,JsonObject record,InspectorField field)
    {
        var at=InspectorDocument.Child(path,field.Key);var value=record[field.Key];var type=field.Type;
        if(type is "value" or "condition" or "predicate" or "query" or "selector" or "filter" or "native-filter" or "cardPool" or "effect" or "actions" or "options" or "effectList" or "targets" or "native-types" or "conditions" or "keywords" or "cardIds" or "data" or "data-list")
        {
            if(type=="condition"&&value is JsonObject wrapper&&wrapper["op"]?.ToString()=="predicate")Slot(at,InspectorPart.Condition,field.Label,type);
            else if(type=="data"&&value is JsonArray)Slot(at,InspectorPart.List,field.Label,"data-list");
            else if(type=="data"&&value is JsonValue)Slot(at,InspectorPart.Value,field.Label);
            else Slot(at,InspectorSchema.Part(type),field.Label,type=="effect"?"effect":type);
        }
        else if(type=="boolean")
        {
            var check=new CheckBox{Text=field.Label,ButtonPressed=value?.GetValueKind()==JsonValueKind.True};_body.AddChild(check);Controls[at]=check;check.Toggled+=v=>Change(()=>Set(at,JsonValue.Create(v)));
        }
        else if(type=="integer")Integer(at,field.Label,value);
        else if(type=="event-card-filter")
        {
            var raw=value?.ToString()??"";var pieces=raw.Split(':',2);var dimension=pieces[0];var argument=pieces.Length>1?pieces[1]:"";
            Pick(at+"#dimension","检查事件卡的哪个信息",dimension.Length>0?dimension:null,OrcEffectEditing.ConditionDims.Select(d=>d.Id).ToArray(),id=>OrcEffectEditing.ConditionDims.FirstOrDefault(d=>d.Id==id)?.Label??id,id=>
            {
                var current=_session!.Draft.At(at)?.ToString().Split(':',2)??[];
                Set(at,JsonValue.Create(id+":"+(current.Length>1?current[1]:"")));
            });
            _body.AddChild(MenuStyles.Label("需要匹配的值",14,MenuStyles.Dim));
            var input=new LineEdit{Text=argument,PlaceholderText="填写匹配值",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};_body.AddChild(input);Controls[at]=input;
            input.TextChanged+=s=>Set(at,JsonValue.Create((_session!.Draft.At(at)?.ToString().Split(':',2)[0]??dimension)+":"+s));
        }
        else if(type=="code")
        {
            _body.AddChild(MenuStyles.Label(field.Label,14,MenuStyles.Dim));
            var input=new TextEdit{Text=value?.ToString()??"",CustomMinimumSize=new(0,250),SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,WrapMode=TextEdit.LineWrappingMode.Boundary};_body.AddChild(input);Controls[at]=input;input.TextChanged+=()=>Set(at,JsonValue.Create(input.Text));
        }
        else if(type is "card" or "native-card")
        {
            var cards=Catalog?.Invoke()??[];var ids=cards.Select(c=>type=="card"?c.CardId:c.Name).Distinct().ToArray();
            Pick(at,field.Label,value?.ToString(),ids,id=>cards.FirstOrDefault(c=>(type=="card"?c.CardId:c.Name)==id)?.Name??id,id=>Set(at,JsonValue.Create(id)));
        }
        else
        {
            var choices=InspectorSchema.Choices(type);
            if(choices.Count>0)Pick(at,field.Label,value?.ToString(),choices,id=>InspectorSchema.ChoiceLabel(type,id),id=>Change(()=>Set(at,JsonValue.Create(id))));
            else Text(at,field.Label,value?.ToString()??"");
        }
        if(field.Optional&&value is not null)
        {
            var clear=MenuStyles.Button("取消「"+field.Label+"」",()=>Change(()=>_session!.Draft.Remove(at)));_body.AddChild(clear);Controls[at+"#remove"]=clear;
        }
    }
    private void Integer(string path,string label,JsonNode? value)
    {
        _body.AddChild(MenuStyles.Label(label,14,MenuStyles.Dim));
        var input=new LineEdit{Text=value?.ToString()??"",PlaceholderText="填写整数，0 也是有效值",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};_body.AddChild(input);Controls[path]=input;
        input.TextChanged+=s=>Set(path,int.TryParse(s,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out var number)?JsonValue.Create(number):null);
    }
    private void List(string path,JsonNode? value,string schema)
    {
        if(value is not JsonArray items)
        {_body.AddChild(MenuStyles.Button("建立列表",()=>Change(()=>Set(path,new JsonArray()))));return;}
        var (part,itemSchema)=schema switch
        {
            "native-ops"=>(InspectorPart.NativeOperation,"native-op"),"actions"=>(InspectorPart.Record,"action"),"effectList"=>(InspectorPart.Record,"effect"),
            "options"=>(InspectorPart.Record,"option"),"targets"=>(InspectorPart.Record,"target"),"queries"=>(InspectorPart.Query,""),"conditions"=>(InspectorPart.Condition,""),
            "values"=>(InspectorPart.Value,""),_=>(InspectorPart.Record,schema)
        };
        if(items.Count==0)Hint("还没有内容。按从上到下的顺序处理；添加后可以继续组合。");
        for(var i=0;i<items.Count;i++)
        {
            var at=path+"/"+i;
            if(schema is "strings" or "keywords" or "cardIds" or "native-types")
            {
                var type=schema switch{"keywords"=>"keyword","native-types"=>"native-types","cardIds"=>"card",_=>"text"};
                if(type=="card")
                {var cards=Catalog?.Invoke()??[];Pick(at,"第 "+(i+1)+" 项",items[i]?.ToString(),cards.Select(c=>c.CardId).ToArray(),id=>cards.FirstOrDefault(c=>c.CardId==id)?.Name??id,id=>Set(at,JsonValue.Create(id)));}
                else if(InspectorSchema.Choices(type) is {Count:>0} choices)Pick(at,"第 "+(i+1)+" 项",items[i]?.ToString(),choices,id=>InspectorSchema.ChoiceLabel(type,id),id=>Set(at,JsonValue.Create(id)));
                else Text(at,"第 "+(i+1)+" 项",items[i]?.ToString()??"");
            }
            else if(schema=="data-list")Slot(at,items[i] is JsonObject?InspectorPart.Record:items[i] is JsonArray?InspectorPart.List:InspectorPart.Value,"第 "+(i+1)+" 项",items[i] is JsonObject?"data":items[i] is JsonArray?"data-list":"");
            else Slot(at,part,"第 "+(i+1)+" 项",itemSchema);
            var tools=new HBoxContainer();_body.AddChild(tools);
            foreach(var (label,command) in new (string,Action)[]{("↑",()=>_session!.MoveItem(at,-1)),("↓",()=>_session!.MoveItem(at,1)),("复制",()=>_session!.DuplicateItem(at)),("移除",()=>_session!.RemoveItem(at))})
                tools.AddChild(MenuStyles.Button(label,()=>Change(()=>{_literalTypes.Clear();command();})));
        }
        var add=MenuStyles.Button("＋ 添加一项",()=>Change(()=>
        {
            JsonNode? seed=schema switch
            {"native-ops"=>new JsonObject{["Op"]=""},"actions"=>new JsonObject{["op"]=""},"effectList"=>InspectorVocabulary.Effect(),"options"=>new JsonObject{["label"]="新选项",["actions"]=new JsonArray()},"targets"=>new JsonObject{["id"]="target"+(items.Count+1),["kind"]="unit"},"strings" or "keywords" or "cardIds" or "native-types"=>JsonValue.Create(""),_=>null};
            var index=items.Count;_session!.AppendItem(path);Set(path+"/"+index,seed);
        }));
        _body.AddChild(add);Controls[path+"#add"]=add;
    }
    private void NativeEffect(string path,JsonNode? value)
    {
        if(value is not JsonObject effect){Hint("原生效果格式无效，原文已保留。");return;}
        if(effect["PrefabJson"] is {})
        {
            Hint("这是原生预制体。保留完整声明及身份；修改后仍需通过原生编译和挂载检查。");
            Slot(path,InspectorPart.Record,"编辑预制体声明","prefab");return;
        }
        if(effect["TextCardType"] is {})
        {
            Hint("基础效果由后端文本编译，时机随卡牌类型确定。需要其他时机或复杂参数时，可先转为高级结构。");
            Slot(path+"/Ops",InspectorPart.List,"依次执行哪些动作","native-ops");
            _body.AddChild(MenuStyles.Button("解除文本来源，转为高级结构",()=>Change(()=>_session!.Draft.Remove(path+"/TextCardType"))));return;
        }
        var events=OrcEffectEditing.Templates.Select(t=>t.Hook??"@"+t.Id).Distinct().ToArray();
        var selected=effect["Hook"]?.ToString()??(effect["Template"]?.ToString() is {Length:>0} template?"@"+template:null);
        string EventName(string id)=>id switch{"unit.deployed"=>"单位部署","card.played"=>"卡牌打出","card.died"=>"卡牌被消灭","turn.start"=>"回合开始","turn.end"=>"回合结束","card.damaged"=>"受到伤害","unit.damage.dealt"=>"造成伤害","unit.acted"=>"完成攻击","@attack_basic"=>"攻击发起",_=>id};
        Pick(path+"#event","何时发生",selected,events,EventName,id=>Change(()=>
        {
            Set(path+"/Hook",id.StartsWith('@')?null:JsonValue.Create(id));
            Set(path+"/Template",JsonValue.Create(id.StartsWith('@')?id[1..]:""));Set(path+"/Guard",JsonValue.Create(""));
        }));
        var candidates=OrcEffectEditing.Templates.Where(t=>(t.Hook??"@"+t.Id)==selected).ToArray();
        Pick(path+"/Guard","事件涉及谁",effect["Guard"]?.ToString() is {Length:>0} guard?guard:null,candidates.Select(t=>t.Guard.ToString()).ToArray(),id=>id switch{"SelfUnit" or "SelfCard"=>"效果来源","OwnerSame"=>"友方单位","OwnerSameByPlayer"=>"友方玩家","None"=>"由事件模板决定",_=>id},id=>Change(()=>
        {
            var match=candidates.Single(t=>t.Guard.ToString()==id);_session!.Draft.Transaction(()=>{Set(path+"/Template",JsonValue.Create(match.Id));Set(path+"/Hook",match.Hook is null?null:JsonValue.Create(match.Hook));Set(path+"/Guard",JsonValue.Create(id));});
        }));
        Slot(path+"/Ops",InspectorPart.List,"依次执行哪些动作","native-ops");
    }
    private void Trigger(string path,JsonNode? value)
    {
        var mode=InspectorComposition.Mode(value,InspectorPart.Trigger);
        Pick(path+"#mode","时机来源",mode=="unset"?null:mode,["event","behavior"],id=>id=="event"?"常规事件":"某个行为的前后阶段",id=>Change(()=>_session!.Switch(path,InspectorPart.Trigger,id)));
        if(mode=="event")
        {
            var ids=InspectorVocabulary.Catalog.GetProperty("labels").GetProperty("TRIGGERS").EnumerateObject().Select(p=>p.Name).ToArray();
            Pick(path+"#event","何时发生",value?.ToString(),ids,P.Timing,id=>Set(path,JsonValue.Create(id)));return;
        }
        if(mode!="behavior")return;
        var behavior=value as JsonObject;
        if(behavior is null)
        {
            var parts=value!.ToString()[9..].Split(['/',':']);
            if(parts.Length!=3){Hint("已有自定义时机保留原文，可在高级工具检查。");return;}
            behavior=new JsonObject{["kind"]=parts[0],["action"]=parts[1],["phase"]=parts[2]};Set(path,behavior);behavior=_session!.Draft.At(path)!.AsObject();
        }
        Pick(path+"/kind","观察哪类行为",behavior["kind"]?.ToString(),["game","op"],P.Value,id=>Change(()=>{Set(path+"/kind",JsonValue.Create(id));Set(path+"/action",null);}));
        var kind=behavior["kind"]?.ToString();
        var actions=kind=="game"?InspectorVocabulary.Catalog.GetProperty("behaviorActions").EnumerateObject().Select(p=>p.Name).ToArray():kind=="op"?InspectorVocabulary.Operations:[];
        Pick(path+"/action","具体行为",behavior["action"]?.ToString(),actions,id=>kind=="game"?InspectorVocabulary.Catalog.GetProperty("behaviorActions").GetProperty(id).GetString()!:P.Operation(id),id=>Set(path+"/action",JsonValue.Create(id)));
        Pick(path+"/phase","何时响应",behavior["phase"]?.ToString(),["before","during","after"],P.Value,id=>Set(path+"/phase",JsonValue.Create(id)));
    }
}
