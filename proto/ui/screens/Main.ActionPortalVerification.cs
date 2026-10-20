using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyInFlightDrawOnly()
    {
        try { await VerifyInFlightDrawAsync(); GetTree().Quit(); }
        catch (Exception error) { GD.PushError(error.ToString()); StopRealMatch(); GetTree().Quit(1); }
    }
    private async void VerifyActionPortals()
    {
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        try
        {
            StopRealMatch(); Show("battle");
            _clock.SetSpeed(AnimClock.Speed.Faster); _clock.SetReducedMotion(false);
            await VerifyPendingFormationLayoutAsync();
            var checks = 0;
            for (var count = 0; count < 4; count++)
            for (var rank = 0; rank < count + 2; rank++)
            {
                using var fixture = await OrcFormationScenario.CreateAsync(count,
                    _ => "res://proto/art/USG/units/_4.png");
                _battle.ApplyProjection(fixture.View, fixture.Actions); fixture.SetInputReader(_battle.ReadInteractionState);
                UiCommand? sent = null; Task<UiSubmitOutcome>? submit = null;
                void OnCommand(UiCommand command) { sent = command; submit = fixture.SubmitAsync(command); }
                _battle.CommandRequested += OnCommand;
                var offered = fixture.Actions.DeploymentGaps[rank];
                try
                {
                    await _battle.DragAsync(fixture.ActorUid, _battle.DeploymentGapPoint(rank), async () =>
                    {
                        if (_battle.DeploymentMarkerCount != count + 2)
                            throw new Exception("Support drag omitted an engine insertion gap.");
                        if (count == 3 && rank == 2) await ActionPortalCaptureAsync("last-capacity-five-gaps");
                    });
                }
                finally { _battle.CommandRequested -= OnCommand; }
                if (sent is not PlayCard { SupportIndex: null } play || play.GapId != offered.GapId
                    || submit is null || await submit != UiSubmitOutcome.Applied)
                    throw new Exception($"Native gap gesture failed: {count}/{rank}: {sent}");
                _battle.UpdateInputProjection(fixture.View, fixture.Actions);
                foreach (var resolution in fixture.Resolutions) await _battle.PresentSequenceAsync(resolution, fixture.Actions);
                var expected = fixture.View.SelfLine.Append(fixture.View.SelfHq!).OrderBy(c => c.SlotIndex).Select(c => c.Uid);
                if (!_battle.RenderedSupportUids().SequenceEqual(expected) || _battle.PendingActorForVerification(fixture.ActorUid))
                    throw new Exception($"Native support formation and rendered UID order disagree: {count}/{rank}");
                checks++;
            }
            GD.Print($"SUPPORT_GAPS_NATIVE_UI_VERIFY_OK checks={checks} HQ-boundaries middle-insertion full-line logical-order");
            await VerifyInFlightDrawAsync();
            await VerifyOpponentPlaybackInputAsync();
            GD.Print("ACTION_PORTALS_VERIFY_OK"); StopRealMatch(); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); StopRealMatch(); GetTree().Quit(1); }
        finally { _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); }
    }

    private async Task VerifyPendingFormationLayoutAsync()
    {
        using var fixture = await OrcFormationScenario.CreateAsync(0);
        _battle.ApplyProjection(fixture.View, fixture.Actions); fixture.SetInputReader(_battle.ReadInteractionState);
        var uid = fixture.ActorUid;
        if (await fixture.SubmitAsync(new PlayCard(uid, GapId: fixture.Actions.DeploymentGaps[0].GapId)) != UiSubmitOutcome.Applied)
            throw new Exception("Pending formation fixture failed to commit.");
        _battle.UpdateInputProjection(fixture.View, fixture.Actions);
        if (_battle.RenderedSupportUids().Contains(uid) || !fixture.View.SelfLine.Any(c => c.Uid == uid))
            throw new Exception("Pending formation fixture must keep playback behind the native model.");
        var positions = Enumerable.Range(0, fixture.Actions.DeploymentGaps.Count).Select(_battle.DeploymentGapPoint).ToArray();
        if (positions.Length != 3 || positions.Zip(positions.Skip(1)).Any(pair => pair.Second.X <= pair.First.X))
            throw new Exception("Pending deployment hand poses scrambled the native insertion gap order.");
        GD.Print("PENDING_FORMATION_GAPS_VERIFY_OK native-ahead-of-playback no-hand-pose-anchor ordered-gaps");
    }

    private async Task VerifyInFlightDrawAsync()
    {
        StopRealMatch(); Show("battle");
        _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
        var runner = new OrcMatchRunner { InteractiveMulligan = false, DeckSeed = 37, FirstPlayerIndex = 0 };
        BindRunner(runner); await runner.StartAsync();
        _battle.UpdateInputProjection(runner.CurrentView!, runner.CurrentActions);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!_battle.EndTurnEnabled)
            throw new Exception($"Initial native turn was not interactive: {runner.CurrentView!.Phase}/{runner.CurrentView.ActivePlayerSide}; {_battle.ReadInteractionState()}");
        var opening = runner.CurrentView!.SelfHand.Select(c => c.Uid).ToHashSet();
        var phases = new List<string>();
        void OnPhase(string phase) => phases.Add(phase);
        _battle.PresentationPhase += OnPhase;
        try
        {
            await _battle.ClickEndTurnAsync();
            string? incoming = null;
            var deadline = Time.GetTicksMsec() + 20000;
            while (Time.GetTicksMsec() < deadline)
            {
                runner.Pump();
                incoming = runner.CurrentView!.SelfHand.FirstOrDefault(c => !opening.Contains(c.Uid))?.Uid;
                if (incoming is not null && _battle.IsIncomingCardFlying(incoming) && _battle.EndTurnEnabled) break;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (incoming is null || !_battle.IsIncomingCardFlying(incoming))
                throw new Exception($"Native friendly draw did not begin: turn={runner.CurrentView!.Turn} input={_battle.ReadInteractionState()} phases={string.Join(',', phases)}");
            phases.Clear();
            await ToSignal(GetTree().CreateTimer(.24), SceneTreeTimer.SignalName.Timeout);
            if (!_battle.IsIncomingCardFlying(incoming) || phases.Contains("draw-settled"))
                throw new Exception("Flying draw test waited until the card had arrived.");
            await ActionPortalCaptureAsync("draw-in-flight");
            await _battle.DragFlyingCardAsync(incoming, _battle.DeploymentGapPoint(0));
            await WaitForInputCommitAsync(runner, view => view.SelfLine.Any(c => c.Uid == incoming));
            if (!phases.Contains("draw-taken") || !_presentations!.IsBusy)
                throw new Exception("Flying draw did not yield to an immediately committed native deployment.");
            await AwaitIdleAsync(20);
            if (!_battle.MatchesProjection(runner.CurrentView!) || _battle.PendingActorForVerification(incoming))
                throw new Exception("Taken draw reappeared in hand or left the actor locked.");
            await ActionPortalCaptureAsync("draw-taken-deployed");
            GD.Print("INFLIGHT_DRAW_NATIVE_UI_VERIFY_OK real-mouse mid-flight take native-play no-wait no-resurrection final-projection");
        }
        finally { _battle.PresentationPhase -= OnPhase; StopRealMatch(); }
    }

    private async Task ActionPortalCaptureAsync(string name)
    {
        if (!OS.GetCmdlineUserArgs().Contains("--capture-action-portals")) return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var directory = OS.GetEnvironment("KARDS_CAPTURE_DIR");
        if (string.IsNullOrEmpty(directory)) directory = "res://artifacts/action-portals";
        DirAccess.MakeDirRecursiveAbsolute(directory);
        var result = GetViewport().GetTexture().GetImage().SavePng(directory.TrimEnd('/', '\\') + "/" + name + ".png");
        if (result != Error.Ok) throw new Exception($"Portal capture failed: {result}");
    }
}
