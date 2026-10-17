using System.Runtime.CompilerServices;
using System.Text.Json;
using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Orc.Game.Triggers;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>Declarative targets use native pre-play capture and TargeterManager, never "first unit".
/// This bridge owns references and selector transport only; the engine owns admission and mutation.</summary>
public sealed class DesktopTargetContext : IDisposable
{
    private static readonly ConditionalWeakTable<Player,DesktopTargetContext> Owners=new();
    private readonly ConditionalWeakTable<Card,Dictionary<string,Card[]>> _captured=new();
    private readonly List<(Orc.Core.Trigger<CardTriggerView> Trigger,TriggerRegistration Handle)> _registrations=[];
    private readonly Match _match;
    private readonly IReadOnlyDictionary<string,string[]> _plans;
    public DesktopTargetContext(Match match,IReadOnlyDictionary<string,string[]> plans)
    {
        _match=match;_plans=plans;
        foreach(var owner in match.Players)Owners.Add(owner,this);
        foreach(var card in match.Players.SelectMany(p=>p.Hand.OfType<CardBase>().Concat(Enumerable.Range(0,p.Deck.Count).Select(p.Deck.PeekInstance))))
        {
            if(!match.CardLibrary.TryGetRegisteredId(card.Definition,out var id)||!plans.TryGetValue(id,out var graphs))continue;
            var declarations=graphs.SelectMany(g=>
            {
                using var d=JsonDocument.Parse(g);var e=d.RootElement;
                return Text(e,"trigger") is "deploy" or "play" or "order"&&e.TryGetProperty("targets",out var targets)
                    ?targets.EnumerateArray().Select(t=>(Declaration:t.GetRawText(),Scope:Scope(e))).ToArray():[];
            }).Distinct().ToArray();
            if(declarations.Length==0)continue;
            var trigger=card switch{UnitCard unit=>unit.PrePlayTrigger,CommandCard command=>command.PrePlayTrigger,_=>null};
            if(trigger is null)continue;
            var handle=trigger.Register("desktop/capture",async(view,_,ct)=>
            {
                _captured.Remove(card);var selection=new Dictionary<string,Card[]>(StringComparer.Ordinal);
                foreach(var declaration in declarations)
                {
                    var result=await SelectCore(card,declaration.Declaration,ct);
                    if(result.Length==0){view.CaptureBox!.CancelPrePlay();return;}
                    selection[declaration.Scope+"|"+declaration.Declaration]=result;
                }
                _captured.Add(card,selection);
            });
            _registrations.Add((trigger,handle));
        }
    }
    public static void ValidateTarget(JsonElement target)
    {
        var allowed=new[]{"id","kind","side","zone","prompt"};
        if(string.IsNullOrWhiteSpace(Text(target,"id")))throw new InvalidDataException("声明目标必须具有唯一 id");
        foreach(var p in target.EnumerateObject())if(!allowed.Contains(p.Name,StringComparer.Ordinal))throw new InvalidDataException("OrC-KSD 选靶字段尚无无损适配："+p.Name);
        if(Text(target,"kind")!="unit"||Text(target,"zone") is not ("" or "field" or "frontline" or "support"))throw new InvalidDataException("OrC-KSD 声明选靶目前仅接单位战场区域");
        if(Text(target,"side") is not ("" or "any" or "friendly" or "enemy" or "both"))throw new InvalidDataException("选靶阵营无效");
    }
    internal static string Scope(JsonElement graph)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(graph.GetRawText())))[..24];
    public static async Task<Card[]> SelectAsync(CardBase source,string declaration,string scope,CancellationToken ct)
    {
        if(source.Owner is null||!Owners.TryGetValue(source.Owner,out var context))throw new InvalidOperationException("原生声明选靶上下文未装配");
        if(context._captured.TryGetValue(source,out var selection)&&selection.TryGetValue(scope+"|"+declaration,out var captured))return captured;
        return await context.SelectCore(source,declaration,ct);
    }
    private async Task<Card[]> SelectCore(CardBase source,string declaration,CancellationToken ct)
    {
        using var doc=JsonDocument.Parse(declaration);var target=doc.RootElement;ValidateTarget(target);
        var candidates=(await ResolveCandidates(source,target,ct)).OfType<UnitCard>().ToArray();
        if(candidates.Length==0)return [];
        Card? selected=null;
        var result=await _match.TargeterManager.RunAsync(async flow=>
        {
            var references=candidates.Select(c=>c.Ref).ToArray();
            var step=await flow.Step(SelectorTemplates.FieldUnit,new ReferenceSetParameter(references,1,1,
                domainValidator:r=>r.Value is UnitCard unit&&candidates.Contains(unit)&&!unit.GetData<UnitStateData>().IsDestroyed,
                tag:new OrderSelectorTag(Text(target,"id"))));
            while(step.IsFailed&&step.Failure==SelectorFailureReason.InvalidSelection)step=await flow.Retry<Ref<Entity>>();
            if(step.IsCancelled)return TargeterResult.Cancelled();
            if(!step.IsOk)return TargeterResult.FromSelectorFailure(step.Failure);
            selected=(Card)step.Value!.Value;return TargeterResult.Ok();
        });
        ct.ThrowIfCancellationRequested();return result.IsOk&&selected is not null?[selected]:[];
    }
    private static Task<IReadOnlyList<Card>> ResolveCandidates(CardBase source,JsonElement target,CancellationToken ct=default)
    {
        var runtime=EffectRuntime.ResolveFor(source)??throw new InvalidOperationException("原生效果服务未装配");
        var zone=Text(target,"zone");var side=Text(target,"side");
        return runtime.SelectAsync(source,new("all",side is "any" or ""?"both":side,zone is "frontline" or "support"?zone:null),ct);
    }
    public IReadOnlyList<UiOrderTargeting> ReadOrderTargets(Player viewer)
    {
        var result=new List<UiOrderTargeting>();
        foreach(var card in viewer.Hand.OfType<CommandCard>())
        {
            if(!_match.CardLibrary.TryGetRegisteredId(card.Definition,out var id)||!_plans.TryGetValue(id,out var graphs))continue;
            var targets=new List<JsonElement>();foreach(var g in graphs){using var d=JsonDocument.Parse(g);if(Text(d.RootElement,"trigger") is "play" or "order"&&d.RootElement.TryGetProperty("targets",out var ts))targets.AddRange(ts.EnumerateArray().Select(t=>t.Clone()));}
            if(targets.Count==0)continue;var t=targets[0];
            var units=ResolveCandidates(card,t).GetAwaiter().GetResult().OfType<UnitCard>();
            result.Add(new(OrcRefs.KeyOf(card),units.Select(OrcRefs.KeyOf).ToArray()));
        }
        return result;
    }
    private static string Text(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
    public void Dispose(){foreach(var r in _registrations)r.Trigger.Unregister(r.Handle);foreach(var p in _match.Players)Owners.Remove(p);}
}
