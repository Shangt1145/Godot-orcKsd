using Godot;

namespace Kards.Ui;

public static class UiStyles
{
    public static readonly Color MenuPanel = new("1c221eee"), MenuLine = new("666453"), MenuInk = new("efe7d1"),
        MenuDim = new("aaa994"), Accent = new("c17c36");
    // The application shell has its own theme; the battle keeps its existing presentation.
    public static Theme CreateMenuTheme()
    {
        var theme = CreateTheme();
        foreach (var cls in new[] { "Label", "Button", "LineEdit", "OptionButton", "CheckButton", "CheckBox", "RichTextLabel" })
            theme.SetColor("font_color", cls, MenuInk);
        foreach (var cls in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", cls, Box(new("282d27"), new("5b5b4b"), 1, 10));
            theme.SetStylebox("hover", cls, Box(new("3b4135"), Gold, 1, 10));
            theme.SetStylebox("pressed", cls, Box(new("4a4632"), Gold, 1, 10));
            theme.SetStylebox("focus", cls, Box(Colors.Transparent, Gold, 1, 10));
            theme.SetStylebox("disabled", cls, Box(new("20261f"), new("3f463b"), 1, 10));
            theme.SetColor("font_disabled_color", cls, new("737b6b"));
        }
        theme.SetStylebox("normal", "LineEdit", Box(new("1a211d"), new("535949"), 1, 10));
        theme.SetStylebox("focus", "LineEdit", Box(new("232b23"), Gold, 1, 10));
        theme.SetStylebox("panel", "PanelContainer", Box(MenuPanel, MenuLine, 1, 16));
        theme.SetStylebox("panel", "PopupMenu", Box(new("20281f"), MenuLine, 1, 10));
        theme.SetColor("font_color", "PopupMenu", MenuInk);
        theme.SetStylebox("panel", "AcceptDialog", Box(new("20281f"), MenuLine, 2, 18));
        return theme;
    }
    public static void Primary(Button button)
    {
        button.AddThemeStyleboxOverride("normal", Box(new("a2632c"), new("ddb476"), 1, 12));
        button.AddThemeStyleboxOverride("hover", Box(new("be7e38"), new("eed2a0"), 1, 12));
        button.AddThemeStyleboxOverride("pressed", Box(new("875327"), Gold, 1, 12));
        button.AddThemeFontSizeOverride("font_size", 18);
        button.CustomMinimumSize = new(0, 48);
    }
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
        // 中文字体是跨平台硬依赖：桌面有微软雅黑，Android/iOS 没有——只写雅黑会在手机上满屏方块。
        // AllowSystemFallback 让引擎自己找 CJK，但把 Android/Linux 常见的字体名显式列出来更稳。
        var t = new Theme
        {
            DefaultFontSize = 15,
            DefaultFont = new SystemFont
            {
                FontNames = ["Microsoft YaHei", "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC",
                             "Droid Sans Fallback", "Noto Sans CJK JP", "Noto Sans CJK TC"],
                AllowSystemFallback = true,
            },
        };
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
