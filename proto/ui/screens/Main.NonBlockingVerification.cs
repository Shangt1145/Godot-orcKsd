using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyInputScheduling()
    {
        try
        {
            StopRealMatch(); Show("battle");
            await _battle.VerifyTurnAndPriorityAsync();
            await _battle.VerifyDemoNonBlockingAsync();
            await VerifyNonBlockingInputAsync();
            GD.Print("INPUT_SCHEDULING_VERIFY_OK"); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    private async Task VerifyNonBlockingInputAsync()
    {
        StopRealMatch(); Show("battle");
        var speed = _clock.Current; var reduced = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
        // A real engine probe has enough affordable cards for two successive gestures.
        var runner = new OrcMatchRunner { Hotseat = true, InteractiveMulligan = false, DeckSeed = 7 };
        BindRunner(runner);
        var phases = new List<string>();
        void OnPhase(string phase) => phases.Add(phase);
        try
        {
            await runner.StartAsync();
            await runner.SubmitAsync(new EndTurn()); runner.SwitchSeat();
            await runner.SubmitAsync(new EndTurn()); runner.SwitchSeat();
            var before = runner.CurrentView!;
            if (before.SelfKredits < 2) throw new Exception("Non-blocking fixture needs two kredits.");
            var cards = before.SelfHand.Take(2).Select(c => c.Uid).ToArray();
            _battle.PresentationPhase += OnPhase;
            await _battle.DragAsync(cards[0], _battle.DropAtRowEdge(false, false));
            runner.Pump();
            var firstPlayback = _presentations!.Completion;
            if (!phases.Contains("deployment-start") || firstPlayback.IsCompleted)
                throw new Exception("First deployment animation did not stay in flight.");
            await _battle.DragAsync(cards[1], _battle.DropAtRowEdge(false, false), () =>
            {
                if (firstPlayback.IsCompleted || !_battle.Interaction.Contains("dragging=True"))
                    throw new Exception("Second gesture was blocked or interrupted the first animation.");
                return Task.CompletedTask;
            });
            runner.Pump();
            var accepted = runner.CurrentView!;
            if (accepted.SelfLine.Count != 2 || accepted.SelfKredits != before.SelfKredits - 2)
                throw new Exception("Second operation did not settle before the first animation finished.");
            if (accepted.SelfLine.Select(c => c.SlotIndex).Distinct().Count() != 2)
                throw new Exception("Second drop used an occupied historical slot.");
            if (firstPlayback.IsCompleted || phases.Count(p => p == "deployment-start") != 1)
                throw new Exception("Successive operations interrupted or overlapped deployment playback.");
            var queue = _presentations;
            var renders = 0;
            void OnProjection(UiMatchView _, UiBattleActions __) => renders++;
            runner.ProjectionReady += OnProjection;
            try { await runner.SubmitAsync(new PlayCard("unavailable-card", 0)); }
            finally { runner.ProjectionReady -= OnProjection; }
            if (renders != 0 || _presentations != queue || firstPlayback.IsCompleted)
                throw new Exception("A refused operation reset active playback.");
            await _battle.ClickEndTurnAsync(); runner.Pump();
            if (runner.CurrentView!.ActivePlayerSide != "enemy" || _battle.EndTurnEnabled)
                throw new Exception("End-turn input read the historical animation state.");
            await _presentations!.Completion;
            var deployments = phases.Where(p => p is "deployment-start" or "deployment-settled").ToArray();
            if (!deployments.SequenceEqual(new[] { "deployment-start", "deployment-settled", "deployment-start", "deployment-settled" }))
                throw new Exception($"Deployment playback was not FIFO: {string.Join(',', deployments)}");
            if (!_battle.MatchesProjection(runner.CurrentView!) || _battle.EndTurnEnabled)
                throw new Exception("Historical playback overwrote current availability or the final board.");
            // Hold another gesture beyond the first animation's final repaint boundary.
            runner.SwitchSeat(); await runner.SubmitAsync(new EndTurn()); runner.SwitchSeat();
            cards = runner.CurrentView!.SelfHand.Take(2).Select(c => c.Uid).ToArray();
            phases.Clear();
            await _battle.DragAsync(cards[0], _battle.DropAtRowEdge(false, true)); runner.Pump();
            firstPlayback = _presentations!.Completion;
            await _battle.DragAsync(cards[1], _battle.DropAtRowEdge(false, true), async () =>
            {
                await ToSignal(GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);
                if (!_battle.Interaction.Contains("dragging=True") || firstPlayback.IsCompleted)
                    throw new Exception("Animation's final repaint discarded a held gesture.");
            });
            runner.Pump(); await _presentations!.Completion;
            if (runner.CurrentView!.SelfLine.Count != 4 || !_battle.MatchesProjection(runner.CurrentView))
                throw new Exception("A gesture held across the animation boundary failed to settle.");
            if (!phases.Where(p => p is "deployment-start" or "deployment-settled").SequenceEqual(deployments))
                throw new Exception("Held gesture changed FIFO playback order.");
            GD.Print("NONBLOCKING_INPUT_VERIFY_OK two-real-drags immediate-settlement distinct-slots refusal-preserves-playback endturn-live FIFO-final-projection");
            GD.Print("NONBLOCKING_HELD_GESTURE_VERIFY_OK hold-across-repaint fourth-deployment FIFO-final-projection");
        }
        finally
        {
            _battle.PresentationPhase -= OnPhase;
            StopRealMatch(); _battle.ResetDemo();
            _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced);
        }
    }
}
