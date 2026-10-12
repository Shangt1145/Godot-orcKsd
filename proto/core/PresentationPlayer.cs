using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>Direct actions precede unrelated feedback, after their prerequisites; each lane is FIFO.</summary>
public sealed class PresentationPlayer : IDisposable
{
    private readonly string _matchId;
    private readonly Func<UiPresentationResolution, UiBattleActions, CancellationToken, Task> _play;
    private sealed record Item(UiPresentationResolution Resolution, UiBattleActions Actions, long Order);
    private readonly Queue<Item> _direct = new(), _indirect = new();
    private readonly PresentationProjection _projection;
    private UiMatchView? _observed;
    private long _order;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _draining, _disposed;
    private bool _terminalReceived;
    private Item? _terminal;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool IsBusy => _draining || _direct.Count > 0 || _indirect.Count > 0 || _terminal is not null;
    public event Action<Exception>? Failed;

    public PresentationPlayer(string matchId,
        Func<UiPresentationResolution, UiBattleActions, CancellationToken, Task> play,
        UiMatchView? initial = null)
    {
        _matchId = matchId;
        _play = play;
        _observed = initial is null ? null : UiSnapshots.Freeze(initial);
        _projection = new(_observed);
    }

    public Task Enqueue(UiPresentationResolution resolution, UiBattleActions actions)
    {
        if (_disposed || _terminalReceived || resolution.MatchId != _matchId || resolution.After.MatchId != _matchId
            || resolution.Before is not null && resolution.Before.MatchId != _matchId) return Completion;
        var frozen = UiSnapshots.Freeze(resolution);
        frozen = frozen with { Before = frozen.Before ?? _observed };
        _observed = frozen.After;
        var available = UiSnapshots.Freeze(actions);
        frozen = frozen with { Steps = frozen.Steps.Select((step, index) => step is UiDrawPresentation draw
            && (index > 0 && frozen.Steps[index - 1] is UiDrawPresentation previous && previous.Side == draw.Side
                || index + 1 < frozen.Steps.Count && frozen.Steps[index + 1] is UiDrawPresentation next && next.Side == draw.Side)
                ? draw with { Consecutive = true } : step).ToArray() };
        var direct = frozen.Priority == UiPresentationPriority.Direct || frozen.Steps.Any(IsDirectStep)
            || frozen.Steps.All(s => s is UiResourcePresentation) && Moved(frozen.Before, frozen.After);
        if (direct) frozen = SeparateDeaths(frozen);
        // Arrivals caused by an action belong to that action: order/deployment, then its draws,
        // before the next action's presentation. Input still commits without waiting for playback.
        // Independent feedback and defeated units' retirements can yield to a later action.
        bool Primary(UiPresentationStep s) => IsDirectStep(s)
            || direct && (s is UiBoardImpactsPresentation or UiDrawPresentation or UiStatusPresentation or UiShufflePresentation
                || s is UiResourcePresentation resource && resource.NewValue < resource.OldValue);
        var primary = frozen.Steps.Where(Primary).ToArray();
        var feedback = frozen.Steps.Where(s => !Primary(s)).ToArray();
        if (frozen.After.Phase == "over")
        {
            // Finish all previously accepted beats before showing the result; nothing may
            // run after the result or re-open a terminal board.
            _terminalReceived = true;
            _terminal = new(frozen, available, ++_order);
        }
        else if (direct && primary.Length > 0 && feedback.Length > 0)
        {
            var intermediate = WithoutFeedbackChanges(frozen, feedback);
            AddAction(frozen with { Steps = primary, After = intermediate }, available);
            AddFeedback(frozen with { Steps = feedback, Before = intermediate }, available);
        }
        else if (direct) AddAction(frozen, available);
        else AddFeedback(frozen, available);
        if (!_draining) Completion = DrainAsync();
        return Completion;
    }

    private void Add(UiPresentationResolution resolution, UiBattleActions actions, bool direct) =>
        (direct ? _direct : _indirect).Enqueue(new(resolution, actions, ++_order));

