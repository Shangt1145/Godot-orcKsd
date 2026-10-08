using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>A released card's pose in board coordinates, retained until its queued deployment begins.</summary>
public readonly record struct DeploymentPose(Vector2 Position, Vector2 Size, Vector2 Scale, float Rotation)
{
    public Vector2 Center => Position + Size / 2;
    public static DeploymentPose From(BattleCard card) => new(card.Position, card.Size, card.Scale, card.Rotation);
}

public partial class BattleScreen
{
    private BattleSequence _sequence = null!;
    private UiPresentationResolution? _pendingPresentation;
    private UiBattleActions? _pendingPresentationActions;
    private readonly Dictionary<string, DeploymentPose> _deploymentOrigins = new();
    // Defeated units waiting for their low-priority removal keep a visual at their old pose,
    // outside the board's occupied-slot layout and input hit tests.
    private readonly Dictionary<string, BattleCard> _removalActors = new();

    private void ClearRemovalActors()
    {
        foreach (var card in _removalActors.Values)
            if (GodotObject.IsInstanceValid(card)) { card.GetParent()?.RemoveChild(card); card.QueueFree(); }
        _removalActors.Clear();
    }

    private void SyncRemovalActors(UiMatchView state)
    {
        var defeated = state.SelfLine.Concat(state.EnemyLine).Where(c => c.Health == 0 && !c.IsHq)
            .ToDictionary(c => c.Uid);
        foreach (var uid in _removalActors.Keys.Where(uid => !defeated.ContainsKey(uid)).ToArray())
        {
            var old = _removalActors[uid]; old.GetParent()?.RemoveChild(old); old.QueueFree(); _removalActors.Remove(uid);
        }
        foreach (var (uid, view) in defeated)
        {
            if (_removalActors.ContainsKey(uid) || !_cards.TryGetValue(uid, out var source)) continue;
            var pose = DeploymentPose.From(source);
            var card = new BattleCard { Position = pose.Position, RestPosition = pose.Position, Size = pose.Size,
                Rotation = pose.Rotation, RestRotation = pose.Rotation, Scale = pose.Scale, MouseFilter = MouseFilterEnum.Ignore };
            card.PivotOffset = pose.Size / 2; card.Bind(view, BattleCardMode.Field, _textures);
            var parent = _cardLayer.GetParent(); parent.AddChild(card); parent.MoveChild(card, _cardLayer.GetIndex());
            _removalActors[uid] = card;
        }
    }

    private void RestorePendingDeploymentPoses()
    {
        foreach (var (uid, pose) in _deploymentOrigins)
        {
            if (!_cards.TryGetValue(uid, out var card) || card.Mode != BattleCardMode.Hand) continue;
            StopHandMotion(uid);
            card.Size = pose.Size; card.PivotOffset = pose.Size / 2;
            card.Position = pose.Position; card.Rotation = pose.Rotation; card.Scale = pose.Scale;
            card.ZIndex = 140; card.MouseFilter = MouseFilterEnum.Ignore;
        }
    }
    private void InitializePresentation()
    {
        _sequence = new BattleSequence { Size = BoardSize, ZIndex = 210 };
        _sequence.Initialize(_textures, _clock, _sfx); _canvas.AddChild(_sequence);
        // The field card and board move together after impact; weight comes from defense.
        _sequence.PhaseChanged += phase =>
        {
            PresentationPhase?.Invoke(phase);
            if (phase == "deployment-slam-1") ShakeBoard(BattleSequence.SlamStyle(4));
            if (phase == "deployment-slam-2") ShakeBoard(BattleSequence.SlamStyle(7));
        };
    }

