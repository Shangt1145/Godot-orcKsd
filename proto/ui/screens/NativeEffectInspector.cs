using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>Godot-native lossless inspector. The navigation tree and property controls operate on
/// the complete source declaration. No browser, HTML view, listener or UI-side rule execution.</summary>
internal partial class NativeEffectInspector : VBoxContainer
{
    internal InspectorDocument Document {get;private set;}=new(new JsonObject());
    internal Tree Navigation=null!;
    internal OptionButton OperationPick=null!;
    internal Button AddButton=null!,UndoButton=null!,RedoButton=null!;
    internal TextEdit RawEditor=null!;
    internal readonly Dictionary<string,Control> FieldControls=[];
    internal Func<IReadOnlyList<UiCardDefinition>>? Catalog;
    internal event Action? Edited;
    internal event Action? RawEdited;
    internal bool HasPendingRaw=>RawEditor.Text!=Document.Root.ToJsonString(Pretty);
    internal bool CommitPendingRaw()=>!HasPendingRaw||TryApplyRaw(RawEditor.Text);
    private VBoxContainer _properties=null!;
    private Label _status=null!,_location=null!;
    private LineEdit _search=null!;
    private TextEdit _engineReport=null!;
    private string _path="/effects";
    private bool _refreshing;
    private readonly Dictionary<string,TreeItem> _items=[];
    private readonly List<string> _opIds=[];
    private static readonly JsonSerializerOptions Pretty=new(){WriteIndented=true};

