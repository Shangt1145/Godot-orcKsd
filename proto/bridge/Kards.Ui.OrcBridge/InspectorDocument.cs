using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kards.Ui.OrcBridge;

/// <summary>Every JSON node is editable, including unknown extensions. History stores the entire
/// declaration, never a projection onto the subset supported by a match engine.</summary>
public sealed class InspectorDocument
{
    private readonly List<string> _history=[];
    private int _cursor;
    public JsonNode Root {get;private set;}
    public bool CanUndo=>_cursor>0;
    public bool CanRedo=>_cursor<_history.Count-1;
    public event Action? Changed;
    public InspectorDocument(JsonNode root){Root=root.DeepClone();_history.Add(Root.ToJsonString());}
    public static string Child(string path,string key)=>path+"/"+key.Replace("~","~0").Replace("/","~1");
    public JsonNode? At(string path)
    {
        JsonNode? node=Root;
        foreach(var key in Keys(path))node=node switch {JsonObject o=>o[key],JsonArray a=>a[int.Parse(key)],_=>null};
        return node;
    }
    private static IEnumerable<string> Keys(string path)=>path.Split('/').Skip(1).Select(k=>k.Replace("~1","/").Replace("~0","~"));
    private (JsonNode Parent,string Key) Parent(string path)
    {
        if(path.Length==0)throw new InvalidDataException("不能删除文档根节点");
        var split=path.LastIndexOf('/');return (At(path[..split])!,Keys(path).Last());
    }
    public void Set(string path,JsonNode? value)
    {
        if(path.Length==0)Root=value?.DeepClone()??throw new InvalidDataException("文档根节点不能为空");
        else {var (parent,key)=Parent(path);if(parent is JsonArray a)a[int.Parse(key)]=value?.DeepClone();else parent[key]=value?.DeepClone();}
        Commit();
    }
    public void Remove(string path)
    {
        var (parent,key)=Parent(path);if(parent is JsonArray a)a.RemoveAt(int.Parse(key));else parent.AsObject().Remove(key);Commit();
    }
    public void Append(string path,JsonNode? value){At(path)!.AsArray().Add(value?.DeepClone());Commit();}
    public void Move(string path,int delta)
    {
        var (parent,key)=Parent(path);if(parent is not JsonArray a)return;
        var index=int.Parse(key);var target=Math.Clamp(index+delta,0,a.Count-1);
        if(index==target)return;var value=a[index];a.RemoveAt(index);a.Insert(target,value);Commit();
    }
    public void Duplicate(string path)
    {
        var (parent,key)=Parent(path);if(parent is JsonArray a){var i=int.Parse(key);a.Insert(i+1,a[i]?.DeepClone());Commit();}
    }
    public void Undo(){if(CanUndo){Root=JsonNode.Parse(_history[--_cursor])!;Changed?.Invoke();}}
    public void Redo(){if(CanRedo){Root=JsonNode.Parse(_history[++_cursor])!;Changed?.Invoke();}}
    private void Commit()
    {
        var json=Root.ToJsonString();if(json==_history[_cursor])return;
        _history.RemoveRange(_cursor+1,_history.Count-_cursor-1);_history.Add(json);
        if(_history.Count>80)_history.RemoveAt(0);_cursor=_history.Count-1;Changed?.Invoke();
    }
    public JsonElement Element()=>JsonSerializer.SerializeToElement(Root);
}