    private void AddAction(UiPresentationResolution resolution, UiBattleActions actions)
    {
        if (!resolution.Steps.Any(s => s is UiDrawPresentation)) { Add(resolution, actions, true); return; }
        // Keep the entire causal chain in direct FIFO, but settle each arrival separately so
        // the first card becomes an interactive hand card while the next draw is animating.
        var before = resolution.Before; var first = 0;
        void Beat(int end)
        {
            var after = WithoutFeedbackChanges(resolution, resolution.Steps.Skip(end).ToArray());
            Add(resolution with { Steps = resolution.Steps.Skip(first).Take(end - first).ToArray(), Before = before, After = after }, actions, true);
            before = after; first = end;
        }
        for (var i = 0; i < resolution.Steps.Count; i++)
            if (resolution.Steps[i] is UiDrawPresentation)
            {
                if (i > first) Beat(i);
                Beat(i + 1);
            }
        if (first < resolution.Steps.Count) Beat(resolution.Steps.Count);
    }

    private void AddFeedback(UiPresentationResolution resolution, UiBattleActions actions)
    {
        if (resolution.Steps.Count <= 1) { Add(resolution, actions, false); return; }
        var before = resolution.Before;
        for (var i = 0; i < resolution.Steps.Count; i++)
        {
            var first = i;
            // A triggered counter holds its reveal through the attached removal. That visual
            // dependency is one beat; independent draws/removals remain separate scheduling points.
            if (resolution.Steps[i] is UiCounterPresentation { Stage: UiCounterStage.Triggered }
                && i + 1 < resolution.Steps.Count && resolution.Steps[i + 1] is UiRemovalPresentation) i++;
            var steps = resolution.Steps.Skip(first).Take(i - first + 1).ToArray();
            var after = WithoutFeedbackChanges(resolution, resolution.Steps.Skip(i + 1).ToArray());
            Add(resolution with { Steps = steps, Before = before, After = after }, actions, false);
            before = after;
        }
    }

    private static bool IsDirectStep(UiPresentationStep step) => step is UiDeploymentPresentation or UiOrderPresentation
        or UiCounterPresentation { Stage: UiCounterStage.Armed or UiCounterStage.Disarmed }
        || step is UiBoardImpactsPresentation hits && hits.Impacts.Any(i => i.Source is not null);

    private static bool Moved(UiMatchView? before, UiMatchView after) => before is not null
        && before.SelfLine.Concat(before.EnemyLine).Any(c => after.SelfLine.Concat(after.EnemyLine)
            .Any(a => a.Uid == c.Uid && (a.Zone != c.Zone || a.SlotIndex != c.SlotIndex)));

    private static UiPresentationResolution SeparateDeaths(UiPresentationResolution resolution)
    {
        var steps = new List<UiPresentationStep>();
        foreach (var step in resolution.Steps)
        {
            var deaths = new List<UiRemovalPresentation>();
            UiOrderImpact Hit(UiOrderImpact hit)
            {
                // Damage belongs to the attack; a unit's disappearance is feedback. HQ destruction
                // remains part of the result beat because ending the match is terminal.
                if (hit.After is not null || hit.Before.IsHq) return hit;
                var defeated = hit.Before with { Health = 0, EffectiveDefense = 0 };
                deaths.Add(new(defeated));
                return hit with { After = defeated };
            }
            steps.Add(step switch
            {
                UiBoardImpactsPresentation hits => hits with { Impacts = hits.Impacts.Select(Hit).ToArray() },
                UiOrderPresentation order => order with { Impacts = order.Impacts.Select(Hit).ToArray() },
                _ => step
            });
            steps.AddRange(deaths.DistinctBy(d => d.Card.Uid));
        }
        return resolution with { Steps = steps };
    }

    private static UiMatchView WithoutFeedbackChanges(UiPresentationResolution resolution, UiPresentationStep[] feedback)
    {
        var drawn = feedback.OfType<UiDrawPresentation>().Select(s => s.Card?.Uid).OfType<string>().ToHashSet();
        var removals = feedback.OfType<UiRemovalPresentation>().Select(s => s.Card).ToArray();
        var after = resolution.After;
        var hand = after.SelfHand.Where(c => !drawn.Contains(c.Uid)).Select((c, i) => c with { SlotIndex = i }).ToArray();
        return after with
        {
            SelfHand = hand, SelfHandCount = hand.Length,
            EnemyHandCount = after.EnemyHandCount is { } enemyCount
                ? Math.Max(0, enemyCount - feedback.OfType<UiDrawPresentation>().Count(s => s.Side == "enemy")) : null,
            SelfLine = after.SelfLine.Concat(removals.Where(c => c.OwnerSide == "self" && !c.IsHq && c.Zone != "hand"))
                .DistinctBy(c => c.Uid).ToArray(),
            EnemyLine = after.EnemyLine.Concat(removals.Where(c => c.OwnerSide == "enemy" && !c.IsHq && c.Zone != "hand"))
                .DistinctBy(c => c.Uid).ToArray()
        };
    }

