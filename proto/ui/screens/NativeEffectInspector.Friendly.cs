using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using P=Kards.Ui.OrcBridge.InspectorPresentation;

namespace Kards.Ui;

internal partial class NativeEffectInspector
{
    private VBoxContainer _advancedRoot=null!,_effectList=null!,_effectForm=null!;
    private HSplitContainer _friendlyRoot=null!;
    private Label _sentence=null!;
    private ScrollContainer _friendlyScroll=null!;
    private int _selectedEffect;
    private int _renderedEffect=-1;
    private bool _renderingFriendly;
    private readonly HashSet<string> _expanded=[];
    internal Button NewEffectButton=null!,PickerAddButton=null!;
    internal Button CardKeywordsButton=null!,KeywordDoneButton=null!;
    internal ItemList PickerItems=null!;
    internal LineEdit PickerSearch=null!;
    internal readonly Dictionary<string,Control> FriendlyControls=[];
    private AcceptDialog _actionPicker=null!;
    private OptionButton _pickerGroup=null!;
    private Label _pickerExplanation=null!;
    private readonly List<string> _pickerOps=[];
    private string _addActionsPath="";
    private AcceptDialog _keywordDialog=null!;
    private VBoxContainer _keywordRows=null!;
    internal readonly Dictionary<string,CheckBox> KeywordControls=[];

