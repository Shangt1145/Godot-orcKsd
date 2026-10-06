using Godot;
using Kards.Ui.Core;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class Main : Control
{
    private readonly CardCatalog _catalog = new();
    private readonly TextureCache _textures = new();
    private readonly Dictionary<string, Control> _screens = new();
    private readonly Dictionary<string, Button> _nav = new();
    private AnimClock _clock = null!;
    private AnimationEngine _fx = null!;
    private AnimationGallery _gallery = null!;
    private BattleScreen _battle = null!;
    private TabContainer _helpTabs = null!;
    private Label _pool = null!, _speedLabel = null!;
    private PanelContainer _shellBar = null!, _shellFooter = null!;
    private MarginContainer _contentMargin = null!;
    private string _active = "battle";
    private UiMatchView? _match;
    private OrcMatchRunner? _runner;
    public override void _Ready()
    {
        Theme = UiStyles.CreateTheme();
        _clock = new AnimClock();
        AddChild(_clock);
        var sfx = new SfxPlayer { Clock = _clock };
        AddChild(sfx);
        var overlay = new Control { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 100 };
        AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _fx = new AnimationEngine { Clock = _clock, Sfx = sfx, Overlay = overlay, Textures = _textures };
        AddChild(_fx);
        try
        {
            _catalog.Load(ProjectSettings.GlobalizePath("res://proto/data/nations"), CardCatalog.ResolveArtResource);
        }
        catch (Exception e) { GD.PushError(e.ToString()); AddChild(UiStyles.Label("卡池加载失败，请先运行 tools/prepare.ps1", 24)); return; }
        var root = new VBoxContainer();
        AddChild(root);
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        var bar = new PanelContainer();
        _shellBar = bar;
        bar.AddThemeStyleboxOverride("panel", UiStyles.Box(new("191c22"), UiStyles.Line, 0, 16));
        root.AddChild(bar);
        var nav = new HBoxContainer();
        bar.AddChild(nav);
        var brand = UiStyles.Label("OrC-KSD", 22, UiStyles.Gold);
        nav.AddChild(brand);
        foreach (var (id, text) in new[] { ("battle", "战斗"), ("deck", "卡组"), ("collection", "图鉴"), ("decklib", "牌库"), ("editor", "编辑器"), ("help", "帮助与演示"), ("settings", "设置") })
        {
            var key = id;
            var b = UiStyles.Button(text, () => Show(key));
            b.ToggleMode = true;
            _nav[key] = b;
            nav.AddChild(b);
        }
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        nav.AddChild(spacer);
        _pool = UiStyles.Label($"{_catalog.Cards.Count} 张卡", 13, UiStyles.Dim);
        nav.AddChild(_pool);
        var margin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _contentMargin = margin;
        foreach (var edge in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride("margin_" + edge, 24);
        root.AddChild(margin);
        var content = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        margin.AddChild(content);
        var collection = new CollectionScreen();
        collection.Initialize(_catalog, _textures);
        AddScreen(content, "collection", collection);
        _battle = new BattleScreen();
        _battle.Initialize(_catalog, _textures, _clock, sfx);
        _battle.CommandRequested += command =>
        {
            if (_runner?.IsRunning == true) _ = _runner.SubmitAsync(command);
            CommandRequested?.Invoke(command);
        };
        _battle.RealMatchRequested += () => _ = StartRealMatchAsync();
        _battle.NavigationRequested += Show;
        AddScreen(content, "battle", _battle);
        AddScreen(content, "deck", Placeholder("卡组编辑", "卡组保存、数量限制与可用性将在卡组契约确定后接入。", "浏览卡牌", () => Show("collection")));
        AddScreen(content, "decklib", Placeholder("牌库", "牌库导航已就绪，当前可在图鉴浏览完整卡池。", "打开图鉴", () => Show("collection")));
        AddScreen(content, "editor", Placeholder("效果编辑器", "效果编辑器将在引擎能力查询接口就绪后接入。", "查看帮助", () => Show("help")));
        _helpTabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _gallery = new AnimationGallery { Name = "动画演示" };
        _helpTabs.AddChild(_gallery);
        _gallery.Initialize(_catalog, _textures, _fx);
        var bridge = new BridgeDemo { Name = "交互与事件" };
        _helpTabs.AddChild(bridge);
        bridge.Initialize(_fx, _catalog, _textures);
        var help = Placeholder("使用说明", "图鉴支持按国别、卡类、兵种、稀有度、词条筛选，也可以搜索卡名和卡面文本。\n\n点击或长按卡牌查看大图。动画演示页可选择兵种与防御值，并随时打断或复位。\n\n设置中的减弱动效保留声音、卡面揭示与动作时序。", "浏览图鉴", () => Show("collection"));
        help.Name = "使用说明";
        _helpTabs.AddChild(help);
        _helpTabs.TabChanged += _ => _fx.InterruptAll();
        AddScreen(content, "help", _helpTabs);
        AddScreen(content, "settings", Settings());
        var footer = new PanelContainer();
        _shellFooter = footer;
        footer.AddThemeStyleboxOverride("panel", UiStyles.Box(new("191c22"), UiStyles.Line, 0, 8));
        root.AddChild(footer);
        var footRow = new HBoxContainer();
        footer.AddChild(footRow);
        footRow.AddChild(UiStyles.Label("对战重做 · 第二阶段" + VersionSuffix(), 12, UiStyles.Dim));
        var gap = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        footRow.AddChild(gap);
        _speedLabel = UiStyles.Label("", 12, UiStyles.Dim);
        footRow.AddChild(_speedLabel);
        _clock.Changed += UpdateStatus;
        UpdateStatus();
        Show("battle");
        var args = OS.GetCmdlineUserArgs();
        if (args.Contains("--verify-ui"))
            CallDeferred(MethodName.VerifyUi);
        else if (args.Contains("--capture-ui"))
            CallDeferred(MethodName.CaptureUi);
    }
    /// <summary>Runs the screen from a real engine match instead of the local demo fixture.</summary>
    public async Task StartRealMatchAsync()
    {
        if (_runner is not null) return;
        var runner = new OrcMatchRunner();
        runner.ProjectionReady += (view, actions) => _battle.ApplyProjection(view, actions);
        runner.PresentationReady += (resolution, actions) => _ = _battle.PresentSequenceAsync(resolution, actions);
        var matchId = runner.MatchId;
        runner.CombatReady += (impacts, after, actions) => _ = _battle.PresentBoardImpactsAsync(matchId, impacts, after, actions);
        runner.ErrorRaised += message => GD.PushError("[engine] " + message);
        runner.HintRequested += message => _battle.ShowHint(message);
        _runner = runner;
        await runner.StartAsync();
    }

    public override void _Process(double delta) => _runner?.Pump();

    private async Task VerifyBridgeAsync()
    {
        Show("battle");
        await StartRealMatchAsync();
        var view = _runner?.CurrentView ?? throw new Exception("Real match produced no projection.");
        if (view.Phase != "play") throw new Exception($"Real match did not reach play: {view.Phase}.");
        if (view.SelfHand.Count != 4) throw new Exception($"Opening hand mismatch: {view.SelfHand.Count}.");
        if (view.SelfHq?.Health != 20 || view.EnemyHq?.Health != 20) throw new Exception("HQ projection failed.");
        if (view.EnemyHandCount != 5) throw new Exception("Opponent hand count missing.");
        // The engine exposes the opponent's cards; the bridge must never carry them across.
        if (view.SelfHand.Any(c => c.OwnerSide != "self") || view.SelfLine.Any(c => c.Zone == "hand"))
            throw new Exception("Projection leaked opponent identity.");
        _runner!.Pump();
        var settled = _runner!.CurrentView!;
        if (settled.MatchId != view.MatchId || settled.Phase != "play" || settled.SelfHand.Count != view.SelfHand.Count)
            throw new Exception("Pump mutated the authoritative projection.");

        // Playable loop: submit a real play and a real end-turn; the engine decides both outcomes.
        var actions = _runner!.CurrentActions;
        if (!actions.CanEndTurn || actions.PlayableUids.Count == 0)
            throw new Exception("Engine availability did not reach the UI.");
        var uid = actions.PlayableUids[0];
        var handBefore = settled.SelfHand.Count;
        var deployed = false;
        _runner!.PresentationReady += (resolution, _) => deployed |= resolution.Steps.Any(s => s is UiDeploymentPresentation);
        var phases = new List<string>();
        _battle.PresentationPhase += phase => phases.Add(phase);
        await _runner!.SubmitAsync(new PlayCard(uid));
        await ToSignal(GetTree().CreateTimer(1.8), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[bridge] presentation steps fired={deployed}, phases=[{string.Join(' ', phases)}]");
        if (!phases.Contains("deployment-start") || !phases.Any(p => p.StartsWith("deployment-slam")))
            throw new Exception("Deployment choreography never played.");
        var played = _runner!.CurrentView!;
        if (played.SelfHand.Count != handBefore - 1) throw new Exception("Playing a card did not consume it.");
        if (!played.SelfLine.Any(c => c.Uid == uid)) throw new Exception("Played unit never reached the board.");
        if (!deployed) throw new Exception("Deployment produced no presentation.");

        await _runner!.SubmitAsync(new EndTurn());
        await ToSignal(GetTree().CreateTimer(1.8), SceneTreeTimer.SignalName.Timeout);
        if (_runner!.CurrentView!.ActivePlayerSide != "enemy") throw new Exception("End turn did not pass the turn.");

        // Combat choreography: advance and strike the enemy HQ; the engine decides the outcome.
        var combatSeen = false;
        _runner!.CombatReady += (impacts, _, _) => combatSeen |= impacts.Count > 0;
        await _runner!.SubmitAsync(new EndTurn());
        await ToSignal(GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);
        var attacker = _runner!.CurrentView!.SelfLine.FirstOrDefault(c => !c.IsHq && c.Zone == "support")
            ?? throw new Exception("No unit on the support line to advance.");
        await _runner!.SubmitAsync(new MoveUnit(attacker.Uid, "frontline"));
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        if (_runner!.CurrentView!.SelfLine.First(c => c.Uid == attacker.Uid).Zone != "frontline")
            throw new Exception("Real move did not reach the front line.");
        // A unit that moved cannot attack the same turn (engine rule); wait for its next turn.
        await _runner!.SubmitAsync(new EndTurn());
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        await _runner!.SubmitAsync(new EndTurn());
        await ToSignal(GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);
        var hqUid = _runner!.CurrentView!.EnemyHq!.Uid;
        await _runner!.SubmitAsync(new AttackUnit(attacker.Uid, hqUid));
        await ToSignal(GetTree().CreateTimer(2.4), SceneTreeTimer.SignalName.Timeout);
        if (!combatSeen) throw new Exception("Attack produced no impact presentation.");
        var hqHealth = _runner!.CurrentView!.EnemyHq!.Health;
        if (hqHealth is null || hqHealth >= 20) throw new Exception("Attack did not damage the enemy HQ.");
        GD.Print("BRIDGE_VERIFY_OK real-match play opening-hand=4 opponent-hand-count-only hq=20 pump-stable deploy endturn combat hq-damaged");
    }

    /// <summary>Version comes from project.godot (single source of truth: root VERSION file).</summary>
    private static string VersionSuffix()
    {
        var value = ProjectSettings.GetSetting("application/config/version", "").AsString();
        return value.Length == 0 ? "" : " · " + value;
    }

    private void AddScreen(Control host, string id, Control screen)
    {
        host.AddChild(screen);
        screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        screen.Visible = false;
        _screens[id] = screen;
    }
    private static VBoxContainer Placeholder(string title, string description, string action, Action handler)
    {
        var v = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        v.AddChild(UiStyles.Label(title, 34, UiStyles.Gold));
        var p = UiStyles.Label(description, 18, UiStyles.Dim);
        p.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(p);
        var row = new HBoxContainer();
        row.AddChild(UiStyles.Button(action, handler));
        v.AddChild(row);
        return v;
    }
    private Control Settings()
    {
        var v = new VBoxContainer();
        v.AddChild(UiStyles.Label("设置", 30));
        v.AddChild(UiStyles.Label("演出与音效偏好会在下次启动时保留。", 14, UiStyles.Dim));
        v.AddChild(UiStyles.Label("动画速度", 18, UiStyles.Gold));
        var speed = new OptionButton();
        speed.AddItem("正常 · 1.00");
        speed.AddItem("快 · 0.60");
        speed.AddItem("极快 · 0.32");
        speed.Select((int)_clock.Current);
        speed.ItemSelected += i => _clock.SetSpeed((AnimClock.Speed)i);
        v.AddChild(speed);
        var motion = new CheckButton { Text = "减弱动效", ButtonPressed = _clock.ReducedMotion };
        motion.Toggled += on => { _clock.SetReducedMotion(on); _fx.InterruptAll(); };
        v.AddChild(motion);
        v.AddChild(UiStyles.Label("减少位移与碎屑，保留音效和可读的卡面揭示。", 14, UiStyles.Dim));
        var mute = new CheckButton { Text = "静音", ButtonPressed = _clock.Muted };
        mute.Toggled += on => _clock.SetMuted(on);
        v.AddChild(mute);
        v.AddChild(UiStyles.Label("音效音量", 18, UiStyles.Gold));
        var volume = new HSlider { MinValue = 0, MaxValue = 1, Step = .01, Value = _clock.Volume, CustomMinimumSize = new(350, 32) };
        volume.ValueChanged += x => _clock.SetVolume((float)x);
        v.AddChild(volume);
        return v;
    }
    private void UpdateStatus()
    {
        _speedLabel.Text = $"演出 {_clock.Scale:0.00}×" + (_clock.ReducedMotion ? " · 减弱动效" : "");
    }
    private void Show(string id)
    {
        if (!_screens.ContainsKey(id))
            return;
        if (id != _active)
            _fx.InterruptAll();
        _active = id;
        foreach (var (key, screen) in _screens)
            screen.Visible = key == id;
        foreach (var (key, b) in _nav)
            b.SetPressedNoSignal(key == id);
        SetBattlePresentation(id == "battle");
    }
    public event Action<UiCommand>? CommandRequested;
    public void ApplyMatch(UiMatchView? match, UiBattleActions? actions = null)
    {
        _match = match;
        if (match is null) _battle.ResetDemo();
        else _battle.ApplyProjection(match, actions ?? new());
        Show(_active);
    }
    private void SetBattlePresentation(bool activeMatch)
    {
        _pool.Visible = !activeMatch;
        _shellBar.Visible = _shellFooter.Visible = !activeMatch;
        foreach (var edge in new[] { "left", "right", "top", "bottom" })
            _contentMargin.AddThemeConstantOverride("margin_" + edge, activeMatch ? 0 : 24);
    }
    public Task PresentCombatAsync(UiCombatResolution resolution, UiBattleActions afterActions) => _battle.PresentCombatAsync(resolution, afterActions);
    public Task PresentSequenceAsync(UiPresentationResolution resolution, UiBattleActions afterActions) => _battle.PresentSequenceAsync(resolution, afterActions);
    private void OpenGallery()
    {
        Show("help");
        _helpTabs.CurrentTab = 0;
    }
    private async void VerifyUi()
    {
        try
        {
            foreach (var id in _screens.Keys)
            {
                Show(id);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            ApplyMatch(new UiMatchView { MatchId = "presentation-probe", Phase = "play" });
            Show("battle");
            if (_pool.Visible || _shellBar.Visible || _shellFooter.Visible)
                throw new Exception("Battle presentation failed.");
            ApplyMatch(new UiMatchView { MatchId = "presentation-probe", Phase = "over", ResultTitle = "展示结果" });
            if (_shellBar.Visible || _shellFooter.Visible) throw new Exception("Battle result restored application chrome.");
            Show("collection");
            if (!_pool.Visible)
                throw new Exception("Battle presentation did not restore.");
            ApplyMatch(null);
            if (_catalog.Cards.Count != 315)
                throw new Exception("Wrong catalogue size.");
            foreach (var c in _catalog.Cards.Where(c => c.ArtPath.Length > 0))
                if (!ResourceLoader.Exists(c.ArtPath))
                    throw new Exception($"Missing texture: {c.CardId}");
            var artRoot=ProjectSettings.GlobalizePath("res://proto/art");
            var checkedTextures=0;
            foreach(var file in System.IO.Directory.EnumerateFiles(artRoot,"*.png",System.IO.SearchOption.AllDirectories))
            {
                var resource="res://proto/art/"+System.IO.Path.GetRelativePath(artRoot,file).Replace('\\','/');
                if(_textures.Get(resource) is null)throw new Exception($"Texture cannot load: {resource}");
                if(_textures.Count>_textures.Capacity)throw new Exception("Texture cache exceeded its capacity.");
                checkedTextures++;
                if(checkedTextures%64==0)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            }
            GD.Print($"ASSET_VERIFY_OK textures={checkedTextures} cache={_textures.Count}/{_textures.Capacity}");
            var original = _clock.Current;
            foreach (var speed in Enum.GetValues<AnimClock.Speed>())
            {
                _clock.SetSpeed(speed);
                if (_clock.Ms(20) != 30)
                    throw new Exception("Timing floor failed.");
            }
            _clock.SetSpeed(original);
            Show("battle");
            await _battle.VerifyAsync();
            await _battle.VerifyCombatAsync();
            await _battle.VerifyPresentationAsync();
            await _battle.VerifyFeedbackAsync();
            await VerifyBridgeAsync();
            OpenGallery();
            // Restoring the shell changes the container's transform; measure actors only after layout settles.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await _gallery.VerifyLifecycleAsync();
            if (OS.GetCmdlineUserArgs().Contains("--verify-effects"))
                await _gallery.VerifyAllEffectsAsync();
            GD.Print($"UI_VERIFY_OK screens={_screens.Count} cards={_catalog.Cards.Count} texture_cache={_textures.Count}/{_textures.Capacity}");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
    }
    private async void CaptureUi()
    {
        Show("collection");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var dir = ProjectSettings.GlobalizePath("res://artifacts");
        System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(dir+"/.gdignore","");
        GetViewport().GetTexture().GetImage().SavePng(dir + "/collection.png");
        Show("battle");
        _battle.ResetDemo();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _battle.CancelSelection();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(dir + "/battle-stage2.png");
        _battle.CapturePreview();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(dir + "/battle-stage2-aim.png");
        _battle.CancelSelection();
        _battle.CaptureHandInspect();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(dir + "/battle-stage2-hand.png");
        _battle.CancelSelection();
        var originalWindowSize = GetWindow().Size;
        GetWindow().Size = new(1100, 720);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _battle.ResetDemo();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(dir + "/battle-stage2-compact.png");
        GetWindow().Size = originalWindowSize;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await _battle.CaptureCombatFramesAsync(dir);
        await _battle.CapturePresentationFramesAsync(dir);
        await _battle.CaptureFeedbackFramesAsync(dir);
        OpenGallery();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(dir + "/animation-gallery.png");
        GD.Print("UI_CAPTURE_OK");
        GetTree().Quit();
    }
}
