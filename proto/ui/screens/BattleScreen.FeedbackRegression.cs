using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    public async Task VerifyFeedbackRegressionsAsync(Func<string, Task> capture)
    {
        var speed = _clock.Current; var reduced = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
        try
        {
            ResetDemo(); var draw = _demo.CreatePresentationScenario("draw"); Render(draw.Before, new(), false);
            var drawn = draw.Resolution.Steps.OfType<UiDrawPresentation>().Single();
            var starts = 0;
            void DrawPhase(string phase)
            {
                if (phase != "draw-start") return;
                starts++;
                if (!_cards.TryGetValue(drawn.Card!.Uid, out var card) || card.Position.DistanceTo(new(1156, 618)) > 1)
                    throw new Exception("Incoming card did not begin at the deck.");
            }
            _sequence.PhaseChanged += DrawPhase;
            using (var player = new PresentationPlayer(draw.Before.MatchId, (r, a, _) => PresentSequenceAsync(r, a), draw.Before))
            {
                var task = player.Enqueue(draw.Resolution with { Steps = [new UiTurnPresentation("self", draw.Before.Turn + 1, UiTurnKind.TurnStarted), drawn] }, draw.Actions);
                while (!task.IsCompleted && starts == 0)
                {
                    if (_cards.TryGetValue(drawn.Card!.Uid, out var early) && early.Visible)
                        throw new Exception("Drawn card flashed in hand before its draw began.");
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                await capture("draw-start"); await task;
            }
            _sequence.PhaseChanged -= DrawPhase;
            if (starts != 1) throw new Exception("Incoming card draw did not run exactly once.");
            GD.Print("TURN_DRAW_VISIBILITY_OK no-hand-flash one-deck-origin-flight");

            ResetDemo(); var initial = _state;
            var source = initial.SelfHand.First(c => c.Definition.CardType == "unit");
            var target = initial.EnemyLine.First(c => c.Visibility == Visibility.Full);
            _deploymentOrigins[source.Uid] = new(new(580, 460), new(144, 202), Vector2.One, 0);
            _deploymentChoiceCenters[source.Uid] = new(1000, SelfArea.GetCenter().Y);
            RestorePendingDeploymentPoses();
            UiTargetRequest Request(string id) => new(id, [new("pick", ChoiceKind.Reference, 1, 1,
                [new(target.Uid, target.Definition.Name, ChoiceKind.Reference, target)])]) { SourceCard = source, SelectorName = "fieldUnit" };
            var request = Request("pending-deployment"); var responder = new MockTargetResponder(request);
            ShowTargetChoice(request, responder);
            for (var i = 0; i < 3; i++)
            {
                Render(initial, _actions, true);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (_cards[source.Uid].Mode != BattleCardMode.Field || _cards[source.Uid].Position.Y >= 600
                    || _handMotion.ContainsKey(source.Uid) || _ghosts.Count > 0)
                    throw new Exception("Pending deployment replayed a hand return during a projection refresh.");
                if (i == 1) { CloseTargetChoice(); ShowTargetChoice(request, responder); }
            }
            await capture("deployment-waiting-target");
            await ClickFixtureAsync(CardPoint(target.Uid)); await WaitChoiceClosedAsync();
            if (responder.Calls != 1 || _cards[source.Uid].Mode != BattleCardMode.Field)
                throw new Exception("Target click did not confirm or returned the accepted deployment to hand.");
            GD.Print("DEPLOYMENT_CHOICE_STABILITY_OK refresh-and-request-transition no-hand-return click-release-confirms");

            _deploymentOrigins.Clear(); _deploymentChoiceCenters.Clear(); ResetDemo();
            var units = _state.SelfLine.Concat(_state.EnemyLine).Where(c => c.Visibility == Visibility.Full).Take(3).ToArray();
            if (units.Length != 3) throw new Exception("Batch feedback fixture needs three visible units.");
            var ids = units.Select(c => c.Uid).ToHashSet();
            var status = units.Select(c => new UiStatusPresentation(c, c with { IsSuppressed = true }, UiStatusKind.Suppressed)).ToArray();
            var began = new List<ulong>();
            void StatusPhase(string phase) { if (phase == "status-suppressed") began.Add(Time.GetTicksMsec()); }
            _sequence.PhaseChanged += StatusPhase;
            var statusAfter = _state with { SelfLine = _state.SelfLine.Select(c => ids.Contains(c.Uid) ? c with { IsSuppressed = true } : c).ToArray(),
                EnemyLine = _state.EnemyLine.Select(c => ids.Contains(c.Uid) ? c with { IsSuppressed = true } : c).ToArray() };
            await PresentSequenceAsync(new(_state.MatchId, status, statusAfter), _actions);
            _sequence.PhaseChanged -= StatusPhase;
            if (began.Count != units.Length || began.Max() - began.Min() > 40)
                throw new Exception("Independent status updates were serialized.");
            var removed = units.Select(c => new UiRemovalPresentation(c)).ToArray();
            var removedAfter = _state with { SelfLine = _state.SelfLine.Where(c => !ids.Contains(c.Uid)).ToArray(),
                EnemyLine = _state.EnemyLine.Where(c => !ids.Contains(c.Uid)).ToArray() };
            began.Clear();
            void RemovalPhase(string phase) { if (phase == "unit-destroyed") began.Add(Time.GetTicksMsec()); }
            _combat.PhaseChanged += RemovalPhase;
            await PresentSequenceAsync(new(_state.MatchId, removed, removedAfter), _actions);
            _combat.PhaseChanged -= RemovalPhase;
            if (began.Count != units.Length || began.Max() - began.Min() > 40)
                throw new Exception("Independent removals were serialized.");
            await capture("numbers-and-removal-settled");
            GD.Print("BATCH_FEEDBACK_PACING_OK parallel-status parallel-removal shortened-cues");
        }
        finally { _deploymentOrigins.Clear(); _deploymentChoiceCenters.Clear(); CloseTargetChoice(); ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced); }
    }
}
