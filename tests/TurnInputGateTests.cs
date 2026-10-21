using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class TurnInputGateTests
{
    private static UiMatchView View(string side = "self", int turn = 4) => new()
    { MatchId = "turn-input", Phase = "play", ActivePlayerSide = side, Turn = turn };

    [Fact]
    public void DelayedEnemySnapshotsCannotRelockAnAlreadyPresentedFriendlyTurn()
    {
        var gate = new TurnInputGate(); gate.Reset(View("enemy", 1));
        gate.Present(new("self", 2));
        var friendly = View("self", 2);
        gate.Observe(new UiPresentationResolution(friendly.MatchId, [], friendly) { Before = View("enemy", 1) });
        gate.Observe(View("enemy", 1));
        Assert.True(gate.AllowsInput(friendly));
        gate.EndTurnRequested(friendly);
        Assert.False(gate.AllowsInput(friendly));
    }

    [Fact]
    public void EngineReturningToSelfDoesNotUnlockPendingEnemyPlayback()
    {
        var gate = new TurnInputGate(); gate.Reset(View());
        gate.Observe(View("enemy", 5));
        Assert.False(gate.AllowsInput(View("enemy", 5)));
        Assert.False(gate.AllowsInput(View("self", 6)));
        gate.Present(new("self", 6));
        Assert.True(gate.AllowsInput(View("self", 6)));
    }

    [Fact]
    public void EndTurnLocksBeforeEngineOrQueueCanRespond()
    {
        var gate = new TurnInputGate(); gate.Reset(View());
        gate.EndTurnRequested(View());
        Assert.False(gate.AllowsInput(View()));
        Assert.False(gate.AllowsInput(View("self", 6)));
    }

    [Fact]
    public void RawEnemyResolutionLocksEvenWhenOnlyLatestFriendlyViewWasObserved()
    {
        var gate = new TurnInputGate(); gate.Reset(View());
        var returned = View("self", 6);
        gate.Observe(new UiPresentationResolution(returned.MatchId, [new UiTurnPresentation("self", 6)], returned)
        { Before = View("enemy", 5) });
        Assert.False(gate.AllowsInput(returned));
        gate.Present(new("self", 6));
        Assert.True(gate.AllowsInput(returned));
    }

    [Fact]
    public void OldFriendlyBannerCannotUnlockNewlyEndedTurn()
    {
        var gate = new TurnInputGate(); gate.Reset(View("self", 6));
        gate.EndTurnRequested(View("self", 6));
        gate.Present(new("self", 6));
        Assert.False(gate.AllowsInput(View("self", 8)));
        gate.Observe(View("enemy", 7));
        gate.Present(new("self", 6));
        Assert.False(gate.AllowsInput(View("self", 8)));
        gate.Present(new("self", 8));
        Assert.True(gate.AllowsInput(View("self", 8)));
    }

    [Fact]
    public void RefusedEndTurnRestoresUnchangedFriendlyTurnOnly()
    {
        var gate = new TurnInputGate(); gate.Reset(View());
        gate.EndTurnRequested(View()); gate.RefuseEndTurn(View());
        Assert.True(gate.AllowsInput(View()));
        gate.EndTurnRequested(View()); gate.Observe(View("enemy", 5));
        gate.RefuseEndTurn(View("self", 6));
        Assert.False(gate.AllowsInput(View("self", 6)));
    }

    [Fact]
    public void FreshProjectionResetsPreviousMatchOrHotseatLock()
    {
        var gate = new TurnInputGate(); gate.Reset(View("enemy", 5));
        gate.Reset(View());
        Assert.True(gate.AllowsInput(View()));
        gate.Reset(View("enemy", 5));
        Assert.False(gate.AllowsInput(View("self", 6)));
    }

    [Theory]
    [InlineData("mulligan")]
    [InlineData("over")]
    public void NonPlayingPhaseIsAlwaysBlocked(string phase)
    {
        var gate = new TurnInputGate(); var view = View() with { Phase = phase };
        gate.Reset(view); Assert.False(gate.AllowsInput(view));
    }

    [Fact]
    public async Task PriorityPlaybackKeepsInputLockedUntilFriendlyTurnBoundary()
    {
        var initial = View(); var enemy = View("enemy", 5); var friendly = View("self", 6);
        var gate = new TurnInputGate(); gate.Reset(initial); gate.EndTurnRequested(initial);
        var finishBeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<long>();
        using var player = new PresentationPlayer(initial.MatchId, async (r, _, _) =>
        {
            order.Add(r.Sequence);
            if (r.Sequence == 1) await finishBeat.Task;
            foreach (var step in r.Steps)
                if (step is UiTurnPresentation turn) gate.Present(turn);
            Assert.Equal(r.Sequence == 4, gate.AllowsInput(friendly));
        }, initial);
        void Enqueue(long sequence, UiMatchView before, UiMatchView after, params UiPresentationStep[] steps)
        {
            var r = new UiPresentationResolution(initial.MatchId, steps, after) { Before = before, Sequence = sequence };
            gate.Observe(r); _ = player.Enqueue(r, new());
        }
        Enqueue(1, initial, enemy, new UiTurnPresentation("enemy", 5));
        Enqueue(2, enemy, enemy, new UiDrawPresentation("enemy", 0));
        var unit = new UiCardView { Uid = "enemy-unit", OwnerSide = "enemy", Zone = "support", Definition = new() { CardId = "enemy-unit" } };
        Enqueue(3, enemy, enemy, new UiDeploymentPresentation(unit, enemy));
        Enqueue(4, enemy, friendly, new UiTurnPresentation("self", 6));
        Assert.False(gate.AllowsInput(friendly));
        finishBeat.SetResult(); await player.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, order); // Hidden arrival precedes the enemy card's use.
        Assert.True(gate.AllowsInput(friendly));
    }
}
