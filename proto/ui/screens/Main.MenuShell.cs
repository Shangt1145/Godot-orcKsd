using Godot;

namespace Kards.Ui;

public partial class Main
{
    private HomeScreen _home = null!;
    private MenuBackdrop _menuBackdrop = null!;
    private Button _resume = null!;

    private Control BuildMenuShell(VBoxContainer root)
    {
        root.Theme = UiStyles.CreateMenuTheme();
        _menuBackdrop = new MenuBackdrop(); _menuBackdrop.Initialize(_textures);
        AddChild(_menuBackdrop); MoveChild(_menuBackdrop, 0); _menuBackdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _shellBar = new PanelContainer(); root.AddChild(_shellBar);
        _shellBar.AddThemeStyleboxOverride("panel", UiStyles.Box(new("171f1bed"), new("626454"), 0, 12));
        var bar = new HBoxContainer(); bar.AddThemeConstantOverride("separation", 14); _shellBar.AddChild(bar);
        var brand = UiStyles.Button("✦  OrC-KSD", () => Show("home")); brand.CustomMinimumSize = new(204, 46); brand.ToggleMode = true;
        brand.TooltipText = "返回指挥部";
        brand.AddThemeColorOverride("font_color", UiStyles.Gold); brand.AddThemeFontSizeOverride("font_size", 23); bar.AddChild(brand); _nav["home"] = brand;
        foreach (var (id, text) in new[] { ("decklib", "我的卡组"), ("collection", "收藏"), ("editor", "DIY 工坊") })
        {
            var key = id; var button = UiStyles.Button(text, () => NavigateMenu(key)); button.ToggleMode = true;
            button.CustomMinimumSize = new(116, 46); bar.AddChild(button); _nav[id] = button;
        }
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var update=UiStyles.Button("离线更新",OpenOfflineUpdater);bar.AddChild(update);
        update.Disabled=OS.HasFeature("editor");update.TooltipText="使用已下载的增量补丁更新游戏";
        _resume = UiStyles.Button("返回战场 →", () => Show("battle")); bar.AddChild(_resume);
        _pool = UiStyles.Label($"{_deckService.PlayableCards.Count} 张可用卡牌", 13, UiStyles.MenuDim); bar.AddChild(_pool);
        foreach (var (id, text) in new[] { ("help", "帮助"), ("settings", "设置") })
        {
            var key = id; var button = UiStyles.Button(text, () => Show(key)); button.ToggleMode = true;
            bar.AddChild(button); _nav[id] = button;
        }
        _contentMargin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; root.AddChild(_contentMargin);
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) _contentMargin.AddThemeConstantOverride("margin_" + edge, 24);
        var content = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; _contentMargin.AddChild(content);
        MenuHoverAccent.Watch(root, _clock);
        return content;
    }
    private void NavigateMenu(string id)
    {
        if (id is "decklib" or "collection") _deckLibrary.SetPlaySelection(false);
        Show(id);
    }
    private void OpenPlaySelection()
    {
        _deckLibrary.SetPlaySelection(true); Show("decklib");
    }
    private void RefreshMenuData()
    {
        _deckLibrary.Refresh(); _home?.Refresh(); UpdateMenuNavigation();
    }
    private void UpdateMenuNavigation()
    {
        foreach (var (id, button) in _nav)
            button.SetPressedNoSignal(id == _active || (id == "decklib" && _active == "deck"));
        _resume.Visible = _runner is { IsRunning: true };
    }
    public override void _UnhandledInput(InputEvent @event)
    {
        if (_active is not ("battle" or "deck" or "editor")
            && @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Show("home"); GetViewport().SetInputAsHandled();
        }
    }
}
