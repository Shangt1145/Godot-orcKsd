using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kards.Ui.OrcBridge;

public enum InspectorPart { Condition, Value, Query, Filter, Record, List, Trigger, NativeEffect, NativeOperation, LegacyEffect }

/// <summary>A cancellable authoring copy. This never evaluates a rule or grants match admission.</summary>
public sealed class InspectorComposition
{
    private readonly InspectorDocument _source;
    private readonly string _sourceJson;
    private readonly string _destination;
    private readonly bool _wrapped;
    private readonly JsonObject? _originalWrapper;
    private readonly Dictionary<string,JsonNode?> _alternatives=[];
    public InspectorDocument Draft {get;}
    public InspectorPart Part {get;}
    public string Schema {get;}
    public bool InFilter {get;}
    public bool IsStale=>_source.Root.ToJsonString()!=_sourceJson;
    public InspectorComposition(InspectorDocument source,string path,InspectorPart part,bool wrapped=false,string schema="")
    {
        _source=source;_destination=path;Part=part;Schema=schema;_sourceJson=source.Root.ToJsonString();
        InFilter=path.Split('/').Any(k=>k is "where" or "filter");
        var original=source.At(path);
        _wrapped=wrapped;
        if(wrapped&&original is JsonObject o&&o["op"]?.ToString()=="predicate")
        {_originalWrapper=o.DeepClone().AsObject();original=o["test"];}
        Draft=new InspectorDocument(new JsonObject{["value"]=original?.DeepClone()});
    }
    public void Switch(string path,InspectorPart part,string mode)
    {
        var current=Draft.At(path);var old=Mode(current,part);
        if(old==mode)return;
        _alternatives[path+"|"+old]=current?.DeepClone();
        Draft.Set(path,_alternatives.TryGetValue(path+"|"+mode,out var prior)?prior:Seed(part,mode));
    }
    public void SelectRecordKind(string path,string schema,string key,string id,JsonObject seed)
    {
        var current=Draft.At(path) as JsonObject;var previous=current?[key]?.ToString()??"";
        if(previous==id)return;
        _alternatives[path+"|record:"+previous]=current?.DeepClone();
        if(_alternatives.TryGetValue(path+"|record:"+id,out var prior)&&prior is JsonObject){Draft.Set(path,prior);return;}
        var oldFields=current is not null?InspectorSchema.Fields(schema,current).Select(f=>f.Key).Append(key).ToHashSet():[];
        var nextFields=InspectorSchema.Fields(schema,seed).Select(f=>f.Key).ToHashSet();
        if(current is not null)foreach(var (name,value) in current)
            if(name!=key&&(nextFields.Contains(name)||!oldFields.Contains(name)))seed[name]=value?.DeepClone();
        Draft.Set(path,seed);
    }
    public void AppendItem(string path)
    {
        ForgetDescendants(path);Draft.Append(path,null);
    }
    public void RemoveItem(string path)
    {
        ForgetDescendants(path[..path.LastIndexOf('/')]);Draft.Remove(path);
    }
    public void MoveItem(string path,int direction){ForgetDescendants(path[..path.LastIndexOf('/')]);Draft.Move(path,direction);}
    public void DuplicateItem(string path){ForgetDescendants(path[..path.LastIndexOf('/')]);Draft.Duplicate(path);}
    public IReadOnlyDictionary<string,string> References(string localPath)
    {
        var refs=new Dictionary<string,string>{{"self","效果来源"},{"source","效果来源（原声明别名）"},{"friendlyPlayer","我方玩家"},{"enemyPlayer","敌方玩家"}};
        if(InFilter||localPath.Split('/').Any(k=>k is "where" or "filter"))refs["candidate"]="正在筛选的对象";
        var context=new InspectorDocument(_source.Root);var local=Draft.At("/value")?.DeepClone();var wrapped=_wrapped&&local is JsonObject localObject&&localObject.ContainsKey("test");
        context.Set(_destination,wrapped?new JsonObject{["op"]="predicate",["test"]=local}:local);
        var currentPath=_destination+(wrapped?"/test":"")+localPath["/value".Length..];
        string? effectPath=null;JsonObject? effect=null;
        for(var ancestor=currentPath;ancestor.Length>0;ancestor=ancestor[..ancestor.LastIndexOf('/')])
            if(context.At(ancestor) is JsonObject node&&node.ContainsKey("trigger")&&!node.ContainsKey("op")){effectPath=ancestor;effect=node;break;}
        if(effect is null)return refs;
        foreach(var target in (effect["targets"] as JsonArray??new()).OfType<JsonObject>())
            if(target["id"] is {} id)refs[id.ToString()]="玩家选择的目标「"+id+"」";
        // Only earlier siblings and enclosing loop bindings are in scope. A nested effect starts a new scope.
        for(var ancestor=currentPath;ancestor.Length>effectPath!.Length;ancestor=ancestor[..ancestor.LastIndexOf('/')])
        {
            var split=ancestor.LastIndexOf('/');var parent=ancestor[..split];
            if(context.At(parent) is JsonArray siblings&&int.TryParse(ancestor[(split+1)..],out var index))
                foreach(var previous in siblings.Take(index).OfType<JsonObject>())
                    if(previous["as"] is {} name&&name.ToString().Length>0)refs[name.ToString()]="前序记录「"+name+"」";
            if(context.At(ancestor) is JsonObject container&&container["op"]?.ToString()=="forEach"&&container["as"] is {} binding)
                refs[binding.ToString()]="当前遍历的对象「"+binding+"」";
        }
        // Existing authoring event families; these references do not grant native execution admission.
        var trigger=effect["trigger"]?.ToString()??"";
        var eventRefs=trigger switch
        {
            "deploy"=>new[]{"eventUnit","eventCard"},"death"=>["dead","eventUnit","eventCard"],"damaged"=>["eventUnit","eventVictim"],
            "afterAttack"=>["attacker","defender","attackTarget","eventUnit"],"play" or "order"=>["eventCard"],
            _ when trigger.StartsWith("behavior:game/attack:",StringComparison.Ordinal)=>["attacker","defender","attackTarget"],
            _=>Array.Empty<string>()
        };
        foreach(var reference in eventRefs)refs[reference]="事件 · "+InspectorPresentation.Value(reference);
        return refs;
    }
    private void ForgetDescendants(string path)
    {
        // Array indexes change after removal. Never restore another item's cached parameters.
        foreach(var key in _alternatives.Keys.Where(k=>k.StartsWith(path+"/",StringComparison.Ordinal)).ToArray())_alternatives.Remove(key);
    }
    public bool Apply(out string problem)
    {
        if(IsStale){problem="原效果已发生变化，请关闭后重新打开，避免覆盖其他修改。";return false;}
        var value=Draft.At("/value");var issues=Issues(value,Part,Schema);
        if(issues.Count>0){problem=issues[0];return false;}
        JsonNode? output=value?.DeepClone();
        if(Part==InspectorPart.Trigger&&value is JsonObject behavior)
            output=JsonValue.Create($"behavior:{behavior["kind"]}/{behavior["action"]}:{behavior["phase"]}");
        if(_wrapped&&value is JsonObject obj&&obj.ContainsKey("test"))
        {
            var wrapper=_originalWrapper?.DeepClone().AsObject()??new JsonObject{["op"]="predicate"};
            wrapper["test"]=output;output=wrapper;
        }
        _source.Transaction(()=>_source.Set(_destination,output));problem="";return true;
    }
    public static string Mode(JsonNode? value,InspectorPart part)=>part switch
    {
        InspectorPart.Condition=>value is JsonObject c?c["op"]?.ToString()=="predicate"?"predicate-wrapper":c["test"] is JsonValue?c["test"]!.ToString():c.ContainsKey("op")?"library":"retained":"unset",
        InspectorPart.Query=>value is JsonObject q?q["sel"]?.ToString() switch{"self"=>"self","ref" or "refAll"=>"recorded","adjacent"=>"adjacent","union"=>"union",_=>"query"}:value is JsonValue?"reference":"unset",
        InspectorPart.Trigger=>value is JsonObject?"behavior":value?.ToString().StartsWith("behavior:",StringComparison.Ordinal)==true?"behavior":value is null?"unset":"event",
        _=>value is JsonObject v?v["expr"]?.ToString()??"retained":value is null?"unset":"literal"
    };
    public static JsonNode? Seed(InspectorPart part,string mode)=>part switch
    {
        InspectorPart.Condition=>mode switch
        {
            "compare"=>new JsonObject{["test"]="compare",["left"]=null,["cmp"]=null,["right"]=null},
            "exists"=>new JsonObject{["test"]="exists",["left"]=null},
            "all" or "any"=>new JsonObject{["test"]=mode,["items"]=new JsonArray()},
            "not"=>new JsonObject{["test"]="not",["item"]=null},"library"=>new JsonObject{["op"]=""},_=>null
        },
        InspectorPart.Query=>mode switch
        {"self"=>new JsonObject{["sel"]="self"},"reference"=>JsonValue.Create(""),"recorded"=>new JsonObject{["sel"]="refAll",["ref"]=null},"adjacent"=>new JsonObject{["sel"]="adjacent",["ref"]=null},"union"=>new JsonObject{["sel"]="union",["queries"]=new JsonArray()},_=>new JsonObject{["sel"]="all",["side"]=null,["zone"]=null}},
        InspectorPart.Trigger=>mode=="behavior"?new JsonObject{["kind"]=null,["action"]=null,["phase"]=null}:JsonValue.Create(""),
        InspectorPart.Filter or InspectorPart.Record or InspectorPart.NativeOperation or InspectorPart.LegacyEffect=>new JsonObject(),
        InspectorPart.List=>new JsonArray(),
        _=>mode switch
        {
            "read"=>new JsonObject{["expr"]="read",["of"]=null,["path"]=null},
            "count"=>new JsonObject{["expr"]="count",["query"]=Seed(InspectorPart.Query,"query")},
            "math"=>new JsonObject{["expr"]="math",["operator"]=null,["items"]=new JsonArray((JsonNode?)null,null)},
            "memory"=>new JsonObject{["expr"]="memory",["scope"]=null,["key"]=null},
            "variable"=>new JsonObject{["expr"]="variable",["name"]=null},"size"=>new JsonObject{["expr"]="size",["value"]=null},
            "random"=>new JsonObject{["expr"]="random",["min"]=null,["max"]=null},"query"=>new JsonObject{["expr"]="query",["query"]=Seed(InspectorPart.Query,"query")},
            "union" or "intersection"=>new JsonObject{["expr"]=mode,["items"]=new JsonArray()},
            _=>null
        }
    };
    // Local completeness checks, never a substitute for backend compilation and execution checks.
    public static IReadOnlyList<string> Issues(JsonNode? value,InspectorPart part,string schema="")
    {
        var issues=new List<string>();Visit(value,part,"当前设置",issues,0,schema);return issues;
    }
    public static string ValueType(JsonNode? value)=>value switch
    {
        JsonObject o=>o["expr"]?.ToString() switch
        {
            "count" or "size" or "math" or "random"=>"number",
            "query" or "union" or "intersection"=>"collection",
            "read"=>o["path"]?.ToString() switch
            {
                "attack" or "defense" or "cost" or "opCost" or "kredits" or "maxKredits" or "hq" or "hqMax" or "maxDefense" or "missingDefense" or "actionsLeft"=>"number",
                "unitType" or "cardType" or "name" or "zone" or "rarity"=>"text",
                "keywords" or "printed.keywords" or "extraTypes"=>"collection",
                "alive" or "damaged" or "pinned" or "canAct" or "revealed" or "silenced"=>"boolean",_=>"unknown"
            },_=>"unknown"
        },
        null=>"unknown",
        _=>value.GetValueKind() switch{JsonValueKind.Number=>"number",JsonValueKind.String=>"text",JsonValueKind.True or JsonValueKind.False=>"boolean",JsonValueKind.Array=>"collection",_=>"unknown"}
    };
    public static IReadOnlyList<string> Comparisons(JsonNode? left)=>ValueType(left) switch
    {
        "number"=>["==","!=",">",">=","<","<="],
        "boolean" or "text"=>["==","!="],"collection"=>["contains","==","!="],
        _=>["==","!=",">",">=","<","<=","contains","in"]
    };
    private static void Visit(JsonNode? value,InspectorPart part,string location,List<string> issues,int depth,string schema="")
    {
        if(depth>40){issues.Add(location+"：层次过深，请在高级工具检查。");return;}
        if(value is null){issues.Add(location+"：尚未填写。");return;}
        void Required(JsonObject o,string key){if(o[key] is null||o[key]!.ToString().Length==0)issues.Add(location+"：请选择"+InspectorPresentation.Field(key)+"。");}
        void Child(JsonObject o,string key,InspectorPart kind,string childSchema="")=>Visit(o[key],kind,location+" · "+InspectorPresentation.Field(key),issues,depth+1,childSchema);
        void Items(JsonObject o,InspectorPart kind)
        {
            if(o["items"] is not JsonArray {Count:>0} items){issues.Add(location+"：至少添加一项。");return;}
            for(var i=0;i<items.Count;i++)Visit(items[i],kind,location+" · 第 "+(i+1)+" 项",issues,depth+1);
        }
        if(part==InspectorPart.Condition&&value is JsonObject c)
        {
            if(c["op"]?.ToString()=="predicate"){Child(c,"test",InspectorPart.Condition);return;}
            if(c.ContainsKey("op")){Visit(c,InspectorPart.Record,location,issues,depth+1,"condition-library");return;}
            switch(c["test"]?.ToString())
            {
                case "compare":
                    Required(c,"cmp");Child(c,"left",InspectorPart.Value);Child(c,"right",InspectorPart.Value);
                    var leftType=ValueType(c["left"]);var rightType=ValueType(c["right"]);var cmp=c["cmp"]?.ToString();
                    if(cmp is not null&&!Comparisons(c["left"]).Contains(cmp)&&cmp!="in")issues.Add(location+"：比较方式与取值类型不匹配。");
                    if(cmp is "==" or "!=" or ">" or ">=" or "<" or "<="&&leftType!="unknown"&&rightType!="unknown"&&leftType!=rightType)issues.Add(location+"：两边需要可比较的同类值。");
                    if(cmp=="in"&&rightType is not ("unknown" or "collection"))issues.Add(location+"：所属范围需要一组对象或值。");
                    break;
                case "exists":Child(c,"left",InspectorPart.Value);break;
                case "all":case "any":Items(c,InspectorPart.Condition);break;
                case "not":Child(c,"item",InspectorPart.Condition);break;
            }
        }
        else if(part==InspectorPart.Query&&value is JsonObject q)
        {
            Required(q,"sel");
            if(q["sel"]?.ToString() is "all" or "random" or "first" or "last")
            {Required(q,"side");Required(q,"zone");}
            if(q.ContainsKey("where"))Child(q,"where",InspectorPart.Condition);
            if(q.ContainsKey("filter")&&q["filter"] is not null)Child(q,"filter",InspectorPart.Filter,"filter");
            if(q["sel"]?.ToString() is "ref" or "refAll" or "adjacent")Required(q,"ref");
            if(q["sel"]?.ToString()=="union")Child(q,"queries",InspectorPart.List,"queries");
            if(q.ContainsKey("count")&&q["count"] is not null)Child(q,"count",InspectorPart.Value);
        }
        else if(part==InspectorPart.Query&&value.ToString().Length==0)issues.Add(location+"：请选择引用的对象。");
        else if(part==InspectorPart.Value&&value is JsonObject v)
        {
            switch(v["expr"]?.ToString())
            {
                case "read":Required(v,"of");Required(v,"path");break;
                case "count":case "query":Child(v,"query",InspectorPart.Query);break;
                case "variable":Required(v,"name");break;
                case "size":case "literal":Child(v,"value",InspectorPart.Value);break;
                case "random":Child(v,"min",InspectorPart.Value);Child(v,"max",InspectorPart.Value);break;
                case "union":case "intersection":Items(v,InspectorPart.Value);break;
                case "math":
                    Required(v,"operator");Items(v,InspectorPart.Value);
                    if(v["items"] is JsonArray terms&&terms.Any(t=>ValueType(t) is not ("unknown" or "number")))issues.Add(location+"：计算项需要数值，不能使用文字、是非或对象集合。");
                    break;
                case "memory":Required(v,"scope");Required(v,"key");break;
            }
        }
        else if(part==InspectorPart.Trigger)
        {
            if(value is JsonObject behavior){Required(behavior,"kind");Required(behavior,"action");Required(behavior,"phase");}
            else if(value.ToString().Length==0)issues.Add(location+"：请选择发生时机。");
        }
        else if(part==InspectorPart.NativeEffect&&value is JsonObject native)
        {
            if(native["PrefabJson"] is {} prefab)
            {try{if(JsonNode.Parse(prefab.ToString()) is not JsonObject)issues.Add("原生预制体必须是对象。");}catch(JsonException){issues.Add("原生预制体 JSON 格式无效。");}return;}
            if(!OrcEffectEditing.Templates.Any(t=>t.Id==native["Template"]?.ToString()&&t.Guard.ToString()==native["Guard"]?.ToString()&&t.Hook==native["Hook"]?.ToString()))issues.Add("请选择有效的事件与范围组合。");
            Child(native,"Ops",InspectorPart.List,"native-ops");
        }
        else if(part==InspectorPart.List&&value is JsonArray list)
        {
            var (itemPart,itemSchema)=schema switch
            {"native-ops"=>(InspectorPart.NativeOperation,"native-op"),"actions"=>(InspectorPart.Record,"action"),"effectList"=>(InspectorPart.Record,"effect"),"options"=>(InspectorPart.Record,"option"),"queries"=>(InspectorPart.Query,""),"conditions"=>(InspectorPart.Condition,""),"targets"=>(InspectorPart.Record,"target"),_=>(InspectorPart.Value,"")};
            for(var i=0;i<list.Count;i++)Visit(list[i],itemPart,location+" · 第 "+(i+1)+" 项",issues,depth+1,itemSchema);
            if(schema=="native-ops"&&list.Count==0)issues.Add("至少添加一个原生动作。");
            if(schema=="targets"&&list.OfType<JsonObject>().Where(t=>t["id"] is not null).GroupBy(t=>t["id"]!.ToString()).Any(g=>g.Count()>1))issues.Add("目标名称不能重复，请为新增目标设置不同名称。");
        }
        else if(part is InspectorPart.Record or InspectorPart.Filter or InspectorPart.NativeOperation or InspectorPart.LegacyEffect&&value is JsonObject record)
        {
            var kind=part switch{InspectorPart.Filter=>schema.Length>0?schema:"filter",InspectorPart.NativeOperation=>"native-op",InspectorPart.LegacyEffect=>"legacy-effect",_=>schema};
            if(kind is "action" or "condition-library")Required(record,"op");
            foreach(var field in InspectorSchema.Fields(kind,record))
            {
                if(!record.TryGetPropertyValue(field.Key,out var item)||item is null){if(!field.Optional)issues.Add(location+"：请填写"+field.Label+"。");continue;}
                if(field.Type=="integer")
                {if(item is not JsonValue n||!n.TryGetValue<int>(out _))issues.Add(field.Label+"：需要有效的整数。");}
                else if(field.Type=="counter-name")
                {if(item.ToString().Length>0)try{_=OrcCounterCodegen.Normalize(item.ToString());}catch(InvalidDataException e){issues.Add(e.Message);}}
                else if(field.Type is "value" or "condition" or "predicate" or "query" or "selector" or "filter" or "trigger" or "actions" or "options" or "effectList" or "native-ops" or "targets")
                    Visit(item,InspectorSchema.Part(field.Type),location+" · "+field.Label,issues,depth+1,field.Type);
                else if(!field.Optional&&item.ToString().Length==0)issues.Add(location+"：请填写"+field.Label+"。");
            }
        }
    }
    public static string Describe(JsonNode? value,int depth=0)
    {
        if(value is null)return "尚未填写";
        if(depth>6)return "嵌套组合";
        string D(JsonNode? n)=>Describe(n,depth+1);
        if(value is JsonObject o)
        {
            if(o["Op"] is {} nativeOp)
            {
                try{return OrcEffectEditing.Summarize(JsonSerializer.Deserialize<Kards.Ui.Contracts.OrcOpDraft>(o.ToJsonString())!);}
                catch(JsonException){return OrcEffectEditing.Op(nativeOp.ToString())?.Label??nativeOp.ToString();}
            }
            if(o["Template"] is {} template)return OrcEffectEditing.Templates.FirstOrDefault(t=>t.Id==template.ToString()&&t.Guard.ToString()==o["Guard"]?.ToString())?.Label??"原生效果";
            if(o["op"]?.ToString()=="predicate")return D(o["test"]);
            if(o["test"] is {} test)return test.ToString() switch
            {
                "compare"=>D(o["left"])+" "+(o["cmp"] is {} cmp?InspectorPresentation.Value(cmp.ToString()):"选择比较关系")+" "+D(o["right"]),
                "exists"=>D(o["left"])+"存在",
                "all" or "any"=>(test.ToString()=="all"?"全部满足":"任一满足")+"："+string.Join("；",(o["items"] as JsonArray??new()).Select(D)),
                "not"=>"不满足（"+D(o["item"])+"）",_=>"已有扩展条件"
            };
            if(o["expr"] is {} expr)return expr.ToString() switch
            {
                "read"=>(o["of"]?.ToString()=="self"?"效果来源":InspectorPresentation.Value(o["of"]?.ToString()??"尚未选择对象"))+"的"+InspectorPresentation.Value(o["path"]?.ToString()??"尚未选择属性"),
                "count"=>"数量（"+D(o["query"])+"）",
                "math"=>(o["operator"]?.ToString()=="add"?"相加":InspectorPresentation.Value(o["operator"]?.ToString()??"选择运算"))+"（"+string.Join("，",(o["items"] as JsonArray??new()).Select(D))+"）",
                "variable"=>"前序记录「"+o["name"]+"」","size"=>"列表长度（"+D(o["value"])+"）","random"=>"随机值（"+D(o["min"])+" ～ "+D(o["max"])+"）",
                "query"=>D(o["query"]),"literal"=>D(o["value"]),"union" or "intersection"=>(expr.ToString()=="union"?"合并":"共有对象")+"（"+string.Join("，",(o["items"] as JsonArray??new()).Select(D))+"）",
                "memory"=>InspectorPresentation.Value(o["scope"]?.ToString()??"未选范围")+"的记忆「"+o["key"]+"」",
                _=>InspectorPresentation.Value(expr.ToString())+"（已有设置）"
            };
            if(o["sel"]?.ToString()=="self")return "效果来源";
            if(o["op"] is {} op)return InspectorPresentation.Operation(op.ToString())+string.Join("",o.Where(p=>p.Key!="op"&&p.Value is not null).Take(2).Select(p=>" · "+InspectorSchema.Label(p.Key)+" "+D(p.Value)));
            if(o.ContainsKey("sel"))return InspectorPresentation.Value(o["side"]?.ToString()??"未选归属")+" · "+InspectorPresentation.Value(o["zone"]?.ToString()??"未选区域")+" · "+InspectorPresentation.Value(o["sel"]?.ToString()??"未选方式")+(o.ContainsKey("where")?"，筛选："+D(o["where"]):"")+(o["filter"] is {} filter?"，卡牌信息："+D(filter):"");
            if(o["id"] is {} target)return "目标「"+target+"」 · "+InspectorPresentation.Value(o["kind"]?.ToString()??"unit");
            if(o["label"] is {} label)return "选项「"+label+"」："+D(o["actions"]);
            if(o.Count==0)return "尚未限制";
            return string.Join("，",o.Where(p=>p.Value is not null).Take(4).Select(p=>InspectorSchema.Label(p.Key)+"："+D(p.Value)));
        }
        if(value is JsonArray array)return array.Count==0?"还没有内容":string.Join("；",array.Take(3).Select(D))+(array.Count>3?$"等 {array.Count} 项":"");
        if(value.GetValueKind() is JsonValueKind.True or JsonValueKind.False)return value.ToString()=="true"?"是":"否";
        return value.ToString();
    }
}
