using System.Text.Json;
using Orc.Game.EffectParsing.Dsl;
using Kards.Ui.OrcBridge.OrcText;

namespace Kards.Ui.OrcBridge;

/// <summary>Admission from the lossless Desktop graph into the actual native DSL.
/// Every field must be accounted for. Unsupported graphs stay editable drafts.</summary>
internal static class DesktopNativeLowering
{
    public static OrcParsedEffect Lower(string json)
    {
        using var document=JsonDocument.Parse(json);
        var graph=document.RootElement;
        Fields(graph,"effect","trigger","actions","targets","selfOnly","eventSide","condition","else","once","oncePerTurn","listenZone","filter","watchOpponent");
        if(graph.TryGetProperty("filter",out var filter))DesktopEffectRuntime.ValidateFilter(filter);
        var trigger=Text(graph,"trigger");
        var entry=trigger switch
        {
            "deploy"=>OrcEffectEditing.Templates[0],
            "play" or "order"=>OrcEffectEditing.Templates[1],
            "death"=>OrcEffectEditing.Templates[2],
            "turnStart"=>OrcEffectEditing.Templates[3],
            "turnEnd"=>OrcEffectEditing.Templates[4],
            "damaged"=>OrcEffectEditing.Templates[5],
            "afterAttack"=>OrcEffectEditing.Templates[7],
            "attack"=>OrcEffectEditing.Templates[8],
            "unitDeployed"=>OrcEffectEditing.Templates[9],
            "friendlyDeath"=>OrcEffectEditing.Templates[10],
            "orderPlayed"=>new OrcTemplateEntry("order_listener","指令打出时","on_event","card.played",OrcTriggerGuard.None),
            "unitLeft"=>new OrcTemplateEntry("leave_listener","单位离场时","on_event","card.leaving",OrcTriggerGuard.None),
            "upgraded"=>new OrcTemplateEntry("upgraded_self","升为老兵时","on_event","unit.upgraded",OrcTriggerGuard.SelfUnit),
            "unitUpgraded"=>new OrcTemplateEntry("upgraded_listener","单位升为老兵时","on_event","unit.upgraded",OrcTriggerGuard.None),
            "friendlyDamaged"=>new OrcTemplateEntry("damaged_listener","单位受伤时","on_event","card.damaged",OrcTriggerGuard.None),
            "passive"=>new OrcTemplateEntry("continuous_aura","持续光环","on_event","unit.deployed|unit.joined",OrcTriggerGuard.SelfUnit),
            _=>throw Gap("trigger",trigger)
        };
        if(graph.TryGetProperty("selfOnly",out var selfOnly)&&!selfOnly.GetBoolean()||graph.TryGetProperty("eventSide",out _)||graph.TryGetProperty("watchOpponent",out _))entry=entry with{Guard=OrcTriggerGuard.None};
        if(graph.TryGetProperty("listenZone",out var zone)&&zone.GetString() is not ("" or "any" or "board" or "field"))throw Gap("listenZone",zone.ToString());
        var code = new DesktopNativeCodegen().Compile(graph);
        return new(entry.Id,entry.Slot,[new DslOp("csx",script:code)],entry.Hook,entry.Guard,NativeGenerated:true);
    }
    private static string Text(JsonElement o,string key)=>o.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
    private static void Fields(JsonElement o,string path,params string[] fields)
    {
        foreach(var p in o.EnumerateObject())if(!fields.Contains(p.Name,StringComparer.Ordinal))throw Gap(path+"."+p.Name,p.Value.GetRawText());
    }
    private static InvalidDataException Gap(string path,string detail)=>new($"OrC-KSD 尚无无损执行适配：{path} = {detail}。完整声明已保留在 Inspector 草稿中。");
}
