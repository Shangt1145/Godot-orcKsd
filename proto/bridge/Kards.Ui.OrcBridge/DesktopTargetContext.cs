using System.Text.Json;
using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

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
    public static async Task<Card[]> SelectAsync(Card source,string declaration,string scope,CancellationToken ct)
    {
        var chosen=await AuthoringTargetContext.SelectAsync(source,declaration,scope,ct);
        GameLog.Debug("target",$"{OrcRefs.KeyOf(source)} 选靶 {declaration}（scope={scope}）→ "
            +(chosen.Length==0?"空":string.Join("、",chosen.Select(OrcRefs.KeyOf))));
        return chosen;
    }
    internal bool TryReplayNativeSelection(ISelectorInstance selector)=>_native.TryReplayNativeSelection(selector);
    internal void PrepareCard(CardBase card)=>_native.PrepareCard(card);
    public IReadOnlyList<UiOrderTargeting> ReadOrderTargets(Player viewer)
    {
        var result=new List<UiOrderTargeting>();
        // 只遍历 OrderCard 会让「部署：使 1 个友方单位…」这类单位卡的选靶声明彻底消失，
        // 引擎拿不到候选就只能自己选——玩家看到的就是「指向的效果变成随机」。
        // 所以这里要覆盖手牌里的全部卡，触发面也要带上 deploy。
        foreach(var card in viewer.Hand)
        {
            if(card is not CardBase shaped)continue;
            if(!_match.CardLibrary.TryGetRegisteredId(shaped.Definition,out var id)||!_plans.TryGetValue(id,out var graphs))continue;
            foreach(var graph in graphs)
            {
                using var document=JsonDocument.Parse(graph);var root=document.RootElement;
                if(root.GetProperty("trigger").GetString() is not ("play" or "order" or "deploy"))continue;
                if(!AuthoringTargetContext.HasInteractivePlayEffect(shaped,root.GetProperty("trigger").GetString()!))continue;
                if(!root.TryGetProperty("targets",out var targets)||targets.GetArrayLength()==0)continue;
                var candidates=AuthoringTargetContext.ResolveCandidates(card,targets[0]).GetAwaiter().GetResult();
                result.Add(new(OrcRefs.KeyOf(card),candidates.Select(OrcRefs.KeyOf).ToArray()));break;
            }
        }
        return result;
    }
    public void Dispose()=>_native.Dispose();
}
