using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>Enemy ownership stays locked until playback reaches the next friendly turn.</summary>
public sealed class TurnInputGate
{
    private bool _waitingForFriendlyTurn;
    private int _lastEnemyOrEndedTurn;

    public bool AllowsInput(UiMatchView view) => view.Phase == "play"
        && view.ActivePlayerSide == "self" && !_waitingForFriendlyTurn;

    public void Reset(UiMatchView view)
    {
        _waitingForFriendlyTurn = false;
        _lastEnemyOrEndedTurn = 0;
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
        else if (turn.Side == "self" && turn.Turn > _lastEnemyOrEndedTurn)
            _waitingForFriendlyTurn = false;
    }

    public void RefuseEndTurn(UiMatchView view)
    {
        // Only the refused request's unchanged friendly turn can undo its immediate lock.
        if (view.ActivePlayerSide == "self" && view.Turn == _lastEnemyOrEndedTurn)
            _waitingForFriendlyTurn = false;
    }

    private void HoldThrough(int turn)
    {
        _waitingForFriendlyTurn = true;
        _lastEnemyOrEndedTurn = Math.Max(_lastEnemyOrEndedTurn, turn);
    }
}
