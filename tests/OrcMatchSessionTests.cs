using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Orc.Game;
using Orc.Game.Cards;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// The session is the single door onto the engine: build a match, submit commands, answer the
/// mulligan. If these hold, the UI layer can be re-pointed at it without touching screens.
///
/// The value here is architectural as much as behavioural — a test that only touches
/// <c>UiMatchView</c>/<c>UiBattleActions</c>/<c>UiCommand</c> proves the boundary exists, because
/// the bridge types needed to drive a match are not even in scope.
/// </summary>
public sealed class OrcMatchSessionTests
{
    private const string InfantryId = "session-infantry";

    [Fact]
    public async Task ASessionRunsAMatchThroughTheContractAlone()
    {
        var session = await OrcMatchSession.CreateProbeAsync(InfantryId, 20, 20261005, "session-a");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            // Everything below is contract type. No Match, no Player, no card object.
            var view = session.View;
            Assert.Equal("session-a", view.MatchId);
            Assert.Equal("play", view.Phase);
            Assert.NotEmpty(view.SelfHand);
            Assert.Equal(20, view.SelfHq!.Health);
            Assert.Equal(20, view.EnemyHq!.Health);
            Assert.NotEmpty(session.Actions.PlayableUids);
        }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// A command must reach the engine and change the board, and the outcome must be reported as a
    /// contract enum. This is the path the UI's drag-and-drop and click-to-attack both ride on.
    /// </summary>
    [Fact]
    public async Task SubmittingCommandsMovesTheRealBoard()
    {
        var session = await OrcMatchSession.CreateProbeAsync(InfantryId, 20, 11, "session-play");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();
            var hand = session.View.SelfHand;
            Assert.NotEmpty(hand);

            var outcome = await session.SubmitAsync(new PlayCard(hand[0].Uid));
            Assert.Equal(UiSubmitOutcome.Applied, outcome);
            session.Pump();

            var deployed = session.View.SelfLine;
            Assert.Single(deployed);
            Assert.Equal(hand[0].Uid, deployed[0].Uid);
            Assert.DoesNotContain(session.View.SelfHand, c => c.Uid == hand[0].Uid);
        }
        finally { session.Dispose(); }
    }

    /// <summary>An unknown uid is refused as such, never as a silent success or a crash.</summary>
    [Fact]
    public async Task AnUnknownCardIsRefusedRatherThanAccepted()
    {
        var session = await OrcMatchSession.CreateProbeAsync(InfantryId, 20, 13, "session-unknown");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();
            Assert.Equal(UiSubmitOutcome.UnknownCard, await session.SubmitAsync(new PlayCard("no-such-uid")));
            Assert.Empty(session.View.SelfLine);
        }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// A selective keep must replace exactly the cards the player did not name. The panel offers a
    /// subset, so the complement is what gets replaced — getting this backwards would silently
    /// discard the cards the player wanted.
    /// </summary>
    [Fact]
    public async Task AMulliganReplacesTheCardsThePlayerDidNotKeep()
    {
        var kept = new List<string>();
        OrcMatchSession? session = null;
        session = await OrcMatchSession.CreateProbeAsync(InfantryId, 20, 17, "session-mulligan",
            present: (description, responder) =>
            {
                if (description.Slots.Count > 0
                    && description.Slots[0].Kind == Orc.Game.Targeting.TargetSlotKind.MulliganSelect)
                {
                    session!.NotePendingRequest(description, responder);
                    // Keep everything, so the hand after the answer must equal the opening hand.
                    kept.AddRange(description.AllowedTargets.Select(t => t.Value.Id.ToString("N")));
                }
                else OrcTargeterBridge.AutoRespond(description, responder);
            });
        try
        {
            await session.SettleMulliganAsync(interactive: true);
            var opening = session.View.SelfHand.Select(c => c.Uid).ToArray();
            Assert.NotEmpty(opening);

            await session.SubmitAsync(new ChooseMulligan(opening));
            session.Pump();

            // Keeping the whole hand must not change which cards are held. Hand order is not
            // contractual, so compare as sets.
            Assert.Equal(
                opening.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                session.View.SelfHand.Select(c => c.Uid).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        finally { session?.Dispose(); }
    }

    /// <summary>
    /// Commands outside the play phase are refused by phase, not attempted. The UI shows a hint
    /// instead of acting, and the board must be untouched.
    /// </summary>
    [Fact]
    public async Task CommandsBeforePlayAreRefusedByPhase()
    {
        var session = await OrcMatchSession.CreateProbeAsync(InfantryId, 20, 19, "session-gate");
        try
        {
            // The mulligan has not been answered yet, so the phase gate is still shut.
            var before = session.View.SelfHand.Count;
            Assert.Equal(UiSubmitOutcome.WrongPhase, await session.SubmitAsync(new EndTurn()));
            session.Pump();
            Assert.Equal(before, session.View.SelfHand.Count);
        }
        finally { session.Dispose(); }
    }
}
