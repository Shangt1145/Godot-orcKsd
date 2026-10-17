using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

public static class OrcDiscardVerification
{
    public static async Task<NativeStateFeedback> RunAsync(bool burn = false)
    {
        using var session = await OrcMatchSession.CreateProbeAsync("verify/discard-paper", 24, 37, "native-discard");
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        if (burn)
            foreach (var owner in session.Host.Match.Players)
                while (owner.Hand.Count < 9) await session.Host.Match.PlayerManager.DrawCard(owner);
        session.SettleProjection();
        var initial = UiSnapshots.Freeze(session.View);
        var resolutions = new List<UiPresentationResolution>();
        session.Host.PresentationReady += (resolution, _) => resolutions.Add(resolution);
        foreach (var side in new[] { 0, 1 })
        {
            var owner = session.Host.Match.Players[side];
            var card = owner.Hand.OfType<CardBase>().First();
            using (session.Host.Match.Engine.BeginAction())
                if (burn) await session.Host.Match.PlayerManager.DrawCard(owner);
                else await session.Host.Match.PlayerManager.DiscardCardAsync(owner, card);
            session.Host.CaptureCompletedActions();
            session.Pump();
        }
        return new(initial, resolutions.AsReadOnly(), UiSnapshots.Freeze(session.View));
    }
}
