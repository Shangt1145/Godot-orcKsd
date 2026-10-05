using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

public sealed class MockFeed : IUiEventSource
{
    private readonly Queue<UiEventSegment> _segments = new();
    private event Action<UiEvent>? Immediate;
    public int SubscriberCount => Immediate?.GetInvocationList().Length ?? 0;
    public void Enqueue(UiEventSegment segment) => _segments.Enqueue(UiSnapshots.Freeze(segment));
    public void EmitImmediate(UiEvent e) => Immediate?.Invoke(UiSnapshots.Freeze(e));
    public IReadOnlyList<UiEventSegment> TakeSegments()
    {
        var result = _segments.ToArray();
        _segments.Clear();
        return result;
    }
    public IDisposable OnImmediateUpdate(Action<UiEvent> callback)
    {
        Immediate += callback;
        return new Subscription(() => Immediate -= callback);
    }
    private sealed class Subscription(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

public sealed class MockTargetResponder(UiTargetRequest request, bool rejectFirst = false) : IUiTargetResponder
{
    private bool _terminal, _rejected;
    public int Calls
    {
        get; private set;
    }
    public bool Complete(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> selectionsBySlot)
    {
        Calls++;
        if (_terminal || requestId != request.RequestId)
            return false;
        if (rejectFirst && !_rejected)
        {
            _rejected = true;
            return false;
        }
        foreach (var slot in request.Slots)
        {
            var selected = selectionsBySlot.TryGetValue(slot.Name, out var s) ? s : Array.Empty<string>();
            if (selected.Count < slot.Min || selected.Count > slot.Max || selected.Distinct().Count() != selected.Count
                || selected.Any(id => !slot.Allowed.Any(a => a.Id == id)))
                return false;
        }
        if (selectionsBySlot.Keys.Any(k => !request.Slots.Any(s => s.Name == k)))
            return false;
        return _terminal = true;
    }
    public bool Cancel(string requestId)
    {
        if (_terminal || requestId != request.RequestId)
            return false;
        return _terminal = true;
    }
}
