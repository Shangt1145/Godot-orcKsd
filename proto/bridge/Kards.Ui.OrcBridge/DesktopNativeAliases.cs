using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kards.Ui.OrcBridge;

/// <summary>Equivalent legacy spellings lower to the same native primitives, with strict field checks.</summary>
internal static class DesktopNativeAliases
{
    public static JsonElement? Rewrite(JsonElement value)
    {
        var op=value.GetProperty("op").GetString();var a=JsonNode.Parse(value.GetRawText())!.AsObject();
        JsonNode? Get(string key,JsonNode? fallback=null)=>a[key]?.DeepClone()??fallback;
        JsonNode Target()=>Get("target",JsonNode.Parse("{\"sel\":\"self\"}"))!;
        JsonElement Done(params JsonObject[] actions)=>JsonSerializer.SerializeToElement(actions);
        void Only(params string[] fields)
        {foreach(var p in a)if(p.Key!="op"&&!fields.Contains(p.Key))throw new InvalidDataException("动作参数尚未接入："+op+"."+p.Key);}
        switch(op)
        {
            case "heal":case "healAll":case "damage":case "damageAll":case "destroy":case "destroyAll":case "silence":case "pin":case "unpin":case "returnToHand":case "retreat":case "removeUnit":
            {
                Only("target","amount");var mapped=op switch{"heal" or "healAll"=>"restoreHealth","damage" or "damageAll"=>"dealDamage","destroy" or "destroyAll"=>"destroyUnit","silence"=>"suppressUnit","pin" or "unpin"=>"setPinned","removeUnit"=>"leaveUnit",_=>"returnUnit"};
                var action=new JsonObject{["op"]=mapped,["target"]=Target()};if(mapped is "restoreHealth" or "dealDamage")action["amount"]=Get("amount",JsonValue.Create(1));
                if(mapped=="setPinned"){action["enabled"]=op=="pin";action["turns"]=1;}return Done(action);
            }
            case "gainKredits":case "loseKredits":case "gainKreditSlot":case "loseKreditSlots":case "setKreditSlots":
                Only("side","amount","value");return Done(new JsonObject{["op"]="changeResource",["side"]=Get("side",JsonValue.Create("self")),["property"]=op!.Contains("Slot")?"maxKredits":"kredits",["mode"]=op=="setKreditSlots"?"set":"add",["value"]=op.StartsWith("lose")?new JsonObject{["expr"]="math",["operator"]="multiply",["items"]=new JsonArray(JsonValue.Create(-1),Get("amount",JsonValue.Create(1)))}:Get("amount",Get("value",JsonValue.Create(1)))});
            case "setDefense":case "setOpCost":case "opCostMod":
                Only("target","amount","value","attack","defense","duration");
                return Done(new JsonObject{["op"]="changeAttribute",["target"]=Target(),["property"]=op=="setDefense"?"defense":"opCost",["mode"]=op!.StartsWith("set")?"set":"add",["value"]=Get("value",Get("amount",JsonValue.Create(1))),["duration"]=Get("duration",JsonValue.Create("permanent"))});
            case "healHQ":case "hqMaxUp":
                Only("side","amount");return Done(new JsonObject{["op"]="restoreHealth",["target"]=new JsonObject{["sel"]="all",["zone"]="hq",["side"]=Get("side",JsonValue.Create("friendly"))},["amount"]=Get("amount",JsonValue.Create(1))});
            case "move":
                Only("target","to");return Done(new JsonObject{["op"]="moveUnit",["target"]=Target(),["to"]=Get("to",JsonValue.Create("support"))});
            case "fight":
                Only("target","with");return Done(new JsonObject{["op"]="battleUnits",["target"]=Target(),["with"]=Get("with",JsonNode.Parse("{\"sel\":\"all\",\"side\":\"enemy\",\"zone\":\"field\"}"))});
            case "draw":case "discard":
                Only("side","count");return Done(new JsonObject{["op"]="loop",["times"]=Get("count",JsonValue.Create(1)),["actions"]=new JsonArray(new JsonObject{["op"]=op=="draw"?"drawOne":"discardOne",["side"]=Get("side",JsonValue.Create("self"))})});
        }
        return null;
    }
}