    private void InitializeFriendly()
    {
        _friendlyRoot=new HSplitContainer{SizeFlagsVertical=SizeFlags.ExpandFill,SplitOffsets=[240]};AddChild(_friendlyRoot);
        var left=Card(_friendlyRoot);left.CustomMinimumSize=new(200,0);
        left.GetParent<PanelContainer>().SizeFlagsHorizontal=SizeFlags.Fill;
        _friendlyRoot.Resized+=()=>_friendlyRoot.SplitOffsets=[240];
        left.AddChild(UiStyles.Label("这张牌的效果",18,UiStyles.Gold));
        left.AddChild(Note("每条效果都有自己的时机，\n也可以包含多个动作。"));
        var scroll=new ScrollContainer{SizeFlagsVertical=SizeFlags.ExpandFill,HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};left.AddChild(scroll);
        _effectList=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};scroll.AddChild(_effectList);
        NewEffectButton=UiStyles.Button("＋ 添加一条效果",()=>
        {
            var seed=InspectorVocabulary.Effect();
            if(Document.Root["cardType"]?.ToString()=="order")seed["trigger"]="order";
            Structural(()=>{Document.Append("/effects",seed);_selectedEffect=((JsonArray)Document.Root["effects"]!).Count-1;});
        });left.AddChild(NewEffectButton);
        CardKeywordsButton=UiStyles.Button("卡牌自带的词条",OpenCardKeywords);left.AddChild(CardKeywordsButton);
        var right=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};_friendlyRoot.AddChild(right);
        _sentence=UiStyles.Label("",17,UiStyles.Gold);_sentence.AutowrapMode=TextServer.AutowrapMode.WordSmart;right.AddChild(_sentence);
        _friendlyScroll=new ScrollContainer{SizeFlagsVertical=SizeFlags.ExpandFill,HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};right.AddChild(_friendlyScroll);
        _effectForm=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};_effectForm.AddThemeConstantOverride("separation",14);_friendlyScroll.AddChild(_effectForm);
        BuildActionPicker();
        _keywordDialog=new AcceptDialog{Title="卡牌自带的词条",MinSize=new(620,480)};AddChild(_keywordDialog);
        var keywordPage=new VBoxContainer{CustomMinimumSize=new(580,0)};_keywordDialog.AddChild(keywordPage);
        var keywordHint=Note("勾选卡牌本身具有的词条。若要在某个时机获得词条，请在动作中设置。");keywordHint.CustomMinimumSize=new(550,44);keywordPage.AddChild(keywordHint);
        var keywordScroll=new ScrollContainer{CustomMinimumSize=new(0,330),SizeFlagsVertical=SizeFlags.ExpandFill,HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};keywordPage.AddChild(keywordScroll);
        _keywordRows=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};keywordScroll.AddChild(_keywordRows);
        KeywordDoneButton=_keywordDialog.GetOkButton();KeywordDoneButton.Text="完成选择";
    }
    private void OpenCardKeywords()
    {
        if(!CommitPendingRaw())return;
        Clear(_keywordRows);KeywordControls.Clear();
        foreach(var keyword in InspectorVocabulary.Catalog.GetProperty("keywords").EnumerateObject())
        {
            var id=keyword.Name;var row=new HBoxContainer();_keywordRows.AddChild(row);
            var isOn=Document.Root["kwMap"]?[id] is JsonValue flag&&flag.GetValueKind()==JsonValueKind.True;
            var check=new CheckBox{Text=keyword.Value.GetProperty("cn").GetString(),ButtonPressed=isOn,SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(check);KeywordControls[id]=check;
            check.Toggled+=on=>
            {
                SetCardMap("kwMap",id,JsonValue.Create(on));
                foreach(var child in row.GetChildren())
                    if(child is BaseButton button&&child!=check)button.Disabled=!on;
                    else if(child is SpinBox number)number.Editable=on;
            };
            var hasNumber=Document.Root["kwValues"]?[id] is JsonValue n&&n.GetValueKind()==JsonValueKind.Number;
            if(hasNumber)
            {
                var value=new SpinBox{Editable=isOn,Value=Document.Root["kwValues"]![id]!.GetValue<double>(),AllowGreater=true,AllowLesser=true,MinValue=-1000,MaxValue=1000,Step=1,CustomMinimumSize=new(120,0)};row.AddChild(value);
                value.ValueChanged+=v=>SetCardMap("kwValues",id,JsonValue.Create(v));
                var clear=UiStyles.Button("不指定数值",()=>{Document.Remove("/kwValues/"+id);OpenCardKeywords();});clear.Disabled=!isOn;row.AddChild(clear);
            }
            else{var add=UiStyles.Button("指定数值",()=>{SetCardMap("kwValues",id,JsonValue.Create(1));OpenCardKeywords();});add.Disabled=!isOn;row.AddChild(add);}
        }
        _keywordDialog.PopupCentered(new Vector2I(650,Math.Min(520,(int)GetViewportRect().Size.Y-80)));
    }
    private void SetCardMap(string field,string key,JsonNode? value)
    {
        var map=(Document.Root[field] as JsonObject)?.DeepClone().AsObject()??new JsonObject();map[key]=value;Document.Set("/"+field,map);
    }
    private static VBoxContainer Card(Container parent)
    {
        var panel=new PanelContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};parent.AddChild(panel);
        var style=new StyleBoxFlat{BgColor=new Color("202722ed"),BorderColor=new Color("4d584b"),BorderWidthLeft=1,BorderWidthRight=1,BorderWidthTop=1,BorderWidthBottom=1,
            CornerRadiusTopLeft=6,CornerRadiusTopRight=6,CornerRadiusBottomLeft=6,CornerRadiusBottomRight=6,
            ContentMarginLeft=16,ContentMarginRight=16,ContentMarginTop=12,ContentMarginBottom=12};panel.AddThemeStyleboxOverride("panel",style);
        var box=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};box.AddThemeConstantOverride("separation",10);panel.AddChild(box);return box;
    }
    private static Label Note(string text)
    {
        var label=UiStyles.Label(text,13,UiStyles.Dim);label.AutowrapMode=TextServer.AutowrapMode.WordSmart;return label;
    }
    private static void Clear(Container box){foreach(var n in box.GetChildren()){box.RemoveChild(n);n.QueueFree();}}
    private void UpdateFriendlySummary()
    {
        if(_sentence is null)return;
        var keywordCount=(Document.Root["kwMap"] as JsonObject)?.Count(p=>p.Value?.GetValueKind()==JsonValueKind.True)??0;
        CardKeywordsButton.Text="卡牌自带词条"+(keywordCount>0?"（"+keywordCount+"）":"");
        var effects=Document.Root["effects"] as JsonArray;
        _sentence.Text=effects is {Count:>0}?P.Brief(effects[Math.Clamp(_selectedEffect,0,effects.Count-1)]):"先选一个发生时机，再告诉它要做什么。";
    }
    private void RenderFriendly()
    {
        if(_effectList is null||_renderingFriendly)return;
        _renderingFriendly=true;
        try
        {
            var scroll=_renderedEffect==_selectedEffect?_friendlyScroll.ScrollVertical:0;
            Clear(_effectList);Clear(_effectForm);FriendlyControls.Clear();
            if(Document.Root["effects"] is not JsonArray effects){_effectForm.AddChild(Note("这张牌包含自定义结构，请在高级工具中检查；原内容已保留。"));return;}
            _selectedEffect=Math.Clamp(_selectedEffect,0,Math.Max(0,effects.Count-1));
            for(var i=0;i<effects.Count;i++)
            {
                var index=i;var b=UiStyles.Button("效果 "+(i+1)+"\n"+P.Timing(effects[i]?["trigger"]?.ToString()??"deploy"),()=>{_selectedEffect=index;RenderFriendly();});
                b.Alignment=HorizontalAlignment.Left;b.ToggleMode=true;b.SetPressedNoSignal(i==_selectedEffect);b.CustomMinimumSize=new(0,65);_effectList.AddChild(b);
            }
            UpdateFriendlySummary();
            if(effects.Count==0)
            {
                var empty=Card(_effectForm);empty.AddChild(UiStyles.Label("从一条新效果开始",22,UiStyles.Gold));
                empty.AddChild(Note("例如：部署时抽牌、受伤后加强友军、回合结束时检查条件。\n这些都可以继续组合和修改。"));
                empty.AddChild(UiStyles.Button("添加一条效果",()=>NewEffectButton.EmitSignal(Button.SignalName.Pressed)));
                AddSentenceStarter(empty);return;
            }
            var path="/effects/"+_selectedEffect;
            if(effects[_selectedEffect] is not JsonObject effect){_effectForm.AddChild(Note("这一条是自定义结构，已保留。可以通过高级工具继续编辑。"));return;}
            var top=new HBoxContainer();_effectForm.AddChild(top);
            top.AddChild(UiStyles.Label("正在编辑：效果 "+(_selectedEffect+1),15,UiStyles.Dim));
            top.AddChild(UiStyles.Button("复制这条",()=>Structural(()=>Document.Duplicate(path))));
            top.AddChild(UiStyles.Button("上移",()=>{Structural(()=>Document.Move(path,-1));_selectedEffect=Math.Max(0,_selectedEffect-1);}));
            top.AddChild(UiStyles.Button("下移",()=>{Structural(()=>Document.Move(path,1));_selectedEffect=Math.Min(effects.Count-1,_selectedEffect+1);}));
            top.AddChild(UiStyles.Button("删除这条",()=>Structural(()=>Document.Remove(path))));
            FriendlyEffect(_effectForm,path,effect,0,true);
            Callable.From(()=>_friendlyScroll.ScrollVertical=scroll).CallDeferred();
        }
        finally{_renderedEffect=_selectedEffect;_renderingFriendly=false;}
    }
    private void AddSentenceStarter(VBoxContainer parent)
    {
        parent.AddChild(Note("也可以先用一句话描述，让解析器生成起点。"));
        var input=new LineEdit{PlaceholderText="例如：部署：抽两张牌。",SizeFlagsHorizontal=SizeFlags.ExpandFill};parent.AddChild(input);
        parent.AddChild(UiStyles.Button("生成后继续编辑",()=>
        {
            try
            {
                var result=DesktopOrc.CompileGraph(input.Text,Document.Root["cardType"]?.ToString()??"unit",Catalog?.Invoke());
                if(!result.GetProperty("complete").GetBoolean())
                {_status.Text="这句话还没有完整识别，原效果未改动。可以换一种说法，或直接用按钮搭建。";return;}
                var copy=Document.Root.DeepClone().AsObject();copy["effects"]=JsonNode.Parse(result.GetProperty("effects").GetRawText());
                foreach(var f in result.GetProperty("cardFields").EnumerateObject())copy[f.Name]=JsonNode.Parse(f.Value.GetRawText());
                foreach(var key in new[]{"kwMap","kwValues"})
                {
                    copy[key]??=new JsonObject();foreach(var pair in result.GetProperty(key).EnumerateObject())copy[key]![pair.Name]=JsonNode.Parse(pair.Value.GetRawText());
                }
                Structural(()=>Document.Set("",copy));
            }
            catch(InvalidDataException){_status.Text="暂时无法识别这句话；你仍可以直接用按钮搭建。";}
        }));
    }
    private void FriendlyEffect(VBoxContainer parent,string path,JsonObject effect,int depth,bool main=false)
    {
        if(depth>12){parent.AddChild(Note("更深层的组合已保留，可在高级工具中继续编辑。"));return;}
        var timing=Card(parent);timing.AddChild(UiStyles.Label(main?"① 什么时候触发":"这段新效果什么时候触发",18,UiStyles.Gold));
        FriendlyTiming(timing,InspectorDocument.Child(path,"trigger"),effect["trigger"]?.ToString()??"deploy");
        var conditionPath=InspectorDocument.Child(path,"condition");
        if(effect["condition"] is {} condition)
        {
            timing.AddChild(Note("只有下面的条件满足时才执行："));FriendlyValue(timing,conditionPath,condition,"condition","条件",depth+1);
            timing.AddChild(UiStyles.Button("取消这项前提",()=>Structural(()=>Document.Remove(conditionPath))));
        }
        var timingTools=new HBoxContainer();timing.AddChild(timingTools);
        if(effect["condition"] is null)timingTools.AddChild(UiStyles.Button("＋ 添加生效条件",()=>Structural(()=>Document.Set(conditionPath,JsonNode.Parse("{\"op\":\"predicate\",\"test\":{\"test\":\"all\",\"items\":[]}}")))));
        var opts=new[]{"once","oncePerTurn","eventSide","selfOnly","listenZone","firstEvent"};
        var flags=opts.Where(k=>effect[k] is not null&&effect[k]!.ToString()!="false").Select(P.Field).ToArray();
        var timingId=path+"#timing";var open=_expanded.Contains(timingId);
        timingTools.AddChild(UiStyles.Button((open?"▾ ":"▸ ")+(flags.Length>0?"触发限制（已设置）":"更多触发设置"),()=>{if(!_expanded.Add(timingId))_expanded.Remove(timingId);RenderFriendly();}));
        if(open)
        {
            var box=new VBoxContainer();timing.AddChild(box);
            foreach(var key in opts)
            {
                var p=InspectorDocument.Child(path,key);
                if(effect.ContainsKey(key))FriendlyValue(box,p,effect[key],key is "once" or "oncePerTurn" or "selfOnly" or "firstEvent"?"boolean":"text",P.Field(key),depth+1,key);
                else box.AddChild(UiStyles.Button("设置"+P.Field(key),()=>Structural(()=>Document.Set(p,InspectorVocabulary.Initial(key)))));
            }
        }
        if(effect["targets"] is JsonArray targets&&targets.Count>0)
        {
            var targetBox=Card(parent);targetBox.AddChild(UiStyles.Label("让玩家先选目标",16,UiStyles.Gold));
            for(var i=0;i<targets.Count;i++)
            {
                var targetPath=path+"/targets/"+i;var index=i;
                if(targets[i] is JsonObject target)
                {
                    targetBox.AddChild(Note("目标 "+(i+1)));
                    foreach(var key in new[]{"kind","side","zone","prompt","filter"})
                        if(target.ContainsKey(key))FriendlyValue(targetBox,targetPath+"/"+key,target[key],key=="filter"?"filter":"text",P.Field(key),depth+1,key);
                    targetBox.AddChild(UiStyles.Button("移除目标 "+(i+1),()=>Structural(()=>Document.Remove(path+"/targets/"+index))));
                }
            }
        }
        var actions=Card(parent);actions.AddChild(UiStyles.Label(main?"② 要做什么":"执行哪些动作",18,UiStyles.Gold));
        FriendlyActions(actions,path+"/actions",effect["actions"] as JsonArray??new(),depth+1);
        if(effect["else"] is JsonArray otherwise)
        {
            var other=Card(parent);other.AddChild(UiStyles.Label("条件不满足时",16,UiStyles.Gold));FriendlyActions(other,path+"/else",otherwise,depth+1);
        }
    }
    private void FriendlyTiming(VBoxContainer parent,string path,string trigger)
    {
        var ordinary=InspectorVocabulary.Catalog.GetProperty("labels").GetProperty("TRIGGERS").EnumerateObject().Select(p=>p.Name).ToArray();
        var selected=trigger.StartsWith("behavior:")?"behavior":trigger;
        var ids=ordinary.Concat(new[]{"behavior"}).ToArray();
        Choose(parent,path,"生效时机",selected,ids,v=>v=="behavior"?"某个行为发生前、发生时或发生后":P.Timing(v),value=>
            Structural(()=>Document.Set(path,JsonValue.Create(value=="behavior"?"behavior:game/playCard:after":value))));
        if(selected!="behavior")return;
        var parts=trigger[9..].Split(['/',':']);if(parts.Length!=3)return;
        Choose(parent,path+"#kind","什么行为",parts[0],["game","op"],P.Value,v=>Structural(()=>Document.Set(path,JsonValue.Create($"behavior:{v}/{(v=="game"?"playCard":"drawOne")}:{parts[2]}"))));
        var actions=parts[0]=="game"?InspectorVocabulary.Catalog.GetProperty("behaviorActions").EnumerateObject().Select(p=>p.Name).ToArray():InspectorVocabulary.Operations;
        Choose(parent,path+"#action","具体行为",parts[1],actions,v=>parts[0]=="game"?InspectorVocabulary.Catalog.GetProperty("behaviorActions").GetProperty(v).GetString()!:P.Operation(v),v=>Structural(()=>Document.Set(path,JsonValue.Create($"behavior:{parts[0]}/{v}:{parts[2]}"))));
        Choose(parent,path+"#phase","何时响应",parts[2],["before","during","after"],P.Value,v=>Structural(()=>Document.Set(path,JsonValue.Create($"behavior:{parts[0]}/{parts[1]}:{v}"))));
    }
    private void FriendlyActions(VBoxContainer parent,string path,JsonArray actions,int depth)
    {
        if(actions.Count==0)parent.AddChild(Note("还没有动作。添加后会按从上到下的顺序执行。"));
        for(var i=0;i<actions.Count;i++)
        {
            var index=i;var p=path+"/"+i;
            if(actions[i] is not JsonObject action){parent.AddChild(Note("自定义步骤已保留，可在高级工具中查看。"));continue;}
            var block=Card(parent);var head=new HBoxContainer();block.AddChild(head);
            var title=UiStyles.Label((i+1)+". "+P.Operation(action["op"]?.ToString()??""),17,UiStyles.Gold);title.SizeFlagsHorizontal=SizeFlags.ExpandFill;head.AddChild(title);
            if(actions.Count>1){head.AddChild(UiStyles.Button("↑",()=>Structural(()=>Document.Move(p,-1))));head.AddChild(UiStyles.Button("↓",()=>Structural(()=>Document.Move(p,1))));}
            head.AddChild(UiStyles.Button("复制",()=>Structural(()=>Document.Duplicate(p))));head.AddChild(UiStyles.Button("移除",()=>Structural(()=>Document.Remove(p))));
            if(depth>12){block.AddChild(Note("深层组合已保留，可在高级工具中继续编辑。"));continue;}
            var op=action["op"]?.ToString()??"";
            if(!InspectorVocabulary.Operations.Contains(op)){block.AddChild(Note("这是一个扩展动作，内容已保留。"));continue;}
            var parameters=P.Parameters(op);var covered=new HashSet<string>{"op"};
            foreach(var field in parameters)
            {
                var key=field.GetProperty("name").GetString()!;covered.Add(key);
                var optional=field.TryGetProperty("optional",out var opt)&&opt.GetBoolean();
                if(!action.ContainsKey(key))
                {
                    if(!optional)block.AddChild(UiStyles.Button("填写"+P.Field(key),()=>Structural(()=>Document.Set(p+"/"+key,JsonNode.Parse(field.GetProperty("initial").GetRawText())))));
                    continue;
                }
                FriendlyValue(block,p+"/"+key,action[key],field.GetProperty("type").GetString()!,P.Field(key),depth+1,key);
            }
            var missing=parameters.Where(f=>!action.ContainsKey(f.GetProperty("name").GetString()!)).ToArray();
            if(missing.Length>0||parameters.Any(f=>f.TryGetProperty("optional",out var optional)&&optional.GetBoolean()))Fold(block,p+"#more","更多设置",box=>
            {
                foreach(var field in missing)
                {
                    var key=field.GetProperty("name").GetString()!;
                    box.AddChild(UiStyles.Button("＋ "+P.Field(key),()=>Structural(()=>Document.Set(p+"/"+key,JsonNode.Parse(field.GetProperty("initial").GetRawText())))));
                }
                var removable=parameters.Where(f=>f.TryGetProperty("optional",out var opt)&&opt.GetBoolean()&&action.ContainsKey(f.GetProperty("name").GetString()!)).Select(f=>f.GetProperty("name").GetString()!).ToArray();
                if(removable.Length>0)
                {
                    var picked=removable[0];Choose(box,p+"#removeOptional","取消一项可选设置",picked,removable,P.Field,k=>picked=k);
                    box.AddChild(UiStyles.Button("恢复该项的默认行为",()=>Structural(()=>Document.Remove(p+"/"+picked))));
                }
            });
            if(action.Any(pair=>!covered.Contains(pair.Key)))block.AddChild(Note("这一步还包含扩展设置，已原样保留。高级工具中可以继续编辑。"));
        }
        var add=UiStyles.Button(actions.Count==0?"＋ 选择要做的事":"＋ 再添加一个动作",()=>OpenActionPicker(path));parent.AddChild(add);FriendlyControls[path+"#add"]=add;
    }
    private void Fold(VBoxContainer parent,string id,string label,Action<VBoxContainer> content)
    {
        var open=_expanded.Contains(id);var button=UiStyles.Button((open?"▾ ":"▸ ")+label,()=>{if(!_expanded.Add(id))_expanded.Remove(id);RenderFriendly();});parent.AddChild(button);
        if(open){var box=new VBoxContainer();parent.AddChild(box);content(box);}
    }
    private void FriendlyValue(VBoxContainer parent,string path,JsonNode? value,string type,string label,int depth,string key="")
    {
        if(depth>14){parent.AddChild(Note(label+"：深层组合已保留。"));return;}
        if(type is "query" or "selector"){FriendlyQuery(parent,path,value,label,depth);return;}
        if(type=="actions"){parent.AddChild(UiStyles.Label(label,15,UiStyles.Gold));FriendlyActions(parent,path,value as JsonArray??new(),depth);return;}
        if(type=="effect"&&value is JsonObject e){var box=Card(parent);box.AddChild(UiStyles.Label(label,16,UiStyles.Gold));FriendlyEffect(box,path,e,depth+1);return;}
        if(type is "predicate" or "condition"){FriendlyCondition(parent,path,value,label,depth,type=="condition");return;}
        if(type=="value"){FriendlyNumber(parent,path,value,label,depth);return;}
        var choices=type switch
        {
            "side" or "legacySide"=>new[]{"self","enemy","both","any"},"scope"=>["unit","player","match"],
            "attribute"=>["attack","defense","opCost","cost"],"resource"=>["kredits","maxKredits"],"mode"=>["add","set"],
            "duration"=>["permanent","turn","nextTurn"],"position"=>["support","frontline"],"weather"=>["clear","mist","gale","snow"],
            "cardZone"=>["deck","hand"],"cardPosition"=>["shuffle","top","bottom","left","right"],"offerMode"=>["fixed","random"],
            "keyword"=>InspectorVocabulary.Choices("keyword"),_=>key switch
            {
                "piles"=>["hand","deck","both"],"from"=>["deck","hand","discard","pool"],"on"=>["unit","global"],
                "listenZone"=>["auto","hand","field","any"],_=>InspectorVocabulary.Choices(key)
            }
        };
        if(type=="trigger"){FriendlyTiming(parent,path,value?.ToString()??"deploy");return;}
        if(type=="card")
        {
            var cards=Catalog?.Invoke()??[];
            Choose(parent,path,label,value?.ToString()??"",cards.Select(c=>c.CardId).Prepend("").ToArray(),id=>id.Length==0?"请选择一张卡牌":cards.FirstOrDefault(c=>c.CardId==id)?.Name??"已有引用",id=>Document.Set(path,JsonValue.Create(id)));return;
        }
        if(choices.Length>0)
        {
            var current=value is JsonValue scalar&&scalar.GetValueKind()==JsonValueKind.String?value.ToString():"#custom";
            Choose(parent,path,label,current,choices,v=>v=="#custom"?"已有自定义设置（保留中）":type=="scope"&&v=="unit"?"本单位":key is "of" or "ref" or "sel"&&v=="self"?"效果来源":P.Value(v),v=>{if(v!="#custom")Document.Set(path,JsonValue.Create(v));});return;
        }
        if(value is JsonValue flag&&flag.TryGetValue<bool>(out var boolean))
        {var check=new CheckBox{Text=label,ButtonPressed=boolean};parent.AddChild(check);FriendlyControls[path]=check;check.Toggled+=b=>Document.Set(path,JsonValue.Create(b));return;}
        if(value is JsonValue number&&number.GetValueKind()==JsonValueKind.Number){FriendlyNumber(parent,path,value,label,depth);return;}
        if(value is JsonObject obj)
        {
            var box=Card(parent);box.AddChild(UiStyles.Label(label,15,UiStyles.Gold));
            foreach(var (k,v) in obj)FriendlyValue(box,InspectorDocument.Child(path,k),v,P.ParameterType(obj,k),P.Field(k),depth+1,k);
            if(type is "filter" or "cardPool")
                AddOptionalFields(box,path,obj,type=="filter"?["unitType","cardType","set","minCost","maxCost","keyword","where"]:["cardIds","filter"],depth);
            return;
        }
        if(value is JsonArray array)
        {
            var box=Card(parent);box.AddChild(UiStyles.Label(label,15,UiStyles.Gold));
            for(var i=0;i<array.Count;i++)
            {
                var p=path+"/"+i;var row=Card(box);
                if(type=="options"&&array[i] is JsonObject option)
                {
                    FriendlyValue(row,p+"/label",option["label"]??JsonValue.Create("选项 "+(i+1)),"text","方案名称",depth+1);
                    FriendlyActions(row,p+"/actions",option["actions"] as JsonArray??new(),depth+1);
                }
                else if(type=="effectList"&&array[i] is JsonObject effect)FriendlyEffect(row,p,effect,depth+1);
                else FriendlyValue(row,p,array[i],key=="cardIds"?"card":key is "keywords" or "removeKeywords"?"keyword":"data","第 "+(i+1)+" 项",depth+1);
                row.AddChild(UiStyles.Button("移除这一项",()=>Structural(()=>Document.Remove(p))));
            }
            box.AddChild(UiStyles.Button("＋ 添加一项",()=>Structural(()=>Document.Append(path,type=="options"?JsonNode.Parse("{\"label\":\"新方案\",\"actions\":[]}"):type=="effectList"?InspectorVocabulary.Effect():JsonValue.Create("")))));return;
        }
        var line=new HBoxContainer();parent.AddChild(line);line.AddChild(FieldLabel(label));
        if(type=="code")
        {
            var edit=new TextEdit{Text=value?.ToString()??"",CustomMinimumSize=new(0,130),SizeFlagsHorizontal=SizeFlags.ExpandFill};line.AddChild(edit);FriendlyControls[path]=edit;edit.TextChanged+=()=>Document.Set(path,JsonValue.Create(edit.Text));return;
        }
        var input=new LineEdit{Text=value?.ToString()??"",PlaceholderText="在这里填写",SizeFlagsHorizontal=SizeFlags.ExpandFill};line.AddChild(input);FriendlyControls[path]=input;
        input.TextChanged+=s=>Document.Set(path,JsonValue.Create(s));
    }
    private static Label FieldLabel(string label){var l=UiStyles.Label(label,14,UiStyles.Dim);l.CustomMinimumSize=new(125,0);return l;}
    private OptionButton Choose(VBoxContainer parent,string id,string label,string current,IReadOnlyList<string> items,Func<string,string> name,Action<string> change)
    {
        var row=new HBoxContainer();parent.AddChild(row);row.AddChild(FieldLabel(label));
        var pick=new OptionButton{FitToLongestItem=false,CustomMinimumSize=new(150,38),SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(pick);
        var ids=items.Distinct().ToList();if(!ids.Contains(current))ids.Insert(0,current);
        foreach(var value in ids){pick.AddItem(name(value));pick.SetItemMetadata(pick.ItemCount-1,value);}pick.Select(ids.IndexOf(current));
        pick.ItemSelected+=i=>change(ids[(int)i]);FriendlyControls[id]=pick;return pick;
    }
    private void FriendlyNumber(VBoxContainer parent,string path,JsonNode? value,string label,int depth)
    {
        if(depth>14){parent.AddChild(Note(label+"：深层取值已保留。"));return;}
        var expression=value as JsonObject;var mode=expression?["expr"]?.ToString()??"literal";
        var showMode=expression is not null||_expanded.Contains(path+"#calculation");
        var modes=new[]{"literal","variable","read","count","math","size","random","memory","query","union","intersection"};
        if(expression is not null&&!expression.ContainsKey("expr"))mode="legacy";
        if(showMode)Choose(parent,path+"#valueMode",label+"的来源",mode,modes,P.Value,next=>
        {
            var seed=next switch
            {
                "literal"=>JsonValue.Create(1) as JsonNode,"variable"=>JsonNode.Parse("{\"expr\":\"variable\",\"name\":\"记录一\"}"),
                "read"=>new JsonObject{["expr"]="read",["of"]=path.Contains("/where",StringComparison.Ordinal)||path.Contains("/filter",StringComparison.Ordinal)?"candidate":"self",["path"]="attack"},
                "count" or "query"=>JsonNode.Parse("{\"expr\":\""+next+"\",\"query\":{\"sel\":\"all\",\"side\":\"friendly\",\"zone\":\"field\"}}"),
                "math"=>JsonNode.Parse("{\"expr\":\"math\",\"operator\":\"add\",\"items\":[1,1]}"),
                "size"=>JsonNode.Parse("{\"expr\":\"size\",\"value\":{\"expr\":\"variable\",\"name\":\"记录一\"}}"),
                "random"=>JsonNode.Parse("{\"expr\":\"random\",\"min\":1,\"max\":3}"),
                "memory"=>JsonNode.Parse("{\"expr\":\"memory\",\"scope\":\"unit\",\"key\":\"记录一\"}"),
                _=>JsonNode.Parse("{\"expr\":\""+next+"\",\"items\":[[],[]]}")
            };
            Structural(()=>Document.Set(path,seed));
        });
        if(expression is not null)
        {
            var box=Card(parent);
            foreach(var (key,v) in expression.Where(p=>p.Key!="expr"))
                if(key=="items"&&v is JsonArray a)
                {
                    for(var i=0;i<a.Count;i++)FriendlyNumber(box,path+"/items/"+i,a[i],"第 "+(i+1)+" 个值",depth+1);
                    box.AddChild(UiStyles.Button("＋ 再加一个值",()=>Structural(()=>Document.Append(path+"/items",JsonValue.Create(0)))));
                }
                else FriendlyValue(box,path+"/"+key,v,key=="query"?"query":key=="value"?"value":P.ParameterType(expression,key),P.Field(key),depth+1,key);
            return;
        }
        var row=new HBoxContainer();parent.AddChild(row);row.AddChild(FieldLabel(label));
        if(value is JsonValue number&&number.GetValueKind()==JsonValueKind.Number)
        {
            var spin=new SpinBox{Value=number.GetValue<double>(),MinValue=-1e9,MaxValue=1e9,AllowGreater=true,AllowLesser=true,Step=1,CustomMinimumSize=new(150,38)};row.AddChild(spin);FriendlyControls[path]=spin;
            spin.ValueChanged+=n=>Document.Set(path,JsonValue.Create(n));
            if(!showMode)
            {var calc=UiStyles.Button("改用计算值",()=>{_expanded.Add(path+"#calculation");RenderFriendly();});row.AddChild(calc);FriendlyControls[path+"#calculation"]=calc;}
        }
        else
        {
            var text=new LineEdit{Text=value?.ToString()??"",SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(text);FriendlyControls[path]=text;text.TextChanged+=s=>Document.Set(path,JsonValue.Create(s));
        }
    }
    private void FriendlyQuery(VBoxContainer parent,string path,JsonNode? value,string label,int depth)
    {
        var obj=value as JsonObject;
        var effectPath="/effects/"+_selectedEffect;
        for(var ancestor=path;ancestor.Length>0;ancestor=ancestor[..ancestor.LastIndexOf('/')])
            if(Document.At(ancestor) is JsonObject scope&&scope.ContainsKey("trigger")&&!scope.ContainsKey("op")){effectPath=ancestor;break;}
        var effect=Document.At(effectPath) as JsonObject;
        var refs=(effect?["targets"] as JsonArray??new()).OfType<JsonObject>().Where(t=>t["id"] is not null).Select((t,i)=>(Id:t["id"]!.ToString(),Name:"玩家选的目标 "+(i+1))).ToList();
        var mode=obj is not null?(obj["sel"]?.ToString()=="self"?"self":"query"):value?.ToString()=="self"?"self":"ref:"+value;
        var ids=new[]{"self","query","pick"}.Concat(refs.Select(r=>"ref:"+r.Id)).ToArray();
        Choose(parent,path+"#target",label,mode,ids,id=>id switch{"self"=>"本单位 / 效果来源","query"=>"按条件找到一组对象","pick"=>"让玩家先选择目标",_=>refs.FirstOrDefault(r=>"ref:"+r.Id==id).Name??"已记录的对象"},next=>
        {
            if(next=="pick"&&effect is not null)
            {
                var copy=effect.DeepClone().AsObject();copy["targets"]??=new JsonArray();var n=1;
                while(refs.Any(r=>r.Id=="chosen"+n))n++;
                var id="chosen"+n;copy["targets"]!.AsArray().Add(new JsonObject{["id"]=id,["kind"]="unit",["side"]="enemy",["prompt"]="请选择一个目标"});
                var relative=path[(effectPath.Length+1)..].Split('/');JsonNode node=copy;foreach(var k in relative[..^1])node=node is JsonArray a?a[int.Parse(k)]!:node[k]!;
                if(node is JsonArray array)array[int.Parse(relative[^1])]=id;else node[relative[^1]]=id;
                Structural(()=>Document.Set(effectPath,copy));return;
            }
            Structural(()=>Document.Set(path,next.StartsWith("ref:")?JsonValue.Create(next[4..]):JsonNode.Parse(next=="self"?"{\"sel\":\"self\"}":"{\"sel\":\"all\",\"side\":\"friendly\",\"zone\":\"field\"}")));
        });
        if(obj is null||mode=="self")return;
        var box=Card(parent);
        foreach(var key in new[]{"sel","side","zone","ref","count","where","filter","queries","includeAnchor"})
            if(obj.ContainsKey(key))FriendlyValue(box,path+"/"+key,obj[key],key=="where"?"predicate":key=="count"?"value":key=="filter"?"filter":"data",P.Field(key),depth+1,key);
        AddOptionalFields(box,path,obj,["where","filter","count","ref","queries"],depth);
    }
    private void FriendlyCondition(VBoxContainer parent,string path,JsonNode? value,string label,int depth,bool outer=false)
    {
        if(depth>14){parent.AddChild(Note("深层条件已保留，可通过高级工具继续编辑。"));return;}
        if(outer&&value is JsonObject wrapper&&wrapper["op"]?.ToString()=="predicate")
        {FriendlyCondition(parent,path+"/test",wrapper["test"],label,depth+1);return;}
        var obj=value as JsonObject??new JsonObject();var kind=obj["test"]?.ToString()??(obj["op"] is not null?"legacy":"all");
        Choose(parent,path+"#condition",label,kind,["all","any","not","compare","exists","legacy"],id=>id switch{"all"=>"下面的条件全部满足","any"=>"下面任意一个条件满足","not"=>"下面的条件不满足","compare"=>"比较两个值","exists"=>"某个值或对象存在",_=>"其他判断方式"},id=>
            Structural(()=>Document.Set(path,WrapCondition(JsonNode.Parse(id switch{"all" or "any"=>"{\"test\":\""+id+"\",\"items\":[]}","not"=>"{\"test\":\"not\",\"item\":{\"test\":\"all\",\"items\":[]}}","legacy"=>"{\"op\":\"and\",\"items\":[]}",_=>"{\"test\":\""+id+"\",\"left\":{\"expr\":\"read\",\"of\":\""+(path.Contains("/where",StringComparison.Ordinal)||path.Contains("/filter",StringComparison.Ordinal)?"candidate":"self")+"\",\"path\":\"attack\"},\"cmp\":\">=\",\"right\":1}"}),outer))));
        if(kind is "all" or "any")
        {
            var items=obj["items"] as JsonArray??new();
            if(items.Count==0)parent.AddChild(Note(kind=="all"?"未添加条件时，不限制生效。":"至少添加一个条件，才能满足这项判断。"));
            for(var i=0;i<items.Count;i++)
            {
                var p=path+"/items/"+i;var box=Card(parent);FriendlyCondition(box,p,items[i],"条件 "+(i+1),depth+1);box.AddChild(UiStyles.Button("移除这个条件",()=>Structural(()=>Document.Remove(p))));
            }
            parent.AddChild(UiStyles.Button("＋ 添加一个条件",()=>Structural(()=>
            {var copy=obj.DeepClone().AsObject();copy["items"]??=new JsonArray();copy["items"]!.AsArray().Add(JsonNode.Parse("{\"test\":\"compare\",\"left\":1,\"cmp\":\">=\",\"right\":1}"));Document.Set(path,copy);})));return;
        }
        if(kind=="not"){FriendlyCondition(Card(parent),path+"/item",obj["item"],"不满足的条件",depth+1);return;}
        if(kind=="legacy")
        {
            var id=obj["op"]?.ToString()??"and";
            Choose(parent,path+"/op","判断方式",id,InspectorVocabulary.Conditions,P.Condition,next=>Structural(()=>Document.Set(path,new JsonObject{["op"]=next})));
            if(InspectorVocabulary.Conditions.Contains(id))
                foreach(var f in P.Parameters(id,"conditions"))
                {
                    var key=f.GetProperty("name").GetString()!;
                    if(obj.ContainsKey(key))FriendlyValue(parent,path+"/"+key,obj[key],f.GetProperty("type").GetString()!,P.Field(key),depth+1,key);
                    else parent.AddChild(UiStyles.Button("填写"+P.Field(key),()=>Structural(()=>Document.Set(path+"/"+key,JsonNode.Parse(f.GetProperty("initial").GetRawText())))));
                }
            return;
        }
        FriendlyNumber(parent,path+"/left",obj["left"],"左边的值",depth+1);
        if(kind=="compare")
        {
            Choose(parent,path+"/cmp","比较方式",obj["cmp"]?.ToString()??"==",InspectorVocabulary.Choices("cmp"),P.Value,v=>Document.Set(path+"/cmp",JsonValue.Create(v)));
            FriendlyNumber(parent,path+"/right",obj["right"],"右边的值",depth+1);
        }
    }
    private static JsonNode? WrapCondition(JsonNode? value,bool outer)=>outer&&value is JsonObject o&&!o.ContainsKey("op")?new JsonObject{["op"]="predicate",["test"]=o}:value;
    private void AddOptionalFields(VBoxContainer box,string path,JsonObject obj,IEnumerable<string> keys,int depth)
    {
        Fold(box,path+"#options","增加筛选或细节",parent=>
        {
            foreach(var key in keys.Where(k=>!obj.ContainsKey(k)))
                parent.AddChild(UiStyles.Button("＋ "+P.Field(key),()=>Structural(()=>Document.Set(path+"/"+key,key switch{"where"=>JsonNode.Parse("{\"test\":\"all\",\"items\":[]}"),"filter"=>new JsonObject(),"queries" or "cardIds"=>new JsonArray(),_=>InspectorVocabulary.Initial(key)}))));
        });
    }
    private void BuildActionPicker()
    {
        _actionPicker=new AcceptDialog{Title="选择要做的事",Exclusive=true,MinSize=new(650,500)};AddChild(_actionPicker);
        _actionPicker.GetOkButton().Visible=false;
        var box=new VBoxContainer{CustomMinimumSize=new(610,0)};_actionPicker.AddChild(box);
        var row=new HBoxContainer();box.AddChild(row);
        PickerSearch=new LineEdit{PlaceholderText="搜索：抽牌、伤害、重复、变量……",SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(PickerSearch);
        _pickerGroup=new OptionButton{FitToLongestItem=false,CustomMinimumSize=new(160,0)};row.AddChild(_pickerGroup);
        _pickerGroup.AddItem("所有动作");foreach(var group in InspectorVocabulary.Operations.Select(P.Group).Distinct().Order())_pickerGroup.AddItem(group);
        PickerItems=new ItemList{AutoHeight=false,CustomMinimumSize=new(0,270),SizeFlagsVertical=SizeFlags.ExpandFill,AllowReselect=true};box.AddChild(PickerItems);
        _pickerExplanation=Note("");_pickerExplanation.CustomMinimumSize=new(600,52);box.AddChild(_pickerExplanation);
        PickerAddButton=UiStyles.Button("添加这个动作",ConfirmAction);box.AddChild(PickerAddButton);
        PickerSearch.TextChanged+=_=>FilterFriendlyPicker();_pickerGroup.ItemSelected+=_=>FilterFriendlyPicker();
        PickerItems.ItemSelected+=i=>_pickerExplanation.Text="需要设置："+string.Join("、",P.Parameters(_pickerOps[(int)i]).Where(f=>!f.TryGetProperty("optional",out var optional)||!optional.GetBoolean()).Select(f=>P.Field(f.GetProperty("name").GetString()!)));
        PickerItems.ItemActivated+=_=>ConfirmAction();
    }
    private void OpenActionPicker(string path)
    {
        _addActionsPath=path;PickerSearch.Text="";_pickerGroup.Select(0);FilterFriendlyPicker();
        _actionPicker.PopupCentered(new Vector2I(Math.Min(760,(int)GetViewportRect().Size.X-80),Math.Min(590,(int)GetViewportRect().Size.Y-80)));PickerSearch.GrabFocus();
    }
    private void FilterFriendlyPicker()
    {
        PickerItems.Clear();_pickerOps.Clear();
        var category=_pickerGroup.GetItemText(_pickerGroup.Selected);
        foreach(var op in InspectorVocabulary.Operations.OrderBy(op=>Array.IndexOf(new[]{"卡牌","单位与总部","流程","记录","资源与环境"},P.Group(op))).ThenBy(P.Operation))
        {
            if(_pickerGroup.Selected>0&&P.Group(op)!=category)continue;
            if(!(P.Operation(op)+P.Group(op)+op).Contains(PickerSearch.Text,StringComparison.OrdinalIgnoreCase))continue;
            _pickerOps.Add(op);PickerItems.AddItem(P.Operation(op)+"    ·    "+P.Group(op));
        }
        PickerAddButton.Disabled=_pickerOps.Count==0;
        _pickerExplanation.Text=_pickerOps.Count==0?"没有找到。试试动作、对象或资源的中文名称。":$"可选择 {_pickerOps.Count} 种动作。添加后可以自由修改参数和嵌套组合。";
        if(_pickerOps.Count>0)PickerItems.Select(0);
    }
    private void ConfirmAction()
    {
        var selected=PickerItems.GetSelectedItems();if(selected.Length==0)return;
        var op=_pickerOps[selected[0]];var action=InspectorVocabulary.Action(op);
        // New legacy operations have optional-only schemas. Seed their declared controls,
        // without modifying any already-loaded declaration or limiting the available catalog.
        if(action.Count==1)
            foreach(var field in P.Parameters(op).OrderBy(f=>f.GetProperty("type").GetString() is "selector" or "query" or "legacySide" or "side"?0:f.GetProperty("type").GetString()=="value"?1:2).Take(4))
                action[field.GetProperty("name").GetString()!]=JsonNode.Parse(field.GetProperty("initial").GetRawText());
        Structural(()=>
        {
            var existing=Document.At(_addActionsPath) as JsonArray;
            if(existing is null)Document.Set(_addActionsPath,new JsonArray(action));else Document.Append(_addActionsPath,action);
        });
        _actionPicker.Hide();
    }
    internal void EnsureFriendlyVisible(Control control){if(_friendlyScroll.IsAncestorOf(control))_friendlyScroll.EnsureControlVisible(control);}
    internal void PickFriendlyAction(string op)
    {
        var index=_pickerOps.IndexOf(op);if(index<0)throw new InvalidOperationException("动作未出现在完整选择器中");PickerItems.Select(index);
    }
}