    /// <summary>Paper-layer choreography phases, for diagnostics and verification.</summary>
    public event Action<string>? PresentationPhase;
    private void ShakeBoard(BattleSequence.SlamProfile style)
    {
        if (_clock.ReducedMotion) return;
        var basePosition = _canvas.Position;
        var scale = _canvas.Scale.X;
        var center = BoardSize / 2;
        var shake = CreateTween();
        var duration = style.Shake >= 10 ? 1.0 : .47;
        shake.TweenMethod(Callable.From<float>(p =>
        {
            var fade = MathF.Pow(1 - p, 1.4f);
            var offset = new Vector2(MathF.Sin(p * 79), MathF.Sin(p * 103 + .8f)) * style.Shake * fade;
            var roll = MathF.Sin(p * 67) * style.Shake * .00035f * fade;
            _canvas.Rotation = roll;
            _canvas.Position = basePosition + (offset + center - center.Rotated(roll)) * scale;
        }), 0f, 1f, duration * _clock.Scale);
        shake.Finished += () => { _canvas.Rotation = 0; FitBoard(); _tweens.Remove(shake); };
        _tweens.Add(shake);
    }
    private void ClearPresentation()
    { _pendingPresentation = null; _pendingPresentationActions = null; _sequence?.Interrupt(); }
    private static (Vector2 Position, float Rotation) HandPose(int slot, int count, bool self)
    {
        var relative = slot - (count - 1) / 2f;
        if (!self) return (new(585 + relative * 51, -90 + MathF.Abs(relative) * 4), -relative * .045f);
        var spacing = Math.Min(98, 590f / Math.Max(1, count - 1));
        return (new(568 + relative * spacing, 628 + MathF.Abs(relative) * 7), relative * .042f);
    }
    private void MakeHandRoom(UiMatchView after, bool self)
    {
        var cards = self ? _cards.Values.Where(c => c.Mode == BattleCardMode.Hand).ToArray()
            : _cardLayer.GetChildren().OfType<BattleCard>().Where(c => c.View is null && c.Mode == BattleCardMode.Hidden && c.Visible).ToArray();
        for (var i = 0; i < cards.Length; i++)
        {
            var card = cards[i];
            if (card == _pressed || _deploymentOrigins.ContainsKey(card.Uid)) continue;
            var slot = self ? after.SelfHand.FirstOrDefault(c => c.Uid == card.Uid)?.SlotIndex : i;
            if (slot is null) continue;
            var pose = HandPose(slot.Value, self ? after.SelfHand.Count : Math.Clamp(after.EnemyHandCount ?? 0, 0, 9), self);
            if (_clock.ReducedMotion) { card.Position = pose.Position; card.Rotation = pose.Rotation; }
            else
            {
                if (self) StopHandMotion(card.Uid);
                var t = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
                t.TweenProperty(card, "position", pose.Position, .32 * _clock.Scale);
                t.TweenProperty(card, "rotation", pose.Rotation, .32 * _clock.Scale); _tweens.Add(t);
                if (self) _handMotion[card.Uid] = t;
            }
        }
    }
    private void HideOpponentHandToCount(int? count)
    {
        if (count is null) return;
        foreach (var back in _cardLayer.GetChildren().OfType<BattleCard>()
            .Where(c => c.View is null && c.Mode == BattleCardMode.Hidden && c.Visible).Skip(Math.Max(0, count.Value))) back.Visible = false;
    }
    public async Task PresentSequenceAsync(UiPresentationResolution supplied, UiBattleActions afterActions)
    {
        if (supplied.MatchId != _state.MatchId || supplied.After.MatchId != supplied.MatchId) return;
        var initialGeneration = _generation;
        await WaitForGestureReleaseAsync();
        if (!IsInsideTree() || initialGeneration != _generation || supplied.MatchId != _state.MatchId) return;
        CancelSelection();
        var resolution = UiSnapshots.Freeze(supplied); var actions = UiSnapshots.Freeze(afterActions);
        _pendingPresentation = resolution; _pendingPresentationActions = actions;
        _busy = true; UpdateEndTurnButton(); var generation = _generation;
        _selfResource.Bind(resolution.After.SelfKredits, resolution.After.SelfMaxKredits, resolution.After.SelfPlayerName);
        _enemyResource.Bind(resolution.After.EnemyKredits, resolution.After.EnemyMaxKredits, resolution.After.EnemyPlayerName);
        _deckCount.Text = resolution.After.SelfDeckCount.ToString();
        try
        {
            for (var stepIndex = 0; stepIndex < resolution.Steps.Count; stepIndex++)
            {
                var step = resolution.Steps[stepIndex];
                switch (step)
                {
                    case UiBoardImpactsPresentation hits:
                        await PlayBoardImpactsAsync(VisibleImpacts(hits.Impacts, resolution.After));
                        break;
                    case UiDeploymentPresentation deployment when deployment.Deployed.MatchId == resolution.MatchId:
                        await WaitForGestureReleaseAsync();
                        if (!IsInsideTree() || generation != _generation || _pendingPresentation != resolution) return;
                        var deployPose = new DeploymentPose(deployment.Card.OwnerSide == "enemy" ? new Vector2(585, -60) : new Vector2(568, 628),
                            new(144, 202), Vector2.One, deployment.Card.OwnerSide == "enemy" ? -.15f : .15f);
                        if (_deploymentOrigins.Remove(deployment.Card.Uid, out var releasedPose)) deployPose = releasedPose;
                        else if (_cards.TryGetValue(deployment.Card.Uid, out var deployingHand)) deployPose = DeploymentPose.From(deployingHand);
                        else if (deployment.Card.OwnerSide == "enemy")
                        {
                            var back = _cardLayer.GetChildren().OfType<BattleCard>().LastOrDefault(c => c.View is null && c.Mode == BattleCardMode.Hidden && c.Visible);
                            if (back is not null) deployPose = DeploymentPose.From(back);
                        }
                        Render(deployment.Deployed, new(), true, deployment.Card.Uid); generation = _generation;
                        _busy = true; UpdateEndTurnButton();
                        if (deployment.Card.Visibility == Visibility.Full && _cards.TryGetValue(deployment.Card.Uid, out var deployedCard)
                            && deployedCard.View?.Visibility == Visibility.Full)
                            await _sequence.DeployAsync(deployment.Card, deployedCard, deployPose);
                        break;
                    case UiDrawPresentation draw when draw.Side is "self" or "enemy":
                        var self = draw.Side == "self";
                        var count = self ? resolution.After.SelfHand.Count : Math.Clamp(resolution.After.EnemyHandCount ?? 0, 0, 9);
                        if (draw.SlotIndex < 0 || draw.SlotIndex >= count) break;
                        MakeHandRoom(resolution.After, self);
                        var pose = HandPose(draw.SlotIndex, count, self);
                        var consecutive = draw.Consecutive || (stepIndex > 0 && resolution.Steps[stepIndex - 1] is UiDrawPresentation previousDraw && previousDraw.Side == draw.Side)
                            || (stepIndex + 1 < resolution.Steps.Count && resolution.Steps[stepIndex + 1] is UiDrawPresentation nextDraw && nextDraw.Side == draw.Side);
                        await _sequence.DrawAsync(draw, pose.Position, pose.Rotation, consecutive);
                        break;
                    case UiOrderPresentation order:
                        var from = order.Card.OwnerSide == "enemy" ? new Vector2(585, -60) : new Vector2(568, 628);
                        if (_cards.TryGetValue(order.Card.Uid, out var handCard)) { from = handCard.Position; handCard.Visible = false; }
                        if (order.Card.OwnerSide == "enemy" && resolution.After.EnemyHandCount < _state.EnemyHandCount)
                        {
                            var back = _cardLayer.GetChildren().OfType<BattleCard>().LastOrDefault(c => c.View is null && c.Mode == BattleCardMode.Hidden && c.Visible);
                            if (back is not null) { from = back.Position; back.Visible = false; }
                        }
                        MakeHandRoom(resolution.After, order.Card.OwnerSide != "enemy");
                        await _sequence.RevealOrderAsync(order.Card, from);
                        if (order.Card.Visibility == Visibility.Full) Record(order.Card, order.Card.Definition.Name);
                        var targets = VisibleImpacts(order.Impacts, resolution.After)
                            .Where(i => i.Before.Visibility == Visibility.Full && _cards.TryGetValue(i.Before.Uid, out var card)
                                && card.Visible && card.View?.Visibility == Visibility.Full)
                            .GroupBy(i => i.Before.Uid).Select(g => (Impact: g.First(), Card: _cards[g.Key])).ToArray();
                        await _combat.PresentImpactsAsync(targets);
                        await _sequence.RetireOrderAsync();
                        break;
                    case UiCounterPresentation counter:
                        var counterCard = counter.Card is null ? null : _cards.GetValueOrDefault(counter.Card.Uid);
                        if (counter.Stage == UiCounterStage.Triggered)
                        {
                            HideOpponentHandToCount(resolution.After.EnemyHandCount);
                            MakeHandRoom(resolution.After, false);
                            MakeHandRoom(resolution.After, counter.Side == "self");
                            if (counter.BlockedCard is { } blocked && _cards.TryGetValue(blocked.Uid, out var blockedHand)) blockedHand.Visible = false;
                        }
                        var removal = counter.Stage == UiCounterStage.Triggered && stepIndex + 1 < resolution.Steps.Count
                            ? resolution.Steps[stepIndex + 1] as UiRemovalPresentation : null;
                        async Task RemoveCounterTarget()
                        {
                            var removedCard = removal is null ? null : _cards.GetValueOrDefault(removal.Card.Uid) ?? _removalActors.GetValueOrDefault(removal.Card.Uid);
                            if (removal?.Card.Visibility == Visibility.Full && removedCard is not null
                                && removedCard.View?.Visibility == Visibility.Full)
                                await _combat.PresentRemovalAsync(removal.Card, removedCard);
                        }
                        await _sequence.CounterAsync(counter, counterCard, removal is null ? null : RemoveCounterTarget);
                        if (removal is not null) stepIndex++;
                        if (counter.Stage == UiCounterStage.Triggered && counter.Card?.Visibility == Visibility.Full)
                            Record(counter.Card, counter.Card.Definition.Name);
                        break;
                    case UiRemovalPresentation removed when removed.Card.Visibility == Visibility.Full:
                        var removedActor = _cards.GetValueOrDefault(removed.Card.Uid) ?? _removalActors.GetValueOrDefault(removed.Card.Uid);
                        if (removedActor?.View?.Visibility == Visibility.Full)
                            await _combat.PresentRemovalAsync(removed.Card, removedActor);
                        break;
                    case UiDiscardPresentation discard:
                        // Discarded and hand-limit-burned cards leave the table. The bridge has always
                        // emitted this step; the switch used to drop it, so the burn cue never played.
                        _cards.TryGetValue(discard.Card?.Uid ?? "", out var discardedSource);
                        await _sequence.DiscardAsync(discard.Card, discardedSource, discard.Kind);
                        break;
                    case UiTurnPresentation turn:
                        // "Your turn" / "opponent's turn". Also always emitted, always dropped.
                        await _sequence.TurnBannerAsync(turn);
                        break;
                    case UiStatusPresentation status when status.Before.Uid == status.After.Uid && status.Before.Visibility == Visibility.Full && status.After.Visibility == Visibility.Full:
                        if (_cards.TryGetValue(status.Before.Uid, out var statusCard) && statusCard.View?.Visibility == Visibility.Full)
                            await _sequence.StatusAsync(statusCard, status, PresentationCard(resolution.After, status.After.Uid));
                        break;
                    case UiResourcePresentation resource:
                        // The bar counts to its new value; the authoritative render below re-binds it.
                        if (resource.Side == "self" && resource.NewValue > 0 && resource.NewValue == resolution.After.SelfKredits)
                            _selfResource.AnimateTo(resource.NewValue, resource.NewSlots, resource.Cause);
                        else if (resource.Side == "enemy" && resource.NewSlots is not null && resource.NewSlots == resolution.After.EnemyMaxKredits)
                            _enemyResource.AnimateTo(null, resource.NewSlots, resource.Cause);
                        break;
                }
            }
            // The match ending is a board-level event, not a step: it gets its own beat before the
            // authoritative state lands, so the result reads as a moment rather than a repaint.
            if (generation == _generation && _pendingPresentation == resolution
                && resolution.After.Phase == "over" && resolution.After.ResultTitle is not null)
            {
                var victory = resolution.After.ResultTitle == "胜利";
                var reason = resolution.After.ResultReason switch
                {
                    "HqZero" => "总部被摧毁",
                    "Concede" => "对手认输",
                    _ => "对局结束",
                };
                await _sequence.ResultBannerAsync(victory, reason);
            }
            if (generation != _generation || _pendingPresentation != resolution) return;
            await WaitForGestureReleaseAsync();
            if (!IsInsideTree() || generation != _generation || _pendingPresentation != resolution) return;
            Render(resolution.After, actions, true); generation = _generation;
            // Layout movements belong to this item too; a later item must not kill them on render.
            while (IsInsideTree() && generation == _generation && _pendingPresentation == resolution
                && _tweens.Any(t => GodotObject.IsInstanceValid(t) && t.IsRunning()))
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (generation == _generation && _pendingPresentation == resolution) ClearPresentation();
        }
        catch (OperationCanceledException) { /* Interrupt already settled or replaced the supplied state. */ }
        catch (Exception e) { GD.PushError(e.ToString()); if (generation == _generation) CancelSelection(); }
    }
    private static UiCardView? PresentationCard(UiMatchView view, string uid) => view.SelfHand.Concat(view.SelfLine)
        .Concat(view.EnemyLine).Concat(new[] { view.SelfHq, view.EnemyHq }.OfType<UiCardView>()).FirstOrDefault(c => c.Uid == uid);

