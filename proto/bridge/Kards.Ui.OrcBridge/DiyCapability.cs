using Kards.Ui.Contracts;
using Orc.Cards;
using Orc.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace Kards.Ui.OrcBridge;

public enum DiyPlayability { Playable, DraftOnly }
public sealed record DiyCapabilityRow(int EffectIndex,DiyPlayability State,IReadOnlyList<string> Reasons,string Source="structured");

/// <summary>Compiler-backed diagnostics. Effect admission and card-level validation remain separate.</summary>
public static class DiyCapability
{
    private static readonly ConcurrentDictionary<string,string> OperationProblems=new();
    public static string OperationProblem(string op)=>OperationProblems.GetOrAdd(op,id=>
    {
        try { DesktopNativeLowering.Lower(new JsonObject{["trigger"]="deploy",["actions"]=new JsonArray(InspectorVocabulary.Action(id))}.ToJsonString());return ""; }
        catch(Exception error) when(error is InvalidDataException or ArgumentException or InvalidOperationException){return error.Message;}
    });
    public static IReadOnlyList<string> CheckEffect(JsonElement effect)
    {
        try
        {
            var draft=effect.TryGetProperty(InspectorCardRecord.NativeKey,out var native)?JsonSerializer.Deserialize<OrcEffectDraft>(native.GetRawText())!:
                new OrcEffectDraft{DesktopJson=effect.GetRawText()};
            if(effect.TryGetProperty(InspectorCardRecord.LegacyKey,out var legacy))
            {DiyCardCompiler.CompileEffects("unit",[JsonSerializer.Deserialize<DiyEffectDraft>(legacy.GetRawText())!],"diy.inspector.legacy");return [];}
            var program=OrcEffectEditing.Compile("diy.inspector.preview",[draft]);
            return program.Where(p=>p.Error is not null).Select(p=>p.Error!).ToArray();
        }
        catch(Exception error) when(error is InvalidDataException or ArgumentException or InvalidOperationException)
        {return [error.Message];}
    }
    public static IReadOnlyList<DiyCapabilityRow> Probe(DiyCardDraft draft,IReadOnlyList<UiCardDefinition> catalog)
    {
        var rows=new List<DiyCapabilityRow>();
        foreach(var (effect,index) in OrcEffectEditing.Compile(draft.Id.Replace('/','.'),draft.OrcEffects).Select((e,i)=>(e,i)))
        {
            var reasons=new List<string>();if(effect.Error is { } error)reasons.Add(error);
            else foreach(var snapshot in effect.Snapshots)
            {
                var instance=DynamicEffectFactory.Instantiate(new LogicEngine{ScriptEvaluator=NativeScriptHost.Evaluator},snapshot);
                if(!instance.Success)reasons.Add("效果不能实例化："+instance.Error);
            }
            rows.Add(new(index,reasons.Count==0?DiyPlayability.Playable:DiyPlayability.DraftOnly,reasons));
        }
        // Legacy rows are diagnosed even when a structured program currently takes precedence.
        foreach(var (effect,index) in draft.Effects.Select((e,i)=>(e,i)))
        {
            var reasons=new List<string>();
            try {DiyCardCompiler.CompileEffects(draft.CardType,[effect],draft.Id.Replace('/','.')+".legacy."+index);}
            catch(Exception e) when(e is InvalidDataException or ArgumentException or InvalidOperationException){reasons.Add(e.Message);}
            rows.Add(new(index,reasons.Count==0?DiyPlayability.Playable:DiyPlayability.DraftOnly,reasons,"legacy"));
        }
        if(draft.OrcEffects.Length==0&&draft.Effects.Length==0&&!draft.UseStructuredEffects&&draft.EffectText.Length>0)
        {
            var text=OrcInspector.CompileText(draft.Id,draft.EffectText,draft.CardType,catalog);
            var reasons=text.Problems.ToArray();
            rows.Add(new(0,reasons.Length==0?DiyPlayability.Playable:DiyPlayability.DraftOnly,reasons,"text"));
        }
        // An inert unit or a harmless valid order program isolates card-shape admission.
        var basic=draft with{OrcEffects=[],Effects=draft.CardType=="order"?[new(){Op="draw",Amount=1}]:[],EffectText="",UseStructuredEffects=true};
        var card=DiyCardCompiler.Validate(basic,catalog);
        if(!card.Success)rows.Add(new(-1,DiyPlayability.DraftOnly,card.Errors,"card"));
        return rows;
    }
}
