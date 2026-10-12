using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyNativeFeedback()
    {
        try { await VerifyNativeFeedbackAsync(); StopRealMatch(); GetTree().Quit(); }
        catch (Exception e) { GD.PushError(e.ToString()); StopRealMatch(); GetTree().Quit(1); }
    }
    private async Task VerifyNativeFeedbackAsync()
    {
        StopRealMatch(); Show("battle");
        var fixture = await OrcStateFeedbackVerification.RunAsync();
        await _battle.VerifyNativeFeedbackAsync(fixture, NativeFeedbackCaptureAsync);
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Faster); _clock.SetReducedMotion(false);
        var phases = new List<string>();
        void OnPhase(string phase) => phases.Add(phase);
        async Task Until(Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!done())
            {
                if (DateTime.UtcNow > deadline) throw new Exception("Native terminal/restart verification timed out.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        try
        {
            await _battle.VerifyNativeHqZeroAsync(await OrcStateFeedbackVerification.RunHqZeroAsync(), NativeFeedbackCaptureAsync);
            await StartRealMatchAsync(false, manualOpponentForVerification: true, deckSeed: 17, verificationHand: ["USG/units/_4"]);
            var runner = _runner!; var hand = runner.CurrentView!.SelfHand;
            if (!_battle.ConcedeEnabledForVerification) throw new Exception("Friendly native turn has no concede entry.");
            _battle.OpenConcedeForVerification(); await DeckClickAsync(_battle.ConcedeDialogForVerification.GetCancelButton());
            if (runner.CurrentView.Phase != "play" || _battle.ConcedeDialogForVerification.Visible)
                throw new Exception("Cancelling concede changed the game or left the dialog open.");
            await runner.SubmitAsync(new EndTurn()); runner.Pump();
            if (_battle.ConcedeEnabledForVerification) throw new Exception("Enemy turn enabled concede.");
            _battle.OpenConcedeForVerification(); if (_battle.ConcedeDialogForVerification.Visible) throw new Exception("Enemy turn opened concede dialog.");
            runner.SwitchViewerForVerification(); await runner.SubmitAsync(new EndTurn()); runner.SwitchViewerForVerification();
            await AwaitIdleAsync(20);
            _battle.PresentationPhase += OnPhase;
            if (hand[0].EffectiveCost > runner.CurrentView!.SelfKredits) throw new Exception("Concede fixture lacks deployment credits.");
            await _battle.DragAsync(hand[0].Uid, _battle.DropAtRowEdge(false, false));
            await WaitForInputCommitAsync(runner, v => v.SelfLine.Any(c => c.Uid == hand[0].Uid));
            if (!_presentations!.IsBusy) throw new Exception("Terminal fixture did not preserve queued playback.");
            _battle.OpenConcedeForVerification(); await DeckClickAsync(_battle.ConcedeDialogForVerification.GetOkButton());
            await Until(() => runner.CurrentView!.Phase == "over");
            if (_battle.EndTurnEnabled || _battle.ConcedeEnabledForVerification) throw new Exception("Native terminal state left actions enabled.");
            await AwaitIdleAsync(20);
            if (!_battle.ResultVisibleForVerification || !_battle.ResultLabelsForVerification.Contains("我方认输")
                || runner.CurrentView!.ResultReason != "Concede" || !_battle.MatchesProjection(runner.CurrentView)
                || phases.Count(p => p == "result-defeat") != 1
                || phases.IndexOf("deployment-settled") >= phases.IndexOf("result-defeat"))
                throw new Exception("Native concede/result playback did not settle once after earlier actions.");
            await NativeFeedbackCaptureAsync("concede");
            await DeckClickAsync(_battle.RestartForVerification);
            await Until(() => _runner != runner && _runner?.MulliganPending == true);
            var fresh = _runner!; if (fresh.MatchId == runner.MatchId || runner.IsRunning || _battle.ResultVisibleForVerification
                || _battle.TargetChoiceVisible || _battle.PendingPresentationEffects != 0)
                throw new Exception("Restart retained a result, old runner or old effects.");
            await fresh.SubmitAsync(new ChooseMulligan(fresh.CurrentView!.SelfHand.Select(c => c.Uid).ToArray(), fresh.MulliganRequestId));
            fresh.Pump(); await AwaitIdleAsync(20);
            if (fresh.CurrentView!.Phase != "play" || !_battle.EndTurnEnabled)
                throw new Exception("Restart failed to reach a playable friendly turn.");
            // The same restart entry must cancel a real command parked on its native option request.
            await StartRealMatchAsync(false, manualOpponentForVerification: true, deckSeed: 7, verificationHand: ["deran/command/_13"]);
            var choiceRunner = _runner!;
            for (var round = 0; round < 6; round++)
            {
                await choiceRunner.SubmitAsync(new EndTurn()); choiceRunner.SwitchViewerForVerification();
                await choiceRunner.SubmitAsync(new EndTurn()); choiceRunner.SwitchViewerForVerification();
            }
            await AwaitIdleAsync(20);
            IUiTargetResponder? oldResponder = null; UiTargetRequest? oldRequest = null;
            choiceRunner.TargetRequested += (r, responder) => { oldRequest = r; oldResponder = responder; };
            var order = choiceRunner.CurrentView!.SelfHand.First(c => c.CardId == "deran/command/_13");
            var pending = choiceRunner.SubmitAsync(new PlayCard(order.Uid));
            await Until(() => _battle.TargetChoiceVisible);
            var nextStart = StartRealMatchAsync(false, deckSeed: 9);
            await nextStart; await pending.WaitAsync(TimeSpan.FromSeconds(3));
            if (choiceRunner.IsRunning || _battle.TargetChoiceVisible || _battle.PendingPresentationEffects != 0
                || oldRequest is null || oldResponder is null || oldResponder.Complete(oldRequest.RequestId,
                    new Dictionary<string, IReadOnlyList<string>> { [oldRequest.Slots[0].Name] = ["0"] }))
                throw new Exception("Old native selection survived restart or answered the new game.");
            GD.Print("NATIVE_TERMINAL_UI_VERIFY_OK mouse-cancel-confirm one-result-after-deployment immediate-input-lock restart-to-mulligan old-native-choice-cancelled stale-answer-rejected");
        }
        finally { _battle.PresentationPhase -= OnPhase; StopRealMatch(); _battle.ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); }
    }
    private async Task NativeFeedbackCaptureAsync(string name)
    {
        if (!OS.GetCmdlineUserArgs().Contains("--capture-native-feedback")) return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var dir = ProjectSettings.GlobalizePath("res://artifacts/native-feedback"); System.IO.Directory.CreateDirectory(dir);
        var image = GetViewport().GetTexture().GetImage(); image.SavePng($"{dir}/{name}-{image.GetWidth()}.png");
    }
}
