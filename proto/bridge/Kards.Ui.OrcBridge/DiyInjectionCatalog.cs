using System.Text.Json;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

public sealed record DiyInjectionInfo(string Target,string Band,string Event,int Priority,string? Error)
{
    public string Summary=>$"注入 → {Target} · {(Band.Length==0?"默认":Band)} 段 · 事件 {Event} · 优先级 {Priority}";
}
/// <summary>The vocabulary describes the checked-out engine, not proposed future action bands.</summary>
public static class DiyInjectionCatalog
{
    public static IReadOnlyDictionary<string,IReadOnlyList<string>> Schemes {get;}=new Dictionary<string,IReadOnlyList<string>>
    {
        ["OrderFlowBands"]=["Counter","Resolve","Finalize"],
        // 引擎 S2：底层动作的触发端/处理端各自统一成一套 band（原 AttackFlowBands / DamageFlowBands 已并入）。
        ["ActionTriggerBands"]=["Initiate","Announce","CounterGuard","Counter","Adjust","Commit"],
        ["ActionHandleBands"]=["Rewrite","Veto","Apply"],
        ["DefaultBands"]=["Default"]
    };
    public const string EngineRestriction="当前引擎的预制体注入只支持空白默认区段；非空 band 会被隔离，不会生效。";
    public static IReadOnlyList<DiyInjectionInfo> Read(string? json)
    {
        if(string.IsNullOrWhiteSpace(json))return [];
        using var document=JsonDocument.Parse(json);var rows=new List<DiyInjectionInfo>();
        void Inspect(JsonElement item)
        {
            if(item.ValueKind==JsonValueKind.Array){foreach(var element in item.EnumerateArray())Inspect(element);return;}
            if(item.ValueKind!=JsonValueKind.Object)return;
            var root=item.TryGetProperty("root",out var nested)?nested:item;
            if(!root.TryGetProperty("injects",out var injects))return;
            foreach(var inject in injects.EnumerateArray())
            {
                string S(string name)=>inject.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()!:"";
                var target=S("target");var band=S("band");var eventId=S("event");
                var known=Schemes.Values.SelectMany(s=>s).Contains(band,StringComparer.Ordinal);
                var error=target.Length==0||eventId.Length==0?"注入目标和事件不可为空":band.Length==0?null:
                    !known?"未知 band："+band+"。"+EngineRestriction:EngineRestriction;
                rows.Add(new(target,band,eventId,inject.TryGetProperty("priority",out var priority)?priority.GetInt32():0,error));
            }
        }
        Inspect(document.RootElement);return rows;
    }
    public static IReadOnlyList<DiyInjectionInfo> Read(OrcEffectDraft draft)=>Read(draft.PrefabJson??draft.DesktopJson);
}
