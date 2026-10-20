using Godot;

namespace Kards.Ui;

public static class MenuStyles
{
    public static readonly Color Bg = new("141a18"), Panel = new("202925"), Panel2 = new("2a342e"),
        Line = new("465048"), Ink = new("eee7d6"), Dim = new("abb3a5"), Gold = new("d7af69"), Green = new("afc69b");
    public static readonly Color MenuPanel = new("202925"), MenuLine = new("465048"), MenuInk = Ink,
        MenuDim = Dim, Accent = new("bb8846");
    public static Theme CreateMenuTheme() => CreateTheme();
    public static Theme CreateTheme()
    {
        var t = new Theme { DefaultFontSize = 15, DefaultFont = new SystemFont { FontNames = ["Microsoft YaHei", "Noto Sans CJK SC"], AllowSystemFallback = true } };
        foreach (var cls in new[] { "Label", "Button", "LineEdit", "OptionButton", "CheckButton", "CheckBox", "RichTextLabel", "TextEdit", "PopupMenu", "Tree", "ItemList" })
        {
            t.SetColor("font_color", cls, Ink); t.SetColor("font_disabled_color", cls, new("768174"));
        }
        foreach (var cls in new[] { "Button", "OptionButton", "MenuButton" })
        {
            t.SetStylebox("normal", cls, Box(Panel2, new("536052"), 2, 10));
            t.SetStylebox("hover", cls, Box(new("39463a"), Gold, 2, 10));
            t.SetStylebox("pressed", cls, Box(new("5b5135"), Gold, 2, 10));
            t.SetStylebox("disabled", cls, Box(new("1c2420"), new("354036"), 2, 10));
            t.SetStylebox("focus", cls, Focus());
            t.SetColor("font_hover_color", cls, new("fff4d8")); t.SetColor("font_pressed_color", cls, new("fff4d8"));
            t.SetColor("font_hover_pressed_color", cls, new("fff4d8"));
        }
        foreach (var cls in new[] { "LineEdit", "TextEdit" })
        {
            t.SetStylebox("normal", cls, Box(new("151e19"), new("536052"), 2, 10));
            t.SetStylebox("focus", cls, Box(new("1b271f"), Gold, 2, 10));
            t.SetStylebox("read_only", cls, Box(new("1b2420"), Line, 2, 10));
            t.SetColor("font_placeholder_color", cls, new("85917e"));
            t.SetColor("caret_color", cls, Gold); t.SetColor("selection_color", cls, new("6a6144"));
        }
        foreach (var cls in new[] { "PanelContainer", "Panel", "PopupMenu", "AcceptDialog", "Window", "Tree", "ItemList" })
            t.SetStylebox("panel", cls, Box(Panel, Line, 2, 16));
        t.SetStylebox("panel", "TabContainer", Box(Panel, Line, 2, 16));
        foreach (var cls in new[] { "TabContainer", "TabBar" })
        {
            t.SetStylebox("tab_selected", cls, Box(new("394137"), Gold, 2, 12));
            t.SetStylebox("tab_unselected", cls, Box(new("1a231d"), Line, 2, 12));
            t.SetStylebox("tab_hovered", cls, Box(new("303c30"), Gold, 2, 12));
            t.SetColor("font_selected_color", cls, Ink); t.SetColor("font_unselected_color", cls, Dim);
        }
        t.SetStylebox("panel", "TooltipPanel", Box(new("e0d6bc"), Gold, 2, 10));
        t.SetColor("font_color", "TooltipLabel", new("242d24"));
        t.SetStylebox("hover", "PopupMenu", Box(new("3a4636"), Colors.Transparent, 1, 8));
        t.SetStylebox("slider", "HSlider", Box(new("121c15"), Colors.Transparent, 2, 2));
        t.SetStylebox("grabber_area", "HSlider", Box(new("9c8952"), Colors.Transparent, 2, 2));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(Gold, Colors.Transparent, 2, 2));
        foreach (var cls in new[] { "VScrollBar", "HScrollBar" })
        {
            t.SetStylebox("scroll", cls, Box(new("172019"), Colors.Transparent, 2, 3));
            t.SetStylebox("grabber", cls, Box(new("62715c"), Colors.Transparent, 2, 3));
            t.SetStylebox("grabber_highlight", cls, Box(Gold, Colors.Transparent, 2, 3));
            t.SetStylebox("grabber_pressed", cls, Box(Accent, Colors.Transparent, 2, 3));
        }
        t.SetConstant("separation", "VBoxContainer", 12); t.SetConstant("separation", "HBoxContainer", 12);
        t.SetConstant("h_separation", "GridContainer", 18); t.SetConstant("v_separation", "GridContainer", 18);
        return t;
    }
    private static StyleBoxFlat Focus()
    {
        var box = Box(Colors.Transparent, new("e9c788"), 2, 0); box.BorderWidthLeft = box.BorderWidthTop = box.BorderWidthRight = box.BorderWidthBottom = 2; return box;
    }
    public static void Primary(Button button)
    {
        button.AddThemeStyleboxOverride("normal", Box(new("b58746"), new("d7b47a"), 2, 12));
        button.AddThemeStyleboxOverride("hover", Box(new("d2a45d"), new("f3d7a0"), 2, 12));
        button.AddThemeStyleboxOverride("pressed", Box(new("97703c"), Gold, 2, 12));
        foreach (var name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" })
            button.AddThemeColorOverride(name, new("171e18"));
        button.AddThemeFontSizeOverride("font_size", 18); button.CustomMinimumSize = new(0, 48);
    }
    public static void Navigation(Button button)
    {
        foreach (var (state, fill, border) in new[] { ("normal", "161e19", "161e19"), ("hover", "29372b", "29372b"), ("pressed", "333d2c", "d7af69"), ("hover_pressed", "3c4833", "d7af69") })
        {
            var box = Box(new(fill), new(border), 0, 14);
            box.BorderWidthLeft = 3; box.BorderWidthTop = box.BorderWidthRight = box.BorderWidthBottom = 0;
            button.AddThemeStyleboxOverride(state, box);
        }
        button.Alignment = HorizontalAlignment.Left; button.CustomMinimumSize = new(0, 52);
    }
    public static StyleBoxFlat Box(Color fill, Color? border = null, int radius = 2, int padding = 12) => new()
    {
        BgColor = fill, BorderColor = border ?? Line,
        BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
        ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = padding, ContentMarginBottom = padding
    };
    public static Label Label(string text, int size = 15, Color? color = null)
    {
        var l = new Label { Text = text }; l.AddThemeFontSizeOverride("font_size", size); l.AddThemeColorOverride("font_color", color ?? Ink); return l;
    }
    public static Button Button(string text, Action action)
    {
        var b = new Button { Text = text, MouseDefaultCursorShape = Control.CursorShape.PointingHand, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }; b.Pressed += action; return b;
    }
    public static void Clear(Node node) { foreach (var child in node.GetChildren()) { node.RemoveChild(child); child.QueueFree(); } }
    public static string TypeName(string type) => type switch { "unit" => "单位", "order" => "指令", "counter" => "反制", "unknown" => "未分类", _ => type };
    public static string RarityName(string rarity) => rarity switch { "iron" => "铁", "bronze" => "铜", "silver" => "银", "gold" => "金", "token" => "衍生", "common" => "普通", _ => rarity };
    public static Color RarityColor(string rarity) => rarity switch { "gold" => new("c9962b"), "silver" => new("9fb0c6"), "bronze" => new("9c5a24"), "iron" => new("6d7684"), _ => new("555b61") };
}
