using System.Text.Json;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

/// <summary>Compiles structured expressions/control flow into native EffectRuntime calls.
/// This is code generation, never a second match simulator. User strings are emitted only as literals.</summary>
internal sealed class DesktopNativeCodegen
{
    private const string Card="Orc.Cards.Card",Base="Orc.Game.Cards.CardBase",Unit="Orc.Game.Cards.UnitCard",Fields="Orc.Game.Cards.CardStatFields";
    private readonly Dictionary<string,(string Code,bool Cards)> _vars=new(StringComparer.Ordinal);
    private int _serial;
    private string Fresh()=>"orcV"+_serial++;
    private static string Literal(string s)=>JsonSerializer.Serialize(s);
    private static string T(JsonElement o,string k)=>o.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
    private static InvalidDataException Gap(string s)=>new("OrC-KSD 尚无无损执行适配："+s+"。完整声明保留可编辑。");
    private static void Only(JsonElement o,params string[] fields)
    {foreach(var p in o.EnumerateObject())if(!fields.Contains(p.Name,StringComparer.Ordinal))throw Gap("字段 "+p.Name);}
    public string Compile(JsonElement graph)
    {
        var selections=new List<string>();
        if(graph.TryGetProperty("targets",out var targets))foreach(var target in targets.EnumerateArray())
        {
            DesktopTargetContext.ValidateTarget(target);var name=T(target,"id");if(_vars.ContainsKey(name))throw Gap("重复目标声明 "+name);
            var local=Fresh();_vars[name]=(local,true);
            selections.Add($"var {local}=await Kards.Ui.OrcBridge.DesktopTargetContext.SelectAsync(orcSelf,{Literal(target.GetRawText())},{Literal(DesktopTargetContext.Scope(graph))},ct);");
        }
        var body=Actions(graph.GetProperty("actions"));
        return $"var orcRuntime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);\nif (orcRuntime is not null && self is {Base} orcSelf && orcSelf.Owner is not null)\n{{\n{string.Join("\n",selections)}\n{body}\n}}";
    }
    private string Actions(JsonElement actions)=>string.Join("\n",actions.EnumerateArray().Select(Action));
    private string Action(JsonElement a)
    {
        var op=T(a,"op");
        switch(op)
        {
            case "let":
            {
                Only(a,"op","name","value");var name=T(a,"name");var value=a.GetProperty("value");
                var isCards=IsCards(value);var code=Fresh();var expression=isCards?Cards(value):Number(value);
                _vars[name]=(code,isCards);return $"var {code} = {expression};";
            }
            case "sequence":Only(a,"op","actions");return Actions(a.GetProperty("actions"));
            case "loop":
            {
                Only(a,"op","times","actions");var limit=Fresh();var i=Fresh();var n=Number(a.GetProperty("times"));
                var scope=_vars.ToArray();var body=Actions(a.GetProperty("actions"));Restore(scope);
                return $"var {limit} = {n}; for(var {i}=0; {i}<{limit}; {i}++) {{\n{body}\n}}";
            }
            case "branch":
            {
                Only(a,"op","test","then","else");var condition=Predicate(a.GetProperty("test"),"orcSelf");var scope=_vars.ToArray();
                var yes=Actions(a.GetProperty("then"));Restore(scope);var no=a.TryGetProperty("else",out var other)?Actions(other):"";Restore(scope);
                return $"if({condition}) {{\n{yes}\n}} else {{\n{no}\n}}";
            }
            case "iterate":
            {
                Only(a,"op","target","as","actions");var query=Query(a.GetProperty("target"));var scope=_vars.ToArray();var item=Fresh();
                _vars[T(a,"as")]=($"new {Card}[]{{{item}}}",true);var body=Actions(a.GetProperty("actions"));Restore(scope);
                return $"foreach(var {item} in {query}) {{\n{body}\n}}";
            }
            case "drawOne":
            {
                Only(a,"op","side","as","target");if(T(a,"side") is not ("" or "self" or "friendly"))throw Gap("敌方抽牌");
                if(a.TryGetProperty("target",out var filter))throw Gap("定向抽牌 "+filter.GetRawText());
                var name=T(a,"as");if(name.Length==0)return "await orcRuntime.DrawAsync(orcSelf,1,ct);";
                var before=Fresh();var result=Fresh();_vars[name]=(result,true);
                return $"var {before}=orcSelf.Owner.Hand.Cast<{Card}>().ToArray(); await orcRuntime.DrawAsync(orcSelf,1,ct); var {result}=orcSelf.Owner.Hand.Cast<{Card}>().Except({before}).ToArray();";
            }
            case "changeResource":
            {
                Only(a,"op","side","mode","property","value");if(T(a,"side") is not ("self" or "friendly")||T(a,"mode")!="add")throw Gap("指挥资源侧或模式");
                var value=Number(a.GetProperty("value"));var local=Fresh();
                var methods=T(a,"property") switch{"kredits"=>("GainPointsAsync","LosePointsAsync"),"maxKredits"=>("GainPointSlotsAsync","LosePointSlotsAsync"),_=>throw Gap("资源 "+T(a,"property"))};
                return $"var {local}={value}; if({local}>=0) await orcRuntime.{methods.Item1}(orcSelf,{local},ct); else await orcRuntime.{methods.Item2}(orcSelf,-{local},ct);";
            }
            default:
            {
                if(!a.TryGetProperty("target",out var target))throw Gap("动作 "+op);
                var query=Query(target);var item=Fresh();string body;
                switch(op)
                {
                    case "dealDamage":Only(a,"op","target","amount");body=$"await orcRuntime.DamageAsync({item},{Number(a.GetProperty("amount"),item)},orcSelf,ct);";break;
                    case "destroyUnit":Only(a,"op","target");body=$"await orcRuntime.KillAsync({item},orcSelf,ct);";break;
                    case "suppressUnit":Only(a,"op","target");body=$"await orcRuntime.SilenceAsync({item},ct);";break;
                    case "setPinned":
                        Only(a,"op","target","enabled","turns");if(a.TryGetProperty("turns",out var turns)&&turns.GetInt32()!=1)throw Gap("压制期限");
                        body=a.GetProperty("enabled").GetBoolean()?$"await orcRuntime.PinAsync({item},ct);":$"await orcRuntime.RevokeAsync({item},{Literal(KeywordIds.Suppressed)},ct);";break;
                    case "changeAttribute":
                    {
                        Only(a,"op","target","property","mode","value","duration");
                        var duration=T(a,"duration") switch{"" or "permanent"=>"Orc.Game.Effects.EffectDuration.Permanent","turn"=>"Orc.Game.Effects.EffectDuration.TurnEnd",_=>throw Gap("持续时间 "+T(a,"duration"))};
                        var value=Number(a.GetProperty("value"),item);var property=T(a,"property");var mode=T(a,"mode");
                        if(mode!="add")throw Gap("属性写入模式 "+mode);
                        body=property switch
                        {
                            "attack"=>$"await orcRuntime.BuffAsync({item},{value},0,{duration},ct);",
                            "defense"=>$"await orcRuntime.BuffAsync({item},0,{value},{duration},ct);",
                            "opCost"=>$"await orcRuntime.CostModAsync({item},{value},{duration},ct);",
                            _=>throw Gap("属性 "+property)
                        };break;
                    }
                    case "setKeyword":
                    {
                        Only(a,"op","target","keyword","enabled","value");var keyword=NativeKeyword(T(a,"keyword"));
                        body=a.TryGetProperty("enabled",out var enabled)&&!enabled.GetBoolean()?$"await orcRuntime.RevokeAsync({item},{Literal(keyword)},ct);":
                            $"await orcRuntime.GrantAsync({item},{Literal(keyword)}{(a.TryGetProperty("value",out var v)?","+Number(v,item):"")},ct);";break;
                    }
                    case "moveUnit":Only(a,"op","target","to");if(T(a,"to") is not ("frontline" or "support"))throw Gap("移动落点");body=$"await orcRuntime.MoveAsync({item},{Literal(T(a,"to"))},ct);";break;
                    case "reveal":Only(a,"op","target");body=$"await orcRuntime.RevealAsync({item},ct);";break;
                    default:throw Gap("动作 "+op);
                }
                return $"foreach(var {item} in {query}) {{\n{body}\n}}";
            }
        }
    }
    private void Restore(KeyValuePair<string,(string Code,bool Cards)>[] scope){_vars.Clear();foreach(var v in scope)_vars.Add(v.Key,v.Value);}
    private bool IsCards(JsonElement e)=>e.ValueKind==JsonValueKind.Object&&T(e,"expr") is "query" or "union" || e.ValueKind==JsonValueKind.Object&&T(e,"expr")=="variable"&&_vars.TryGetValue(T(e,"name"),out var v)&&v.Cards;
    private string Cards(JsonElement e)
    {
        Only(e,T(e,"expr") switch{"query"=>["expr","query"],"variable"=>["expr","name"],"union"=>["expr","items"],_=>["expr"]});
        return T(e,"expr") switch {
        "query"=>Query(e.GetProperty("query")),
        "variable"=>_vars.TryGetValue(T(e,"name"),out var v)&&v.Cards?v.Code:throw Gap("对象变量 "+T(e,"name")),
        "union"=>$"new {Card}[][]{{{string.Join(",",e.GetProperty("items").EnumerateArray().Select(Cards))}}}.SelectMany(x=>x).Distinct().ToArray()",
        _=>throw Gap("对象表达式 "+T(e,"expr"))
        };
    }
    private string Query(JsonElement q)
    {
        if(q.ValueKind==JsonValueKind.String)return _vars.TryGetValue(q.GetString()!,out var named)&&named.Cards?named.Code:throw Gap("选靶引用 "+q.GetString());
        if(T(q,"sel")=="union")
        {
            Only(q,"sel","queries");
            return $"new {Card}[][]{{{string.Join(",",q.GetProperty("queries").EnumerateArray().Select(Query))}}}.SelectMany(x=>x).Distinct().ToArray()";
        }
        Only(q,"sel","side","zone","where","count","ref","excludeSelf");
        var sel=T(q,"sel");if(sel=="self")return $"new {Card}[]{{orcSelf}}";
        if(sel is "ref" or "refAll")return _vars.TryGetValue(T(q,"ref"),out var v)&&v.Cards?v.Code:throw Gap("选靶引用 "+T(q,"ref"));
        if(sel is not ("all" or "random"))throw Gap("选择方式 "+sel);
        var zone=T(q,"zone");var side=T(q,"side");string query;
        if(zone is "hand" or "deck")
        {
            if(side is not ("" or "friendly" or "self"))throw Gap("敌方私有区域");
            query=zone=="hand"?$"orcSelf.Owner.Hand.Cast<{Card}>().ToArray()":$"Enumerable.Range(0,orcSelf.Owner.Deck.Count).Select(i=>({Card})orcSelf.Owner.Deck.PeekInstance(i)).ToArray()";
        }
        else
        {
            if(zone is not ("" or "field" or "frontline" or "support" or "hq"))throw Gap("目标区域 "+zone);
            query=$"(await orcRuntime.SelectAsync(orcSelf,new Orc.Game.Effects.EffectSelector({Literal(sel)},{Literal(side.Length==0?"both":side)},{(zone is "frontline" or "support" or "hq"?Literal(zone):"null")},Count:{(q.TryGetProperty("count",out var count)?Number(count):"null")}),ct)).ToArray()";
        }
        if(q.TryGetProperty("where",out var where))
        {
            if(sel=="random"&&!(T(where,"test")=="all"&&where.GetProperty("items").GetArrayLength()==0))throw Gap("带筛选的随机选择");
            query+=$".Where(orcCandidate=>{Predicate(where,"orcCandidate")}).ToArray()";
        }
        if(q.TryGetProperty("excludeSelf",out var exclude)&&exclude.GetBoolean())query+=".Where(x=>!object.ReferenceEquals(x,orcSelf)).ToArray()";
        return query;
    }
    private string Number(JsonElement e,string candidate="orcSelf")
    {
        if(e.ValueKind==JsonValueKind.Number&&e.TryGetInt32(out var n))return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if(e.ValueKind!=JsonValueKind.Object)throw Gap("数值表达式 "+e.GetRawText());
        Only(e,T(e,"expr") switch{"count"=>["expr","query"],"size" or "literal"=>["expr","value"],"variable"=>["expr","name"],"read"=>["expr","of","path"],"math"=>["expr","operator","items"],_=>["expr"]});
        return T(e,"expr") switch
        {
            "count"=>$"({Query(e.GetProperty("query"))}).Length",
            "size"=>$"({Cards(e.GetProperty("value"))}).Length",
            "variable"=>_vars.TryGetValue(T(e,"name"),out var v)&&!v.Cards?v.Code:throw Gap("数值变量"),
            "read"=>Read(e,candidate),
            "math"=>Math(e,candidate),
            "literal"=>Number(e.GetProperty("value"),candidate),
            _=>throw Gap("数值表达式 "+T(e,"expr"))
        };
    }
    private string Math(JsonElement e,string candidate)
    {
        var symbol=T(e,"operator") switch{"add"=>"+","subtract"=>"-","multiply"=>"*",_=>throw Gap("算术 "+T(e,"operator"))};
        return "("+string.Join(symbol,e.GetProperty("items").EnumerateArray().Select(v=>"("+Number(v,candidate)+")"))+")";
    }
    private string Subject(string name,string candidate)=>name switch
    {
        "self"=>"orcSelf","candidate"=>candidate,
        _=>_vars.TryGetValue(name,out var v)&&v.Cards?$"({v.Code}).FirstOrDefault()":throw Gap("对象引用 "+name)
    };
    private string Read(JsonElement e,string candidate)
    {
        var property=T(e,"path");var of=T(e,"of");
        if(of is "friendlyPlayer" or "selfPlayer")return property switch{"kredits"=>"orcSelf.Owner.Points","maxKredits"=>"orcSelf.Owner.PointSlots",_=>throw Gap("玩家属性 "+property)};
        var subject=Subject(of,candidate);
        var stat=property switch{"attack"=>"Attack","defense"=>"Defense","cost"=>"DeployCost","opCost"=>"OperateCost",_=>null};
        if(stat is not null)
        {
            var read=$"(({subject} as {Base})?.Modifiers.GetEffectiveValue({Fields}.{stat}) ?? 0)";
            if(property=="defense"){var hq=Fresh();return $"({subject} is Orc.Game.Players.Hq {hq} ? {hq}.Health : {read})";}
            return read;
        }
        throw Gap("读取属性 "+property);
    }
    private string Value(JsonElement e,string candidate)
    {
        if(e.ValueKind==JsonValueKind.String)return Literal(e.GetString()!);
        if(e.ValueKind is JsonValueKind.True or JsonValueKind.False)return e.GetBoolean()?"true":"false";
        if(e.ValueKind==JsonValueKind.Object&&T(e,"expr")=="read")
        {
            Only(e,"expr","of","path");
            var subject=Subject(T(e,"of"),candidate);var property=T(e,"path");
            if(property=="unitType")return $"(({subject} as {Base})?.Definition.UnitTypes.FirstOrDefault() ?? \"\")";
            if(property=="extraTypes")return $"(({subject} as {Unit})?.GetData<Orc.Game.Cards.UnitStateData>().UnitTypes ?? new string[0])";
            if(property=="cardType")return $"({subject} is {Unit} ? \"unit\" : \"order\")";
            if(property=="name")return $"({subject}?.Name ?? \"\")";
        }
        return Number(e,candidate);
    }
    private string Predicate(JsonElement e,string candidate)
    {
        Only(e,T(e,"test") switch{"all" or "any"=>["test","items"],"not"=>["test","item"],"compare"=>["test","left","cmp","right"],_=>["test"]});
        switch(T(e,"test"))
        {
            case "all":case "any":
            {var children=e.GetProperty("items").EnumerateArray().Select(v=>"("+Predicate(v,candidate)+")").ToArray();return children.Length==0?(T(e,"test")=="all"?"true":"false"):string.Join(T(e,"test")=="all"?" && ":" || ",children);}
            case "not":return "!("+Predicate(e.GetProperty("item"),candidate)+")";
            case "compare":
            {
                var left=e.GetProperty("left");var right=e.GetProperty("right");var cmp=T(e,"cmp");
                if(cmp=="contains")
                {
                    if(T(left,"expr")=="read"&&T(left,"path")=="keywords"&&right.ValueKind==JsonValueKind.String)
                    {
                        Only(left,"expr","path","of");var local=Fresh();
                        return $"({Subject(T(left,"of"),candidate)} is {Base} {local} && {local}.Keywords.Has({Literal(NativeKeyword(right.GetString()!))}))";
                    }
                    if(T(left,"expr")=="read"&&T(left,"path")=="extraTypes"&&right.ValueKind==JsonValueKind.String)
                    {
                        Only(left,"expr","path","of");var local=Fresh();
                        return $"({Subject(T(left,"of"),candidate)} is {Unit} {local} && {local}.GetData<Orc.Game.Cards.UnitStateData>().UnitTypes.Contains({Literal(right.GetString()!)}))";
                    }
                    throw Gap("集合比较");
                }
                if(cmp is not ("==" or "!=" or ">" or "<" or ">=" or "<="))throw Gap("比较 "+cmp);
                return $"({Value(left,candidate)} {cmp} {Value(right,candidate)})";
            }
            default:throw Gap("条件 "+T(e,"test"));
        }
    }
    private static string NativeKeyword(string token)
    {
        if(WorkshopAuthoring.TryKeyword(token,out var custom))return custom;
        var keywords=DesktopOrc.Catalog.GetProperty("keywords");
        var cn=keywords.TryGetProperty(token,out var item)&&item.TryGetProperty("cn",out var label)?label.GetString()!:token;
        if(!KeywordIds.All.Contains(cn,StringComparer.Ordinal))throw Gap("词条 "+cn);return cn;
    }
}
