using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
namespace Kards.Ui.OrcBridge;

/// <summary>Lossless authoring declaration; independent of UI transport and rule execution.</summary>
public static class InspectorCardRecord
{
    public const string NativeKey="$nativeDraft", LegacyKey="$legacyDraft", SourceMarker="$orcEditorSource";
    public const string TextModeKey="$textSyncMode";
    public static bool IsNative(JsonNode? node)=>node is JsonObject o&&o[SourceMarker]?.ToString()=="native-v1"&&o[NativeKey] is JsonObject;
    public static bool IsLegacy(JsonNode? node)=>node is JsonObject o&&o[SourceMarker]?.ToString()=="legacy-v1"&&o[LegacyKey] is JsonObject;
    public static string EffectLabel(JsonNode? node)
    {
        if(IsNative(node))
        {
            var draft=node![NativeKey]!;var template=draft["Template"]?.ToString();var guard=draft["Guard"]?.ToString();
            return OrcEffectEditing.Templates.FirstOrDefault(t=>t.Id==template&&t.Guard.ToString()==guard)?.Label??(draft["PrefabJson"] is not null?"原生预制效果":"原生效果");
        }
        if(IsLegacy(node))return "兼容效果 · "+(DiyCardCompiler.Operations.FirstOrDefault(o=>o.Id==node![LegacyKey]!["Op"]?.ToString())?.Label??"原有动作");
        return node?["trigger"]?.ToString() is {Length:>0} trigger?InspectorPresentation.Timing(trigger):"选择发生时机";
    }
    public static DiyCardDraft ApplyRecord(DiyCardDraft draft,JsonElement record)
    {
        var normalized=JsonNode.Parse(record.GetRawText())!;SynchronizeText(normalized);
        using var normalizedDocument=JsonDocument.Parse(normalized.ToJsonString());record=normalizedDocument.RootElement;
        var effects=record.GetProperty("effects");
        if(effects.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("效果必须是数组");
        var program=new List<OrcEffectDraft>();var legacy=new List<DiyEffectDraft>();var desktop=new JsonArray();
        foreach(var effect in effects.EnumerateArray())
        {
            var source=effect.TryGetProperty(SourceMarker,out var marker)&&marker.ValueKind==JsonValueKind.String?marker.GetString():null;
            if(source=="native-v1"&&effect.TryGetProperty(NativeKey,out var native)&&native.ValueKind==JsonValueKind.Object)
            {
                var parsed=JsonSerializer.Deserialize<OrcEffectDraft>(native.GetRawText())??throw new InvalidDataException("原生效果格式无效");
                if(parsed.DesktopJson is not null)throw new InvalidDataException("原生效果不能混入其他声明来源");
                program.Add(parsed);
            }
            else if(source=="legacy-v1"&&effect.TryGetProperty(LegacyKey,out var old)&&old.ValueKind==JsonValueKind.Object)
                legacy.Add(JsonSerializer.Deserialize<DiyEffectDraft>(old.GetRawText())??throw new InvalidDataException("旧效果格式无效"));
            else
            {
                program.Add(new OrcEffectDraft{Template=effect.TryGetProperty("trigger",out var t)?t.GetString()??"unknown":"unknown",DesktopJson=effect.GetRawText()});
                desktop.Add(JsonNode.Parse(effect.GetRawText()));
            }
        }
        // Editor envelopes are never submitted to the Desktop compiler as gameplay fields.
        var stored=JsonNode.Parse(record.GetRawText())!.AsObject();stored["effects"]=desktop;
        stored.Remove(TextModeKey);
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
            EffectText=Str("text",draft.EffectText),TextSyncMode=Str(TextModeKey,draft.TextSyncMode),Effects=legacy.ToArray(),UseStructuredEffects=true,
            Nation=Str("nation",Str("set",draft.Nation)) switch{"苏联"=>"av76","德国"=>"deran","美国"=>"USG","英国"=>"UN",var value=>value},
            Rarity=Str("rarity",draft.Rarity),Flavor=Str("flavor",draft.Flavor),
            Keywords=keywords,Armor=record.TryGetProperty("kwValues",out var parameters)&&parameters.TryGetProperty("armor",out var armor)?armor.GetInt32():draft.Armor,
            OrcEffects=program.ToArray(),
            DesktopCardJson=stored.ToJsonString()
        };
        Kards.Ui.Core.DiyCardLibrary.CheckShape(updated);return updated;
    }
    public static JsonObject FromDraft(DiyCardDraft draft)
    {
        var record=draft.DesktopCardJson is { } json ? JsonNode.Parse(json)!.AsObject() : new JsonObject();
        record["id"]=draft.Id;record["name"]=draft.Name;record["cardType"]=draft.CardType;record["unitType"]=draft.UnitType;
        record["cost"]=draft.Cost;record["opCost"]=draft.OpCost;record["attack"]=draft.Attack;record["defense"]=draft.Defense;
        record["text"]=draft.EffectText;record["nation"]=draft.Nation;record["rarity"]=draft.Rarity;record["flavor"]=draft.Flavor;
        record[TextModeKey]=draft.TextSyncMode;
        record["effects"]=new JsonArray(draft.OrcEffects.Select(e=>e.DesktopJson is not null?JsonNode.Parse(e.DesktopJson):new JsonObject{[SourceMarker]="native-v1",[NativeKey]=JsonSerializer.SerializeToNode(e)})
            .Concat(draft.Effects.Select(e=>(JsonNode?)new JsonObject{[SourceMarker]="legacy-v1",[LegacyKey]=JsonSerializer.SerializeToNode(e)})).ToArray());
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
    public static void SynchronizeText(JsonNode node)
    {
        if(node is not JsonObject record||record[TextModeKey]?.ToString()!="linked")return;
        try
        {
            var effects=record["effects"] as JsonArray??new();
            if(effects.Any(e=>!IsNative(e))){record[TextModeKey]="detached";return;}
            var program=effects.Select(e=>JsonSerializer.Deserialize<OrcEffectDraft>(e![NativeKey]!.ToJsonString())!).ToArray();
            if(BackendTextAuthoring.TryWrite(record["cardType"]?.ToString()??"unit",program,out var text))record["text"]=text;
            else record[TextModeKey]="detached";
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or InvalidDataException){record[TextModeKey]="detached";}
    }
}
