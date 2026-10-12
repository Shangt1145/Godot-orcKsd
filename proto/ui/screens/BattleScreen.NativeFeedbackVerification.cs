using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class BattleScreen
{
    internal ConfirmationDialog ConcedeDialogForVerification => _concedeDialog;
    internal bool ConcedeEnabledForVerification => !_menu.IsItemDisabled(_menu.GetItemIndex(29));
    internal bool ResultVisibleForVerification => _result.Visible;
    internal int PendingPresentationEffects => _sequence.LiveCardCount + _combat.LiveEffectCount + _removalActors.Count;
    internal IReadOnlyList<string> ResultLabelsForVerification => _result.GetChildren().OfType<Label>().Select(l => l.Text).ToArray();
    internal Button RestartForVerification => _result.GetChildren().OfType<Button>().Single();
    internal void OpenConcedeForVerification() => _menu.EmitSignal(PopupMenu.SignalName.IdPressed, 29);

    public async Task VerifyNativeFeedbackAsync(NativeStateFeedback fixture, Func<string, Task>? capture = null)
    {
        var speed = _clock.Current; var reduced = _clock.ReducedMotion;
        var phases = new List<string>();
        void OnPhase(string phase) => phases.Add(phase);
        try
        {
            _clock.SetSpeed(AnimClock.Speed.Faster); PresentationPhase += OnPhase;
            foreach (var quiet in new[] { false, true })
            {
                _clock.SetReducedMotion(quiet); phases.Clear(); ApplyProjection(fixture.Initial, new() { CanEndTurn = true });
                foreach (var resolution in fixture.Resolutions)
                    await PresentSequenceAsync(resolution, new() { CanEndTurn = true });
                var expected = fixture.Resolutions.SelectMany(r => r.Steps).OfType<UiStatusPresentation>()
                    .Select(s => "status-" + s.Status.ToString().ToLowerInvariant()).ToArray();
                if (!expected.SequenceEqual(phases.Where(p => p.StartsWith("status-")))
                    || !phases.Contains("shuffle-self") || !phases.Contains("shuffle-enemy")
                    || !MatchesProjection(fixture.Final) || PendingPresentationEffects != 0)
                    throw new Exception($"Native state UI feedback failed (quiet={quiet}): {string.Join(',', phases)}");
                var view = fixture.Final.SelfLine.Single(); var actor = _cards[view.Uid]; ShowDetail(actor);
                if (!_inspectStats.Text.Contains("烟幕") || !_inspectStats.Text.Contains("被抑制")
                    || !_inspectStats.Text.Contains("轰炸机") || actor.View!.IsVeteran || !actor.View.IsSilenced)
                    throw new Exception("Runtime keyword/type detail failed to reflect native replacement.");
                if (capture is not null) await capture(quiet ? "states-reduced" : "states");
                CancelSelection();
            }
            GD.Print("NATIVE_STATE_UI_VERIFY_OK upgrade reveal suppress extend clear inhibit runtime-keywords types hq-heal both-shuffles normal-and-reduced effects=0");
        }
        finally { PresentationPhase -= OnPhase; _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced); ResetDemo(); }
    }
    public async Task VerifyNativeHqZeroAsync(NativeStateFeedback fixture, Func<string, Task>? capture = null)
    {
        ApplyProjection(fixture.Initial, new() { CanEndTurn = true });
        foreach (var resolution in fixture.Resolutions) await PresentSequenceAsync(resolution, new());
        if (!ResultVisibleForVerification || !ResultLabelsForVerification.Contains("胜利")
            || !ResultLabelsForVerification.Contains("总部被摧毁") || _cards.ContainsKey(fixture.Final.EnemyHq!.Uid)
            || !MatchesProjection(fixture.Final) || EndTurnEnabled || PendingPresentationEffects != 0)
            throw new Exception("Native HQ-zero result retained a destroyed headquarters or failed to settle.");
        if (capture is not null) await capture("hq-zero");
        GD.Print("NATIVE_HQ_ZERO_UI_VERIFY_OK native-attack destruction victory no-hq-resurrection input-locked effects=0");
    }
}