    private static IEnumerable<string> RequiredCards(UiPresentationResolution resolution)
    {
        foreach (var step in resolution.Steps)
        {
            switch (step)
            {
                case UiDeploymentPresentation deployment: yield return deployment.Card.Uid; break;
                case UiOrderPresentation order:
                    yield return order.Card.Uid;
                    foreach (var hit in order.Impacts) yield return hit.Before.Uid;
                    break;
                case UiCounterPresentation { Card: { } card }: yield return card.Uid; break;
                case UiBoardImpactsPresentation hits:
                    foreach (var hit in hits.Impacts)
                    { yield return hit.Before.Uid; if (hit.Source is { } source) yield return source.Uid; }
                    break;
                case UiStatusPresentation status: yield return status.Before.Uid; break;
                case UiRemovalPresentation removal: yield return removal.Card.Uid; break;
                case UiDiscardPresentation { Card: { } card }: yield return card.Uid; break;
            }
        }
        // Some adapter actions have no explicit actor step. A departure or change of zone is
        // meaningful; merely reindexing an unrelated hand card must not make it a prerequisite.
        if (resolution.Before is not { } before) yield break;
        var after = resolution.After.SelfHand.Concat(resolution.After.SelfLine).Concat(resolution.After.EnemyLine)
            .ToDictionary(c => c.Uid);
        foreach (var card in before.SelfHand.Concat(before.SelfLine).Concat(before.EnemyLine))
            if (!after.TryGetValue(card.Uid, out var next) || next.Zone != card.Zone) yield return card.Uid;
    }

    private bool Take(out Item? item)
    {
        if (_direct.TryPeek(out var action))
        {
            var required = RequiredCards(action.Resolution).ToHashSet(StringComparer.Ordinal);
            var enemyAction = action.Resolution.Steps.Any(s => s is UiDeploymentPresentation { Card.OwnerSide: "enemy" }
                or UiOrderPresentation { Card.OwnerSide: "enemy" } or UiCounterPresentation { Side: "enemy" });
            // Never show a newly acquired card being used before its arrival. Hidden enemy draws
            // carry no UID; use only side/order, without bringing their identities across the bridge.
            var prerequisite = _indirect.Any(pending => pending.Order < action.Order && pending.Resolution.Steps
                .OfType<UiDrawPresentation>().Any(draw => draw.Card is { } card && required.Contains(card.Uid)
                    || draw.Side == "enemy" && enemyAction));
            // Preserve feedback FIFO while reaching the prerequisite; do not extract a later draw
            // past earlier feedback, and do not let another direct action pass this waiting action.
            if (prerequisite) return _indirect.TryDequeue(out item);
            return _direct.TryDequeue(out item);
        }
        if (_indirect.TryDequeue(out item)) return true;
        item = _terminal; _terminal = null;
        return item is not null;
    }

    private async Task DrainAsync()
    {
        _draining = true;
        try
        {
            while (!_disposed && Take(out var next))
            {
                var after = _projection.Apply(next!.Resolution.Before, next.Resolution.After, next.Order);
                var resolution = next.Resolution with
                {
                    After = after,
                    Steps = next.Resolution.Steps.Where(s => s is not UiDrawPresentation { Side: "self", Card: { } drawn }
                            || after.SelfHand.Any(c => c.Uid == drawn.Uid))
                        .Select(s => s switch
                        {
                            UiDeploymentPresentation deployment => (UiPresentationStep)(deployment with { Deployed = after }),
                            UiDrawPresentation { Side: "self", Card: { } card } draw => draw with
                            { SlotIndex = after.SelfHand.First(c => c.Uid == card.Uid).SlotIndex },
                            _ => s
                        }).ToArray()
                };
                await _play(resolution, next.Actions, _lifetime.Token).WaitAsync(_lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { _direct.Clear(); _indirect.Clear(); _terminal = null; Failed?.Invoke(error); }
        finally { _draining = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _direct.Clear(); _indirect.Clear();
        _terminal = null;
        _lifetime.Cancel();
    }
}
