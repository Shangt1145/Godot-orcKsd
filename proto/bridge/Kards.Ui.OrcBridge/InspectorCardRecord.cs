using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
namespace Kards.Ui.OrcBridge;

/// <summary>Lossless authoring declaration; independent of UI transport and rule execution.</summary>
public static class InspectorCardRecord
{
    public static DiyCardDraft ApplyRecord(DiyCardDraft draft,JsonElement record)
    {
        var effects=record.GetProperty("effects");
        if(effects.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("效果必须是数组");
        string Str(string key,string fallback)=>record.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:fallback;
        int Num(string key,int fallback)=>record.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number?v.GetInt32():fallback;
        var keywords=draft.Keywords;
        if(record.TryGetProperty("kwMap",out var switches))keywords=switches.EnumerateObject().Where(p=>p.Value.ValueKind==JsonValueKind.True).Select(p=>
        {
            var words=DesktopOrc.Catalog.GetProperty("keywords");var name=words.TryGetProperty(p.Name,out var item)&&item.TryGetProperty("cn",out var label)?label.GetString()!:p.Name;
            return DiyCardCompiler.KeywordToken(name);
        }).ToArray();
        var updated=draft with
        {
            Name=Str("name",draft.Name),CardType=Str("cardType",draft.CardType),UnitType=Str("unitType",draft.UnitType),
            Cost=Num("cost",draft.Cost),OpCost=Num("opCost",draft.OpCost),Attack=Num("attack",draft.Attack),Defense=Num("defense",draft.Defense),
            EffectText=Str("text",draft.EffectText),Effects=[],
            Nation=Str("nation",Str("set",draft.Nation)) switch{"苏联"=>"av76","德国"=>"deran","美国"=>"USG","英国"=>"UN",var value=>value},
            Rarity=Str("rarity",draft.Rarity),Flavor=Str("flavor",draft.Flavor),
            Keywords=keywords,Armor=record.TryGetProperty("kwValues",out var parameters)&&parameters.TryGetProperty("armor",out var armor)?armor.GetInt32():draft.Armor,
            OrcEffects=effects.EnumerateArray().Select(e=>new OrcEffectDraft {Template=e.TryGetProperty("trigger",out var t)?t.GetString()??"unknown":"unknown",DesktopJson=e.GetRawText()}).ToArray(),
            DesktopCardJson=record.GetRawText()
        };
        Kards.Ui.Core.DiyCardLibrary.CheckShape(updated);return updated;
    }
    public static JsonObject FromDraft(DiyCardDraft draft)
    {
        var record=draft.DesktopCardJson is { } json ? JsonNode.Parse(json)!.AsObject() : new JsonObject();
        record["id"]=draft.Id;record["name"]=draft.Name;record["cardType"]=draft.CardType;record["unitType"]=draft.UnitType;
        record["cost"]=draft.Cost;record["opCost"]=draft.OpCost;record["attack"]=draft.Attack;record["defense"]=draft.Defense;
        record["text"]=draft.EffectText;record["nation"]=draft.Nation;record["rarity"]=draft.Rarity;record["flavor"]=draft.Flavor;
        record["effects"]=new JsonArray(draft.OrcEffects.Where(e=>e.DesktopJson is not null).Select(e=>JsonNode.Parse(e.DesktopJson!)).ToArray());
        record["kwMap"]??=new JsonObject();record["kwValues"]??=new JsonObject();
        var words=DesktopOrc.Catalog.GetProperty("keywords");
        foreach(var token in draft.Keywords)
        {
            var key=words.EnumerateObject().Where(p=>p.Name==token||DiyCardCompiler.KeywordToken(p.Value.GetProperty("cn").GetString()!)==token).Select(p=>p.Name).FirstOrDefault()??token;
            if(record["kwMap"]![key] is null)record["kwMap"]![key]=true;
        }
        if(draft.Keywords.Contains("armor")&&record["kwValues"]!["armor"] is null)record["kwValues"]!["armor"]=draft.Armor;
        return record;
    }
}
