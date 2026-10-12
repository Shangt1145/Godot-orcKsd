using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    private PresentationPlayer? _demoPresentations;
    private long _demoSequence;
    private int _demoPlaybackEpoch;

    private void StopDemoPlayback()
    {
        _demoPlaybackEpoch++;
        _demoPresentations?.Dispose(); _demoPresentations = null;
    }

    private void QueueDemoPlayback(UiCombatResolution? combat = null, UiCardView? deployment = null, bool direct = true)
    {
        var before = _liveInputView ?? _state;
        _liveInputView = UiSnapshots.Freeze(_demo.State);
        _liveInputActions = UiSnapshots.Freeze(_demo.Actions);
        _turnInput.Observe(_liveInputView);
        RefreshTurnInput();
        if (_demoPresentations is null)
        {
            _demoPresentations = new(_demo.State.MatchId, (resolution, actions, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return PresentSequenceAsync(resolution, actions);
            }, _state);
            _demoPresentations.Failed += error => Godot.GD.PushError($"[demo presentation] {error}");
        }
        var sequence = ++_demoSequence;
        UiPresentationStep[] steps = deployment is null ? [] : [new UiDeploymentPresentation(deployment, _liveInputView)];
        if (before.ActivePlayerSide != _liveInputView.ActivePlayerSide)
            steps = [new UiTurnPresentation(_liveInputView.ActivePlayerSide, _liveInputView.Turn), .. steps];
        if (combat is not null)
        {
            var hits = new List<UiOrderImpact> { new(combat.Defender, combat.DefenderAfter, combat.DamageToDefender, combat.Attacker) };
            if (combat.DamageToAttacker > 0) hits.Add(new(combat.Attacker, combat.AttackerAfter, combat.DamageToAttacker, combat.Defender));
            steps = [new UiBoardImpactsPresentation(hits)];
        }
        _ = _demoPresentations.Enqueue(new(_demo.State.MatchId, steps, _liveInputView)
        { Before = before, Sequence = sequence, Priority = direct ? UiPresentationPriority.Direct : UiPresentationPriority.Indirect }, _liveInputActions);
    }
}
