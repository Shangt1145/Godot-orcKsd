using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    private Control _choicePanel = null!;
    private VBoxContainer _choiceRows = null!;
    private UiTargetRequest? _choiceRequest;
    private IUiTargetResponder? _choiceResponder;
    private readonly Dictionary<string, IReadOnlyList<string>> _choiceSelections = new();
    public bool TargetChoiceVisible => _choicePanel.Visible;
    private void InitializeTargetChoices()
    {
        _choicePanel = new Control { Size = BoardSize, ZIndex = 270, Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _choicePanel.AddChild(new ColorRect { Size = BoardSize, Color = new(.02f, .03f, .02f, .72f) });
        var panel = new PanelContainer { Position = new(280, 140), Size = new(720, 440) };
        var scroll = new ScrollContainer { CustomMinimumSize = new(680, 360) };
        _choiceRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_choiceRows); panel.AddChild(scroll); _choicePanel.AddChild(panel); _canvas.AddChild(_choicePanel);
    }
    public void ShowTargetChoice(UiTargetRequest request, IUiTargetResponder responder)
    {
        CancelGesture(); _choiceRequest = request; _choiceResponder = responder; _choiceSelections.Clear();
        UiStyles.Clear(_choiceRows);
        _choiceRows.AddChild(UiStyles.Label("选择目标或抉择", 24));
        foreach (var slot in request.Slots)
        {
            _choiceRows.AddChild(UiStyles.Label(slot.Kind == ChoiceKind.Option ? "选择一项" : "选择一个单位", 16));
            foreach (var choice in slot.Allowed)
            {
                var caption = choice.Card is { } card ? $"{(card.OwnerSide == "self" ? "我方" : "敌方")} · {choice.Label} · {card.EffectiveAttack}/{card.EffectiveDefense}" : choice.Label;
                var button = UiStyles.Button(caption, () => ChooseTargetOption(slot.Name, choice.Id));
                button.CustomMinimumSize = new(640, 42); _choiceRows.AddChild(button);
            }
        }
        _choiceRows.AddChild(UiStyles.Button("取消出牌", CancelTargetChoice)); _choicePanel.Visible = true;
    }
    public void ChooseTargetOption(string slot, string id)
    {
        if (_choiceRequest is not { } request || _choiceResponder is not { } responder) return;
        _choiceSelections[slot] = [id];
        if (request.Slots.All(s => _choiceSelections.ContainsKey(s.Name))
            && responder.Complete(request.RequestId, _choiceSelections)) CloseTargetChoice();
    }
    public void CancelTargetChoice()
    {
        if (_choiceRequest is { } request) _choiceResponder?.Cancel(request.RequestId);
        CloseTargetChoice();
    }
    public void CloseTargetChoice()
    {
        if (_choicePanel is null) return;
        _choicePanel.Visible = false; _choiceRequest = null; _choiceResponder = null; _choiceSelections.Clear();
    }
    public async Task ClickTargetChoiceAsync(int index)
    {
        var buttons = _choiceRows.GetChildren().OfType<Button>().ToArray();
        var at = buttons[index].GetGlobalTransformWithCanvas() * (buttons[index].Size / 2);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
