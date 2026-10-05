using Godot;

namespace Kards.Ui;

public static class UiStyles
{
    public static readonly Color Bg = new("14161a"), Panel = new("1d2027"), Panel2 = new("23262e"),
        Line = new("333844"), Ink = new("e8e6e1"), Dim = new("9aa1ad"), Gold = new("c7aa71"), Green = new("8fd6a0");
    public static StyleBoxFlat Box(Color fill, Color? border = null, int radius = 6, int padding = 12)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border ?? Line,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            ContentMarginLeft = padding,
            ContentMarginRight = padding,
            ContentMarginTop = padding,
            ContentMarginBottom = padding
        };
        return box;
    }
    public static Theme CreateTheme()
    {
        var t = new Theme { DefaultFontSize = 15, DefaultFont = new SystemFont { FontNames = ["Microsoft YaHei", "Noto Sans CJK SC"], AllowSystemFallback = true } };
        foreach (var cls in new[] { "Label", "Button", "LineEdit", "OptionButton", "CheckButton", "RichTextLabel" })
            t.SetColor("font_color", cls, Ink);
        t.SetStylebox("normal", "Button", Box(Panel2));
        t.SetStylebox("hover", "Button", Box(new Color("303641"), Gold));
        t.SetStylebox("pressed", "Button", Box(new Color("3b3528"), Gold));
        t.SetStylebox("focus", "Button", Box(Colors.Transparent, Gold));
        t.SetStylebox("disabled", "Button", Box(Panel));
        t.SetColor("font_disabled_color", "Button", Dim.Darkened(.25f));
        t.SetStylebox("normal", "OptionButton", Box(Panel2));
        t.SetStylebox("hover", "OptionButton", Box(new Color("303641"), Gold));
        t.SetStylebox("pressed", "OptionButton", Box(Panel2, Gold));
        t.SetStylebox("normal", "LineEdit", Box(Panel2));
        t.SetStylebox("focus", "LineEdit", Box(Panel2, Gold));
        t.SetStylebox("panel", "PanelContainer", Box(Panel));
        t.SetConstant("separation", "VBoxContainer", 12);
        t.SetConstant("separation", "HBoxContainer", 12);
        t.SetConstant("h_separation", "GridContainer", 16);
        t.SetConstant("v_separation", "GridContainer", 16);
        return t;
    }
    public static Label Label(string text, int size = 15, Color? color = null)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color ?? Ink);
        return l;
    }
    public static Button Button(string text, Action action)
    {
        var b = new Button { Text = text, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        b.Pressed += action;
        return b;
    }
    public static void Clear(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }
    public static string TypeName(string type) => type switch { "unit" => "单位", "order" => "指令", "counter" => "反制", "unknown" => "未分类", _ => type };
    public static string RarityName(string rarity) => rarity switch { "iron" => "铁", "bronze" => "铜", "silver" => "银", "gold" => "金", "token" => "衍生", "common" => "普通", _ => rarity };
    public static Color RarityColor(string rarity) => rarity switch { "gold" => new("c9962b"), "silver" => new("9fb0c6"), "bronze" => new("9c5a24"), "iron" => new("6d7684"), _ => new("555b61") };
}
