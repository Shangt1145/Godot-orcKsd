using Kards.Ui.Contracts;
using Orc.Cards;
using Orc.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace Kards.Ui.OrcBridge;

public enum DiyPlayability { Playable, DraftOnly }
public sealed record DiyCapabilityRow(int EffectIndex,DiyPlayability State,IReadOnlyList<string> Reasons,string Source="structured");

/// <summary>动作选择器的分区：<see cref="Alpha"/>＝这版真能跑，<see cref="Preview"/>＝目录里有、还编不出可执行效果。</summary>
public enum DiyOpStage { Alpha, Preview }

/// <summary>目录里的一行。<see cref="Problem"/> 对 <see cref="DiyOpStage.Alpha"/> 恒为空。</summary>
public sealed record DiyOpEntry(string Op,DiyOpStage Stage,string Group,string Problem);

/// <summary>
/// 完整动作目录的**诚实分区**。
///
/// <para>
/// 判据是编译器的答案，不是手写名单：每个动作拿一份默认声明过一遍
/// <see cref="DiyCapability.OperationProblem"/>（走真实的 <c>DesktopNativeLowering</c>），
/// 过不去的进"预览"。所以引擎补上机制后这一格会自动变绿——不需要有人记得回来改名单，
/// 也不会出现"文档说能用、代码里其实早就好了"这种反向漂移。
/// </para>
///
/// <para>
/// 分区只影响**展示顺序与说明**，绝不裁剪目录：全部动作一个不少地留在选择器里，
/// 只是排在"预览"下面并写明原因。藏起来才是真的不诚实——玩家会以为这版没有这个能力。
/// </para>
/// </summary>
public static class DiyOpCatalog
{
    private static readonly string[] GroupOrder=["卡牌","单位与总部","流程","记录","资源与环境"];

    private static readonly Lazy<IReadOnlyList<DiyOpEntry>> Catalog=new(Build);

    public static IReadOnlyList<DiyOpEntry> All=>Catalog.Value;
    public static IReadOnlyList<DiyOpEntry> Alpha=>Catalog.Value.Where(e=>e.Stage==DiyOpStage.Alpha).ToArray();
    public static IReadOnlyList<DiyOpEntry> Preview=>Catalog.Value.Where(e=>e.Stage==DiyOpStage.Preview).ToArray();

    private static IReadOnlyList<DiyOpEntry> Build()=>InspectorVocabulary.Operations
        .Select(op=>
        {
            var problem=DiyCapability.OperationProblem(op);
            return new DiyOpEntry(op,problem.Length==0?DiyOpStage.Alpha:DiyOpStage.Preview,InspectorPresentation.Group(op),problem);
        })
        // 分区在前，分区内沿用原来的"分组 → 名字"顺序：换个界面不该让玩家找不到昨天用的那个动作。
        .OrderBy(e=>e.Stage)
        .ThenBy(e=>Array.IndexOf(GroupOrder,e.Group) is var index&&index<0?GroupOrder.Length:index)
        .ThenBy(e=>InspectorPresentation.Operation(e.Op))
        .ToArray();
}

/// <summary>Compiler-backed diagnostics. Effect admission and card-level validation remain separate.</summary>
public static class DiyCapability
{
    private static readonly ConcurrentDictionary<string,string> OperationProblems=new();
    public static string OperationProblem(string op)=>OperationProblems.GetOrAdd(op,id=>
    {
        try { DesktopNativeLowering.Lower(new JsonObject{["trigger"]="deploy",["actions"]=new JsonArray(InspectorVocabulary.Action(id))}.ToJsonString());return ""; }
        // 这里只能**返回一个说明**，不能让异常跑出去：调用方是效果编辑器的动作选择器，
        // 每个动作都会问一次；某个动作的默认声明缺字段（例如没有 target）时，
        // 以前会抛 KeyNotFoundException 直接打到 Godot 回调上（日志红字 + 选择器状态半死）。
        catch(Exception error) when(error is not OutOfMemoryException){return error.Message;}
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
        catch(Exception error)
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
