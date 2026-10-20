using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using P=Kards.Ui.OrcBridge.InspectorPresentation;

namespace Kards.Ui;

internal partial class NativeEffectInspector
{
    internal InspectorComposer Composer=null!;
    private void BuildComposer()
    {
        Composer=new InspectorComposer{Visible=false};AddChild(Composer);Composer.Initialize();
        Composer.Applied+=()=>{Refresh();_status.Text="已应用组合设置，可撤销。实战能力请查看编译结果。";};
    }
    private void Compose(string path,InspectorPart part,string title,bool wrapped=false,string schema="")
    {
        if(!CommitPendingRaw())return;
        Composer.Catalog=Catalog;Composer.Begin(Document,path,part,title,wrapped,schema);
    }
    private void CompositionSlot(VBoxContainer parent,string path,JsonNode? value,InspectorPart part,string label,int depth,bool wrapped=false,string schema="")
    {
        var box=new VBoxContainer();parent.AddChild(box);box.AddThemeConstantOverride("separation",4);
        box.AddChild(MenuStyles.Label(label,14,MenuStyles.Dim));
        var summary=part==InspectorPart.Trigger&&value is JsonValue?P.Timing(value.ToString()):InspectorComposition.Describe(value);
        var edit=MenuStyles.Button(summary+"   ›",()=>Compose(path,part,label,wrapped,schema));
        edit.Alignment=HorizontalAlignment.Left;edit.AutowrapMode=TextServer.AutowrapMode.WordSmart;edit.SizeFlagsHorizontal=SizeFlags.ExpandFill;
        edit.CustomMinimumSize=new(0,42);box.AddChild(edit);FriendlyControls[path+"#compose"]=edit;
        var inner=wrapped&&value is JsonObject wrapper&&wrapper["op"]?.ToString()=="predicate"?wrapper["test"]:value;
        if(part==InspectorPart.Condition&&inner is JsonObject&&InspectorComposition.Mode(inner,part)=="retained")
            Fold(box,path+"#compatibility","已有条件的兼容参数",compat=>FriendlyCondition(compat,path,value,label,depth,wrapped));
    }
}

