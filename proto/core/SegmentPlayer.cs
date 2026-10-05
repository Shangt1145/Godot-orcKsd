using System.Collections.Concurrent;
using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>Exactly one segment consumer. Entries drive playback; Children are inspection only.</summary>
public sealed class SegmentPlayer : IDisposable
{
    private readonly IUiEventSource _source;
    private readonly IDisposable _subscription;
    private readonly ConcurrentQueue<UiEvent> _immediate = new();
    private readonly SortedDictionary<long, UiEventSegment> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private bool _disposed, _draining;
    public string MatchId
    {
        get;
    }
    public long NextSequence { get; private set; } = 1;
    public long? WaitingForSequence => _pending.Count > 0 && !_pending.ContainsKey(NextSequence) ? NextSequence : null;
    public SegmentPlayer(string matchId, IUiEventSource source)
    {
        MatchId = matchId;
        _source = source;
        _token = _lifetime.Token;
        // Short synchronous callback; never waits for animation or does scene work.
        _subscription = source.OnImmediateUpdate(e => { if (!_disposed && e.Kind == "interaction-hint") _immediate.Enqueue(e); });
    }
    public IReadOnlyList<UiEvent> TakeImmediate()
    {
        var result = new List<UiEvent>();
        while (_immediate.TryDequeue(out var e))
            result.Add(e);
        return result;
    }
    public async Task DrainAsync(Func<UiEvent, CancellationToken, Task> play)
    {
        if (_disposed || _draining)
            return;
        _draining = true;
        try
        {
            foreach (var s in _source.TakeSegments())
                if (s.MatchId == MatchId && s.Sequence >= NextSequence)
                    _pending.TryAdd(s.Sequence, s);
            while (!_disposed && _pending.TryGetValue(NextSequence, out var segment))
            {
                foreach (var entry in segment.Entries)
                {
                    _token.ThrowIfCancellationRequested();
                    await play(entry, _token);
                }
                _pending.Remove(NextSequence++);
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _draining = false; }
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _subscription.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _pending.Clear();
        while (_immediate.TryDequeue(out _))
        {
        }
    }
}
