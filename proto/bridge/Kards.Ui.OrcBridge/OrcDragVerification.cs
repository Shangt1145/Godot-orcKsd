using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

/// <summary>Native fixture operations for GUI audits. The scene consumes only UIDs and UI contracts.</summary>
public static class OrcDragVerification
{
    public static string DeploymentProgram() => new KeywordProgram
    { Handlers=[new(){Actions=[new(){Op="buff",Target="choose.friendly.unit",Value="2"}]}] }.ToJson();
    public static string OrderProgram() => new KeywordProgram
    { Handlers=[new(){Event="card.played",Actions=[new(){Op="damage",Target="choose.friendly.unit",Value="1"}]}] }.ToJson();
    public static string NonTargetOrderProgram() => new KeywordProgram
    { Handlers=[new(){Event="card.played",Actions=[new(){Op="damage",Target="enemy.hq",Value="1"}]}] }.ToJson();

    public static async Task FundAsync(OrcMatchSession session)
    {
        var match=session.Host.Match;
        await match.ResourceManager.GainSlotsAsync(match.Players[0],11);
        await match.ResourceManager.GainPointsAsync(match.Players[0],12);
    }

    public static async Task<string> JoinSupportAsync(OrcMatchSession session,int ownerIndex,string cardId)
    {
        var match=session.Host.Match;var owner=match.Players[ownerIndex];
        var unit=(UnitCard)await match.CardService.CreateAsync(cardId,owner);
        await match.PlayManager.JoinUnitAsync(unit,match.Battlefield.GetSupportLine(owner).GetInsertionGaps().First());
        return OrcRefs.KeyOf(unit);
    }

    public static int Recorded(OrcMatchSession session,string uid,string key)
        => ((CardBase)session.Host.Index[uid]).GetData<KeywordProgramData>().Values[key];
    public static bool HasRecord(OrcMatchSession session,string uid)
        => ((CardBase)session.Host.Index[uid]).TryGetData<KeywordProgramData>(out _);
    public static async Task CycleTurnAsync(OrcMatchSession session)
    { await session.Host.Match.EndTurn();await session.Host.Match.EndTurn(); }
    public static int Points(OrcMatchSession session)=>session.Host.Match.Players[0].Points;

    public static async Task<(string RoutedUid,string MobileUid)> PrepareMovementAsync(OrcMatchSession session)
    {
        await FundAsync(session);var match=session.Host.Match;
        var routed=(UnitCard)await match.CardService.CreateAsync("diy/demo-kuijun",match.Players[0]);
        var mobile=(UnitCard)await match.CardService.CreateAsync("diy/demo-jidong",match.Players[0]);
        await match.PlayManager.JoinUnitAsync(routed,match.Battlefield.FrontLine.GetInsertionGaps().First());
        await match.PlayManager.JoinUnitAsync(mobile,match.Battlefield.FrontLine.GetInsertionGaps().Last());
        // Restore through the native quota policy; forcing attack quota to 1 disables mobility.
        foreach(var unit in new[]{routed,mobile})
            KeywordRules.ResetActionQuotas(unit);
        session.SettleProjection();session.Pump();
        return (OrcRefs.KeyOf(routed),OrcRefs.KeyOf(mobile));
    }

    public static void AssertRetreated(OrcMatchSession session,string uid,int points)
    {
        var unit=(UnitCard)session.Host.Index[uid];var match=session.Host.Match;
        if(!ReferenceEquals(match.Battlefield.PlayerASupportLine,unit.GetData<UnitStateData>().Line)
            ||Points(session)!=points-1||unit.GetData<CommandData>().CanMove)
            throw new Exception("Retreat did not use native movement cost and opportunity.");
    }
    public static bool HasProgram(OrcMatchSession session,string uid)
        => KeywordProgramRuntime.Components((CardBase)session.Host.Index[uid]).Any();
}
