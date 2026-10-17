using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>Native Godot authoring for the complete program document; controls edit data, never rules.</summary>
internal partial class KeywordProgramEditor : VBoxContainer
{
    private JsonObject _root=new();
    private VBoxContainer _body=null!;
    private Label _status=null!;
    private TextEdit _raw=null!;
    internal event Action<string>? Applied;
    internal event Action? Finished;
    internal Func<IReadOnlyList<UiCardDefinition>>? Catalog;
    internal JsonObject Document=>(JsonObject)_root.DeepClone();
    private static readonly Dictionary<string,string> Labels=new()
    {
        ["defaultValue"]="词条参数 X 的默认值",["state"]="记忆与计数",["rules"]="持续规则",["handlers"]="事件行为",["skills"]="主动技能",
        ["bypassRange"]="本次效果攻击忽略攻击距离",["keepHistory"]="返回手牌时保留进场次数",["deploymentEffect"]="算作部署效果（可被部署重放）",["scope"]="规则作用范围",["rule"]="修改哪条规则",["value"]="数值 / 变量",["priority"]="优先级（越大越优先）",["when"]="需要满足的条件",["event"]="何时触发",["subject"]="监听谁",["actions"]="依次执行",["op"]="操作",["target"]="影响对象",["key"]="记录名 / 引用编号",["second"]="第二数值（加防御）",["temporary"]="临时效果",["then"]="条件成立时",["else"]="条件不成立时",["bindings"]="生成卡携带的记录",["left"]="比较左侧",["right"]="比较右侧",["id"]="技能编号",["name"]="技能名称",["cost"]="消耗指挥点",["spendMove"]="消耗移动机会",["spendAttack"]="消耗攻击机会"
    };
    private static readonly Dictionary<string,string> Words=new()
    {
        ["unit.deployed"]="本单位部署完成",["unit.joined"]="单位加入战场",["card.placed"]="每次进入战场",["card.played"]="卡牌被使用",["turn.start"]="回合开始",["turn.end"]="回合结束",["card.damaged"]="受到伤害",["card.dying"]="实际消灭，清理之前",["card.leaving"]="非消灭离场之前",["unit.attack.before"]="攻击结算之前",["unit.attack.after"]="攻击结算之后",["unit.destroy.before"]="即将被消灭，可介入",["keyword.ready"]="本词条就绪",["keyword.consumed"]="本词条被消耗",
        ["move.allowed"]="是否允许移动",["attack.allowed"]="是否允许攻击",["combat.range"]="目标是否在攻击范围",["combat.counter.eligibility"]="对方是否可以反击",["combat.guard.eligibility"]="是否可以攻击被守护者",["combat.interception"]="是否被战斗机拦截",["combat.ambush.condition"]="是否满足伏击条件",["deploy.frontline"]="允许部署前线",["attack.limit"]="每回合攻击次数上限",
        ["self"]="自身",["friendly"]="友方",["enemy"]="敌方",["any"]="双方",["owner"]="己方回合",["opponent"]="敌方回合",["event"]="事件中的单位",["enemy.hq"]="敌方总部",["friendly.hq"]="友方总部",["enemy.units"]="所有敌方单位",["friendly.units"]="所有友方单位",["enemy.frontline"]="前线所有敌军",["friendly.frontline"]="前线所有友军",["random.enemy.frontline"]="前线随机一个敌军",
        ["state.set"]="设置本词条记忆",["state.add"]="增加 / 减少计数",["capture"]="读取并记录单位数值",["move.set"]="设置移动机会",["attack.set"]="设置攻击机会",["attack"]="发动一次真实攻击",["replace"]="转换为指定单位",["retreat"]="撤退到支援线",["damage"]="造成伤害",["buff"]="增加攻击 / 防御",["heal"]="恢复防御",["kill"]="消灭",["keyword.grant"]="授予词条",["keyword.remove"]="移除词条（空编号为自身）",["emit"]="宣布就绪 / 消耗",["death.cancel"]="取消本次消灭",["health.set"]="保留指定防御",["copy.hand"]="复制到手牌并移除本词条",["create.hand"]="将指定卡加入手牌",["move"]="按正常移动规则行动",["draw"]="抽牌",["points"]="增加 / 减少指挥点",["attach.prefab"]="挂载原生效果预制体",["if"]="按条件分支",
        ["linked"]="绑定的单位",["pair"]="自身与绑定单位",["all"]="全部单位",["choose.unit"]="选择一个单位",["choose.enemy.unit"]="选择一个敌军",["choose.friendly.unit"]="选择一个友军",["attack.target"]="本次攻击的敌方目标",["link"]="绑定一个单位",["unlink"]="解除绑定",["return.hand"]="将单位返回手牌",
        ["stat.add"]="增加 / 减少指定属性",["stat.set"]="将指定属性设为数值",["develop"]="从关联卡池开发一张卡",["defense"]="防御力",["opCost"]="行动费用",["deployCost"]="部署费用",["hqHealth"]="总部防御",["unit.acted"]="单位完成一次行动",["turn.start.before"]="回合开始之前",["turn.end.before"]="回合结束之前",
        ["eq"]="等于",["ne"]="不等于",["gt"]="大于",["gte"]="大于或等于",["lt"]="小于",["lte"]="小于或等于"
    };
    internal void Open(string? json)
    {
        var invalid=false;
        if(json is null)_root=JsonNode.Parse(KeywordProgramSchema.DefaultDocument())!.AsObject();
        else if(KeywordProgramSchema.TryNormalize(json,out var document,out _))_root=JsonNode.Parse(document)!.AsObject();
        else{_root=JsonNode.Parse(KeywordProgramSchema.DefaultDocument())!.AsObject();invalid=true;}
        var bar=new HBoxContainer();AddChild(bar);bar.AddChild(UiStyles.Button("返回词条",()=>Finished?.Invoke()));bar.AddChild(UiStyles.Button("校验并应用规则程序",Apply));
        bar.AddChild(UiStyles.Button("高级原始数据",()=>{if(!_raw.Visible)_raw.Text=_root.ToJsonString(new(){WriteIndented=true});_raw.Visible=!_raw.Visible;}));
        _status=UiStyles.Label("0 表示否，1 表示是；数值可以选择词条参数或已声明的计数。",13,UiStyles.Dim);_status.AutowrapMode=TextServer.AutowrapMode.WordSmart;AddChild(_status);
        _raw=new TextEdit{CustomMinimumSize=new(0,240),Visible=invalid,Text=json??"",SizeFlagsVertical=SizeFlags.ExpandFill};AddChild(_raw);
        if(invalid)_status.Text="原始数据格式有误，内容已保留；修正高级数据并校验后才会应用。";
        var scroll=new ScrollContainer{SizeFlagsVertical=SizeFlags.ExpandFill};AddChild(scroll);_body=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};scroll.AddChild(_body);Render();
    }
    private void Render()
    {
        UiStyles.Clear(_body);
        foreach(var key in new[]{"defaultValue","state","rules","handlers","skills"})Field(_body,key,_root[key],v=>_root[key]=v,0);
    }
    private void Apply()
    {
        if(!KeywordProgramSchema.TryNormalize(_raw.Visible?_raw.Text:_root.ToJsonString(),out var document,out var error)){_status.Text="请修正："+error;return;}
        _root=JsonNode.Parse(document)!.AsObject();Applied?.Invoke(document);_status.Text="原生引擎校验通过。返回后保存词条。";Render();
    }
    private static string Label(string key)=>Labels.GetValueOrDefault(key,key);
    private void Field(Node parent,string key,JsonNode? node,Action<JsonNode?> set,int depth,string? operation=null)
    {
        var box=new VBoxContainer();parent.AddChild(box);box.AddThemeConstantOverride("separation",5);
        if(node is JsonArray array)
        {
            if(key=="cards"&&Catalog is not null)
            {
                box.AddChild(UiStyles.Label("关联卡池（可以多选）",16,UiStyles.Gold));var choices=Catalog().ToArray();var list=new ItemList{CustomMinimumSize=new(0,180),SelectMode=ItemList.SelectModeEnum.Multi};box.AddChild(list);
                foreach(var card in choices){list.AddItem(card.Name);if(array.Any(n=>n?.ToString()==card.CardId))list.Select(list.ItemCount-1,false);}
                list.MultiSelected+=(index,selected)=>{var id=choices[(int)index].CardId;if(selected&&!array.Any(n=>n?.ToString()==id))array.Add(id);else if(!selected){var item=array.FirstOrDefault(n=>n?.ToString()==id);if(item is not null)array.Remove(item);}};return;
            }
            var head=new HBoxContainer();box.AddChild(head);head.AddChild(UiStyles.Label(Label(key),16,UiStyles.Gold));
            head.AddChild(UiStyles.Button("＋ 添加",()=>{array.Add(Seed(key));Render();}));
            for(var i=0;i<array.Count;i++)
            {
                var index=i;var panel=new VBoxContainer();box.AddChild(panel);
                var top=new HBoxContainer();panel.AddChild(top);top.AddChild(UiStyles.Label($"{Label(key)} {i+1}",13));
                top.AddChild(UiStyles.Button("删除",()=>{array.RemoveAt(index);Render();}));
                if(i>0)top.AddChild(UiStyles.Button("上移",()=>{var value=array[index];array.RemoveAt(index);array.Insert(index-1,value);Render();}));
                Field(panel,"",array[index],v=>array[index]=v,depth+1);
            }
            return;
        }
        if(node is JsonObject obj)
        {
            if(key is "state" or "bindings")
            {
                var head=new HBoxContainer();box.AddChild(head);head.AddChild(UiStyles.Label(Label(key),16,UiStyles.Gold));var name=new LineEdit{PlaceholderText="填写内部记录名，如 charge",CustomMinimumSize=new(250,0)};head.AddChild(name);
                head.AddChild(UiStyles.Button("＋ 添加记录",()=>{if(name.Text.Length>0&&!obj.ContainsKey(name.Text)){obj[name.Text]=key=="state"?JsonValue.Create(0):JsonValue.Create("0");Render();}}));
            }
            foreach(var pair in obj.ToArray())
            {
                if(pair.Key=="schema")continue;
                if(obj["op"]?.ToString()!="if"&&pair.Key is "then" or "else")continue;
                var op=obj["op"]?.ToString();
                if(pair.Key=="cards"&&op!="develop"||pair.Key=="keepHistory"&&op!="return.hand"||pair.Key=="bypassRange"&&op!="attack"||pair.Key=="second"&&op!="buff")continue;
                if(pair.Key=="temporary"&&op is not("buff" or "copy.hand" or "create.hand")||pair.Key=="bindings"&&op is not("copy.hand" or "create.hand"))continue;
                var name=pair.Key;Field(box,name,pair.Value,v=>obj[name]=v,depth+1,obj["op"]?.ToString());
                if(key is "state" or "bindings")box.AddChild(UiStyles.Button("移除记录 "+name,()=>{obj.Remove(name);Render();}));
            }
            return;
        }
        var row=new HBoxContainer();box.AddChild(row);row.AddChild(UiStyles.Label(key=="temporary"?operation is "copy.hand" or "create.hand"?"回合结束仍在手牌时移除":"仅本次攻击期间":Label(key),13));
        if(node is JsonValue value&&value.TryGetValue<bool>(out var boolean))
        {var toggle=new CheckBox{ButtonPressed=boolean};row.AddChild(toggle);toggle.Toggled+=b=>set(JsonValue.Create(b));return;}
        if(node is JsonValue integer&&integer.TryGetValue<int>(out var number))
        {var spin=new SpinBox{MinValue=-10000,MaxValue=10000,Step=1,Value=number};row.AddChild(spin);spin.ValueChanged+=n=>set(JsonValue.Create((int)n));return;}
        var text=node?.ToString()??"";
        if(key=="key"&&operation is "create.hand" or "replace"&&Catalog is not null)
        {
            var cards=Catalog().Where(c=>operation!="replace"||c.CardType=="unit").ToArray();var pick=new OptionButton{SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(pick);pick.AddItem("选择关联卡牌…");foreach(var card in cards)pick.AddItem(card.Name);var index=Array.FindIndex(cards,c=>c.CardId==text);pick.Select(index+1);
            pick.ItemSelected+=i=>{if(i>0)set(JsonValue.Create(cards[(int)i-1].CardId));};return;
        }
        var options=key switch
        {"key" when operation is "stat.add" or "stat.set"=>["attack","defense","opCost","deployCost","hqHealth"],"key" when operation=="emit"=>["keyword.ready","keyword.consumed"],"scope"=>["self","linked","pair","friendly","enemy","all"],"rule"=>KeywordProgramSchema.Rules,"event"=>KeywordProgramSchema.Events,"subject"=>KeywordProgramSchema.Subjects,"target"=>KeywordProgramSchema.Targets,"op" when text is "eq" or "ne" or "lt" or "lte" or "gt" or "gte"=>["eq","ne","gt","gte","lt","lte"],"op"=>KeywordProgramSchema.Operations,_=>Array.Empty<string>()};
        if(options.Length>0)
        {
            var pick=new OptionButton{SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(pick);foreach(var option in options)pick.AddItem(Words.GetValueOrDefault(option,option));pick.Select(Math.Max(0,Array.IndexOf(options,text)));
            pick.ItemSelected+=i=>{set(JsonValue.Create(options[(int)i]));if(key=="op")Render();};return;
        }
        var edit=new LineEdit{Text=text,SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(edit);edit.TextChanged+=s=>set(JsonValue.Create(s));
        if(key is "value" or "second" or "left" or "right" or "cost")
        {
            var variables=new[]{"value","entries","attacks","target.attack","target.defense","target.cost"}.Concat(_root["state"]!.AsObject().Select(p=>"state."+p.Key)).ToArray();
            var pick=new OptionButton();row.AddChild(pick);pick.AddItem("选择变量…");foreach(var v in variables)pick.AddItem(v=="value"?"词条参数 X":v=="entries"?"进场次数":v=="attacks"?"本回合已攻击次数":v=="target.attack"?"目标当前攻击力":v=="target.defense"?"目标当前防御力":v=="target.cost"?"目标当前费用":"记忆："+v[6..]);
            pick.ItemSelected+=i=>{if(i>0)edit.Text=variables[(int)i-1];};edit.TooltipText="可填写数字、data.字段名，或从右侧选择变量";
        }
    }
    private static JsonNode Seed(string key)=>JsonNode.Parse(KeywordProgramSchema.Seed(key))!;
}