/// <summary>One level of a typed composition at a time; edits remain private until Apply.</summary>
internal partial class InspectorComposer : Window
{
    private InspectorComposition? _session;
    private readonly List<(string Path,InspectorPart Part,string Label,string Schema)> _pages=[];
    private VBoxContainer _body=null!;
    private ScrollContainer _scroll=null!;
    private Label _trail=null!,_preview=null!,_issue=null!;
    private Button _back=null!;
    private Button _apply=null!,_cancel=null!;
    private readonly Dictionary<string,string> _literalTypes=[];
    internal readonly Dictionary<string,Control> Controls=[];
    internal Button BackButton=>_back;
    internal Button GetOkButton()=>_apply;
    internal Button GetCancelButton()=>_cancel;
    internal event Action? Applied;
    internal InspectorComposition Session=>_session!;
    internal Func<IReadOnlyList<UiCardDefinition>>? Catalog;
    internal void Initialize()
    {
        Title="组合设置";Exclusive=true;Transient=true;Unresizable=true;Visible=false;MinSize=new(540,430);WrapControls=false;
        var panel=new PanelContainer();AddChild(panel);panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.AddThemeStyleboxOverride("panel",MenuStyles.Box(MenuStyles.Panel,MenuStyles.Line,2,16));
        var layout=new VBoxContainer();panel.AddChild(layout);layout.AddThemeConstantOverride("separation",12);
        var nav=new HBoxContainer();layout.AddChild(nav);
        _back=MenuStyles.Button("‹ 返回上一级",()=>{_pages.RemoveAt(_pages.Count-1);Render();});nav.AddChild(_back);
        _trail=MenuStyles.Label("",16,MenuStyles.Gold);_trail.AutowrapMode=TextServer.AutowrapMode.WordSmart;_trail.MaxLinesVisible=2;_trail.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;nav.AddChild(_trail);
        var scroll=new ScrollContainer{CustomMinimumSize=new(0,240),SizeFlagsVertical=Control.SizeFlags.ExpandFill,HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,VerticalScrollMode=ScrollContainer.ScrollMode.Auto};_scroll=scroll;layout.AddChild(scroll);
        _body=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};_body.AddThemeConstantOverride("separation",12);scroll.AddChild(_body);
        layout.AddChild(MenuStyles.Label("组合预览",13,MenuStyles.Dim));
        _preview=MenuStyles.Label("",15,MenuStyles.Ink);_preview.AutowrapMode=TextServer.AutowrapMode.WordSmart;_preview.MaxLinesVisible=2;layout.AddChild(_preview);
        _issue=MenuStyles.Label("",13,MenuStyles.Gold);_issue.AutowrapMode=TextServer.AutowrapMode.WordSmart;_issue.MaxLinesVisible=2;layout.AddChild(_issue);
        var buttons=new HBoxContainer{Alignment=BoxContainer.AlignmentMode.End};layout.AddChild(buttons);
        _cancel=MenuStyles.Button("取消",CancelEdit);buttons.AddChild(_cancel);
        _apply=MenuStyles.Button("应用修改",()=>{if(_session is not null&&_session.Apply(out var error)){Hide();Applied?.Invoke();}else UpdatePreview();});buttons.AddChild(_apply);MenuStyles.Primary(_apply);
        CloseRequested+=CancelEdit;
        WindowInput+=input=>{if(input is InputEventKey {Pressed:true,Keycode:Key.Escape}){CancelEdit();SetInputAsHandled();}};
    }
    private void CancelEdit(){Hide();_session=null;_pages.Clear();}
    internal void Begin(InspectorDocument source,string path,InspectorPart part,string title,bool wrapped=false,string schema="")
    {
        _session=new(source,path,part,wrapped,schema);_session.Draft.Changed+=UpdatePreview;
        var available=GetParent<Control>().GetViewportRect().Size;
        var size=new Vector2I(Math.Min(820,(int)available.X-70),Math.Min(660,(int)available.Y-70));MaxSize=size;Size=size;
        _pages.Clear();_literalTypes.Clear();_pages.Add(("/value",part,title,schema));Title=title;Render();
        PopupCentered(size);
    }
    private void UpdatePreview()
    {
        if(_session is null)return;
        var current=_session.Draft.At("/value");
        _preview.Text=_session.Part==InspectorPart.Trigger&&current is JsonValue?P.Timing(current.ToString()):InspectorComposition.Describe(current);
        _preview.TooltipText=_preview.Text;
        var issues=InspectorComposition.Issues(_session.Draft.At("/value"),_session.Part,_session.Schema);
        _issue.Text=_session.IsStale?"原效果已经变化，请取消后重新打开。":issues.Count>0?issues[0]:"填写完整。应用后仍由规则编译检查实际支持范围。";
        _issue.TooltipText=_issue.Text;
        GetOkButton().Disabled=_session.IsStale||issues.Count>0;
    }
    private void Change(Action edit){edit();Callable.From(Render).CallDeferred();}
    private void Render()
    {
        if(_session is null||_pages.Count==0)return;
        foreach(var child in _body.GetChildren()){_body.RemoveChild(child);child.QueueFree();}Controls.Clear();
        var (path,part,label,schema)=_pages[^1];var value=_session.Draft.At(path);
        _trail.Text=string.Join(" › ",_pages.Select(p=>p.Label));_back.Disabled=_pages.Count==1;
        switch(part)
        {
            case InspectorPart.Condition:Condition(path,value);break;
            case InspectorPart.Value:Value(path,value);break;
            case InspectorPart.Query:Query(path,value);break;
            case InspectorPart.Trigger:Trigger(path,value);break;
            case InspectorPart.NativeEffect:NativeEffect(path,value);break;
            case InspectorPart.NativeOperation:Record(path,value,"native-op");break;
            case InspectorPart.LegacyEffect:Record(path,value,"legacy-effect");break;
            case InspectorPart.Filter:Record(path,value,schema.Length>0?schema:"filter");break;
            case InspectorPart.List:List(path,value,schema);break;
            case InspectorPart.Record:Record(path,value,schema);break;
        }
        UpdatePreview();
        Callable.From(()=>_scroll.ScrollVertical=0).CallDeferred();
    }
    private void Hint(string text)=>_body.AddChild(Wrapped(text));
    private static Label Wrapped(string text){var label=MenuStyles.Label(text,14,MenuStyles.Dim);label.AutowrapMode=TextServer.AutowrapMode.WordSmart;return label;}
    private void Pick(string id,string label,string? current,IReadOnlyList<string> values,Func<string,string> name,Action<string> changed)
    {
        _body.AddChild(MenuStyles.Label(label,14,MenuStyles.Dim));
        var pick=new OptionButton{FitToLongestItem=false,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,CustomMinimumSize=new(0,40)};_body.AddChild(pick);
        var ids=values.Distinct().ToList();if(current is not null&&!ids.Contains(current))ids.Insert(0,current);
        pick.AddItem("请选择…");pick.SetItemMetadata(0,"");
        foreach(var v in ids){pick.AddItem(name(v));pick.SetItemMetadata(pick.ItemCount-1,v);}
        pick.Select(current is not null?ids.IndexOf(current)+1:0);
        pick.ItemSelected+=i=>{if(i>0)changed(ids[(int)i-1]);};Controls[id]=pick;
    }
    private void Slot(string path,InspectorPart part,string label,string schema="")
    {
        _body.AddChild(MenuStyles.Label(label,14,MenuStyles.Dim));
        var edit=MenuStyles.Button(InspectorComposition.Describe(_session!.Draft.At(path))+"   ›",()=>{_pages.Add((path,part,label,schema));Render();});
        edit.Alignment=HorizontalAlignment.Left;edit.AutowrapMode=TextServer.AutowrapMode.WordSmart;edit.CustomMinimumSize=new(0,44);edit.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        _body.AddChild(edit);Controls[path+"#open"]=edit;
    }
    private void Condition(string path,JsonNode? value)
    {
        var mode=InspectorComposition.Mode(value,InspectorPart.Condition);
        if(mode=="predicate-wrapper"){Slot(path+"/test",InspectorPart.Condition,"组合判断");Extensions(value!.AsObject(),["op","test"]);return;}
        Pick(path+"#mode","如何判断",mode=="unset"?null:mode,["compare","exists","all","any","not","library"],id=>id switch
        {"compare"=>"比较两个值","exists"=>"对象或值是否存在","all"=>"以下条件全部满足","any"=>"以下条件任一满足","not"=>"以下条件不满足","library"=>"兼容已有条件库",_=>"已有扩展条件（保留）"},id=>Change(()=>_session!.Switch(path,InspectorPart.Condition,id)));
        if(value is not JsonObject o){Hint("先选择判断方式，再填写相关的对象或数值。");return;}
        switch(mode)
        {
            case "library":Record(path,value,"condition-library");return;
            case "compare":
                Slot(path+"/left",InspectorPart.Value,"要判断的值");
                Pick(path+"/cmp","比较关系",o["cmp"]?.ToString(),InspectorComposition.Comparisons(o["left"]),P.Value,id=>_session!.Draft.Set(path+"/cmp",JsonValue.Create(id)));
                Slot(path+"/right",InspectorPart.Value,"与什么比较");break;
            case "exists":Slot(path+"/left",InspectorPart.Value,"要检查的对象或值");break;
            case "not":Slot(path+"/item",InspectorPart.Condition,"不满足的条件");break;
            case "all":case "any":
                if(o["items"] is not JsonArray items){Hint("已有非标准条件列表已保留，请在高级工具检查。");break;}
                for(var i=0;i<items.Count;i++)
                {
                    var p=path+"/items/"+i;Slot(p,InspectorPart.Condition,"条件 "+(i+1));
                    _body.AddChild(MenuStyles.Button("移除条件 "+(i+1),()=>Change(()=>{_literalTypes.Clear();_session!.RemoveItem(p);})));
                }
                var add=MenuStyles.Button("＋ 添加条件",()=>Change(()=>_session!.AppendItem(path+"/items")));_body.AddChild(add);Controls[path+"#add"]=add;break;
            default:Hint("已有条件原样保留。可在高级工具编辑；选择其他判断方式后，应用前可以切回恢复。");break;
        }
        Extensions(o,mode switch{"compare"=>["test","left","cmp","right"],"exists"=>["test","left"],"not"=>["test","item"],"all" or "any"=>["test","items"],_=>[]});
    }
    private void Value(string path,JsonNode? value)
    {
        var mode=InspectorComposition.Mode(value,InspectorPart.Value);
        if(value is JsonObject literal&&literal["expr"]?.ToString()=="literal"){Slot(path+"/value",InspectorPart.Value,"填写的值");Extensions(literal,["expr","value"]);return;}
        Pick(path+"#mode","这个值从哪里来",mode=="unset"?null:mode,["literal","read","variable","count","math","size","random","memory","query","union","intersection"],id=>id switch
        {"literal"=>"直接填写","read"=>"对象的属性","count"=>"符合条件的对象数量","math"=>"根据其他值计算","memory"=>"已记录的跨回合记忆",_=>P.Value(id)+"（已有设置）"},id=>Change(()=>
        {
            if(id=="literal")_literalTypes[path]="number";
            _session!.Switch(path,InspectorPart.Value,id);
        }));
        if(mode=="literal"||mode=="unset"&&_literalTypes.ContainsKey(path)){Literal(path,value);return;}
        if(value is not JsonObject o){Hint("可填一个固定值，也可继续选择对象属性、数量或计算。");return;}
        switch(mode)
        {
            case "read":
                var refs=_session!.References(path);
                Pick(path+"/of","读取谁的属性",o["of"]?.ToString(),refs.Keys.ToArray(),id=>refs.GetValueOrDefault(id)??P.Value(id),id=>Change(()=>_session.Draft.Set(path+"/of",JsonValue.Create(id))));
                var properties=o["of"]?.ToString() is "friendlyPlayer" or "enemyPlayer"?new[]{"kredits","maxKredits","hq","hqMax"}:InspectorVocabulary.Choices("path").Except(new[]{"kredits","maxKredits","hq","hqMax"}).ToArray();
                Pick(path+"/path","读取哪个属性",o["path"]?.ToString(),properties,P.Value,id=>_session.Draft.Set(path+"/path",JsonValue.Create(id)));
                Hint("只列出当前事件和前序步骤可引用的对象；原有其他引用会保留。");break;
            case "variable":
                var named=_session!.References(path).Where(r=>r.Value.StartsWith("前序记录",StringComparison.Ordinal)||r.Value.StartsWith("玩家选择",StringComparison.Ordinal)||r.Value.StartsWith("当前遍历",StringComparison.Ordinal)).ToDictionary();
                Pick(path+"/name","使用哪个记录",o["name"]?.ToString(),named.Keys.ToArray(),id=>named.GetValueOrDefault(id)??"已有记录「"+id+"」",id=>Set(path+"/name",JsonValue.Create(id)));
                if(named.Count==0)Hint("先在前面的步骤设置“记录为”，或声明玩家选择的目标。");break;
            case "size":Slot(path+"/value",InspectorPart.Value,"统计哪个列表");break;
            case "random":Slot(path+"/min",InspectorPart.Value,"最小值");Slot(path+"/max",InspectorPart.Value,"最大值");break;
            case "query":Slot(path+"/query",InspectorPart.Query,"取得哪些对象");break;
            case "union":case "intersection":Slot(path+"/items",InspectorPart.List,mode=="union"?"合并哪些集合":"哪些集合的共有对象","values");break;
            case "count":Slot(path+"/query",InspectorPart.Query,"统计哪些对象");Hint("统计符合筛选的对象；不要求玩家选择目标。");break;
            case "math":
                Pick(path+"/operator","怎么计算",o["operator"]?.ToString(),["add","subtract","multiply","divide","min","max","floor","ceil"],id=>id=="add"?"相加":P.Value(id),id=>_session!.Draft.Set(path+"/operator",JsonValue.Create(id)));
                if(o["items"] is not JsonArray terms){Hint("已有非标准计算项已保留，请在高级工具检查。");break;}
                for(var i=0;i<terms.Count;i++)
                {
                    var p=path+"/items/"+i;Slot(p,InspectorPart.Value,"计算项 "+(i+1));
                    _body.AddChild(MenuStyles.Button("移除计算项 "+(i+1),()=>Change(()=>{_literalTypes.Clear();_session!.RemoveItem(p);})));
                }
                _body.AddChild(MenuStyles.Button("＋ 添加计算项",()=>Change(()=>_session!.AppendItem(path+"/items"))));break;
            case "memory":
                Pick(path+"/scope","记忆的范围",o["scope"]?.ToString(),["unit","player","match"],id=>id=="unit"?"效果来源":P.Value(id),id=>_session!.Draft.Set(path+"/scope",JsonValue.Create(id)));
                Text(path+"/key","记忆名称",o["key"]?.ToString()??"");break;
            default:Hint("已有取值方式原样保留，其他取值方式可在高级工具编辑。");break;
        }
        Extensions(o,mode switch{"read"=>["expr","of","path"],"count" or "query"=>["expr","query"],"math"=>["expr","operator","items"],"memory"=>["expr","scope","key"],"variable"=>["expr","name"],"size"=>["expr","value"],"random"=>["expr","min","max"],"union" or "intersection"=>["expr","items"],_=>[]});
    }
    private void Literal(string path,JsonNode? value)
    {
        var type=value?.GetValueKind() switch{JsonValueKind.String=>"text",JsonValueKind.True or JsonValueKind.False=>"boolean",_=>_literalTypes.GetValueOrDefault(path,"number")};
        Pick(path+"#literalType","填写什么",type,["number","text","boolean"],id=>id switch{"number"=>"数字","text"=>"文字或标识","boolean"=>"是／否",_=>id},id=>Change(()=>{_literalTypes[path]=id;_session!.Draft.Set(path,null);}));
        if(type=="number")
        {
            var input=new LineEdit{Text=value?.ToString()??"",PlaceholderText="填写数字，0 也是有效值",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};_body.AddChild(input);Controls[path+"#number"]=input;
            input.TextChanged+=s=>_session!.Draft.Set(path,double.TryParse(s,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var number)&&double.IsFinite(number)?JsonValue.Create(number):null);
        }
        else if(type=="boolean")
            Pick(path+"#boolean","选择",value?.ToString(),["true","false"],id=>id=="true"?"是":"否",id=>_session!.Draft.Set(path,JsonValue.Create(id=="true")));
        else Text(path,"填写内容",value?.ToString()??"");
    }
    private void Text(string path,string label,string value)
    {
        _body.AddChild(MenuStyles.Label(label,14,MenuStyles.Dim));
        var input=new LineEdit{Text=value,PlaceholderText="在这里填写",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};_body.AddChild(input);Controls[path]=input;
        input.TextChanged+=s=>_session!.Draft.Set(path,JsonValue.Create(s));
    }
    private void Query(string path,JsonNode? value)
    {
        var mode=InspectorComposition.Mode(value,InspectorPart.Query);
        var refs=_session!.References(path);
        Pick(path+"#mode","对象来源",mode=="unset"?null:mode,["self","query","reference","recorded","adjacent","union"],id=>id switch{"self"=>"效果来源","query"=>"按范围和条件查找","reference"=>"使用事件对象或前序记录","recorded"=>"从记录中选取","adjacent"=>"指定对象的相邻单位","union"=>"合并多组对象",_=>"已有对象引用"},id=>Change(()=>_session!.Switch(path,InspectorPart.Query,id)));
        if(mode=="reference")
        {Pick(path+"#reference","引用谁",value?.ToString(),refs.Keys.ToArray(),id=>refs.GetValueOrDefault(id)??id,id=>Set(path,JsonValue.Create(id)));return;}
        if(value is not JsonObject o||mode=="self")return;
        if(mode=="union"){Slot(path+"/queries",InspectorPart.List,"合并哪些对象范围","queries");Extensions(o,["sel","queries"]);return;}
        if(mode is "recorded" or "adjacent")
        {
            Pick(path+"/ref","以哪个记录为基础",o["ref"]?.ToString(),refs.Keys.ToArray(),id=>refs.GetValueOrDefault(id)??id,id=>Set(path+"/ref",JsonValue.Create(id)));
            if(mode=="recorded")Pick(path+"/sel","从记录中取得",o["sel"]?.ToString(),["ref","refAll"],P.Value,id=>Set(path+"/sel",JsonValue.Create(id)));
            if(mode=="adjacent")RenderField(path,o,new("includeAnchor","boolean","同时包含该对象"));
            Extensions(o,mode=="adjacent"?["sel","ref","includeAnchor"]:["sel","ref"]);return;
        }
        Pick(path+"/side","属于哪一方",o["side"]?.ToString(),["friendly","enemy","both","any"],P.Value,id=>_session!.Draft.Set(path+"/side",JsonValue.Create(id)));
        Pick(path+"/zone","从哪里查找",o["zone"]?.ToString(),["field","support","frontline","hand","deck","discard","hq","pool"],P.Value,id=>_session!.Draft.Set(path+"/zone",JsonValue.Create(id)));
        Pick(path+"/sel","如何取得结果",o["sel"]?.ToString(),["all","random","first","last"],P.Value,id=>Change(()=>_session!.Draft.Set(path+"/sel",JsonValue.Create(id))));
        if(o["sel"]?.ToString() is "random" or "first" or "last")Slot(path+"/count",InspectorPart.Value,"取几个对象");
        Slot(path+"/where",InspectorPart.Condition,"筛选条件（可选）");
        Slot(path+"/filter",InspectorPart.Filter,"按卡牌信息筛选（可选）","filter");
        if(o.ContainsKey("filter"))_body.AddChild(MenuStyles.Button("取消卡牌信息筛选",()=>Change(()=>_session!.Draft.Remove(path+"/filter"))));
        if(o.ContainsKey("where"))_body.AddChild(MenuStyles.Button("取消筛选，不限制属性",()=>Change(()=>_session!.Draft.Remove(path+"/where"))));
        Hint("筛选内的“正在筛选的对象”表示逐个检查的候选对象。");
        RenderField(path,o,new("excludeSelf","boolean","排除效果来源"));
        Extensions(o,["sel","side","zone","where","filter","count","excludeSelf"]);
    }
    private void Extensions(JsonObject value,IReadOnlyList<string> covered)
    {
        var count=value.Count(p=>!covered.Contains(p.Key));
        if(count>0)Hint($"另有 {count} 项已有设置原样保留；高级工具可查看原文。");
    }
}
