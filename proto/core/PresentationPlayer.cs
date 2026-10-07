using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>One match's FIFO playback. Frozen adapter values cross the await boundary, never engine refs.</summary>
public sealed class PresentationPlayer : IDisposable
{
    private readonly string _matchId;
    private readonly Func<UiPresentationResolution, UiBattleActions, CancellationToken, Task> _play;
    private readonly Queue<(UiPresentationResolution Resolution, UiBattleActions Actions)> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _draining, _disposed;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool IsBusy => _draining || _pending.Count > 0;
    public event Action<Exception>? Failed;

    public PresentationPlayer(string matchId,
        Func<UiPresentationResolution, UiBattleActions, CancellationToken, Task> play)
    {
        _matchId = matchId;
        _play = play;
    }

    public Task Enqueue(UiPresentationResolution resolution, UiBattleActions actions)
    {
        if (_disposed || resolution.MatchId != _matchId || resolution.After.MatchId != _matchId) return Completion;
        _pending.Enqueue((UiSnapshots.Freeze(resolution), UiSnapshots.Freeze(actions)));
        if (!_draining) Completion = DrainAsync();
        return Completion;
    }

    private async Task DrainAsync()
    {
        _draining = true;
        try
        {
            while (!_disposed && _pending.TryDequeue(out var next))
                await _play(next.Resolution, next.Actions, _lifetime.Token).WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { _pending.Clear(); Failed?.Invoke(error); }
        finally { _draining = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending.Clear();
        _lifetime.Cancel();
    }
}
