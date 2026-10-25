using System.Text.Json;
using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>Native target execution lives in the engine; this adapter only projects editor targeting hints.</summary>
public sealed class DesktopTargetContext : IDisposable
{
    private readonly AuthoringTargetContext _native;
    private readonly Match _match;
    private readonly IReadOnlyDictionary<string,string[]> _plans;
    public DesktopTargetContext(Match match,IReadOnlyDictionary<string,string[]> plans)
    { _match=match;_plans=plans;_native=new(match,plans); }
    public static void ValidateTarget(JsonElement target)=>AuthoringTargetContext.ValidateTarget(target);
    internal static string Scope(JsonElement graph)=>AuthoringTargetContext.Scope(graph);
    public static Task<Card[]> SelectAsync(Card source,string declaration,string scope,CancellationToken ct)=>AuthoringTargetContext.SelectAsync(source,declaration,scope,ct);
    internal bool TryReplayNativeSelection(ISelectorInstance selector)=>_native.TryReplayNativeSelection(selector);
    public IReadOnlyList<UiOrderTargeting> ReadOrderTargets(Player viewer)
    {
        var result=new List<UiOrderTargeting>();
        foreach(var card in viewer.Hand.OfType<OrderCard>())
        {
            if(!_match.CardLibrary.TryGetRegisteredId(card.Definition,out var id)||!_plans.TryGetValue(id,out var graphs))continue;
            foreach(var graph in graphs)
            {
                using var document=JsonDocument.Parse(graph);var root=document.RootElement;
                if(root.GetProperty("trigger").GetString() is not ("play" or "order")||!root.TryGetProperty("targets",out var targets)||targets.GetArrayLength()==0)continue;
                var candidates=AuthoringTargetContext.ResolveCandidates(card,targets[0]).GetAwaiter().GetResult();
                result.Add(new(OrcRefs.KeyOf(card),candidates.Select(OrcRefs.KeyOf).ToArray()));break;
            }
        }
        return result;
    }
    public void Dispose()=>_native.Dispose();
}