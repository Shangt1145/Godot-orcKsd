using System;
using System.Threading.Tasks;
using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    private UiMatchView? _liveInputView;
    private UiBattleActions? _liveInputActions;
    private readonly TurnInputGate _turnInput = new();
    private readonly BattleInteractionStateMachine _interaction = new();
    private string? _dispatchingActor;
    private UiCommand? _dispatchingCommand;
    private bool _dispatchRefused;
    private IReadOnlyList<UiDeploymentGap> _dragDeploymentGaps = Array.Empty<UiDeploymentGap>();
    private TaskCompletionSource? _gestureReleased;
    private readonly Dictionary<string, Tween> _handMotion = new();

    private UiMatchView InteractionView => _liveInputView ?? _state;
    private static readonly UiBattleActions NoTurnActions = new();
    private bool IsFriendlyTurn => _turnInput.AllowsInput(InteractionView);
    private UiBattleActions InteractionActions => IsFriendlyTurn ? _liveInputActions ?? _actions : NoTurnActions;
    private bool CanEndTurnNow => IsFriendlyTurn && InteractionActions.CanEndTurn;
    private bool CanConcedeNow => !_usingDemo && CanEndTurnNow && !TargetChoiceVisible;
    private bool? _endTurnFriendlyStyle;

    public UiActionInteractionState ReadInteractionState()
    {
        _interaction.Phase = !IsFriendlyTurn ? UiInteractionPhase.TurnTransition
            : TargetChoiceVisible || MulliganPanelVisible ? UiInteractionPhase.Choosing
            : _dragging ? UiInteractionPhase.Dragging : UiInteractionPhase.Ready;
        var state = _interaction.Read();
        return _dispatchingActor is null ? state : state with
            { PendingActorUids = state.PendingActorUids.Where(uid => uid != _dispatchingActor).ToArray() };
    }

    private float GapX(UiDeploymentGap gap)
    {
        bool HasFieldActor(string uid) => _cards.TryGetValue(uid, out var card)
            && card.Mode is BattleCardMode.Field or BattleCardMode.Hq;
        // A committed unit may still be represented by its released hand card while it waits
        // for deployment playback. Never use that hand position as a formation anchor.
        var waiting = _dragDeploymentGaps.SelectMany(g => new[] { g.LeftUid, g.RightUid })
            .OfType<string>().Any(uid => !HasFieldActor(uid));
        if (waiting)
            return BoardSize.X / 2f + (gap.Ordinal - (_dragDeploymentGaps.Count - 1) / 2f) * SlotPitch;
        float? Center(string? uid) => uid is not null && HasFieldActor(uid) && _cards.TryGetValue(uid, out var card)
            ? card.RestPosition.X + card.Size.X / 2 : null;
        var left = Center(gap.LeftUid); var right = Center(gap.RightUid);
        if (left is { } lx && right is { } rx) return (lx + rx) / 2;
        if (left is { } l) return l + SlotPitch / 2;
        if (right is { } r) return r - SlotPitch / 2;
        // A pending deployment may not have a visible actor yet. Its ordinal still belongs to
        // the authoritative ordered formation, rather than the empty storage slots.
        return BoardSize.X / 2f + (gap.Ordinal - (_dragDeploymentGaps.Count - 1) / 2f) * SlotPitch;
    }

    private UiDeploymentGap? DeploymentGapAt(float x) => _dragDeploymentGaps
        .OrderBy(gap => Math.Abs(GapX(gap) - x)).FirstOrDefault();

    public void UpdateInputProjection(UiMatchView view, UiBattleActions actions)
    {
        if (_usingDemo || _state.MatchId != view.MatchId) return;
        // Latest legal actions never release the lock while enemy playback is still pending.
        _liveInputView = view; _liveInputActions = actions;
        _turnInput.Observe(view);
        RefreshTurnInput();
    }

    public void ObservePresentationTurn(UiPresentationResolution resolution)
    {
        if (_state.MatchId != resolution.MatchId) return;
        _turnInput.Observe(resolution);
        RefreshTurnInput();
    }

    public void SuspendMatchInput()
    {
        if (_usingDemo) return;
        _liveInputView = InteractionView with { Phase = "transition" }; _liveInputActions = NoTurnActions;
        _interaction.Reset();
        _concedeDialog.Hide(); CloseTargetChoice(); MulliganProjection("transition");
        CancelGesture(); ClearPresentation(); _combat.Interrupt(); ClearRemovalActors();
        RefreshTurnInput();
    }

    private void RefreshTurnInput()
    {
        if (!IsFriendlyTurn && (_pressed is not null || _selected is not null || _hovered is not null
            || _detail.Visible || _inspectStats.Visible)) CancelGesture();
        UpdateSelection(); UpdateEndTurnButton();
        if (_dragging && _pressed is { } source && IsTargetedOrder(source.Uid)) UpdateOrderAim(source, _aim.To);
    }

    private void UpdateEndTurnButton()
    {
        var view = InteractionView;
        _endTurn.Text = view.Phase == "over" ? "对局结束" : IsFriendlyTurn ? "结束回合" : "对手回合";
        _endTurn.Disabled = !CanEndTurnNow;
        _menu.SetItemDisabled(_menu.GetItemIndex(29), !CanConcedeNow);
        if (!CanConcedeNow) _concedeDialog.Hide();
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
        if (_hovered is { Mode: BattleCardMode.Hand } hovered && hovered != _pressed
            && GodotObject.IsInstanceValid(hovered) && !HasPendingHandPlay(hovered.Uid))
        {
            StopHandMotion(hovered.Uid);
            hovered.Position = hovered.RestPosition; hovered.Rotation = hovered.RestRotation;
            hovered.ZIndex = 20 + (hovered.View?.SlotIndex ?? 0);
        }
        if (_pressed is { } card && GodotObject.IsInstanceValid(card) && !HasPendingHandPlay(card.Uid))
        {
            StopHandMotion(card.Uid);
            card.Position = card.RestPosition; card.Rotation = card.RestRotation;
            card.ZIndex = card.Mode == BattleCardMode.Hand ? 20 + (card.View?.SlotIndex ?? 0) : 0;
        }
        _pressed = null; _hovered = null; _dragging = false; _selected = null;
        _aim.Visible = false; _aim.Preview = null; _aim.InvalidTarget = false; _detail.Visible = _inspectStats.Visible = false;
        ClearSlotHints(); UpdateSelection(); _hint.Visible = false; ReleaseGestureWaiter();
        _dragDeploymentGaps = Array.Empty<UiDeploymentGap>();
    }

    public void RefuseCommand(UiCommand command)
    {
        _interaction.Refuse(command);
        if (_dispatchingCommand == command) _dispatchRefused = true;
        if (command is EndTurn) { _turnInput.RefuseEndTurn(InteractionView); RefreshTurnInput(); }
        if (command is CommandUnit gesture) _frontPlacements.Remove(gesture.Uid);
        if (command is PlayCard play)
        {
            _orderChoiceCaptions.Remove(play.Uid);
            _orderChoiceFaces.Remove(play.Uid);
            var hadDeployment = _deploymentOrigins.Remove(play.Uid);
            var hadOrder = _orderOrigins.Remove(play.Uid);
            if ((hadDeployment || hadOrder) && _cards.TryGetValue(play.Uid, out var card) && card.Mode == BattleCardMode.Hand)
            {
                if (InteractionCard(play.Uid) is { } restored) card.Bind(restored, BattleCardMode.Hand, _textures);
                card.ChoiceCaption = null;
                card.Position = card.RestPosition; card.Rotation = card.RestRotation; card.Scale = Vector2.One;
                card.ZIndex = 20 + (card.View?.SlotIndex ?? 0); card.MouseFilter = MouseFilterEnum.Stop;
            }
        }
    }
}
