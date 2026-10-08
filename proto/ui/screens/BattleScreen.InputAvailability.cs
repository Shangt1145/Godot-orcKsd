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
    private static readonly UiBattleActions NoTurnActions = new();
    private bool IsFriendlyTurn => InteractionView.Phase == "play" && InteractionView.ActivePlayerSide == "self";
    private UiBattleActions InteractionActions => IsFriendlyTurn ? _liveInputActions ?? _actions : NoTurnActions;
    private bool CanEndTurnNow => IsFriendlyTurn && InteractionActions.CanEndTurn;
    private bool? _endTurnFriendlyStyle;

    public void UpdateInputProjection(UiMatchView view, UiBattleActions actions)
    {
        if (_usingDemo || _state.MatchId != view.MatchId) return;
        // Input follows turn ownership immediately, independently of presentation playback.
        _liveInputView = view; _liveInputActions = actions;
        if (!IsFriendlyTurn && (_pressed is not null || _selected is not null)) CancelGesture();
        UpdateSelection(); UpdateEndTurnButton();
    }

    private void UpdateEndTurnButton()
    {
        var view = InteractionView;
        _endTurn.Text = view.Phase == "over" ? "对局结束" : view.ActivePlayerSide == "self" ? "结束回合" : "对手回合";
        _endTurn.Disabled = !CanEndTurnNow;
        if (_endTurnFriendlyStyle == IsFriendlyTurn) return;
        _endTurnFriendlyStyle = IsFriendlyTurn;
        foreach (var state in new[] { "normal", "disabled", "hover", "pressed", "focus" })
        {
            var fill = IsFriendlyTurn ? new Color(state == "hover" ? "ed9b42" : state == "pressed" ? "b96b22" : "d98632") : new Color("666760");
            var box = UiStyles.Box(fill, new(IsFriendlyTurn ? "f6c078" : "94958c"), 0, 4);
            box.BorderWidthLeft = box.BorderWidthTop = box.BorderWidthRight = box.BorderWidthBottom = 2;
            _endTurn.AddThemeStyleboxOverride(state, box);
        }
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
        if (command is PlayCard play && _deploymentOrigins.Remove(play.Uid)
            && _cards.TryGetValue(play.Uid, out var card) && card.Mode == BattleCardMode.Hand)
        {
            card.Position = card.RestPosition; card.Rotation = card.RestRotation; card.Scale = Vector2.One;
            card.ZIndex = 20 + (card.View?.SlotIndex ?? 0); card.MouseFilter = MouseFilterEnum.Stop;
        }
    }
}
