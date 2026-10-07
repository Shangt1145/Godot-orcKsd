using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    private PresentationPlayer? _demoPresentations;
    private readonly Dictionary<long, UiCombatResolution> _demoCombats = new();
    private long _demoSequence;
    private int _demoPlaybackEpoch;

    private void StopDemoPlayback()
    {
        _demoPlaybackEpoch++;
        _demoPresentations?.Dispose(); _demoPresentations = null; _demoCombats.Clear();
    }

    private void QueueDemoPlayback(UiCombatResolution? combat = null)
    {
        _liveInputView = UiSnapshots.Freeze(_demo.State);
        _liveInputActions = UiSnapshots.Freeze(_demo.Actions);
        UpdateSelection(); UpdateEndTurnButton();
        if (_demoPresentations is null)
        {
            _demoPresentations = new(_demo.State.MatchId, (resolution, actions, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return _demoCombats.Remove(resolution.Sequence, out var battle)
                    ? PresentCombatAsync(battle, actions) : PresentSequenceAsync(resolution, actions);
            });
            _demoPresentations.Failed += error => Godot.GD.PushError($"[demo presentation] {error}");
        }
        var sequence = ++_demoSequence;
        if (combat is not null) _demoCombats[sequence] = UiSnapshots.Freeze(combat);
        _ = _demoPresentations.Enqueue(new(_demo.State.MatchId, [], _liveInputView) { Sequence = sequence }, _liveInputActions);
    }
}
