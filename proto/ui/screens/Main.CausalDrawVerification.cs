using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyCausalDraw()
    {
        try
        {
            Show("battle"); await _battle.VerifyTurnAndPriorityAsync();
            await VerifyCausalDrawOrderAsync(); StopRealMatch(); GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); StopRealMatch(); GetTree().Quit(1); }
    }
    private async Task VerifyCausalDrawOrderAsync()
    {
        StopRealMatch(); Show("battle");
        var speed = _clock.Current; var reduced = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Faster); _clock.SetReducedMotion(false);
        var phases = new List<string>();
        void OnPhase(string phase) => phases.Add(phase);
        try
        {
            await StartRealMatchAsync(interactiveMulligan: false, manualOpponentForVerification: true,
                deckSeed: 7, verificationHand: ["USG/commands/_7", "USG/units/_4"]);
            var runner = _runner ?? throw new Exception("Causal draw fixture did not start.");
            for (var round = 0; round < 5; round++)
            {
                await runner.SubmitAsync(new EndTurn()); runner.SwitchViewerForVerification();
                await runner.SubmitAsync(new EndTurn()); runner.SwitchViewerForVerification();
                await AwaitIdleAsync(20);
            }
            var before = runner.CurrentView!;
            var order = before.SelfHand.First(c => c.CardId == "USG/commands/_7");
            var unit = before.SelfHand.First(c => c.CardId == "USG/units/_4");
            if (before.SelfKredits < order.EffectiveCost + unit.EffectiveCost)
                throw new Exception("Causal draw fixture lacks credits for both operations.");
            var directDraws = 0;
            void Observe(UiPresentationResolution r, UiBattleActions _)
            {
                if (r.Steps.OfType<UiOrderPresentation>().Any(s => s.Card.Uid == order.Uid))
                    directDraws += r.Steps.OfType<UiDrawPresentation>().Count();
            }
            runner.PresentationReady += Observe; _battle.PresentationPhase += OnPhase;
            try
            {
                await _battle.DragAsync(order.Uid, new(640, 350));
                await WaitForInputCommitAsync(runner, v => !v.SelfHand.Any(c => c.Uid == order.Uid));
                if (!_presentations!.IsBusy || runner.CurrentView!.SelfDeckCount != before.SelfDeckCount - 2)
                    throw new Exception("Order draw did not commit while its presentation was running.");
                await _battle.DragAsync(unit.Uid, _battle.DropAtRowEdge(false, false));
                await WaitForInputCommitAsync(runner, v => v.SelfLine.Any(c => c.Uid == unit.Uid));
                if (!_presentations.IsBusy) throw new Exception("Next play waited for the animations instead of committing immediately.");
                await AwaitIdleAsync(20);
            }
            finally { runner.PresentationReady -= Observe; _battle.PresentationPhase -= OnPhase; }
            if (directDraws != 2 || phases.Count(p => p == "draw-start") != 2
                || phases.IndexOf("order-start") < 0 || phases.IndexOf("deployment-start") < 0
                || phases.IndexOf("order-start") >= phases.IndexOf("draw-start")
                || phases.LastIndexOf("draw-settled") >= phases.IndexOf("deployment-start")
                || !_battle.MatchesProjection(runner.CurrentView!))
                throw new Exception($"Native draw causality failed: same-action draws={directDraws}; {string.Join(',', phases)}");
            GD.Print("CAUSAL_DRAW_NATIVE_VERIFY_OK real-mouse draw-order then-two-draws then-deployment both-commands-commit-during-playback final-projection");
        }
        finally { _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced); StopRealMatch(); _battle.ResetDemo(); }
    }
}
