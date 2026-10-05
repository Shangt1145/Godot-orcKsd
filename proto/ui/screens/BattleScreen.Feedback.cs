using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    private static readonly string[] FeedbackScenarios = ["counter-arm", "counter-disarm", "counter-trigger", "status-heal", "status-buff", "status-suppressed", "status-cost"];
    public async Task VerifyFeedbackAsync()
    {
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Faster); _clock.SetReducedMotion(false);
        foreach (var kind in FeedbackScenarios)
        {
            ResetDemo(); var f = _demo.CreatePresentationScenario(kind); Render(f.Before, new(), false);
            var phases = new List<string>();
            void OnPhase(string phase)
            {
                phases.Add(phase);
                if (kind == "counter-trigger" && phase is "deployment-settled" or "counter-reveal")
                {
                    if (!_cards.TryGetValue("deployed-enemy", out var unit) || !unit.Visible || unit.Mode != BattleCardMode.Field)
                        throw new Exception("Counter triggered before deployed unit occupied the field.");
                }
                if (kind == "counter-trigger" && phase == "unit-destroyed" && _combat.GetChildren().OfType<CombatDamage>().Any())
                    throw new Exception("Destroy effect fabricated a damage amount.");
            }
            _sequence.PhaseChanged += OnPhase; _combat.PhaseChanged += OnPhase;
            await PresentSequenceAsync(f.Resolution, f.Actions); _sequence.PhaseChanged -= OnPhase; _combat.PhaseChanged -= OnPhase;
            if (!MatchesProjection(f.Resolution.After) || _sequence.LiveCardCount != 0 || _combat.LiveEffectCount != 0 || _busy)
                throw new Exception($"Feedback cleanup/state failed: {kind}");
            if (kind == "counter-arm" && (!_cards["demo-counter"].View!.IsCounterArmed || _cards["demo-counter"].Mode != BattleCardMode.Hand
                || _state.SelfLine.Count != f.Before.SelfLine.Count || !phases.Contains("counter-armed"))) throw new Exception("Arming moved a counter onto the field.");
            if (kind == "counter-disarm" && _cards["demo-counter"].View!.IsCounterArmed) throw new Exception("Counter remained armed.");
            if (kind == "counter-trigger" && (_cards.ContainsKey("demo-counter") || _cards.ContainsKey("deployed-enemy")
                || phases.IndexOf("deployment-settled") < 0 || phases.IndexOf("counter-reveal") <= phases.IndexOf("deployment-settled")
                || phases.IndexOf("unit-destroyed") <= phases.IndexOf("counter-reveal")))
                throw new Exception("Deployment/counter/destruction order failed.");
            if (kind == "status-heal" && _cards["self-0"].View!.Health != 4) throw new Exception("Heal value was not projected.");
            if (kind == "status-buff" && _cards["self-0"].View!.EffectiveAttack != 4) throw new Exception("Buff value was not projected.");
            if (kind == "status-suppressed" && (!phases.Contains("status-suppressed") || !phases.Contains("status-cleared") || _cards["self-0"].View!.IsSuppressed))
                throw new Exception("Suppression/clear sequence failed.");
            if (kind == "status-cost" && _cards["hand-0"].View!.EffectiveCost != 0) throw new Exception("Hand cost projection failed.");
        }
        ResetDemo(); var hidden = _demo.CreatePresentationScenario("counter-hidden"); ApplyProjection(hidden.Before, new());
        if (((UiCounterPresentation)hidden.Resolution.Steps[0]).Card is not null) throw new Exception("Opponent armed counter retained identity.");
        await PresentSequenceAsync(hidden.Resolution, hidden.Actions);
        if (_history.GetChildCount() != 0 || _state.EnemyHandCount != 5 || _state.EnemyKredits != hidden.Before.EnemyKredits)
            throw new Exception("Opponent arming exposed private information.");
        ResetDemo(); var draw = _demo.CreatePresentationScenario("draw"); Render(draw.Before, new(), false);
        var movedDuringFlip = false; var centered = false;
        void OnDraw(string phase)
        {
            var paper = _sequence.GetChildren().OfType<BattleCard>().FirstOrDefault();
            if (paper is null) return;
            if (phase == "draw-flip") movedDuringFlip = (paper.Position + paper.Size / 2).DistanceTo(new(640, 360)) > 20 && Math.Abs(paper.Rotation) > .01;
            if (phase == "draw-reveal") centered = (paper.Position + paper.Size / 2).DistanceTo(new(640, 360)) < 1;
        }
        _sequence.PhaseChanged += OnDraw; await PresentSequenceAsync(draw.Resolution, draw.Actions); _sequence.PhaseChanged -= OnDraw;
        if (!movedDuringFlip || !centered) throw new Exception("Draw flip was not simultaneous with travel to center.");
        foreach (var kind in new[] { "draw", "counter-trigger", "status-suppressed" })
        {
            ResetDemo(); var f = _demo.CreatePresentationScenario(kind); Render(f.Before, new(), false);
            var running = PresentSequenceAsync(f.Resolution, f.Actions);
            await ToSignal(GetTree().CreateTimer(.12), SceneTreeTimer.SignalName.Timeout); CancelSelection(); await running;
            if (!MatchesProjection(f.Resolution.After) || _sequence.LiveCardCount != 0 || _busy || _cards.Values.Any(c => c.Scale != Vector2.One || c.PaperShear != 0))
                throw new Exception("Feedback interruption retained temporary transforms.");
        }
        _clock.SetReducedMotion(true);
        foreach (var kind in FeedbackScenarios) await StartPresentationScenarioAsync(kind);
        _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); ResetDemo();
        GD.Print("BATTLE_STAGE4_VERIFY_OK center-draw simultaneous-flip counter-arm disarm trigger hidden heal buff suppress clear cost interrupt reduced-motion effects=0");
    }
    public async Task CaptureFeedbackFramesAsync(string dir)
    {
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
        foreach (var kind in FeedbackScenarios)
        {
            var captures = new List<Task>();
            async Task Capture(string phase)
            {
                await ToSignal(GetTree().CreateTimer(.12), SceneTreeTimer.SignalName.Timeout);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng($"{dir}/battle-stage4-{kind}-{phase}.png");
            }
            void OnPhase(string phase)
            {
                if (phase.StartsWith("status-") || phase is "counter-armed" or "counter-disarmed" or "counter-reveal" or "deployment-settled" or "unit-destroyed") captures.Add(Capture(phase));
            }
            _sequence.PhaseChanged += OnPhase; _combat.PhaseChanged += OnPhase;
            await StartPresentationScenarioAsync(kind); _sequence.PhaseChanged -= OnPhase; _combat.PhaseChanged -= OnPhase;
            await Task.WhenAll(captures);
        }
        _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); ResetDemo();
    }
}
