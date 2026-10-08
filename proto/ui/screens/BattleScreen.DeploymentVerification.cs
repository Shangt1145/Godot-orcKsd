using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    private int VisibleCopies(string uid) => _cardLayer.GetChildren().Concat(_sequence.GetChildren()).OfType<BattleCard>()
        .Count(c => c.Uid == uid && c.IsVisibleInTree() && !c.IsQueuedForDeletion());

    public async Task VerifyDeploymentVisualsAsync(Func<string, Task>? capture = null)
    {
        var speed = _clock.Current; var reduced = _clock.ReducedMotion;
        var samples = 0;
        try
        {
            foreach (var current in Enum.GetValues<AnimClock.Speed>())
            foreach (var quiet in new[] { false, true })
            foreach (var side in new[] { "self", "enemy" })
            foreach (var defense in new[] { 3, 6, 10 })
            {
                _clock.SetSpeed(current); _clock.SetReducedMotion(quiet); ResetDemo();
                var before = _state with { MatchId = $"deployment-visual-{current}-{quiet}-{side}" };
                var unit = before.SelfHand[0] with { Uid = "deployment-visual-actor", OwnerSide = side, Zone = "hand",
                    EffectiveDefense = defense, Health = defense };
                before = side == "self" ? before with { SelfHand = [unit], SelfHandCount = 1 }
                    : before with { SelfHand = [], SelfHandCount = 0, EnemyHandCount = 1 };
                var placed = unit with { Zone = "support", SlotIndex = 3 };
                var after = side == "self" ? before with { SelfHand = [], SelfHandCount = 0, SelfLine = before.SelfLine.Append(placed).ToArray() }
                    : before with { EnemyHandCount = 0, EnemyLine = before.EnemyLine.Append(placed).ToArray() };
                ApplyProjection(before, new());
                if (side == "self")
                {
                    _deploymentOrigins[unit.Uid] = new(new(792, 392), new(144, 202), Vector2.One, .07f);
                    RestorePendingDeploymentPoses();
                }
                var expectedOrigin = side == "self" ? _deploymentOrigins[unit.Uid]
                    : DeploymentPose.From(_cardLayer.GetChildren().OfType<BattleCard>().Last(c => c.Mode == BattleCardMode.Hidden));
                Vector2? actualOrigin = null;
                void OnStart(string phase)
                {
                    if (phase != "deployment-start") return;
                    var actor = _sequence.GetChildren().OfType<BattleCard>().Single(c => c.Uid == unit.Uid);
                    actualOrigin = actor.Position + actor.Size / 2;
                }
                _sequence.PhaseChanged += OnStart;
                var playback = PresentSequenceAsync(new(before.MatchId, [new UiDeploymentPresentation(placed, after)], after), new());
                _sequence.PhaseChanged -= OnStart;
                if (!quiet && (actualOrigin is null || actualOrigin.Value.DistanceTo(expectedOrigin.Center) > .1f))
                    throw new Exception($"Deployment restarted away from its origin: {side}/{defense}: {actualOrigin} expected {expectedOrigin.Center}.");
                var photographed = false;
                while (!playback.IsCompleted)
                {
                    if (VisibleCopies(unit.Uid) != 1)
                        throw new Exception($"Deployment has {VisibleCopies(unit.Uid)} visible copies: {current}/{quiet}/{side}/{defense}.");
                    if (_sequence.GetChildren().OfType<BattleCard>().Any(c => c.Uid == unit.Uid)
                        && _cards[unit.Uid].Visible)
                        throw new Exception("Field card became visible before the deployment actor retired.");
                    samples++;
                    if (capture is not null && !photographed && current == AnimClock.Speed.Normal && !quiet)
                    {
                        await ToSignal(GetTree().CreateTimer(.2), SceneTreeTimer.SignalName.Timeout);
                        await capture($"deployment-{side}-{defense}-in-flight"); photographed = true;
                    }
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                await playback;
                if (VisibleCopies(unit.Uid) != 1 || _ghosts.Count != 0 || _sequence.GetChildren().OfType<BattleCard>().Any()
                    || !_cards[unit.Uid].Visible || !MatchesProjection(after) || _deploymentOrigins.Count != 0)
                    throw new Exception("Deployment did not leave exactly one settled field card.");
                if (capture is not null && current == AnimClock.Speed.Normal && !quiet)
                    await capture($"deployment-{side}-{defense}-settled");
            }
            await VerifyDeploymentGestureOriginsAsync();
            GD.Print($"DEPLOYMENT_SINGLE_VISUAL_VERIFY_OK cases=36 frames={samples} self-enemy all-tiers all-speeds reduced-motion release-origin");
        }
        finally { ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced); }
    }

    private async Task VerifyDeploymentGestureOriginsAsync()
    {
        ResetDemo();
        var before = _state with { MatchId = "deployment-release-origin" };
        var unit = before.SelfHand.First(c => c.Definition.CardType == "unit");
        before = before with { SelfHand = [unit], SelfHandCount = 1 };
        ApplyProjection(before, new() { PlayableUids = [unit.Uid] });
        UiCommand? sent = null;
        DeploymentPose? held = null;
        void OnCommand(UiCommand command) => sent = command;
        CommandRequested += OnCommand;
        try
        {
            await DragAsync(unit.Uid, new(945, 535), () =>
            {
                held = DeploymentPose.From(_cards[unit.Uid]);
                return Task.CompletedTask;
            });
            if (sent is not PlayCard || held is null || !_deploymentOrigins.TryGetValue(unit.Uid, out var saved)
                || saved != held.Value || DeploymentPose.From(_cards[unit.Uid]) != held.Value || VisibleCopies(unit.Uid) != 1)
                throw new Exception("Releasing a deployment lost its held pose or duplicated the queued card.");
            RefuseCommand(sent);
            if (_deploymentOrigins.Count != 0 || _cards[unit.Uid].Position != _cards[unit.Uid].RestPosition
                || _cards[unit.Uid].MouseFilter != MouseFilterEnum.Stop)
                throw new Exception("Refused deployment retained its release pose or disabled the hand card.");
            await DragAsync(unit.Uid, new(960, 674));
            if (_deploymentOrigins.Count != 0 || _cards[unit.Uid].Position != _cards[unit.Uid].RestPosition)
                throw new Exception("An invalid drop retained a deployment origin.");
        }
        finally { CommandRequested -= OnCommand; ResetDemo(); }
    }

    public async Task VerifyDemoNonBlockingAsync()
    {
        var speed = _clock.Current; var reduced = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false); ResetDemo();
        var deployments = 0;
        void OnDeployment(string phase) { if (phase == "deployment-start") deployments++; }
        _sequence.PhaseChanged += OnDeployment;
        try
        {
            var initial = _demo.State;
            var first = _demo.State.SelfHand.Where(c => _demo.Actions.PlayableUids.Contains(c.Uid))
                .OrderBy(c => c.EffectiveCost).First().Uid;
            await DragAsync(first, DropAtRowEdge(false, true));
            var playback = _demoPresentations?.Completion ?? throw new Exception("Default battle did not queue playback.");
            var second = _demo.State.SelfHand.Where(c => _demo.Actions.PlayableUids.Contains(c.Uid))
                .OrderBy(c => c.EffectiveCost).First().Uid;
            await DragAsync(second, DropAtRowEdge(false, true), () =>
            {
                if (playback.IsCompleted || !_dragging) throw new Exception("Default battle blocks input during animation.");
                return Task.CompletedTask;
            });
            if (playback.IsCompleted || _demo.State.SelfHand.Count != initial.SelfHand.Count - 2)
                throw new Exception("Default battle did not accept the second operation immediately.");
            await ClickEndTurnAsync();
            if (_demo.State.ActivePlayerSide != "enemy" || EndTurnEnabled)
                throw new Exception("Default battle end-turn input remained tied to animation.");
            await _demoPresentations!.Completion;
            if (!MatchesProjection(_demo.State) || VisibleCopies(first) != 1 || VisibleCopies(second) != 1
                || deployments != 2 || _deploymentOrigins.Count != 0)
                throw new Exception($"Default battle queued deployments did not settle once each: projection={MatchesProjection(_demo.State)} copies={VisibleCopies(first)}/{VisibleCopies(second)} deployments={deployments} origins={_deploymentOrigins.Count}.");
            GD.Print("DEMO_NONBLOCKING_INPUT_VERIFY_OK two-default-battle-drags immediate-settlement endturn-during-animation FIFO-final-projection");
        }
        finally { _sequence.PhaseChanged -= OnDeployment; ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced); }
    }
}
