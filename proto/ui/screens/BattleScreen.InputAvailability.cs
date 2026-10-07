using System;
using System.Threading.Tasks;
using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    private UiMatchView? _liveInputView;
    private UiBattleActions? _liveInputActions;
    private TaskCompletionSource? _gestureReleased;
    private readonly Dictionary<string, Tween> _handMotion = new();

    private UiMatchView InteractionView => _liveInputView ?? _state;
    private UiBattleActions InteractionActions => _liveInputActions ?? _actions;
    private bool CanEndTurnNow => InteractionActions.CanEndTurn;

    public void UpdateInputProjection(UiMatchView view, UiBattleActions actions)
    {
        if (_usingDemo || _state.MatchId != view.MatchId) return;
        // Input reads current engine state. FIFO playback keeps its own historical board.
        _liveInputView = view; _liveInputActions = actions;
        UpdateSelection(); UpdateEndTurnButton();
    }

    private void UpdateEndTurnButton()
    {
        var view = InteractionView;
        _endTurn.Text = view.Phase == "over" ? "对局结束" : view.ActivePlayerSide == "self" ? "结束回合" : "对手回合";
        _endTurn.Disabled = !CanEndTurnNow;
    }

    private UiCardView? InteractionCard(string uid)
    {
        var view = InteractionView;
        return view.SelfHand.Concat(view.SelfLine).Concat(view.EnemyLine)
            .Concat(new[] { view.SelfHq, view.EnemyHq }.OfType<UiCardView>()).FirstOrDefault(c => c.Uid == uid);
    }

    private int[] SupportOccupancy() => InteractionView.SelfLine
        .Where(c => c.Zone == "support").Select(c => c.SlotIndex)
        .Concat(InteractionView.SelfHq is { } hq ? new[] { hq.SlotIndex } : Array.Empty<int>()).ToArray();

    private void StopHandMotion(string uid)
    {
        if (!_handMotion.Remove(uid, out var tween)) return;
        if (GodotObject.IsInstanceValid(tween)) tween.Kill();
        _tweens.Remove(tween);
    }

    private Task WaitForGestureReleaseAsync()
    {
        if (_pressed is null) return Task.CompletedTask;
        return (_gestureReleased ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    private void ReleaseGestureWaiter()
    {
        var waiting = _gestureReleased; _gestureReleased = null; waiting?.TrySetResult();
    }

    private void CancelGesture()
    {
        if (_pressed is { } card && GodotObject.IsInstanceValid(card))
        {
            StopHandMotion(card.Uid);
            card.Position = card.RestPosition; card.Rotation = card.RestRotation;
            card.ZIndex = card.Mode == BattleCardMode.Hand ? 20 + (card.View?.SlotIndex ?? 0) : 0;
        }
        _pressed = null; _hovered = null; _dragging = false; _selected = null;
        _aim.Visible = false; _aim.Preview = null; _detail.Visible = _inspectStats.Visible = false;
        ClearSlotHints(); UpdateSelection(); _hint.Visible = false; ReleaseGestureWaiter();
    }

    public void RefuseCommand(UiCommand command)
    {
        if (command is CommandUnit gesture) _frontPlacements.Remove(gesture.Uid);
    }
}
