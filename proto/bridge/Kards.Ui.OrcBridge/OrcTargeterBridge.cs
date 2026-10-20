using Orc.Core;
using Orc.Game.Targeting;

namespace Kards.Ui.OrcBridge;

/// <summary>Consumes native selector sessions in order. Only the engine supplies candidates.</summary>
public sealed class OrcTargeterBridge(Action<ISelectorInstance>? present = null) : ITargeterBridge
{
    // ---------- AI 动作链（AiPilot 接入） ----------
    // AI 动作期间发生的选择器不交付玩家，挂起 here；决策权交给 AiPilot（经 OrcAiGateway）。
    private readonly object _aiLock = new();
    private bool _aiChain;
    private ISelectorInstance? _aiHeld;
    private TaskCompletionSource<ISelectorInstance>? _aiSignal;

    /// <summary>进入 AI 动作链：此后发生的选择器挂起等 AI 决策，直到 <see cref="EndAiChain"/>。</summary>
    public void BeginAiChain()
    {
        lock (_aiLock)
        {
            _aiChain = true;
            _aiHeld = null;
            _aiSignal = new TaskCompletionSource<ISelectorInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>结束 AI 动作链。</summary>
    public void EndAiChain()
    {
        lock (_aiLock) { _aiChain = false; _aiHeld = null; _aiSignal = null; }
    }

    /// <summary>当前挂起信号（无 AI 链＝null）。gateway 用 WhenAny 等「选择器出现」或「动作完成」。</summary>
    public Task<ISelectorInstance>? AiSignal
    {
        get { lock (_aiLock) return _aiSignal?.Task; }
    }

    /// <summary>取走挂起的选择器并重置信号（下一个选择器会再次挂起，拿新的信号即可）。</summary>
    public ISelectorInstance? TakeHeldAiSelector()
    {
        lock (_aiLock)
        {
            var held = _aiHeld;
            _aiHeld = null;
            if (_aiChain) _aiSignal = new TaskCompletionSource<ISelectorInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
            return held;
        }
    }

    private bool TryHoldForAi(ISelectorInstance selector)
    {
        lock (_aiLock)
        {
            if (!_aiChain || _aiSignal is null) return false;
            _aiHeld = selector;
            _aiSignal.TrySetResult(selector);
            return true;
        }
    }

    /// <summary>把 AI 的选择提交到挂起的选择器（picks 为候选序号；空表＝空选择，Min&gt;0 时空选择按取消算）。</summary>
    public bool SubmitAiPick(ISelectorInstance selector, IReadOnlyList<int> picks, bool cancel)
    {
        try
        {
            if (cancel) { selector.Submit(new CancelEvent()); return true; }
            var candidates = selector.Presentation.Candidates;
            var chosen = picks.Where(i => i >= 0 && i < candidates.Count).Select(i => candidates[i]).ToArray();
            if (chosen.Length == 0)
            {
                if (selector.Presentation.Min > 0) { selector.Submit(new CancelEvent()); return false; }
                selector.Submit(new PickEvent());
                return true;
            }
            selector.Submit(selector.Mode == SelectorInteractionMode.Drag ? new DropEvent(chosen[0]) : new PickEvent(chosen));
            return true;
        }
        catch { try { selector.Submit(new CancelEvent()); } catch { /* 会话已终结 */ } return false; }
    }

    public void BeginTargeting(ITargeterSession session) => _ = ConsumeAsync(session);

    private async Task ConsumeAsync(ITargeterSession session)
    {
        while (await session.NextAsync() is { } selector)
        {
            try
            {
                if (TryHoldForAi(selector)) continue;
                if (present is null) AutoRespond(selector);
                else present(selector);
            }
            catch { selector.Submit(new CancelEvent()); }
        }
    }

    public static void AutoRespond(ISelectorInstance selector)
    {
        var data = selector.Presentation;
        if (selector.SelectorName == SelectorNames.Mulligan && data.Min == 0)
            selector.Submit(new PickEvent());
        else if (data.Identifiers is { Count: > 0 } ids && data.Min <= 1)
            selector.Submit(new PickEvent(identifiers: [ids[0]]));
        else if (data.Candidates.FirstOrDefault(r => r.IsAlive) is { } first && data.Min <= 1)
            selector.Submit(selector.Mode == SelectorInteractionMode.Drag ? new DropEvent(first) : new PickEvent([first]));
        else selector.Submit(new CancelEvent());
    }

    public static IReadOnlyList<Ref<Entity>> SelectReplace(IEnumerable<Ref<Entity>> allowed, IEnumerable<string> keepUids)
    {
        var keep = keepUids.ToHashSet(StringComparer.Ordinal);
        return allowed.Where(reference => !keep.Contains(OrcRefs.KeyOf(reference.Value))).ToArray();
    }
}

/// <summary>Labels attached by reviewed source effects; never an engine rule.</summary>
internal sealed record OrderSelectorTag(string SlotName, IReadOnlyDictionary<string, string>? Labels = null);
