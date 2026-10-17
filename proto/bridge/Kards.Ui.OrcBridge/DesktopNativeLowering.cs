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
        Fields(graph,"effect","trigger","actions","targets","selfOnly");
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
            _=>throw Gap("trigger",trigger)
        };
        if(graph.TryGetProperty("selfOnly",out var selfOnly)&&!selfOnly.GetBoolean())throw Gap("selfOnly","false");
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
