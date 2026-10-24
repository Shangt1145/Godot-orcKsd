using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    private BattleChoices _choicePanel = null!;
    private UiTargetRequest? _choiceRequest;
    private IUiTargetResponder? _choiceResponder;
    private UiChoiceSlot? _choiceSlot;
    private DeploymentPose? _choiceReturnPose;
    private bool _choosing;
    private UiChoiceSlot? _arrowSlot; // 场上目标使用箭头；手牌、牌堆与抉择继续使用选牌界面。
    private bool _arrowPressed;
    private bool _arrowDragged;
    private Vector2 _arrowPressAt;
    private readonly List<string> _arrowPicks = new();
    private DeploymentPose? _choiceSourcePose;
    private readonly Dictionary<string, DeploymentPose> _choiceFormationPoses = new();
    private readonly Dictionary<string, Vector2> _deploymentChoiceCenters = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _choiceSelections = new();
    private readonly Dictionary<string, string> _orderChoiceCaptions = new();
    private readonly Dictionary<string, UiCardView> _orderChoiceFaces = new();
    public bool TargetChoiceVisible => _choiceRequest is not null;
    /// <summary>True while onboard targets are selected by dragging an arrow.</summary>
    internal bool ArrowChoiceActive => _arrowSlot is not null;
    internal bool ChoicePanelVisibleForProbe => _choicePanel is { Visible: true };
    private void InitializeTargetChoices()
    {
        _choicePanel = new BattleChoices { ZIndex = 270 };
        _choicePanel.Initialize(_textures, _clock); _canvas.AddChild(_choicePanel);
        _choicePanel.Chosen += id => { if (_choiceSlot is { } slot) ChooseTargetOption(slot.Name, id); };
        _choicePanel.Confirmed += ids => { if (_choiceSlot is { } slot) ChooseTargetOptions(slot.Name, ids); };
        _choicePanel.Cancelled += CancelTargetChoice;
    }
    public void ShowTargetChoice(UiTargetRequest request, IUiTargetResponder responder)
    {
        if (!IsFriendlyTurn) { responder.Cancel(request.RequestId); return; }
        CloseTargetChoice(); CancelGesture();
        request = UiSnapshots.Freeze(request);
        _choiceRequest = request; _choiceResponder = responder; _choiceSelections.Clear(); _choosing = false;
        if (request.SourceCard is { } source && _cards.TryGetValue(source.Uid, out var hand))
        {
            StopHandMotion(source.Uid);
            _choiceSourcePose = DeploymentPose.From(hand);
            var handPose = HandPose(source.SlotIndex, _state.SelfHand.Count, true);
            _choiceReturnPose = source.Zone == "hand"
                ? new(handPose.Position, new(144, 202), Vector2.One, handPose.Rotation)
                : new(hand.RestPosition, hand.Size, Vector2.One, hand.RestRotation);
            hand.Visible = false;
        }
        OpenChoiceSlot();
        UpdateEndTurnButton();
    }
    private void OpenChoiceSlot(IReadOnlyList<string>? selected = null)
    {
        if (_choiceRequest is not { } request) return;
        _choiceSlot = request.Slots.FirstOrDefault(s => !_choiceSelections.ContainsKey(s.Name));
        if (_choiceSlot is not { } slot) return;
        _arrowPressed = false;
        _arrowDragged = false; _arrowPicks.Clear();
        // Board semantics come from the bridge; a transient missing actor never changes the input mode.
        var boardTargets = slot.BoardTargets || request.SelectorName == "fieldUnit"
            || (request.SelectorName is not ("hand" or "cardPickerReference")
                && slot.Allowed.Any(c => IsBoardTarget(c.Card)
                    || (_cards.TryGetValue(c.Id, out var actor) && IsBoardTarget(actor.View))));
        _arrowSlot = slot.Kind == ChoiceKind.Reference && boardTargets ? slot : null;
        if (_arrowSlot is not null)
        {
            _choicePanel.Reset(); RestoreChoiceSource();
            if (slot.Max < 1 || slot.Allowed.Count < Math.Max(1,slot.Min))
            { CancelTargetChoice(); return; }
            UpdateArrowAim(_canvas.GetGlobalTransformWithCanvas().AffineInverse() * GetViewport().GetMousePosition());
            Hint("拖出箭头或点击目标，松手确认；右键取消"); return;
        }
        _aim.Visible = false;
        var origin = request.SourceCard is { } card && _orderOrigins.TryGetValue(card.Uid, out var pose) ? pose : (DeploymentPose?)null;
        _choicePanel.Open(slot, request.SourceCard, origin, selected);
        if (request.SourceCard is { } source && _cards.TryGetValue(source.Uid, out var hand)) hand.Visible = false;
    }
    private static bool IsBoardTarget(UiCardView? card) => card is not null
        && (card.IsHq || card.Zone is "support" or "frontline");
    public void ChooseTargetOption(string slotName, string id) => ChooseTargetOptions(slotName, [id]);
    public async void ChooseTargetOptions(string slotName, IReadOnlyList<string> ids)
    {
        if (_arrowSlot is not null) return; // No paper/button/programmatic alternative for board targeting.
        if (!IsFriendlyTurn || _choosing || _choiceRequest is not { } request || _choiceResponder is not { } responder
            || _choiceSlot is not { } slot || slot.Name != slotName || ids.Count == 0 || ids.Count < slot.Min || ids.Count != slot.Max
            || ids.Distinct().Count() != ids.Count || ids.Any(id => slot.Allowed.All(c => c.Id != id))) return;
        _choiceSelections[slotName] = ids.ToArray();
        if (!request.Slots.All(s => _choiceSelections.ContainsKey(s.Name))) { OpenChoiceSlot(); return; }
        _choosing = true;
        var single = slot.Min == 1 && slot.Max == 1;
        var id = single ? ids.Single() : null;
        var pose = await _choicePanel.FinishAsync(id, request.SourceCard, _choiceReturnPose, !single);
        if (_choiceRequest != request) return;
        if (!IsFriendlyTurn) { responder.Cancel(request.RequestId); CloseTargetChoice(); return; }
        if (request.SourceCard is { } source && pose is { } chosen)
        {
            _orderOrigins[source.Uid] = chosen;
            var option = single ? slot.Allowed.First(c => c.Id == id) : null;
            if (option?.Kind == ChoiceKind.Option) _orderChoiceCaptions[source.Uid] = option.Label;
            else if (option?.Kind == ChoiceKind.CardDefinition && option.Card is { Visibility: Visibility.Full } candidate)
                _orderChoiceFaces[source.Uid] = candidate with { Uid = source.Uid };
            RestorePendingDeploymentPoses();
        }
        if (responder.Complete(request.RequestId, _choiceSelections))
        { if (_choiceRequest == request) CloseTargetChoice(); }
        else
        {
            _choosing = false; _choiceSelections.Remove(slotName);
            if (request.SourceCard is { } refused) { _orderChoiceCaptions.Remove(refused.Uid); _orderChoiceFaces.Remove(refused.Uid); }
            ShowHint("选择未被接受，请重新确认");
            OpenChoiceSlot(single ? null : ids);
        }
    }
    public async void CancelTargetChoice()
    {
        if (_choosing || _choiceRequest is not { } request || _choiceResponder is not { } responder) return;
        _choosing = true;
        if (_arrowSlot is not null)
        {
            if(responder.Cancel(request.RequestId))
            {
                if(request.SourceCard is { } source)RefuseCommand(new PlayCard(source.Uid));
                if(_choiceRequest==request)CloseTargetChoice();
            }
            else {_choosing=false;OpenChoiceSlot();}
            return;
        }
        await _choicePanel.FinishAsync(null, request.SourceCard, _choiceReturnPose);
        if (_choiceRequest != request) return;
        if (responder.Cancel(request.RequestId))
        {
            if (request.SourceCard is { } source) RefuseCommand(new PlayCard(source.Uid));
            if (_choiceRequest == request) CloseTargetChoice();
        }
        else { _choosing = false; OpenChoiceSlot(); }
    }
    /// <summary>箭头指向：悬停时从来源卡画箭头到候选单位并高亮；空点保持等待，右键取消。</summary>
    private void UpdateArrowAim(Vector2 at)
    {
        if (_arrowSlot is not { } slot) return;
        string? hovered = null;
        foreach (var candidate in slot.Allowed)
            if (_cards.TryGetValue(candidate.Id, out var card)
                && new Rect2(card.Position, card.Size).HasPoint(at)) { hovered = candidate.Id; break; }
        foreach (var pair in _cards)
        {
            var on = pair.Key == hovered || _arrowPicks.Contains(pair.Key);
            if (pair.Value.Targeted != on) { pair.Value.Targeted = on; pair.Value.QueueRedraw(); }
        }
        _aim.From = ArrowSourceCenter();
        _aim.To = hovered is not null && _cards.TryGetValue(hovered, out var target)
            ? target.Position + target.Size / 2 : at;
        _aim.Moving = false; _aim.InvalidTarget = hovered is null; _aim.Preview = null;
        _aim.Visible = true; _aim.QueueRedraw();
    }
    private Vector2 ArrowSourceCenter()
    {
        if (_choiceRequest?.SourceCard is { } source && _cards.TryGetValue(source.Uid, out var card))
            return card.Position + card.Size / 2;
        return _choiceReturnPose?.Center ?? new Vector2(640, 700);
    }
    private void RestoreChoiceSource()
    {
        if (_choiceRequest?.SourceCard is not { } source || !_cards.TryGetValue(source.Uid, out var card)) return;
        card.Visible = _arrowSlot is not null;
        if (_arrowSlot is not null) RestoreDeploymentPreview(source.Uid, source);
    }
    private void RestoreDeploymentPreview(string uid, UiCardView source)
    {
        if (source.Definition.CardType != "unit" || !_cards.TryGetValue(uid, out var card)
            || !_deploymentChoiceCenters.TryGetValue(uid, out var center)) return;
        StopHandMotion(uid);
        // This is only a placement preview. The engine still owns payment and deployment.
        var row = _cards.Values.Where(c => c.Uid != source.Uid && c.View?.OwnerSide == "self"
            && (c.View.Zone == "support" || c.View.IsHq)).OrderBy(c => c.RestPosition.X).ToArray();
        var ordinal = row.Count(c => c.RestPosition.X + c.Size.X / 2 < center.X);
        var firstCenter = BoardSize.X / 2 - row.Length * SlotPitch / 2;
        for (var i = 0; i < row.Length; i++)
        {
            var neighbour = row[i];
            _choiceFormationPoses.TryAdd(neighbour.Uid, DeploymentPose.From(neighbour));
            neighbour.Position = new(firstCenter + (i < ordinal ? i : i + 1) * SlotPitch - neighbour.Size.X / 2, SelfArea.Position.Y);
        }
        center = new(firstCenter + ordinal * SlotPitch, center.Y);
        card.Bind(source, BattleCardMode.Field, _textures);
        card.Size = FieldCardSize; card.PivotOffset = FieldCardSize / 2;
        card.Position = center - FieldCardSize / 2; card.Scale = Vector2.One; card.Rotation = 0;
        card.MouseFilter = MouseFilterEnum.Ignore; card.Visible = true;
    }
    /// <summary>Called after a fresh press releases on a candidate supplied by the engine.</summary>
    private void CommitArrowChoice(Vector2 at)
    {
        if (_arrowSlot is not { } slot || _choosing) return;
        var hit = slot.Allowed.FirstOrDefault(a => _cards.TryGetValue(a.Id, out var card)
            && new Rect2(card.Position, card.Size).HasPoint(at));
        if (hit is null) return;
        if (_choiceRequest is not { } request || _choiceResponder is not { } responder) return;
        if(_arrowPicks.Contains(hit.Id))return;
        _arrowPicks.Add(hit.Id);
        if(_arrowPicks.Count<Math.Min(slot.Max,slot.Allowed.Count)) {UpdateArrowAim(at);return;}
        _choiceSelections[slot.Name] = _arrowPicks.ToArray();
        if (!request.Slots.All(s => _choiceSelections.ContainsKey(s.Name))) { OpenChoiceSlot(); return; }
        _choosing = true; _aim.Visible = false;
        if (responder.Complete(request.RequestId, _choiceSelections))
        { if (_choiceRequest == request) CloseTargetChoice(); }
        else
        {
            _choosing = false; _choiceSelections.Remove(slot.Name);
            ShowHint("选择未被接受，请重新确认"); OpenChoiceSlot();
        }
    }
    public void CloseTargetChoice()
    {
        if (_choicePanel is null) return;
        _arrowSlot = null;
        _arrowPressed = false; _arrowDragged = false; _arrowPicks.Clear(); _aim.Visible = false;
        var keepDeployment = _choiceRequest?.SourceCard is { } pending
            && _deploymentOrigins.ContainsKey(pending.Uid) && _deploymentChoiceCenters.ContainsKey(pending.Uid);
        foreach (var (uid, pose) in _choiceFormationPoses)
            if (!keepDeployment && _cards.TryGetValue(uid, out var neighbour)) neighbour.Position = pose.Position;
        _choiceFormationPoses.Clear();
        foreach (var pair in _cards)
            if (pair.Value.Targeted) { pair.Value.Targeted = false; pair.Value.QueueRedraw(); }
        if (_choiceRequest?.SourceCard is { } source && _cards.TryGetValue(source.Uid, out var hand))
        {
            if (!keepDeployment && _choiceSourcePose is { } original)
            {
                if (source.Zone == "hand" && _choiceReturnPose is { } returned) original = returned;
                hand.Bind(source, source.Zone == "hand" ? BattleCardMode.Hand : BattleCardMode.Field, _textures);
                hand.Size = original.Size; hand.PivotOffset = original.Size / 2;
                hand.Position = original.Position; hand.Scale = original.Scale; hand.Rotation = original.Rotation;
                if (source.Zone == "hand" && !HasPendingHandPlay(source.Uid))
                {
                    hand.Position = hand.RestPosition; hand.Rotation = hand.RestRotation;
                    hand.MouseFilter = MouseFilterEnum.Stop;
                    hand.ZIndex = 20 + source.SlotIndex;
                }
            }
            if (!keepDeployment && _orderChoiceFaces.TryGetValue(source.Uid, out var chosen)) hand.Bind(chosen, BattleCardMode.Hand, _textures);
            hand.ChoiceCaption = _orderChoiceCaptions.GetValueOrDefault(source.Uid);
            hand.Visible = true;
        }
        _choicePanel.Reset(); _choiceRequest = null; _choiceResponder = null; _choiceSlot = null;
        _choiceReturnPose = null; _choiceSelections.Clear(); _choosing = false;
        _choiceSourcePose = null;
        if (_state is not null) UpdateEndTurnButton();
    }
    public async Task ClickTargetChoiceAsync(int index)
    {
        while (_choicePanel.IsEntering) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!TargetChoiceVisible) throw new InvalidOperationException("Choice closed before the mouse selection.");
        var paper = _choicePanel.Papers[index];
        var at = paper.GetGlobalTransformWithCanvas() * (paper.Size / 2);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
