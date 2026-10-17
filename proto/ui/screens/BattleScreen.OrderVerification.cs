using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    public async Task CaptureOrderFilmAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir); System.IO.File.WriteAllText(dir + "/.gdignore", "");
        var speed = _clock.Current; var reduced = _clock.ReducedMotion;
        var sequenceFrames = _sequence.StepFrames; var combatFrames = _combat.StepFrames;
        _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
        _sequence.StepFrames = _combat.StepFrames = 1;
        var frame = 0;
        async Task Tick()
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SaveJpg($"{dir}/order_{frame++:D4}.jpg", .95f);
        }
        try
        {
            foreach (var targeted in new[] { false, true })
            {
                ResetDemo();
                var definition = _demo.Cards.First(c => c.CardId == (targeted ? "USG/commands/_11" : "USG/commands/_7"));
                var order = new UiCardView { Uid = "film-order", OwnerSide = "self", Zone = "hand", Definition = definition,
                    EffectiveCost = definition.Cost };
                var before = _state with { MatchId = $"order-film-{targeted}", SelfHand = [order], SelfHandCount = 1 };
                var victim = before.EnemyLine[0];
                var hit = victim with { Health = victim.Health - 2, EffectiveDefense = victim.EffectiveDefense - 2 };
                var after = before with { SelfHand = [], SelfHandCount = 0,
                    EnemyLine = targeted ? before.EnemyLine.Select(c => c.Uid == victim.Uid ? hit : c).ToArray() : before.EnemyLine };
                var actions = new UiBattleActions { CanEndTurn = true, PlayableUids = [order.Uid],
                    OrderTargets = targeted ? [new(order.Uid, [victim.Uid])] : [] };
                ApplyProjection(before, actions);
                for (var i = 0; i < 18; i++) await Tick();
                await DragAsync(order.Uid, targeted ? BoardRect(victim.Uid).GetCenter() : new(830, 350), async () =>
                {
                    if (targeted) VerifyOrderArrow(order.Uid, victim.Uid);
                    for (var i = 0; i < 24; i++) await Tick();
                });
                var start = frame;
                var playback = PresentSequenceAsync(new(before.MatchId,
                    [new UiOrderPresentation(order, targeted ? [new(victim, hit, 2)] : [])], after), actions);
                while (!playback.IsCompleted) await Tick();
                await playback;
                for (var i = 0; i < 24; i++) await Tick();
                GD.Print($"[order-film] targeted={targeted} start={start} end={frame}");
            }
            GD.Print($"ORDER_FILM_OK frames={frame} dir={dir}");
        }
        finally
        {
            _sequence.StepFrames = sequenceFrames; _combat.StepFrames = combatFrames;
            ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced);
        }
    }

    public void VerifyOrderArrow(string uid, string? targetUid)
    {
        if (_pressed?.Uid != uid || !_dragging || !_aim.Visible || !IsTargetedOrder(uid)
            || _aim.Preview is not null || _aim.InvalidTarget != (targetUid is null)
            || _pressed.Position.DistanceTo(_pressAt - _grabOffset) > 1
            || _aim.From.DistanceTo(_pressed.Position + _pressed.Size / 2) > 1)
            throw new Exception($"Targeted order did not stay in hand with a direct arrow: pressed={_pressed?.Uid == uid} dragging={_dragging} aim={_aim.Visible} targeted={IsTargetedOrder(uid)} invalid={_aim.InvalidTarget} source={_pressed?.Position} rest={_pressed?.RestPosition} from={_aim.From}.");
        if (targetUid is not null && (!_cards[targetUid].Targeted
            || _aim.To.DistanceTo(_cards[targetUid].Position + _cards[targetUid].Size / 2) > 1))
            throw new Exception("Order arrow did not mark the pointed legal unit.");
    }

    public void CancelOrderDragForVerification()
    {
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true }, true);
        if (_pressed is not null || _selected is not null || _aim.Visible || _orderOrigins.Count != 0)
            throw new Exception("Right-click did not cancel the order arrow.");
    }

    public async Task VerifyOrderVisualsAsync()
    {
        var speed = _clock.Current; var reduced = _clock.ReducedMotion; var count = 0;
        try
        {
            foreach (var current in Enum.GetValues<AnimClock.Speed>())
            foreach (var quiet in new[] { false, true })
            foreach (var side in new[] { "self", "enemy" })
            {
                _clock.SetSpeed(current); _clock.SetReducedMotion(quiet); ResetDemo();
                var order = new UiCardView { Uid = "order-visual-actor", OwnerSide = side, Zone = "hand",
                    Definition = _demo.Cards.First(c => c.CardType == "order"), SlotIndex = 0 };
                var before = _state with { MatchId = $"order-visual-{current}-{quiet}-{side}",
                    SelfHand = side == "self" ? [order] : [], SelfHandCount = side == "self" ? 1 : 0,
                    EnemyHandCount = side == "enemy" ? 1 : 0 };
                var after = before with { SelfHand = [], SelfHandCount = 0, EnemyHandCount = 0 };
                ApplyProjection(before, new());
                var origin = side == "self" ? new DeploymentPose(new(792, 392), new(144, 202), new(1.05f, 1.05f), .07f)
                    : DeploymentPose.From(_cardLayer.GetChildren().OfType<BattleCard>().Last(c => c.Mode == BattleCardMode.Hidden));
                if (side == "self") { _orderOrigins[order.Uid] = origin; RestorePendingDeploymentPoses(); }
                var started = false; var docked = false; var offscreen = quiet; var retired = false;
                var heldReadable = false; ulong holdBegan = 0; double holdDuration = 0;
                DeploymentPose? holdPose = null;
                void OnPhase(string phase)
                {
                    var actor = _sequence.GetChildren().OfType<BattleCard>().FirstOrDefault(c => c.Uid == order.Uid);
                    if (phase == "order-start") started = actor is not null && actor.Position == origin.Position
                        && actor.Size == origin.Size && actor.Scale == origin.Scale && actor.Rotation == origin.Rotation
                        && VisibleCopies(order.Uid) == 1;
                    if (phase == "order-docked") docked = actor is not null
                        && (actor.Position + actor.Size / 2).DistanceTo(new(420, 456)) < 1
                        && Math.Abs(actor.Size.X * actor.Scale.X - 180) < 1 && VisibleCopies(order.Uid) == 1;
                    if (phase == "order-hold" && actor is not null)
                    { holdBegan = Time.GetTicksMsec(); holdPose = DeploymentPose.From(actor); }
                    if (phase == "order-held")
                    {
                        holdDuration = (Time.GetTicksMsec() - holdBegan) / 1000.0;
                        heldReadable = actor is not null && holdPose == DeploymentPose.From(actor)
                            && actor.Modulate.A == 1 && VisibleCopies(order.Uid) == 1;
                    }
                    if (phase == "order-offscreen") offscreen = actor is not null
                        && actor.Position.X + actor.Size.X / 2 + actor.Size.X * actor.Scale.X / 2 < 0;
                    if (phase == "order-retired") retired = actor is null;
                }
                _sequence.PhaseChanged += OnPhase;
                var began = Time.GetTicksMsec();
                try { await PresentSequenceAsync(new(before.MatchId, [new UiOrderPresentation(order, [])], after), new()); }
                finally { _sequence.PhaseChanged -= OnPhase; }
                var duration = (Time.GetTicksMsec() - began) / 1000.0;
                if (!started || !docked || !heldReadable || !offscreen || !retired || _orderOrigins.Count != 0
                    || _sequence.LiveCardCount != 0 || _combat.LiveEffectCount != 0 || !MatchesProjection(after)
                    || holdDuration < BattleSequence.OrderHoldSeconds * _clock.Scale - .04
                    || duration > (BattleSequence.OrderHoldSeconds + .9) * _clock.Scale + .2)
                    throw new Exception($"Order motion failed {current}/{quiet}/{side}: start={started} dock={docked} offscreen={offscreen} retired={retired} seconds={duration:0.000}.");
                count++;
            }
            // A queued/rejected order retains the exact release pose and then restores the hand.
            ResetDemo(); var basis = _state;
            var card = basis.SelfHand[0] with { Definition = _demo.Cards.First(c => c.CardType == "order") };
            var view = basis with { MatchId = "order-release-pose", SelfHand = [card], SelfHandCount = 1 };
            ApplyProjection(view, new() { CanEndTurn = true, PlayableUids = [card.Uid] });
            UiCommand? sent = null; DeploymentPose? release = null;
            void OnCommand(UiCommand command) => sent = command;
            CommandRequested += OnCommand;
            try { await DragAsync(card.Uid, new(900, 350), () => { release = DeploymentPose.From(_pressed!); return Task.CompletedTask; }); }
            finally { CommandRequested -= OnCommand; }
            if (sent is not PlayCard { TargetUid: null } || release is null
                || !_orderOrigins.TryGetValue(card.Uid, out var held) || held != release
                || DeploymentPose.From(_cards[card.Uid]) != release)
                throw new Exception("Non-target order did not preserve the cursor's release pose while waiting.");
            RefuseCommand(sent);
            if (_orderOrigins.Count != 0 || _cards[card.Uid].Position != _cards[card.Uid].RestPosition)
                throw new Exception("Rejected order did not restore its hand position.");
            GD.Print($"ORDER_MOTION_VERIFY_OK samples={count} exact-release-origin single-actor direct-dock readable-hold=1.5s left-offscreen concurrent-resolution rejection-restores-pose");
        }
        finally { ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced); }
    }
}