    internal void Initialize()
    {
        SizeFlagsVertical=SizeFlags.ExpandFill;SizeFlagsHorizontal=SizeFlags.ExpandFill;
        var tools=new HBoxContainer();AddChild(tools);
        UndoButton=UiStyles.Button("撤销",()=>{Document.Undo();Refresh();});tools.AddChild(UndoButton);
        RedoButton=UiStyles.Button("重做",()=>{Document.Redo();Refresh();});tools.AddChild(RedoButton);
        var advancedToggle=UiStyles.Button("高级工具",()=>
        {
            if(!CommitPendingRaw())return;
            _advancedRoot.Visible=!_advancedRoot.Visible;_friendlyRoot.Visible=!_advancedRoot.Visible;
        });tools.AddChild(advancedToggle);
        _advancedRoot=new VBoxContainer{Visible=false,SizeFlagsVertical=SizeFlags.ExpandFill};
        InitializeFriendly();AddChild(_advancedRoot);
        var advancedTools=new HBoxContainer();_advancedRoot.AddChild(advancedTools);
        _search=new LineEdit{PlaceholderText="搜索动作 / 中文名",CustomMinimumSize=new(180,0)};advancedTools.AddChild(_search);
        OperationPick=new OptionButton{FitToLongestItem=false,SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new(200,0)};advancedTools.AddChild(OperationPick);
        _search.TextChanged+=_=>FilterOperations();FilterOperations();
        AddButton=UiStyles.Button("＋ 添加到列表",AddSelected);advancedTools.AddChild(AddButton);
        var examples=new HBoxContainer();_advancedRoot.AddChild(examples);
        var examplePick=new OptionButton{FitToLongestItem=false,CustomMinimumSize=new(260,0)};examples.AddChild(examplePick);
        foreach(var (name,_) in Examples)examplePick.AddItem(name);
        examples.AddChild(UiStyles.Button("＋ 常见效果",()=>Structural(()=>
        {
            var used=Document.At("/effects")!.AsArray().OfType<JsonObject>().SelectMany(e=>(e["targets"] as JsonArray??new()).Select(t=>t?["id"]?.ToString())).Where(id=>id is not null).ToArray();
            var merged=DesktopOrc.Request(new {method="merge",effects=new[]{JsonSerializer.Deserialize<JsonElement>(Examples[examplePick.Selected].Json)},used});
            Document.Append("/effects",JsonNode.Parse(merged[0].GetRawText()));
        })));
        examples.AddChild(UiStyles.Button("我已实测，标记通过",()=>Structural(()=>Document.Set("/effectStatus",JsonValue.Create("manual-confirmed")))));
        examples.AddChild(UiStyles.Button("添加词条参数",()=>{SelectPath("/kwValues");}));
        var tabs=new TabContainer{SizeFlagsVertical=SizeFlags.ExpandFill};_advancedRoot.AddChild(tabs);
        var split=new HSplitContainer{Name="结构编辑",SizeFlagsVertical=SizeFlags.ExpandFill,SplitOffsets=[255]};tabs.AddChild(split);
        Navigation=new Tree{HideRoot=false,CustomMinimumSize=new(210,0),SizeFlagsVertical=SizeFlags.ExpandFill};split.AddChild(Navigation);
        Navigation.ItemSelected+=()=>{if(!_refreshing&&Navigation.GetSelected() is {} item)SelectPath(item.GetMetadata(0).AsString());};
        var right=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};split.AddChild(right);
        _location=UiStyles.Label("",13,UiStyles.Gold);right.AddChild(_location);
        var scroll=new ScrollContainer{SizeFlagsVertical=SizeFlags.ExpandFill,HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};right.AddChild(scroll);
        _properties=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};_properties.AddThemeConstantOverride("separation",8);scroll.AddChild(_properties);
        var raw=new VBoxContainer{Name="完整声明 JSON",SizeFlagsVertical=SizeFlags.ExpandFill};tabs.AddChild(raw);
        raw.AddChild(UiStyles.Label("高级编辑：点击应用后生效。格式错误时保留上一份有效结构。",13,UiStyles.Dim));
        RawEditor=new TextEdit{SizeFlagsVertical=SizeFlags.ExpandFill,WrapMode=TextEdit.LineWrappingMode.Boundary};raw.AddChild(RawEditor);RawEditor.TextChanged+=()=>{if(HasPendingRaw)RawEdited?.Invoke();};
        var rawButtons=new HBoxContainer();raw.AddChild(rawButtons);
        rawButtons.AddChild(UiStyles.Button("应用 JSON",()=>TryApplyRaw(RawEditor.Text)));
        rawButtons.AddChild(UiStyles.Button("复制完整声明",()=>DisplayServer.ClipboardSet(Document.Root.ToJsonString(Pretty))));
        rawButtons.AddChild(UiStyles.Button("从剪贴板载入",()=>{RawEditor.Text=DisplayServer.ClipboardGet();}));
        _engineReport=new TextEdit{Name="规则编译结果",Editable=false,SizeFlagsVertical=SizeFlags.ExpandFill,WrapMode=TextEdit.LineWrappingMode.Boundary};tabs.AddChild(_engineReport);
        var help=new RichTextLabel{Name="使用说明",BbcodeEnabled=true,FitContent=false,SizeFlagsVertical=SizeFlags.ExpandFill};tabs.AddChild(help);
        help.Text="[b]在游戏内编辑完整 OrC 声明[/b]\n\n左侧树选择卡面字段、效果、条件、目标或嵌套动作。右侧修改数值、枚举和布尔值，点击列表或字段组继续深入。\n\n选中 actions / then / else 列表后，搜索并添加完整原版动作目录中的任意动作。选中 effects 添加效果；选中 targets 添加指定目标。对象中的字段可增删、改名和切换数据类型；数组可复制、删除、上下移动。\n\n“表达式 / 条件模板”包含变量引用、计数、算式、随机值、记忆、对象查询以及复合条件。所有模板仍可逐字段修改。trigger 可直接填写 behavior:game/attack:before 等行为时机；下拉包含全部行为、阶段和动作。\n\nkwMap 控制词条开关，kwValues 保存词条参数。未知卡面字段和未知嵌套字段同样保留。\n\n撤销 / 重做保留最近 80 次整份声明。格式错误的 JSON 不会覆盖有效草稿。\n\n结构编辑会实时写回卡牌草稿；使用上方“保存卡牌”落盘，或完成后回卡牌编辑器保存。解析完备性与规则层准入分别显示；规则层尚未支持的声明可保存，但不能冒充可对局卡牌。";
        var tutorial=new RichTextLabel{Name="原版教程",BbcodeEnabled=false,SizeFlagsVertical=SizeFlags.ExpandFill};tabs.AddChild(tutorial);
        tutorial.Text="以下保留原版 12 课的语法与组合示例。入口和按钮位置请以本版本“使用说明”为准；实际规则支持以 OrC-KSD 编译结果为准。\n\n"+DesktopOrc.TutorialText;
        _status=UiStyles.Label("",12,UiStyles.Dim);_status.AutowrapMode=TextServer.AutowrapMode.WordSmart;AddChild(_status);
    }
    internal void SetEngineReport(string text){_engineReport.Text=text;}
    internal bool AdvancedVisible=>_advancedRoot.Visible;
    internal void ShowAdvanced(){_friendlyRoot.Visible=false;_advancedRoot.Visible=true;}
    internal void Open(JsonObject card)
    {
        _expanded.Clear();_selectedEffect=0;_renderedEffect=-1;_advancedRoot.Visible=false;_friendlyRoot.Visible=true;
        Document=new InspectorDocument(card);_path="/effects";
        Document.Changed+=()=>{RefreshTree();RawEditor.Text=Document.Root.ToJsonString(Pretty);SyncHistory();UpdateFriendlySummary();Edited?.Invoke();};
        Refresh();
    }
    internal bool TryApplyRaw(string json)
    {
        try
        {
            var value=JsonNode.Parse(json) as JsonObject??throw new JsonException("卡牌必须是对象");
            if(value["effects"] is not JsonArray)throw new JsonException("effects 必须是数组");
            if(value["effects"]!.AsArray().Any(e=>e is not JsonObject))throw new JsonException("每条效果必须是对象");
            Document.Set("",value);Refresh();_status.Text="已应用完整声明。";return true;
        }
        catch(Exception e) when(e is JsonException or InvalidDataException){_status.Text="未应用，已保留有效草稿："+e.Message;return false;}
    }
    internal void RefreshSourceCheck()
    {
        var effects=Document.Root["effects"];
        if(effects is not JsonArray)return;
        try
        {
            var report=DesktopOrc.Request(new {method="validate",value=JsonSerializer.SerializeToElement(effects)});
            var issues=report.GetProperty("errors").EnumerateArray().Concat(report.GetProperty("warnings").EnumerateArray()).Select(e=>e.GetProperty("path").GetString()+"："+e.GetProperty("msg").GetString()).ToArray();
            _status.Text=issues.Length==0?"设置已填写完整。可以继续添加动作，或保存卡牌。":$"还有 {issues.Length} 处设置需要检查："+string.Join("；",issues.Take(2).Select(x=>x[(x.IndexOf('：')+1)..]));
        }
        catch(InvalidDataException e){_status.Text=e.Message;}
    }
    private void SyncHistory(){UndoButton.Disabled=!Document.CanUndo;RedoButton.Disabled=!Document.CanRedo;}
    private void Refresh(){RefreshTree();RenderProperties();RenderFriendly();RawEditor.Text=Document.Root.ToJsonString(Pretty);SyncHistory();}
    private static string Summary(JsonNode? n)=>n switch
    {
        JsonObject o when o["trigger"] is {} t=>InspectorVocabulary.Label("trigger",t.ToString()),
        JsonObject o when o["op"] is {} op=>InspectorVocabulary.Label("op",op.ToString())+" · "+op,
        JsonObject o when o["expr"] is {} e=>"表达式 · "+e,
        JsonObject o=>"字段组 · "+o.Count,
        JsonArray a=>"列表 · "+a.Count,
        _=>n?.ToString()??"null"
    };
    private void RefreshTree()
    {
        var collapsed=_items.Where(p=>GodotObject.IsInstanceValid(p.Value)&&p.Value.Collapsed).Select(p=>p.Key).ToHashSet();
        _refreshing=true;Navigation.Clear();_items.Clear();
        void Add(TreeItem? parent,string path,string label,JsonNode? node,int depth)
        {
            var item=Navigation.CreateItem(parent);_items[path]=item;item.SetMetadata(0,path);
            item.SetText(0,label+" · "+Summary(node));item.SetTooltipText(0,path);
            item.Collapsed=collapsed.Contains(path)||(depth>2&&!_path.StartsWith(path+"/",StringComparison.Ordinal));
            if(node is JsonObject o)foreach(var (key,value) in o.OrderBy(p=>path.Length==0?(p.Key=="effects"?0:p.Key is "kwMap" or "kwValues"?1:2):0))Add(item,InspectorDocument.Child(path,key),InspectorVocabulary.Label("field",key),value,depth+1);
            else if(node is JsonArray a)for(var i=0;i<a.Count;i++)Add(item,InspectorDocument.Child(path,i.ToString()),(i+1).ToString(),a[i],depth+1);
        }
        Add(null,"","卡牌",Document.Root,0);
        if(!_items.ContainsKey(_path))_path=_items.ContainsKey("/effects")?"/effects":"";
        _items[_path].Select(0);_refreshing=false;
    }
    internal void SelectPath(string path)
    {
        _path=path;if(_items.TryGetValue(path,out var item)){_refreshing=true;item.Select(0);for(var ancestor=item.GetParent();ancestor is not null;ancestor=ancestor.GetParent())ancestor.Collapsed=false;Navigation.ScrollToItem(item);_refreshing=false;}
        RenderProperties();
    }
    private void FilterOperations()
    {
        OperationPick.Clear();_opIds.Clear();
        foreach(var op in InspectorVocabulary.Operations)
            if((op+InspectorVocabulary.Label("op",op)).Contains(_search.Text,StringComparison.OrdinalIgnoreCase))
            {OperationPick.AddItem(InspectorVocabulary.Label("op",op)+" · "+op);_opIds.Add(op);}
        AddButton?.SetDisabled(_opIds.Count==0);
    }
    private void Structural(Action edit)
    {
        if(HasPendingRaw&&!CommitPendingRaw())return;
        edit();Callable.From(()=>{RenderProperties();RenderFriendly();}).CallDeferred();
    }
    private void AddSelected()
    {
        if(Document.At(_path) is not JsonArray){_status.Text="先在树中选中要添加的列表（例如 actions）。";return;}
        var key=_path.Split('/').Last();
        JsonNode? seed=key switch
        {
            "effects"=>InspectorVocabulary.Effect(),
            "targets"=>JsonNode.Parse("{\"id\":\"t1\",\"kind\":\"unit\",\"side\":\"any\",\"zone\":\"field\"}"),
            "actions" or "then" or "else"=>_opIds.Count>0?InspectorVocabulary.Action(_opIds[Math.Max(0,OperationPick.Selected)]):new JsonObject(),
            "options"=>JsonNode.Parse("{\"label\":\"选项\",\"actions\":[]}"),
            _=>new JsonObject()
        };
        if(key=="targets")
        {
            var used=Document.At(_path)!.AsArray().Select(t=>t?["id"]?.ToString()).ToHashSet();var index=1;
            while(used.Contains("t"+index))index++;seed!["id"]="t"+index;
        }
        Structural(()=>Document.Append(_path,seed));
    }
    private void RenderProperties()
    {
        foreach(var child in _properties.GetChildren()){_properties.RemoveChild(child);child.QueueFree();}FieldControls.Clear();
        var node=Document.At(_path);var path=_path;
        _location.Text=path.Length==0?"卡牌声明":path;
        var nav=new HBoxContainer();_properties.AddChild(nav);
        nav.AddChild(UiStyles.Button("上一级",()=>SelectPath(path.Length==0?"":path[..path.LastIndexOf('/')])));
        if(path.Length>0)
        {
            nav.AddChild(UiStyles.Button("删除",()=>Structural(()=>Document.Remove(path))));
            var parent=Document.At(path[..path.LastIndexOf('/')]);
            if(parent is JsonArray)
            {
                nav.AddChild(UiStyles.Button("复制",()=>Structural(()=>Document.Duplicate(path))));
                nav.AddChild(UiStyles.Button("↑",()=>Structural(()=>Document.Move(path,-1))));
                nav.AddChild(UiStyles.Button("↓",()=>Structural(()=>Document.Move(path,1))));
            }
        }
        if(node is JsonObject obj)
        {
            foreach(var (key,value) in obj.ToArray())AddField(_properties,path,key,value);
            if(path is "/kwMap" or "/kwValues")
            {
                var keywords=new HBoxContainer();_properties.AddChild(keywords);var keywordPick=new OptionButton{FitToLongestItem=false,CustomMinimumSize=new(220,0)};keywords.AddChild(keywordPick);
                var ids=InspectorVocabulary.Choices("keyword");
                foreach(var id in ids)keywordPick.AddItem(InspectorVocabulary.Label("value",id)+" · "+id);
                keywords.AddChild(UiStyles.Button(path=="/kwMap"?"＋ 启用词条":"＋ 词条参值",()=>Structural(()=>Document.Set(InspectorDocument.Child(path,ids[keywordPick.Selected]),path=="/kwMap"?JsonValue.Create(true):JsonValue.Create(1)))));
            }
            var fields=InspectorVocabulary.Fields(obj).Where(k=>!obj.ContainsKey(k)).ToArray();
            if(fields.Length>0)
            {
                var row=new HBoxContainer();_properties.AddChild(row);var pick=new OptionButton{FitToLongestItem=false,SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(pick);
                foreach(var key in fields)pick.AddItem(InspectorVocabulary.Label("field",key)+" · "+key);
                row.AddChild(UiStyles.Button("＋ 参数",()=>Structural(()=>Document.Set(InspectorDocument.Child(path,fields[pick.Selected]),InspectorVocabulary.Initial(obj,fields[pick.Selected])))));
            }
            var add=new HBoxContainer();_properties.AddChild(add);
            var name=new LineEdit{PlaceholderText="新字段名",SizeFlagsHorizontal=SizeFlags.ExpandFill};add.AddChild(name);
            var valueType=TypePicker(null);add.AddChild(valueType);
            add.AddChild(UiStyles.Button("＋ 字段",()=>
            {
                var key=name.Text.Trim();if(key.Length==0||obj.ContainsKey(key)){_status.Text="请填写未使用的字段名。";return;}
                Structural(()=>Document.Set(InspectorDocument.Child(path,key),DefaultType(valueType.Selected)));
            }));
        }
        else if(node is JsonArray array)
        {
            for(var i=0;i<array.Count;i++)AddField(_properties,path,i.ToString(),array[i]);
            _properties.AddChild(UiStyles.Button("＋ 列表项（按当前列表类型）",AddSelected));
        }
        else AddValue(_properties,path,path.Split('/').Last(),node);
        AddTemplates(_properties,path);
        _status.Text="修改会保留在卡牌草稿中；点击右上角保存。";
    }
    private void AddField(VBoxContainer box,string parent,string key,JsonNode? value)
    {
        var path=InspectorDocument.Child(parent,key);var row=new HBoxContainer();box.AddChild(row);
        var label=UiStyles.Label(InspectorVocabulary.Label(parent is "/kwMap" or "/kwValues"?"value":"field",key),13,UiStyles.Dim);label.CustomMinimumSize=new(95,0);label.TooltipText=key;row.AddChild(label);
        if(value is JsonObject or JsonArray)
            row.AddChild(UiStyles.Button(Summary(value)+" →",()=>SelectPath(path)));
        else AddValue(row,path,key,value);
        var types=TypePicker(value);row.AddChild(types);
        types.ItemSelected+=i=>Structural(()=>Document.Set(path,DefaultType((int)i)));
        row.AddChild(UiStyles.Button("×",()=>Structural(()=>Document.Remove(path))));
        if(Document.At(parent) is JsonObject)
        {
            // Rename keys without projecting or rewriting their contents.
            var rename=new LineEdit{Text=key,CustomMinimumSize=new(80,0),TooltipText="字段名；回车改名"};row.AddChild(rename);
            rename.TextSubmitted+=newName=>
            {
                if(newName.Length==0||newName==key||Document.At(parent)!.AsObject().ContainsKey(newName))return;
                var copy=Document.At(parent)!.DeepClone().AsObject();var retained=copy[key];copy.Remove(key);copy[newName]=retained;
                Structural(()=>Document.Set(parent,copy));
            };
        }
    }
    private void AddValue(Container row,string path,string key,JsonNode? value)
    {
        if(value is JsonValue v&&v.TryGetValue<bool>(out var flag))
        {
            var check=new CheckBox{ButtonPressed=flag,Text="启用"};row.AddChild(check);FieldControls[path]=check;
            check.Toggled+=b=>Document.Set(path,JsonValue.Create(b));return;
        }
        if(value is JsonValue number&&number.GetValueKind()==JsonValueKind.Number)
        {
            var spin=new SpinBox{MinValue=-1e12,MaxValue=1e12,Step=0.01,AllowGreater=true,AllowLesser=true,Value=number.GetValue<double>(),CustomMinimumSize=new(130,0)};
            row.AddChild(spin);FieldControls[path]=spin;spin.ValueChanged+=n=>Document.Set(path,JsonValue.Create(n));return;
        }
        var text=new LineEdit{Text=value?.ToString()??"",PlaceholderText=value is null?"null":"",SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new(100,0)};
        row.AddChild(text);FieldControls[path]=text;
        void Apply(){if(Document.At(path)?.ToString()!=text.Text)Document.Set(path,JsonValue.Create(text.Text));}
        text.TextChanged+=_=>Apply();
        var options=InspectorVocabulary.Choices(key);
        var cardReference=key=="cardId"||path.Contains("/cardIds/",StringComparison.Ordinal);
        if(cardReference)options=(Catalog?.Invoke()??[]).Select(c=>c.CardId).ToArray();
        // Conditions use the same `op` key; expose both vocabularies without dropping aliases.
        if(key=="op")options=options.Concat(InspectorVocabulary.Conditions).Distinct().ToArray();
        if(options.Length>0)
        {
            var pick=new OptionButton{FitToLongestItem=false,CustomMinimumSize=new(110,0)};pick.AddItem("选择…");
            foreach(var option in options)pick.AddItem((cardReference?Catalog?.Invoke().FirstOrDefault(c=>c.CardId==option)?.Name:InspectorVocabulary.Label(key=="trigger"?"trigger":key=="op"?"op":"value",option))+" · "+option);
            row.AddChild(pick);pick.ItemSelected+=i=>{if(i>0){text.Text=options[(int)i-1];Apply();if(key is "op" or "trigger")Callable.From(RenderProperties).CallDeferred();}};
        }
    }
    private static OptionButton TypePicker(JsonNode? node)
    {
        var pick=new OptionButton{FitToLongestItem=false,CustomMinimumSize=new(74,0)};foreach(var s in new[]{"文本","数字","布尔","字段组","列表","空值"})pick.AddItem(s);
        pick.Select(node switch{JsonObject=>3,JsonArray=>4,null=>5,JsonValue v when v.GetValueKind()==JsonValueKind.Number=>1,JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False=>2,_=>0});return pick;
    }
    private static JsonNode? DefaultType(int type)=>type switch{1=>JsonValue.Create(0),2=>JsonValue.Create(false),3=>new JsonObject(),4=>new JsonArray(),5=>null,_=>JsonValue.Create("")};
    private static readonly (string Name,string Json)[] Examples=
    [
        ("部署时抽两张牌","{\"trigger\":\"deploy\",\"actions\":[{\"op\":\"loop\",\"times\":2,\"actions\":[{\"op\":\"drawOne\",\"side\":\"self\"}]}]}"),
        ("部署时让自己 +1/+1","{\"trigger\":\"deploy\",\"actions\":[{\"op\":\"changeAttribute\",\"target\":{\"sel\":\"self\"},\"property\":\"attack\",\"mode\":\"add\",\"value\":1,\"duration\":\"permanent\"},{\"op\":\"changeAttribute\",\"target\":{\"sel\":\"self\"},\"property\":\"defense\",\"mode\":\"add\",\"value\":1,\"duration\":\"permanent\"}]}"),
        ("选一个敌方单位，造成 2 点伤害","{\"trigger\":\"order\",\"targets\":[{\"id\":\"chosenEnemy\",\"side\":\"enemy\",\"kind\":\"unit\",\"prompt\":\"选择一个敌方单位\"}],\"actions\":[{\"op\":\"dealDamage\",\"target\":\"chosenEnemy\",\"amount\":2}]}"),
        ("部署时让己方总部恢复 3 点生命","{\"trigger\":\"deploy\",\"actions\":[{\"op\":\"restoreHealth\",\"target\":{\"sel\":\"all\",\"side\":\"friendly\",\"zone\":\"hq\"},\"amount\":3}]}"),
    ];
    private static readonly (string Name,string Json)[] Templates=
    [
        ("新效果","{\"trigger\":\"deploy\",\"targets\":[],\"actions\":[]}"),
        ("变量引用","{\"expr\":\"variable\",\"name\":\"v1\"}"),
        ("读取属性","{\"expr\":\"read\",\"of\":\"candidate\",\"path\":\"attack\"}"),
        ("对象查询","{\"sel\":\"all\",\"side\":\"friendly\",\"zone\":\"field\"}"),
        ("对象计数","{\"expr\":\"count\",\"query\":{\"sel\":\"all\",\"side\":\"friendly\",\"zone\":\"field\"}}"),
        ("列表取值","{\"expr\":\"query\",\"query\":{\"sel\":\"all\",\"side\":\"friendly\",\"zone\":\"field\"}}"),
        ("列表长度","{\"expr\":\"size\",\"value\":{\"expr\":\"variable\",\"name\":\"v1\"}}"),
        ("算式","{\"expr\":\"math\",\"operator\":\"add\",\"items\":[0,1]}"),
        ("合并列表","{\"expr\":\"union\",\"items\":[[],[]]}"),
        ("列表交集","{\"expr\":\"intersection\",\"items\":[[],[]]}"),
        ("随机数","{\"expr\":\"random\",\"min\":1,\"max\":3}"),
        ("读取记忆","{\"expr\":\"memory\",\"scope\":\"unit\",\"key\":\"counter\"}"),
        ("常量属性组","{\"expr\":\"literal\",\"value\":{}}"),
        ("数值条件","{\"op\":\"compare\",\"left\":0,\"cmp\":\">=\",\"value\":1}"),
        ("谓词比较","{\"test\":\"compare\",\"left\":0,\"cmp\":\">=\",\"right\":1}"),
        ("全部满足","{\"test\":\"all\",\"items\":[]}"),
        ("任一满足","{\"test\":\"any\",\"items\":[]}"),
        ("取反","{\"test\":\"not\",\"item\":{\"test\":\"all\",\"items\":[]}}"),
        ("存在","{\"test\":\"exists\",\"left\":{\"expr\":\"variable\",\"name\":\"v1\"}}")
    ];
    private void AddTemplates(VBoxContainer box,string path)
    {
        var row=new HBoxContainer();box.AddChild(row);var pick=new OptionButton{FitToLongestItem=false,SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(pick);
        foreach(var (name,_) in Templates)pick.AddItem(name);
        row.AddChild(UiStyles.Button("用模板替换当前节点",()=>Structural(()=>Document.Set(path,JsonNode.Parse(Templates[pick.Selected].Json)))));
    }
}
