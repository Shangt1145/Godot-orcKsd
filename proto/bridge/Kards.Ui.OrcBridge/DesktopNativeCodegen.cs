using System.Text.Json;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

/// <summary>Compiles structured expressions/control flow into native EffectRuntime calls.
/// This is code generation, never a second match simulator. User strings are emitted only as literals.</summary>
internal sealed class DesktopNativeCodegen
{
    private const string Card="Orc.Cards.Card",Base="Orc.Game.Cards.CardBase",Unit="Orc.Game.Cards.UnitCard",Fields="Orc.Game.Cards.CardStatFields";
    private const string Runtime="Orc.Game.Effects.AuthoringRuntime";
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
            _vars.TryAdd("target",(local,true));
            selections.Add($"var {local}=await Orc.Game.Effects.AuthoringTargetContext.SelectAsync(orcSelf,{Literal(target.GetRawText())},{Literal(DesktopTargetContext.Scope(graph))},ct);");
        }
        var normalized=DesktopOrc.Request(new {method="nativeActions",actions=graph.GetProperty("actions")});
        if(T(graph,"trigger")=="passive")
        {
            if(graph.TryGetProperty("condition",out _)||graph.TryGetProperty("filter",out _))throw Gap("持续效果的附加条件");
            Orc.Game.Effects.AuthoringAuraRuntime.Validate(normalized);
            return $"if(self is {Card} orcAuraHost) await Orc.Game.Effects.AuthoringAuraRuntime.RegisterAsync(orcAuraHost,view,{Literal(normalized.GetRawText())},ct);";
        }
        var body=Actions(normalized);
        if(graph.TryGetProperty("condition",out var condition))body=$"if({Predicate(condition,"orcSelf")}){{{body}}}"+(graph.TryGetProperty("else",out var alternative)?$"else{{{Actions(alternative)}}}":"");
        return $"var orcRuntime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);\nif (orcRuntime is not null && self is {Card} orcSelf && {Runtime}.ShouldRun(orcSelf,view,{Literal(graph.GetRawText())}))\n{{\n{string.Join("\n",selections)}\n{body}\n}}";
    }
    private string Actions(JsonElement actions)=>string.Join("\n",actions.EnumerateArray().Select(Action));
    private string Action(JsonElement a)
    {
        var op=T(a,"op");
        var alias=DesktopNativeAliases.Rewrite(a);
        if(alias is not null)return Actions(alias.Value);
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
                Only(a,"op","side","as","target");
                if(a.TryGetProperty("target",out var filter))
                {
                    var drawn=Fresh();if(T(a,"as").Length>0)_vars[T(a,"as")]=(drawn,true);
                    return $"var {drawn}=await {Runtime}.DrawSpecificAsync(orcSelf,{Query(filter)},ct);";
                }
                var viewer=$"{Runtime}.Viewer(orcSelf,{Literal(T(a,"side"))})";
                var name=T(a,"as");if(name.Length==0)return $"await orcRuntime.DrawAsync({viewer},1,ct);";
                var before=Fresh();var result=Fresh();_vars[name]=(result,true);
                return $"var {before}={Runtime}.Owner({viewer}).Hand.Cast<{Card}>().ToArray(); await orcRuntime.DrawAsync({viewer},1,ct); var {result}={Runtime}.Owner({viewer}).Hand.Cast<{Card}>().Except({before}).ToArray();";
            }
            case "discardOne":
                Only(a,"op","side","target");return $"foreach(var orcDiscard in {(a.TryGetProperty("target",out var discarded)?Query(discarded):$"{Runtime}.Pick(orcSelf,{Runtime}.Pile(orcSelf,{Literal(T(a,"side"))},\"hand\"),\"random\",1)")}) await {Runtime}.DiscardAsync(orcSelf,orcDiscard,ct);";
            case "choice":
            {
                Only(a,"op","prompt","options");var options=a.GetProperty("options").EnumerateArray().ToArray();var scope=_vars.ToArray();var selected=Fresh();
                var bodies=options.Select((option,index)=>{Only(option,"label","actions");Restore(scope);return $"case {index}:{{{Actions(option.GetProperty("actions"))} break;}}";}).ToArray();Restore(scope);
                return $"var {selected}=await {Runtime}.ChooseOptionAsync(orcSelf,new string[]{{{string.Join(",",options.Select(o=>Literal(T(o,"label"))))}}},ct);switch({selected}){{{string.Join("\n",bodies)}}}";
            }
            case "threeChoice":case "developCard":
            {
                Only(a,"op","pool","mode","side","as","prompt");var pool=a.GetProperty("pool");Only(pool,"source","cardIds","includeTokens","includeReserved");
                if(T(pool,"source") is not ("pool" or "cards"))throw Gap("候选卡池来源 "+T(pool,"source"));
                foreach(var flag in new[]{"includeTokens","includeReserved"})if(pool.TryGetProperty(flag,out var include)&&include.GetBoolean())throw Gap("候选卡池选项 "+flag);
                var ids=pool.TryGetProperty("cardIds",out var members)?members.EnumerateArray().Select(v=>Literal(v.GetString()!)).ToArray():[];
                var local=Fresh();if(T(a,"as").Length>0)_vars[T(a,"as")]=(local,true);
                return $"var {local}=await {Runtime}.DevelopAsync(orcSelf,new string[]{{{string.Join(",",ids)}}},{Literal(T(a,"side"))},{(op=="developCard"||T(a,"mode")=="random"?"true":"false")},ct);";
            }
            case "selectObjects":
            {
                Only(a,"op","target","as","count","prompt","reveal","asCopies");
                if(a.TryGetProperty("asCopies",out var copies)&&copies.GetBoolean())throw Gap("复制候选卡牌的选择");
                var candidates=Query(a.GetProperty("target"));var local=Fresh();_vars[T(a,"as")]=(local,true);
                return $"var {local}=await {Runtime}.ChooseAsync(orcSelf,{candidates},{Number(a.GetProperty("count"))},ct); if({local}.Length==0)return;";
            }
            case "createCard":case "createUnit":case "addCardToHand":case "summon":case "shuffleIn":
            {
                Only(a,"op","cardId","name","side","to","count","as");
                var id=a.TryGetProperty("cardId",out var identity)?Value(identity,"orcSelf"):Literal(T(a,"name"));
                var destination=op=="shuffleIn"?"deck":op is "summon" or "createUnit"?(T(a,"to").Length>0?T(a,"to"):"support"):"hand";
                var expression=$"await {Runtime}.CreateAsync(orcSelf,{id},{Literal(T(a,"side"))},{Literal(destination)},{(a.TryGetProperty("count",out var n)?Number(n):"1")},ct)";
                var variable=T(a,"as");if(variable.Length==0)return expression+";";
                var local=Fresh();_vars[variable]=(local,true);return $"var {local}={expression};";
            }
            case "changeResource":
            {
                Only(a,"op","side","mode","property","value");if(T(a,"mode") is not ("add" or "set"))throw Gap("指挥资源模式");
                var value=Number(a.GetProperty("value"));var local=Fresh();
                var methods=T(a,"property") switch{"kredits"=>("GainPointsAsync","LosePointsAsync"),"maxKredits"=>("GainPointSlotsAsync","LosePointSlotsAsync"),_=>throw Gap("资源 "+T(a,"property"))};
                var viewer=$"{Runtime}.Viewer(orcSelf,{Literal(T(a,"side"))})";
                if(T(a,"mode")=="set")value=$"({value})-{Runtime}.Owner({viewer}).{(T(a,"property")=="kredits"?"Points":"PointSlots")}";
                return $"var {local}={value}; if({local}>=0) await orcRuntime.{methods.Item1}({viewer},{local},ct); else await orcRuntime.{methods.Item2}({viewer},-{local},ct);";
            }
            default:
            {
                if(!a.TryGetProperty("target",out var target))throw Gap("动作 "+op);
                var query=Query(target);var item=Fresh();string body;
                switch(op)
                {
                    case "dealDamage":Only(a,"op","target","amount");body=$"await orcRuntime.DamageAsync({item},{Number(a.GetProperty("amount"),item)},orcSelf,ct);";break;
                    case "destroyUnit":Only(a,"op","target");body=$"await orcRuntime.KillAsync({item},orcSelf,ct);";break;
                    case "restoreHealth":Only(a,"op","target","amount");body=$"await {Runtime}.HealAsync({item},{Number(a.GetProperty("amount"),item)},ct);";break;
                    case "returnUnit":case "leaveUnit":Only(a,"op","target");body=$"await {Runtime}.ReturnAsync(orcSelf,{item},{(op=="returnUnit"?"true":"false")},ct);";break;
                    case "battleUnits":Only(a,"op","target","with");body=$"foreach(var orcOpponent in {Query(a.GetProperty("with"))}) await orcRuntime.BattleAsync({item},orcOpponent,orcSelf,ct);";break;
                    case "bindEvent":case "grantEffect":
                    {
                        Only(a,"op","target","effect");var nested=DesktopNativeLowering.Lower(a.GetProperty("effect").GetRawText());
                        var key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a.GetProperty("effect").GetRawText())))[..16];
                        var snapshot=OrcImportService.CompileOrcEffect("diy.grant."+key,nested).JsonText;
                        body=$"var orcAttached=await orcRuntime.AttachSnapshotAsync({item},{Literal(snapshot)},ct); if(!orcAttached.IsSuccess)throw new InvalidOperationException(orcAttached.ToString());";break;
                    }
                    case "suppressUnit":Only(a,"op","target");body=$"await orcRuntime.SilenceAsync({item},ct);";break;
                    case "setPinned":
                        Only(a,"op","target","enabled","turns");if(a.TryGetProperty("turns",out var turns)&&turns.GetInt32()!=1)throw Gap("压制期限");
                        body=a.GetProperty("enabled").GetBoolean()?$"await orcRuntime.PinAsync({item},ct);":$"await orcRuntime.RevokeAsync({item},{Literal(KeywordIds.Suppressed)},ct);";break;
                    case "changeAttribute":
                    {
                        Only(a,"op","target","property","mode","value","duration");
                        var duration=T(a,"duration") switch{"" or "permanent"=>"Orc.Game.Effects.EffectDuration.Permanent","turn"=>"Orc.Game.Effects.EffectDuration.TurnEnd",_=>throw Gap("持续时间 "+T(a,"duration"))};
                        var value=Number(a.GetProperty("value"),item);var property=T(a,"property");var mode=T(a,"mode");
                        if(mode is not ("add" or "set")||property is not ("attack" or "defense" or "opCost" or "cost"))throw Gap("属性或写入模式 "+property+"/"+mode);
                        body=$"await {Runtime}.AttributeAsync({item},{Literal(property)},{Literal(mode)},{value},{Literal(T(a,"duration"))},ct);";break;
                    }
                    case "setKeyword":
                    {
                        Only(a,"op","target","keyword","enabled","value","duration");if(T(a,"duration") is not ("" or "permanent"))throw Gap("词条期限");var keyword=NativeKeyword(T(a,"keyword"));
                        body=a.TryGetProperty("enabled",out var enabled)&&!enabled.GetBoolean()?$"await orcRuntime.RevokeAsync({item},{Literal(keyword)},ct);":
                            $"await orcRuntime.GrantAsync({item},{Literal(keyword)}{(a.TryGetProperty("value",out var v)?","+Number(v,item):"")},ct);";break;
                    }
                    case "moveUnit":Only(a,"op","target","to");if(T(a,"to") is not ("frontline" or "support"))throw Gap("移动落点");body=$"await orcRuntime.MoveAsync({item},{Literal(T(a,"to"))},ct);";break;
                    case "reveal":Only(a,"op","target");body=$"await orcRuntime.RevealAsync({item},ct);";break;
                    case "upgradeSelf":Only(a,"op","target");body=$"await orcRuntime.UpgradeAsync({item},ct);";break;
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
        if(q.ValueKind==JsonValueKind.String)return q.GetString()=="self"?$"new {Card}[]{{orcSelf}}":_vars.TryGetValue(q.GetString()!,out var named)&&named.Cards?named.Code:throw Gap("选靶引用 "+q.GetString());
        if(T(q,"sel")=="union")
        {
            Only(q,"sel","queries");
            return $"new {Card}[][]{{{string.Join(",",q.GetProperty("queries").EnumerateArray().Select(Query))}}}.SelectMany(x=>x).Distinct().ToArray()";
        }
        Only(q,"sel","side","zone","where","count","ref","excludeSelf","filter","includeAnchor");
        var sel=T(q,"sel");if(sel=="self")return $"new {Card}[]{{orcSelf}}";
        if(sel is "ref" or "refAll")
        {
            var reference=T(q,"ref");
            return reference is "self" or "source"?$"new {Card}[]{{orcSelf}}":reference is "eventUnit" or "eventCard" or "dead" or "attacker"?$"new {Card}[]{{{Runtime}.EventCard(view)}}.Where(c=>c is not null).ToArray()":
                _vars.TryGetValue(reference,out var v)&&v.Cards?v.Code:throw Gap("选靶引用 "+reference);
        }
        if(sel=="adjacent")
        {
            var anchors=T(q,"ref");var source=anchors is "" or "self"?$"new {Card}[]{{orcSelf}}":_vars.TryGetValue(anchors,out var anchor)?anchor.Code:throw Gap("相邻单位的参照");
            var adjacent=$"{Runtime}.Adjacent({source},{(q.TryGetProperty("includeAnchor",out var include)&&include.GetBoolean()?"true":"false")})";
            if(q.TryGetProperty("where",out var predicate))adjacent+=$".Where(orcCandidate=>{Predicate(predicate,"orcCandidate")}).ToArray()";
            return adjacent;
        }
        if(sel is not ("all" or "random" or "first" or "last"))throw Gap("选择方式 "+sel);
        var zone=T(q,"zone");var side=T(q,"side");string query;
        if(zone is "hand" or "deck")
        {
            query=$"{Runtime}.Pile(orcSelf,{Literal(side)},{Literal(zone)})";
        }
        else
        {
            if(zone is not ("" or "field" or "frontline" or "support" or "hq"))throw Gap("目标区域 "+zone);
            query=$"(await orcRuntime.SelectAsync(orcSelf,new Orc.Game.Effects.EffectSelector(\"all\",{Literal(side.Length==0?"both":side)},{(zone is "frontline" or "support" or "hq"?Literal(zone):"null")}),ct)).ToArray()";
        }
        if(q.TryGetProperty("where",out var where))
        {
            query+=$".Where(orcCandidate=>{Predicate(where,"orcCandidate")}).ToArray()";
        }
        if(q.TryGetProperty("filter",out var filter)){DesktopEffectRuntime.ValidateFilter(filter);query+=$".Where(orcCandidate=>{Runtime}.Matches(orcCandidate,{Literal(filter.GetRawText())})).ToArray()";}
        if(q.TryGetProperty("excludeSelf",out var exclude)&&exclude.GetBoolean())query+=".Where(x=>!object.ReferenceEquals(x,orcSelf)).ToArray()";
        if(sel is "random" or "first" or "last")query=$"{Runtime}.Pick(orcSelf,{query},{Literal(sel)},{(q.TryGetProperty("count",out var take)?Number(take):"1")})";
        return query;
    }
    private string Number(JsonElement e,string candidate="orcSelf")
    {
        if(e.ValueKind==JsonValueKind.Number&&e.TryGetInt32(out var n))return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if(e.ValueKind!=JsonValueKind.Object)throw Gap("数值表达式 "+e.GetRawText());
        Only(e,T(e,"expr") switch{"count"=>["expr","query"],"size" or "literal"=>["expr","value"],"variable"=>["expr","name"],"read"=>["expr","of","path"],"math"=>["expr","operator","items"],"random"=>["expr","min","max"],_=>["expr"]});
        return T(e,"expr") switch
        {
            "count"=>$"({Query(e.GetProperty("query"))}).Length",
            "size"=>$"({Cards(e.GetProperty("value"))}).Length",
            "variable"=>_vars.TryGetValue(T(e,"name"),out var v)&&!v.Cards?v.Code:throw Gap("数值变量"),
            "read"=>Read(e,candidate),
            "math"=>Math(e,candidate),
            "literal"=>Number(e.GetProperty("value"),candidate),
            "random"=>$"{Runtime}.Random(orcSelf,{Number(e.GetProperty("min"),candidate)},{Number(e.GetProperty("max"),candidate)})",
            _=>throw Gap("数值表达式 "+T(e,"expr"))
        };
    }
    private string Math(JsonElement e,string candidate)
    {
        var symbol=T(e,"operator") switch{"add"=>"+","subtract"=>"-","multiply"=>"*",_=>throw Gap("算术 "+T(e,"operator"))};
        return "("+string.Join(symbol,e.GetProperty("items").EnumerateArray().Select(v=>"("+Number(v,candidate)+")"))+")";
    }
    private string Subject(string name,string candidate)=>_vars.TryGetValue(name,out var variable)&&variable.Cards?$"({variable.Code}).FirstOrDefault()":name switch
    {
        "self" or "source"=>"orcSelf","candidate" or "current"=>candidate,
        "eventUnit" or "eventCard" or "dead" or "attacker"=>$"{Runtime}.EventCard(view)",
        "target" or "defender" or "attackTarget"=>$"{Runtime}.EventTarget(view)",
        _=>_vars.TryGetValue(name,out var v)&&v.Cards?$"({v.Code}).FirstOrDefault()":throw Gap("对象引用 "+name)
    };
    private string Read(JsonElement e,string candidate)
    {
        var property=T(e,"path");var of=T(e,"of");
        if(of is "friendlyPlayer" or "selfPlayer" or "enemyPlayer")return property switch{"kredits"=>$"{Runtime}.Owner({Runtime}.Viewer(orcSelf,{Literal(of=="enemyPlayer"?"enemy":"self")})).Points","maxKredits"=>$"{Runtime}.Owner({Runtime}.Viewer(orcSelf,{Literal(of=="enemyPlayer"?"enemy":"self")})).PointSlots",_=>throw Gap("玩家属性 "+property)};
        var subject=Subject(of,candidate);
        var stat=property switch{"attack"=>"Attack","defense"=>"Defense","cost"=>"DeployCost","opCost"=>"OperateCost",_=>null};
        if(stat is not null)
        {
            var read=$"(({subject} as {Base})?.Modifiers.GetEffectiveValue({Fields}.{stat}) ?? 0)";
            if(property=="defense"){var hq=Fresh();return $"({subject} is Orc.Game.Players.Hq {hq} ? {hq}.Health : {read})";}
            return read;
        }
        if(property is "maxDefense" or "missingDefense" or "owner" or "printed.attack" or "printed.defense")return $"{Runtime}.Int({subject},{Literal(property)})";
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
            if(property is "set" or "zone" or "uid")return $"{Runtime}.Text({subject},{Literal(property)})";
            if(property is "alive" or "damaged" or "pinned")return $"{Runtime}.Flag({subject},{Literal(property)})";
        }
        return Number(e,candidate);
    }
    private string Predicate(JsonElement e,string candidate)
    {
        if(e.TryGetProperty("op",out _))return LegacyPredicate(e,candidate);
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
    private string LegacyPredicate(JsonElement e,string candidate)
    {
        var op=T(e,"op");
        if(op=="predicate")return Predicate(e.GetProperty("test"),candidate);
        if(op is "and" or "or")return string.Join(op=="and"?" && ":" || ",e.GetProperty("items").EnumerateArray().Select(x=>"("+Predicate(x,candidate)+")"));
        if(op=="not")return "!("+Predicate(e.GetProperty("item"),candidate)+")";
        if(op is "targetPinned" or "targetDead" or "targetIsHQ")
        {
            var target=Subject("target",candidate);return op=="targetIsHQ"?$"({target} is Orc.Game.Players.Hq)":op=="targetPinned"?$"{Runtime}.Flag({target},\"pinned\")":$"!{Runtime}.Flag({target},\"alive\")";
        }
        var comparison=T(e,"cmp");if(comparison.Length==0)comparison=">=";
        if(comparison is not ("==" or "!=" or "<" or ">" or "<=" or ">="))throw Gap("条件比较 "+comparison);
        if(op is "unitCount" or "controlsType")
        {
            var node=e.TryGetProperty("spec",out var spec)?System.Text.Json.Nodes.JsonNode.Parse(spec.GetRawText())!.AsObject():new System.Text.Json.Nodes.JsonObject{["side"]=T(e,"side") is "enemy"?"enemy":"friendly"};
            node["sel"]="all";if(e.TryGetProperty("filter",out var filter))node["filter"]=System.Text.Json.Nodes.JsonNode.Parse(filter.GetRawText());
            return $"({Query(JsonSerializer.SerializeToElement(node))}.Length {comparison} {Number(e.GetProperty("value"))})";
        }
        if(op=="compare")
        {
            var left=e.GetProperty("left");string value;
            if(left.TryGetProperty("stat",out var stat))value=$"{Runtime}.Int({Subject(T(left,"of"),candidate)},{Literal(stat.GetString()!)})";
            else value=Value(left,candidate);
            return $"({value} {comparison} {Number(e.GetProperty("value"),candidate)})";
        }
        throw Gap("条件 "+op);
    }
    private static string NativeKeyword(string token)
    {
        if(WorkshopAuthoring.TryKeyword(token,out var custom))return custom;
        var cn=DesktopEffectRuntime.Keyword(token);
        if(!KeywordIds.All.Contains(cn,StringComparer.Ordinal))throw Gap("词条 "+cn);return cn;
    }
}
