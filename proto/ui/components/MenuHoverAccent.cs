using Godot;

namespace Kards.Ui;

/// <summary>Presentation-only hover/focus feedback. Layout and hit areas stay fixed.</summary>
public partial class MenuHoverAccent : Control
{
    private BaseButton _button = null!;
    private AnimClock _clock = null!;
    private Tween? _tween;
    private float _amount;
    internal float Amount => _amount;

    internal static void Watch(Node node, AnimClock clock)
    {
        // Battle actors use their own motion and input treatment.
        if (node is BattleScreen or MenuHoverAccent || node.HasMeta("menu_motion_watched")) return;
        node.SetMeta("menu_motion_watched", true);
        if (node is Button button)
        {
            var accent = new MenuHoverAccent { _button = button, _clock = clock, MouseFilter = MouseFilterEnum.Ignore };
            button.AddChild(accent); accent.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }
        foreach (var child in node.GetChildren()) Watch(child, clock);
        node.ChildEnteredTree += child => Watch(child, clock);
    }
    public override void _Ready()
    {
        _button.MouseEntered += Refresh;
        _button.MouseExited += Refresh;
        _button.FocusEntered += Refresh;
        _button.FocusExited += Refresh;
        VisibilityChanged += Refresh;
        Resized += QueueRedraw;
        _clock.Changed += Refresh;
    }
    private void Refresh()
    {
        var target = IsVisibleInTree() && !_button.Disabled && (_button.IsHovered() || _button.HasFocus()) ? 1f : 0f;
        _tween?.Kill();
        if (_clock.ReducedMotion || !IsVisibleInTree()) { _amount = target; QueueRedraw(); return; }
        _tween = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _tween.TweenMethod(Callable.From<float>(value => { _amount = value; QueueRedraw(); }), _amount, target, .14);
    }
    public override void _Draw()
    {
        if (!_button.Disabled && _amount > .001f)
            DrawRect(new Rect2(Size.X * (1 - _amount) / 2, Size.Y - 2, Size.X * _amount, 2), new Color(UiStyles.Gold, _amount));
    }
    public override void _ExitTree() { _tween?.Kill(); _clock.Changed -= Refresh; }
}
