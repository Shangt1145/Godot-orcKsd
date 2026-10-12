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
    private readonly Dictionary<string, IReadOnlyList<string>> _choiceSelections = new();
    private readonly Dictionary<string, string> _orderChoiceCaptions = new();
    private readonly Dictionary<string, UiCardView> _orderChoiceFaces = new();
    public bool TargetChoiceVisible => _choicePanel.Visible;
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
            _choiceReturnPose = new(hand.RestPosition, hand.Size, Vector2.One, hand.RestRotation);
            hand.Visible = false;
        }
        OpenChoiceSlot();
    }
    private void OpenChoiceSlot(IReadOnlyList<string>? selected = null)
    {
        if (_choiceRequest is not { } request) return;
        _choiceSlot = request.Slots.FirstOrDefault(s => !_choiceSelections.ContainsKey(s.Name));
        if (_choiceSlot is not { } slot) return;
        var origin = request.SourceCard is { } card && _orderOrigins.TryGetValue(card.Uid, out var pose) ? pose : (DeploymentPose?)null;
        _choicePanel.Open(slot, request.SourceCard, origin, selected);
        if (request.SourceCard is { } source && _cards.TryGetValue(source.Uid, out var hand)) hand.Visible = false;
    }
    public void ChooseTargetOption(string slotName, string id) => ChooseTargetOptions(slotName, [id]);
    public async void ChooseTargetOptions(string slotName, IReadOnlyList<string> ids)
    {
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
        await _choicePanel.FinishAsync(null, request.SourceCard, _choiceReturnPose);
        if (_choiceRequest != request) return;
        if (responder.Cancel(request.RequestId))
        {
            if (request.SourceCard is { } source) RefuseCommand(new PlayCard(source.Uid));
            if (_choiceRequest == request) CloseTargetChoice();
        }
        else { _choosing = false; OpenChoiceSlot(); }
    }
    public void CloseTargetChoice()
    {
        if (_choicePanel is null) return;
        if (_choiceRequest?.SourceCard is { } source && _cards.TryGetValue(source.Uid, out var hand))
        {
            if (_orderChoiceFaces.TryGetValue(source.Uid, out var chosen)) hand.Bind(chosen, BattleCardMode.Hand, _textures);
            hand.ChoiceCaption = _orderChoiceCaptions.GetValueOrDefault(source.Uid);
            hand.Visible = true;
        }
        _choicePanel.Reset(); _choiceRequest = null; _choiceResponder = null; _choiceSlot = null;
        _choiceReturnPose = null; _choiceSelections.Clear(); _choosing = false;
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
