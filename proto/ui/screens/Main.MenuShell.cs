using Godot;

namespace Kards.Ui;

public partial class Main
{
    private HomeScreen _home = null!;
    private MenuBackdrop _menuBackdrop = null!;
    private Button _resume = null!;
    private Control BuildMenuShell(VBoxContainer root)
    {
        root.Theme = MenuStyles.CreateMenuTheme();
        _menuBackdrop = new MenuBackdrop(); _menuBackdrop.Initialize(_textures);
        AddChild(_menuBackdrop); MoveChild(_menuBackdrop, 0); _menuBackdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var workspace = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; workspace.AddThemeConstantOverride("separation", 0); root.AddChild(workspace);
        _shellBar = new PanelContainer { CustomMinimumSize = new(172, 0) }; workspace.AddChild(_shellBar);
        var rail = MenuStyles.Box(new("161e19"), new("3e493b"), 0, 12); rail.BorderWidthLeft = rail.BorderWidthTop = rail.BorderWidthBottom = 0;
        _shellBar.AddThemeStyleboxOverride("panel", rail);
        var bar = new VBoxContainer(); bar.AddThemeConstantOverride("separation", 6); _shellBar.AddChild(bar);
        var brand = MenuStyles.Button("OrC-KSD", () => Show("home")); brand.ToggleMode = true;
        brand.TooltipText = "返回指挥部"; brand.AddThemeColorOverride("font_color", MenuStyles.Gold); brand.AddThemeFontSizeOverride("font_size", 25);
        MenuStyles.Navigation(brand); brand.CustomMinimumSize = new(0, 72); bar.AddChild(brand); _nav["home"] = brand;
        bar.AddChild(MenuStyles.Label("   卡牌作战", 12, MenuStyles.MenuDim));
        bar.AddChild(new Control { CustomMinimumSize = new(0, 24) });
        foreach (var (id, text) in new[] { ("decklib", "我的卡组"), ("collection", "卡牌收藏"), ("editor", "DIY 工坊") })
        {
            var key = id; var button = MenuStyles.Button(text, () => NavigateMenu(key)); button.ToggleMode = true;
            MenuStyles.Navigation(button); bar.AddChild(button); _nav[id] = button;
        }
        _resume = MenuStyles.Button("返回战场 →", () => Show("battle")); MenuStyles.Primary(_resume); bar.AddChild(_resume);
        bar.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        _pool = MenuStyles.Label($"{_deckService.PlayableCards.Count} 张可用卡牌", 12, MenuStyles.MenuDim); bar.AddChild(_pool);
        foreach (var (id, text) in new[] { ("help", "帮助 / 演示"), ("settings", "设置") })
        {
            var key = id; var button = MenuStyles.Button(text, () => Show(key)); button.ToggleMode = true;
            MenuStyles.Navigation(button); bar.AddChild(button); _nav[id] = button;
        }
        var update = MenuStyles.Button("离线更新", OpenOfflineUpdater); MenuStyles.Navigation(update); bar.AddChild(update);
        update.Disabled = OS.HasFeature("editor"); update.TooltipText = "使用已下载的增量补丁更新游戏";
        _contentMargin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; workspace.AddChild(_contentMargin);
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) _contentMargin.AddThemeConstantOverride("margin_" + edge, 28);
        var content = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; _contentMargin.AddChild(content);
        MenuHoverAccent.Watch(root, _clock); return content;
    }
    private void NavigateMenu(string id)
    {
        if (id is "decklib" or "collection") _deckLibrary.SetPlaySelection(false);
        Show(id);
    }
    private void OpenPlaySelection() { _deckLibrary.SetPlaySelection(true); Show("decklib"); }
    private void RefreshMenuData() { _deckLibrary.Refresh(); _home?.Refresh(); UpdateMenuNavigation(); }
    private void UpdateMenuNavigation()
    {
        foreach (var (id, button) in _nav) button.SetPressedNoSignal(id == _active || (id == "decklib" && _active == "deck"));
        _resume.Visible = _runner is { IsRunning: true };
    }
    public override void _UnhandledInput(InputEvent @event)
    {
        if (_active is not ("battle" or "deck" or "editor") && @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        { Show("home"); GetViewport().SetInputAsHandled(); }
    }
}