    private static IReadOnlyList<UiOrderImpact> VisibleImpacts(IReadOnlyList<UiOrderImpact> impacts, UiMatchView visibleAfter) =>
        impacts.Select(i => i.After is null ? i : i with { After = PresentationCard(visibleAfter, i.Before.Uid) ?? i.After }).ToArray();

    public async Task StartPresentationScenarioAsync(string kind)
    {
        if (!_usingDemo) return;
        ResetDemo(); var fixture = _demo.CreatePresentationScenario(kind);
        Render(fixture.Before, new(), false);
        await PresentSequenceAsync(fixture.Resolution, fixture.Actions);
    }
    public async Task VerifyPresentationAsync()
    {
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Faster); _clock.SetReducedMotion(false);
        foreach (var kind in new[] { "draw", "draw-two", "enemy-draw", "order", "multi-order", "enemy-order" })
        {
            ResetDemo(); var fixture = _demo.CreatePresentationScenario(kind); Render(fixture.Before, new(), false);
            var phases = new List<string>();
            void OnPhase(string phase)
            {
                phases.Add(phase);
                if (kind == "enemy-draw" && _sequence.GetChildren().OfType<BattleCard>().Any(c => c.View is not null || c.Mode != BattleCardMode.Hidden))
                    throw new Exception("Opponent draw leaked card identity.");
            }
            _sequence.PhaseChanged += OnPhase; _combat.PhaseChanged += OnPhase;
            await PresentSequenceAsync(fixture.Resolution, fixture.Actions);
            _sequence.PhaseChanged -= OnPhase; _combat.PhaseChanged -= OnPhase;
            var expected = fixture.Resolution.After;
            if (!MatchesProjection(expected) || !_state.SelfHand.Select(c => c.Uid).SequenceEqual(expected.SelfHand.Select(c => c.Uid))
                || _state.SelfDeckCount != expected.SelfDeckCount || _state.EnemyHandCount != expected.EnemyHandCount
                || _sequence.LiveCardCount != 0 || _combat.LiveEffectCount != 0 || _busy)
                throw new Exception($"Presentation state/cleanup failed: {kind}");
            if (kind.Contains("order") && (!phases.Contains("order-reveal") || !phases.Contains("order-impact") || !phases.Contains("order-retired")))
                throw new Exception("Order choreography failed.");
            if (kind == "draw-two" && phases.Count(p => p == "draw-reveal") != 2) throw new Exception("Draw sequence dropped a card.");
            if (kind == "multi-order" && (_state.EnemyLine.Count != 1 || _state.EnemyLine[0].Health != 2)) throw new Exception("Batch outcome was not consumed.");
        }
        ResetDemo(); var interrupted = _demo.CreatePresentationScenario("multi-order");
        Render(interrupted.Before, new(), false); var task = PresentSequenceAsync(interrupted.Resolution, interrupted.Actions);
        await ToSignal(GetTree().CreateTimer(.15), SceneTreeTimer.SignalName.Timeout); CancelSelection(); await task;
        if (!MatchesProjection(interrupted.Resolution.After) || _sequence.LiveCardCount != 0 || _combat.LiveEffectCount != 0 || _busy)
            throw new Exception("Interrupted order did not settle authoritative state.");
        ResetDemo(); interrupted = _demo.CreatePresentationScenario("multi-order"); Render(interrupted.Before, new(), false);
        task = PresentSequenceAsync(interrupted.Resolution, interrupted.Actions);
        await ToSignal(GetTree().CreateTimer(.48), SceneTreeTimer.SignalName.Timeout); CancelSelection(); await task;
        if (!MatchesProjection(interrupted.Resolution.After) || _sequence.LiveCardCount != 0 || _combat.LiveEffectCount != 0)
            throw new Exception("Interrupted batch impact retained effects.");
        ResetDemo(); var stale = _demo.CreatePresentationScenario("draw"); Render(stale.Before, new(), false);
        task = PresentSequenceAsync(stale.Resolution, stale.Actions); ResetDemo(); await task;
        if (_state.SelfHand.Count != 5 || _state.SelfDeckCount != 24 || _sequence.LiveCardCount != 0) throw new Exception("Stale draw mutated reset state.");
        var hidden = _demo.CreatePresentationScenario("enemy-draw");
        var accidental = new UiDrawPresentation("enemy", 5, hidden.Before.SelfHand[0]);
        var frozen = UiSnapshots.Freeze(hidden.Resolution with { Steps = new UiPresentationStep[] { accidental } });
        if (((UiDrawPresentation)frozen.Steps[0]).Card is not null) throw new Exception("Hidden draw snapshot retained identity.");
        ApplyProjection(hidden.Before, new());
        await PresentSequenceAsync(frozen, hidden.Actions);
        if (_usingDemo || _state.EnemyHandCount != 6) throw new Exception("External presentation changed adapter mode.");
        var stable = _state;
        await PresentSequenceAsync(frozen with { MatchId = "wrong-match" }, new());
        if (_state != stable) throw new Exception("Wrong-match sequence changed state.");
        ResetDemo(); var privateTarget = _demo.CreatePresentationScenario("multi-order");
        ApplyProjection(privateTarget.Before with
        {
            EnemyLine = privateTarget.Before.EnemyLine.Select(c => c with { Visibility = Visibility.Hidden }).ToArray(),
            EnemyHq = privateTarget.Before.EnemyHq! with { Visibility = Visibility.Hidden }
        }, new());
        var impactCount = -1;
        void CountImpacts(string phase) { if (phase == "order-impact") impactCount = _combat.LiveEffectCount; }
        _combat.PhaseChanged += CountImpacts;
        await PresentSequenceAsync(privateTarget.Resolution, privateTarget.Actions); _combat.PhaseChanged -= CountImpacts;
        if (impactCount != 0) throw new Exception("Hidden targets revealed positions/effects.");
        ResetDemo(); var mutable = _demo.CreatePresentationScenario("draw-two"); Render(mutable.Before, new(), false);
        var steps = mutable.Resolution.Steps.ToList();
        task = PresentSequenceAsync(mutable.Resolution with { Steps = steps }, mutable.Actions); steps.Clear(); await task;
        if (_state.SelfHand.Count != 7) throw new Exception("Presentation did not freeze adapter step collection.");
        _clock.SetReducedMotion(true); await StartPresentationScenarioAsync("draw-two"); await StartPresentationScenarioAsync("multi-order");
        if (_sequence.LiveCardCount != 0 || _combat.LiveEffectCount != 0 || _busy) throw new Exception("Reduced-motion presentation retained effects.");
        _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); ResetDemo();
        GD.Print("BATTLE_STAGE3_VERIFY_OK draw draw-two hidden-draw order multi-target enemy-order interrupt stale external frozen reduced-motion effects=0");
    }
    public async Task CapturePresentationFramesAsync(string dir)
    {
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
        foreach (var kind in new[] { "draw", "draw-two", "enemy-draw", "order", "multi-order", "enemy-order" })
        {
            var captures = new List<Task>(); var captured = new HashSet<string>();
            async Task Capture(string phase)
            {
                await ToSignal(GetTree().CreateTimer(.10), SceneTreeTimer.SignalName.Timeout);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng($"{dir}/battle-stage3-{kind}-{phase}.png");
            }
            void OnPhase(string phase)
            {
                if (phase is "draw-flip" or "draw-reveal" or "draw-hidden" or "order-reveal" or "order-impact" && captured.Add(phase)) captures.Add(Capture(phase));
            }
            _sequence.PhaseChanged += OnPhase; _combat.PhaseChanged += OnPhase;
            await StartPresentationScenarioAsync(kind);
            _sequence.PhaseChanged -= OnPhase; _combat.PhaseChanged -= OnPhase; await Task.WhenAll(captures);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng($"{dir}/battle-stage3-{kind}-settled.png");
        }
        _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); ResetDemo();
    }
}
