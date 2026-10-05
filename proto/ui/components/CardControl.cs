using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>Layout is owned by this wrapper; animation only modifies Visual.</summary>
public partial class CardControl : Control
{
    public Control Visual { get; private set; } = null!;
    public UiCardDefinition Definition { get; private set; } = null!;
    public UiCardView? View
    {
        get; private set;
    }
    public string Uid => View?.Uid ?? "";
    private TextureCache _cache = null!;
    private bool _selected, _target, _pressing, _longPressed;
    private double _pressTime;
    public event Action<CardControl>? Activated;
    public event Action<CardControl>? LongPressed;

    public void Bind(UiCardDefinition definition, TextureCache cache, float width = 148, UiCardView? view = null)
    {
        Definition = definition;
        View = view;
        _cache = cache;
        CustomMinimumSize = new Vector2(width, width * 1.402f);
        Size = CustomMinimumSize;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        TooltipText = view?.Visibility is Visibility.Hidden or Visibility.Silhouette ? "未知卡牌" : $"{definition.Name}\n{definition.Text}";
        if (view?.Visibility == Visibility.Full && view.BlockedReasons.Count > 0)
            TooltipText += "\n" + string.Join("\n", view.BlockedReasons);
        Rebuild();
    }
    public void SetState(bool selected, bool target)
    {
        _selected = selected;
        _target = target;
        QueueRedraw();
    }
    private void Rebuild()
    {
        UiStyles.Clear(this);
        Visual = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(Visual);
        Visual.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Visual.PivotOffset = Size / 2;
        var full = View is null || View.Visibility == Visibility.Full;
        var tex = full ? _cache.Get(Definition.ArtPath) : null;
        if (tex is not null)
        {
            var image = new TextureRect
            {
                Texture = tex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore
            };
            Visual.AddChild(image);
            image.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            image.OffsetLeft = 2;
            image.OffsetTop = 2;
            image.OffsetRight = -2;
            image.OffsetBottom = -2;
            if (View?.IsSuppressed == true)
            {
                var shader = new Shader { Code = "shader_type canvas_item; void fragment(){ vec4 c=texture(TEXTURE,UV); float g=dot(c.rgb,vec3(.299,.587,.114)); COLOR=vec4(vec3(g)*.66,c.a); }" };
                image.Material = new ShaderMaterial { Shader = shader };
            }
        }
        else
        {
            var back = new Panel { MouseFilter = MouseFilterEnum.Ignore };
            back.AddThemeStyleboxOverride("panel", UiStyles.Box(UiStyles.Panel2));
            Visual.AddChild(back);
            back.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
            foreach (var edge in new[] { "left", "right", "top", "bottom" })
                margin.AddThemeConstantOverride("margin_" + edge, 10);
            Visual.AddChild(margin);
            margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var content = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
            margin.AddChild(content);
            var text = UiStyles.Label(full ? Definition.Name : "？", Size.X > 200 ? 24 : 16, UiStyles.Gold);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            text.MouseFilter = MouseFilterEnum.Ignore;
            content.AddChild(text);
            if (full)
            {
                if (Definition.NameEn.Length > 0)
                {
                    var en = UiStyles.Label(Definition.NameEn, 11, UiStyles.Dim);
                    en.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    en.MouseFilter = MouseFilterEnum.Ignore;
                    content.AddChild(en);
                }
                var cost = UiStyles.Label($"{Definition.Cost?.ToString() ?? "—"} K · {UiStyles.TypeName(Definition.CardType)}", 12, UiStyles.Dim);
                cost.HorizontalAlignment = HorizontalAlignment.Center;
                cost.MouseFilter = MouseFilterEnum.Ignore;
                content.AddChild(cost);
                var desc = UiStyles.Label(Definition.Text, Size.X > 200 ? 15 : 11);
                desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                desc.MouseFilter = MouseFilterEnum.Ignore;
                desc.MaxLinesVisible = Size.X > 200 ? 12 : 5;
                desc.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                content.AddChild(desc);
            }
        }
        if (View is not null && full)
        {
            var stats = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            stats.AddThemeConstantOverride("separation", 2);
            Visual.AddChild(stats);
            stats.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
            stats.OffsetTop = -32;
            foreach (var s in new[] { ("费", View.EffectiveCost), ("攻", View.EffectiveAttack), ("防", View.EffectiveDefense), ("操", View.EffectiveOpCost) })
            {
                var badge = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
                badge.AddThemeStyleboxOverride("panel", UiStyles.Box(new Color(.06f, .07f, .09f, .94f), UiStyles.Gold, 3, 2));
                var l = UiStyles.Label($"{s.Item1}{s.Item2?.ToString() ?? "—"}", 12);
                l.HorizontalAlignment = HorizontalAlignment.Center;
                badge.AddChild(l);
                stats.AddChild(badge);
            }
            var flags = new List<string>();
            if (View.IsSuppressed)
                flags.Add("抑制");
            if (View.IsSilenced)
                flags.Add("静默");
            if (View.IsVeteran)
                flags.Add("老兵");
            foreach (var kw in View.Keywords)
                flags.Add(KeywordLabel(kw, View.KeywordValues));
            if (flags.Count > 0)
            {
                var label = UiStyles.Label(string.Join(" · ", flags), 11, UiStyles.Gold);
                label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                label.MouseFilter = MouseFilterEnum.Ignore;
                Visual.AddChild(label);
                label.Position = new Vector2(6, 34);
                label.Size = new Vector2(Size.X - 12, 48);
            }
        }
        Resized += UpdatePivot;
    }
    private void UpdatePivot()
    {
        if (GodotObject.IsInstanceValid(Visual))
            Visual.PivotOffset = Size / 2;
    }
    public static string KeywordLabel(string keyword, IReadOnlyDictionary<string, int> values)
    {
        var name = keyword switch
        {
            "armor" => "◆ 重甲",
            "blitz" => "➜ 闪击",
            "guard" => "▣ 守护",
            "ambush" => "◈ 伏击",
            "fury" => "✦ 狂怒",
            "veteran" => "★ 老兵",
            _ => "◇ " + keyword
        };
        return values.TryGetValue(keyword, out var n) ? $"{name} {n}" : name;
    }
    public override void _Draw()
    {
        if (Definition is null)
            return;
        var full = View is null || View.Visibility == Visibility.Full;
        var color = _selected ? new Color("6cc0ff") : _target ? UiStyles.Gold : View?.CanAttack == true ? UiStyles.Green : View?.CanPlayCard == true ? UiStyles.Gold : full ? UiStyles.RarityColor(Definition.Rarity) : UiStyles.Line;
        var b = UiStyles.Box(Colors.Transparent, color, 6, 0);
        var w = _selected || _target || View?.CanAttack == true ? 3 : 2;
        b.BorderWidthLeft = b.BorderWidthRight = b.BorderWidthTop = b.BorderWidthBottom = w;
        DrawStyleBox(b, new Rect2(Vector2.Zero, Size));
    }
    public override void _GuiInput(InputEvent e)
    {
        bool? pressed = e switch
        {
            InputEventMouseButton m when m.ButtonIndex == MouseButton.Left => m.Pressed,
            InputEventScreenTouch t => t.Pressed,
            _ => null
        };
        if (pressed == true)
        {
            _pressing = true;
            _pressTime = 0;
            _longPressed = false;
            AcceptEvent();
        }
        if (pressed == false)
        {
            if (_pressing && !_longPressed)
                Activated?.Invoke(this);
            _pressing = false;
            AcceptEvent();
        }
    }
    public override void _Process(double delta)
    {
        if (!_pressing || _longPressed)
            return;
        _pressTime += delta;
        if (_pressTime >= .45)
        {
            _longPressed = true;
            LongPressed?.Invoke(this);
        }
    }
}
