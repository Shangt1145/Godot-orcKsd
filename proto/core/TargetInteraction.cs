using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>UI state only. Membership is supplied by the engine; no game rules live here.</summary>
public sealed class TargetInteraction : IUiInteractionBridge
{
    private readonly Func<IReadOnlyList<string>> _candidates;
    private IUiTargetResponder? _responder;
    public TargetInteraction(Func<IReadOnlyList<string>> candidates) => _candidates = candidates;
    public UiTargetRequest? Active
    {
        get; private set;
    }
    public string Hint { get; private set; } = "";
    public event Action? Changed;
    public Task<IReadOnlyList<string>> CollectCandidateUidsAsync(string requestId) => Task.FromResult(_candidates());
    public void BeginInteraction(UiTargetRequest request, IUiTargetResponder responder)
    {
        if (Active is not null)
            throw new InvalidOperationException("One active request at a time.");
        Active = request;
        _responder = responder;
        Hint = "请选择全部槽位后提交";
        Changed?.Invoke();
    }
    public bool Submit(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> choices)
    {
        if (Active is null || _responder is null)
            return false;
        // Forward requestId even on mismatch so the engine can reject and retain its audit trail.
        var terminal = _responder.Complete(requestId, choices);
        if (terminal)
        {
            Active = null;
            _responder = null;
            Hint = "请求已结束，等待结果";
        }
        else
            Hint = "选择被拒绝；请调整后重试，或取消";
        Changed?.Invoke();
        return terminal;
    }
    public bool Cancel(string requestId)
    {
        if (_responder is null)
            return false;
        var terminal = _responder.Cancel(requestId);
        if (terminal)
        {
            Active = null;
            _responder = null;
            Hint = "已取消";
        }
        else
            Hint = "取消未被接受，请重试";
        Changed?.Invoke();
        return terminal;
    }
}

/// <summary>Used only by adapters. Registry lifetime must match one engine/match instance.</summary>
public sealed class EngineReferenceRegistry<TReference> where TReference : class
{
    private readonly Dictionary<string, TReference> _refs = new(StringComparer.Ordinal);
    public void Register(string uid, TReference reference) => _refs[uid] = reference;
    public IReadOnlyList<object?> ResolveCandidates(IEnumerable<string> uids) =>
        uids.Where(_refs.ContainsKey).Select(x => (object?)_refs[x]).ToArray();
    public TReference Resolve(string uid) => _refs[uid];
    public void Clear() => _refs.Clear();
}
