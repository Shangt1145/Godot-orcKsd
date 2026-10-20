using Godot;

namespace Kards.Ui;

public partial class Main
{
    private MenuSplash? _splash;
    private Tween? _menuTransition;
    private Control? _transitionScreen;
    private ColorRect? _screenCurtain;
    private Tween? _screenCurtainTween;
    private int _startupStages;
    private bool _splashFadeInputVerified;
    private void ResetMenuTransition()
    {
        _menuTransition?.Kill(); _menuTransition = null;
        if (_transitionScreen is { } screen && GodotObject.IsInstanceValid(screen))
        {
            screen.Modulate = Colors.White; screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            screen.OffsetLeft = screen.OffsetRight = screen.OffsetTop = screen.OffsetBottom = 0;
        }
        _transitionScreen = null;
        _screenCurtainTween?.Kill(); _screenCurtainTween = null;
        if (_screenCurtain is { } curtain && GodotObject.IsInstanceValid(curtain))
        {
            curtain.Visible = false; curtain.MouseFilter = Control.MouseFilterEnum.Ignore;
        }
    }
    /// <summary>
    /// 换屏快门的第一拍：把整屏盖住。切换可见性发生在遮罩之下，玩家看不到硬切的那一帧。
    /// 同步完成，不引入异步时序——验证脚本读可见性时不会读到中间态。
    /// </summary>
    private void CoverScreen()
    {
        var curtain = EnsureScreenCurtain();
        _screenCurtainTween?.Kill(); _screenCurtainTween = null;
        curtain.Visible = true;
        curtain.MouseFilter = Control.MouseFilterEnum.Stop;
        curtain.Color = new Color(curtain.Color, .94f);
    }
    /// <summary>第二拍：揭开遮罩，新屏同时淡入。总时长约 180ms，与菜单其它动效同档。</summary>
    private void RevealScreen()
    {
        var curtain = _screenCurtain;
        if (curtain is null || !GodotObject.IsInstanceValid(curtain)) return;
        curtain.MouseFilter = Control.MouseFilterEnum.Ignore;
        _screenCurtainTween = CreateTween();
        _screenCurtainTween.TweenProperty(curtain, "color:a", 0f, .18)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _screenCurtainTween.TweenCallback(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(curtain)) curtain.Visible = false;
        }));
    }
    private ColorRect EnsureScreenCurtain()
    {
        if (_screenCurtain is { } existing && GodotObject.IsInstanceValid(existing)) return existing;
        var curtain = new ColorRect
        {
            Color = new Color(UiStyles.MenuPanel, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false
        };
        AddChild(curtain);
        curtain.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MoveChild(curtain, GetChildCount() - 1);
        _screenCurtain = curtain;
        return curtain;
    }
    private void EnterMenuScreen(string id)
    {
        // 战场占满整屏、内部有自己的演出；它的过场由快门遮罩承担。
        // 这里不再动战场的透明度——否则切屏后立刻截图的验证会截到淡入中的半透明画面。
        if (id == "battle") return;
        var screen = _screens[id];
        if (!_clock.ReducedMotion)
        {
            _transitionScreen = screen; screen.Modulate = new(1, 1, 1, .35f);
            screen.OffsetLeft = screen.OffsetRight = 0; screen.OffsetTop = screen.OffsetBottom = 12;
            _menuTransition = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            _menuTransition.TweenProperty(screen, "modulate:a", 1f, .20);
            _menuTransition.TweenMethod(Callable.From<float>(offset => { screen.OffsetTop = screen.OffsetBottom = offset; }), 12f, 0f, .22);
        }
        if (id == "home") _home.Hero.PlayEntrance(_clock.ReducedMotion);
    }
    private void RefreshMenuMotion()
    {
        if (_clock.ReducedMotion) ResetMenuTransition();
        _home.Hero.SetReducedMotion(_clock.ReducedMotion);
    }
    private async Task StartupStageAsync(float progress, string description)
    {
        _splash!.SetStage(progress, description); _startupStages++;
        // Yield before each real loading stage, so the status is painted before CPU work begins.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (DisplayServer.GetName() != "headless")
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (OS.GetCmdlineUserArgs().Contains("--capture-menu")) SaveMenuImage($"splash-{_startupStages}");
        }
    }
    private async Task FinishStartupAsync()
    {
        _splash!.MouseFilter = MouseFilterEnum.Ignore;
        var verifyFadeInput = OS.GetCmdlineUserArgs().Any(a => a is "--verify-menu" or "--capture-menu" or "--verify-ui");
        if (!_clock.ReducedMotion)
        {
            var fade = CreateTween(); fade.TweenProperty(_splash, "modulate:a", 0f, .18);
            if (verifyFadeInput) VerifySplashFadeInput();
            await ToSignal(fade, Tween.SignalName.Finished);
        }
        else if (verifyFadeInput) VerifySplashFadeInput();
        _splash.QueueFree(); _splash = null;
    }
    /// <summary>Captures the setup screen to disk, then quits. Used to review the real layout, not a mock-up.</summary>
    private async void CaptureSetup()
    {
        try
        {
            Show("setup");
            // 等快门揭开再拍，否则截到的是遮罩本身。
            await ToSignal(GetTree().CreateTimer(.45), SceneTreeTimer.SignalName.Timeout);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await MenuCaptureAsync("setup-layout");
            SaveMenuImage("setup");
            await DeckClickAsync(_setup.DetailsButton);
            await MenuFrameAsync();
            await MenuCaptureAsync("setup-details-layout");
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            SaveMenuImage("setup-details");
            if (!_setup.DetailsButton.ButtonPressed) throw new Exception("Setup details did not expand on click.");
            await DeckClickAsync(_setup.DetailsButton);
            if (_setup.DetailsButton.ButtonPressed) throw new Exception("Setup details did not collapse on click.");
            await DeckClickAsync(_setup.BackButton);
            if (_active != "home" || !_shellBar.Visible || !_shellFooter.Visible)
                throw new Exception("Setup back did not restore the command center navigation.");
            Show("setup"); await MenuSettledAsync(); await MenuFrameAsync();
            await DeckClickAsync(_setup.ChooseDeckButton);
            if (_active != "decklib") throw new Exception("Setup change deck did not open the deck library.");
            GD.Print("SETUP_CAPTURE_OK layout-bounds details-click-expand-collapse back-navigation change-deck");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
    private void SaveMenuImage(string name)
    {
        var directory = OS.GetEnvironment("KARDS_CAPTURE_DIR");
        if (directory.Length == 0) directory = ProjectSettings.GlobalizePath("res://artifacts/menu");
        Directory.CreateDirectory(directory);
        var image = GetViewport().GetTexture().GetImage(); image.SavePng($"{directory}/{name}-{image.GetWidth()}.png");
    }
}
