using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>Direct actions precede waiting feedback; each lane is FIFO and the running beat completes.</summary>
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
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool IsBusy => _draining || _direct.Count > 0 || _indirect.Count > 0;
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
        if (_disposed || resolution.MatchId != _matchId || resolution.After.MatchId != _matchId
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
        // A deployment/order can cause draws or removals. Its feedback tail has the same low
        // priority as independent feedback; it must not keep a later gesture waiting.
        bool Primary(UiPresentationStep s) => IsDirectStep(s)
            || direct && (s is UiBoardImpactsPresentation || s is UiResourcePresentation resource && resource.NewValue < resource.OldValue);
        var primary = frozen.Steps.Where(Primary).ToArray();
        var feedback = frozen.Steps.Where(s => !Primary(s)).ToArray();
        if (frozen.After.Phase == "over") Add(frozen, available, direct);
        else if (direct && primary.Length > 0 && feedback.Length > 0)
        {
            var intermediate = WithoutFeedbackChanges(frozen, feedback);
            Add(frozen with { Steps = primary, After = intermediate }, available, true);
            AddFeedback(frozen with { Steps = feedback, Before = intermediate }, available);
        }
        else if (direct) Add(frozen, available, true);
        else AddFeedback(frozen, available);
        if (!_draining) Completion = DrainAsync();
        return Completion;
    }

    private void Add(UiPresentationResolution resolution, UiBattleActions actions, bool direct) =>
        (direct ? _direct : _indirect).Enqueue(new(resolution, actions, ++_order));

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

    private bool Take(out Item? item) => _direct.TryDequeue(out item) || _indirect.TryDequeue(out item);

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
        catch (Exception error) { _direct.Clear(); _indirect.Clear(); Failed?.Invoke(error); }
        finally { _draining = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _direct.Clear(); _indirect.Clear();
        _lifetime.Cancel();
    }
}
