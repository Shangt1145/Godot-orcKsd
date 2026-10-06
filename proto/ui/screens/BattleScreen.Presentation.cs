using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    private BattleSequence _sequence = null!;
    private UiPresentationResolution? _pendingPresentation;
    private UiBattleActions? _pendingPresentationActions;
    private void InitializePresentation()
    {
        _sequence = new BattleSequence { Size = BoardSize, ZIndex = 210 };
        _sequence.Initialize(_textures, _clock, _sfx); _canvas.AddChild(_sequence);
        // The heaviest deployment slam shakes the table itself, not just the card.
        _sequence.PhaseChanged += phase =>
        {
            PresentationPhase?.Invoke(phase);
            if (phase == "deployment-slam-2") ShakeBoard();
        };
    }

    /// <summary>Paper-layer choreography phases, for diagnostics and verification.</summary>
    public event Action<string>? PresentationPhase;
    private void ShakeBoard()
    {
        if (_clock.ReducedMotion) return;
        var basePosition = _canvas.Position;
        var shake = CreateTween();
        foreach (var offset in new[] { new Vector2(-7, 4), new Vector2(6, -5), new Vector2(-4, 3), new Vector2(3, -2), new Vector2(-1, 1) })
            shake.TweenProperty(_canvas, "position", basePosition + offset, .045);
        shake.TweenProperty(_canvas, "position", basePosition, .05);
        shake.Finished += FitBoard; _tweens.Add(shake);
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
            var slot = self ? after.SelfHand.FirstOrDefault(c => c.Uid == card.Uid)?.SlotIndex : i;
            if (slot is null) continue;
            var pose = HandPose(slot.Value, self ? after.SelfHand.Count : Math.Clamp(after.EnemyHandCount ?? 0, 0, 9), self);
            if (_clock.ReducedMotion) { card.Position = pose.Position; card.Rotation = pose.Rotation; }
            else
            {
                var t = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
                t.TweenProperty(card, "position", pose.Position, .32 * _clock.Scale);
                t.TweenProperty(card, "rotation", pose.Rotation, .32 * _clock.Scale); _tweens.Add(t);
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
        CancelSelection();
        var resolution = UiSnapshots.Freeze(supplied); var actions = UiSnapshots.Freeze(afterActions);
        _pendingPresentation = resolution; _pendingPresentationActions = actions;
        _busy = true; _endTurn.Disabled = true; var generation = _generation;
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
                    case UiDeploymentPresentation deployment when deployment.Deployed.MatchId == resolution.MatchId:
                        var deployFrom = deployment.Card.OwnerSide == "enemy" ? new Vector2(585, -60) : new Vector2(568, 628);
                        if (_cards.TryGetValue(deployment.Card.Uid, out var deployingHand)) deployFrom = deployingHand.Position;
                        else if (deployment.Card.OwnerSide == "enemy")
                        {
                            var back = _cardLayer.GetChildren().OfType<BattleCard>().LastOrDefault(c => c.View is null && c.Mode == BattleCardMode.Hidden && c.Visible);
                            if (back is not null) deployFrom = back.Position;
                        }
                        Render(deployment.Deployed, new(), true); generation = _generation;
                        _busy = true; _endTurn.Disabled = true;
                        if (deployment.Card.Visibility == Visibility.Full && _cards.TryGetValue(deployment.Card.Uid, out var deployedCard)
                            && deployedCard.View?.Visibility == Visibility.Full)
                            await _sequence.DeployAsync(deployment.Card, deployedCard, deployFrom);
                        break;
                    case UiDrawPresentation draw when draw.Side is "self" or "enemy":
                        var self = draw.Side == "self";
                        var count = self ? resolution.After.SelfHand.Count : Math.Clamp(resolution.After.EnemyHandCount ?? 0, 0, 9);
                        if (draw.SlotIndex < 0 || draw.SlotIndex >= count) break;
                        MakeHandRoom(resolution.After, self);
                        var pose = HandPose(draw.SlotIndex, count, self);
                        var consecutive = (stepIndex > 0 && resolution.Steps[stepIndex - 1] is UiDrawPresentation previousDraw && previousDraw.Side == draw.Side)
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
                        var targets = order.Impacts
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
                            if (removal?.Card.Visibility == Visibility.Full && _cards.TryGetValue(removal.Card.Uid, out var removedCard)
                                && removedCard.View?.Visibility == Visibility.Full)
                                await _combat.PresentRemovalAsync(removal.Card, removedCard);
                        }
                        await _sequence.CounterAsync(counter, counterCard, removal is null ? null : RemoveCounterTarget);
                        if (removal is not null) stepIndex++;
                        if (counter.Stage == UiCounterStage.Triggered && counter.Card?.Visibility == Visibility.Full)
                            Record(counter.Card, counter.Card.Definition.Name);
                        break;
                    case UiRemovalPresentation removed when removed.Card.Visibility == Visibility.Full:
                        if (_cards.TryGetValue(removed.Card.Uid, out var removedActor) && removedActor.View?.Visibility == Visibility.Full)
                            await _combat.PresentRemovalAsync(removed.Card, removedActor);
                        break;
                    case UiStatusPresentation status when status.Before.Uid == status.After.Uid && status.Before.Visibility == Visibility.Full && status.After.Visibility == Visibility.Full:
                        if (_cards.TryGetValue(status.Before.Uid, out var statusCard) && statusCard.View?.Visibility == Visibility.Full)
                            await _sequence.StatusAsync(statusCard, status);
                        break;
                }
            }
            if (generation != _generation || _pendingPresentation != resolution) return;
            ClearPresentation(); Render(resolution.After, actions, true);
        }
        catch (OperationCanceledException) { /* Interrupt already settled or replaced the supplied state. */ }
        catch (Exception e) { GD.PushError(e.ToString()); if (generation == _generation) CancelSelection(); }
    }
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