/// <summary>Authoring vocabulary comes from the complete source contract and primitive metadata.</summary>
public static class InspectorVocabulary
{
    public static JsonElement Catalog=>DesktopOrc.Catalog;
    public static string[] Operations=>Catalog.GetProperty("contract").GetProperty("ops").EnumerateObject().Select(p=>p.Name).OrderBy(p=>p).ToArray();
    public static string[] Conditions=>Catalog.GetProperty("contract").GetProperty("conditions").EnumerateObject().Select(p=>p.Name).ToArray();
    public static string Label(string category,string key)
    {
        var labels=Catalog.GetProperty("labels");
        var name=category switch{"op"=>"SEED_OP","condition"=>"SEED_COND","trigger"=>"TRIGGERS","value"=>"SEED_VALUE",_=>"SEED_FIELD"};
        if(labels.GetProperty(name).TryGetProperty(key,out var item))return item.GetString()??key;
        if(category=="op"&&Catalog.GetProperty("vocabulary").GetProperty("ops").TryGetProperty(key,out var primitive))return primitive.GetProperty("name").GetString()??key;
        return key;
    }
    public static JsonObject Effect()=>new(){["trigger"]="deploy",["targets"]=new JsonArray(),["actions"]=new JsonArray()};
    public static JsonObject Action(string op)
    {
        var result=new JsonObject{["op"]=op};
        if(Catalog.GetProperty("vocabulary").GetProperty("ops").TryGetProperty(op,out var primitive))
            foreach(var field in primitive.GetProperty("fields").EnumerateArray())
                if((!field.TryGetProperty("optional",out var optional)||!optional.GetBoolean())&&field.TryGetProperty("initial",out var initial))result[field.GetProperty("name").GetString()!]=JsonNode.Parse(initial.GetRawText());
        return result;
    }
    public static IEnumerable<string> Fields(JsonObject node)
    {
        if(node["op"] is JsonValue op&&Catalog.GetProperty("contract").GetProperty("ops").TryGetProperty(op.ToString(),out var action))
            return action.GetProperty("params").EnumerateObject().Select(p=>p.Name);
        if(node["op"] is JsonValue cond&&Catalog.GetProperty("contract").GetProperty("conditions").TryGetProperty(cond.ToString(),out var condDef))
            return condDef.GetProperty("params").EnumerateObject().Select(p=>p.Name);
        if(node["type"] is JsonValue type&&Catalog.GetProperty("contract").GetProperty("conditions").TryGetProperty(type.ToString(),out var condition))
            return condition.GetProperty("params").EnumerateObject().Select(p=>p.Name);
        if(node.ContainsKey("trigger"))return ["trigger","eventSide","selfOnly","listenZone","once","oncePerTurn","firstEvent","condition","targets","actions","else"];
        return [];
    }
    public static JsonNode? Initial(JsonObject node,string key)
    {
        if(node["op"] is {} op)
            foreach(var table in new[]{"ops","conditions"})
                if(Catalog.GetProperty("vocabulary").GetProperty(table).TryGetProperty(op.ToString(),out var entry))
                    foreach(var field in entry.GetProperty("fields").EnumerateArray())
                        if(field.GetProperty("name").GetString()==key)return JsonNode.Parse(field.GetProperty("initial").GetRawText());
        return Initial(key);
    }
    public static JsonNode? Initial(string key)=>key switch
    {
        "actions" or "then" or "else" or "targets" or "items" or "effects" or "options"=>new JsonArray(),
        "once" or "oncePerTurn" or "selfOnly" or "firstEvent" or "excludeSelf"=>JsonValue.Create(false),
        "amount" or "count" or "times" or "attack" or "defense"=>JsonValue.Create(1),
        "condition" or "test"=>JsonNode.Parse("{\"op\":\"compare\",\"left\":1,\"cmp\":\">=\",\"right\":1}"),
        "target" or "query"=>JsonNode.Parse("{\"zone\":\"field\",\"side\":\"friendly\",\"sel\":\"all\"}"),
        _=>JsonNode.Parse(InspectorPresentation.Infer(key).GetProperty("initial").GetRawText())
    };
    public static string[] Choices(string key)=>key switch
    {
        "trigger"=>Catalog.GetProperty("labels").GetProperty("TRIGGERS").EnumerateObject().Select(p=>p.Name)
            .Concat(new[]{"game","op"}.SelectMany(kind=>(kind=="game"?Catalog.GetProperty("behaviorActions").EnumerateObject().Select(p=>p.Name):Operations)
                .SelectMany(action=>new[]{"before","during","after"}.Select(phase=>$"behavior:{kind}/{action}:{phase}")))).ToArray(),
        "op"=>Operations,
        "type"=>Conditions,
        "keyword"=>Catalog.GetProperty("keywords").EnumerateObject().Select(p=>p.Name).ToArray(),
        "side" or "eventSide"=>["self","enemy","any","both","friendly"],
        "zone" or "listenZone"=>["field","board","support","frontline","hand","deck","discard","hq","pool","any"],
        "pick" or "sel"=>["all","random","first","last","self","refAll","ref","adjacent","union","eventUnit","defender","summoned"],
        "scope"=>["unit","player","match"],
        "duration" or "until"=>["","turn","permanent","nextTurn"],
        "cmp"=>["==","!=",">",">=","<","<=","contains","in"],
        "expr"=>["literal","read","variable","count","query","size","math","union","intersection","random","memory"],
        "test"=>["all","any","not","compare","exists"],
        "operator"=>["add","subtract","multiply","divide","min","max","floor","ceil"],
        "mode"=>["add","set"],
        "property"=>["attack","defense","opCost","cost","kredits","maxKredits"],
        "path" or "stat"=>["attack","defense","maxDefense","missingDefense","cost","opCost","unitType","cardType","set","system","rarity","keywords","owner","zone","alive","damaged","pinned","canAct","actionsLeft","revealed","silenced","chargeIn","printed.attack","printed.defense","printed.keywords","mods.immuneOrders","hq","hqMax","kredits","maxKredits"],
        "of"=>["self","source","eventUnit","event","dead","eventVictim","victim","defender","attackTarget","attacker","summoned","lastSummoned","target","eventCard","remembered","candidate","current","friendlyPlayer","enemyPlayer","state"],
        "kind"=>["unit","hq","card","any"],
        "cardType"=>["unit","order","counter"],
        "unitType"=>["infantry","tank","artillery","fighter","bomber"],
        _=>[]
    };
}
