using Godot;

namespace Kards.Ui;

public partial class Main
{
    private MenuSplash? _splash;
    private Tween? _menuTransition;
    private Control? _transitionScreen;
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
    }
    private void EnterMenuScreen(string id)
    {
        if (id == "battle") return;
        var screen = _screens[id];
        if (!_clock.ReducedMotion)
        {
            _transitionScreen = screen; screen.Modulate = new(1, 1, 1, .3f);
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
    private void SaveMenuImage(string name)
    {
        var directory = OS.GetEnvironment("KARDS_CAPTURE_DIR");
        if (directory.Length == 0) directory = ProjectSettings.GlobalizePath("res://artifacts/menu");
        Directory.CreateDirectory(directory);
        var image = GetViewport().GetTexture().GetImage(); image.SavePng($"{directory}/{name}-{image.GetWidth()}.png");
    }
}
