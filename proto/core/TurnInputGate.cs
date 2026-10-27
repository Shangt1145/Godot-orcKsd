using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>Enemy ownership stays locked until playback reaches the next friendly turn.</summary>
public sealed class TurnInputGate
{
    private bool _waitingForFriendlyTurn;
    private int _lastEnemyOrEndedTurn;
    private int _lastPresentedFriendlyTurn;

    public bool AllowsInput(UiMatchView view) => view.Phase == "play"
        && view.ActivePlayerSide == "self" && !_waitingForFriendlyTurn;

    public void Reset(UiMatchView view)
    {
        _waitingForFriendlyTurn = false;
        _lastEnemyOrEndedTurn = 0;
        _lastPresentedFriendlyTurn = view.Phase == "play" && view.ActivePlayerSide == "self" ? view.Turn : 0;
        Observe(view);
    }

    public void EndTurnRequested(UiMatchView view) => HoldThrough(view.Turn);

    public void Observe(UiMatchView view)
    {
        if (view.ActivePlayerSide == "enemy") HoldThrough(view.Turn);
    }

    public void Observe(UiPresentationResolution resolution)
    {
        if (resolution.Before is { } before) Observe(before);
        Observe(resolution.After);
        foreach (var turn in resolution.Steps.OfType<UiTurnPresentation>())
            if (turn.Side == "enemy") HoldThrough(turn.Turn);
    }

    public void Present(UiTurnPresentation turn)
    {
        if (turn.Side == "enemy") HoldThrough(turn.Turn);
        else if (turn.Side == "self")
        {
            _lastPresentedFriendlyTurn = Math.Max(_lastPresentedFriendlyTurn, turn.Turn);
            if (turn.Turn > _lastEnemyOrEndedTurn) _waitingForFriendlyTurn = false;
        }
    }

    public void RefuseEndTurn(UiMatchView view)
    {
        // Only the refused request's unchanged friendly turn can undo its immediate lock.
        if (view.ActivePlayerSide == "self" && view.Turn == _lastEnemyOrEndedTurn)
            _waitingForFriendlyTurn = false;
    }

    /// <summary>
    /// 权威视图说"轮到我"，本地却还锁着——说明解锁所依赖的那段回合横幅永远没送达（段丢失）。
    /// 返回 true 表示刚解开。解锁后同步把 <see cref="_lastPresentedFriendlyTurn"/> 抬到当前回合，
    /// 免得迟到的旧段又把它锁回去。
    /// </summary>
    public bool Reconcile(UiMatchView view)
    {
        if (!_waitingForFriendlyTurn) return false;
        if (view.Phase != "play" || view.ActivePlayerSide != "self") return false;
        _waitingForFriendlyTurn = false;
        _lastPresentedFriendlyTurn = Math.Max(_lastPresentedFriendlyTurn, view.Turn);
        return true;
    }

    private void HoldThrough(int turn)
    {
        // A delayed segment may still carry the previous enemy Before snapshot. It cannot
        // relock a friendly turn whose banner has already finished playing.
        if (turn < _lastPresentedFriendlyTurn) return;
        _waitingForFriendlyTurn = true;
        _lastEnemyOrEndedTurn = Math.Max(_lastEnemyOrEndedTurn, turn);
    }
}
