using Godot;
using Kards.Ui.Core;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class Main : Control
{
    private readonly CardCatalog _catalog = new();
    /// <summary>Card id -> art path, built once the catalog loads. Empty ids fall back to the text card.</summary>
    private Dictionary<string, string> _artById = new(StringComparer.Ordinal);
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
    private PresentationPlayer? _presentations;

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
        // The engine knows nothing about art, so the catalog is the single place a card id becomes
        // a texture path. A real match reads its art through this table, same as the collection does.
        _artById = _catalog.Cards.Where(c => c.ArtPath.Length > 0)
            .GroupBy(c => c.CardId).ToDictionary(g => g.Key, g => g.First().ArtPath, StringComparer.Ordinal);
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
            if (_runner is { IsRunning: true } runner)
            {
                _ = runner.SubmitAsync(command);
                if (runner.CurrentView is { } view) _battle.UpdateInputProjection(view, runner.CurrentActions);
            }
            CommandRequested?.Invoke(command);
        };
        _battle.RealMatchRequested += () => _ = StartRealMatchAsync();
        _battle.HotseatRequested += () => _ = StartRealMatchAsync(interactiveMulligan: true, hotseat: true);
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
        else if (args.Contains("--verify-card-pool"))
            CallDeferred(MethodName.VerifyCardPool);
        else if (args.Contains("--verify-deployment"))
            CallDeferred(MethodName.VerifyDeployment);
        else if (args.Contains("--capture-slam"))
            CallDeferred(MethodName.CaptureSlam);
        else if (args.Contains("--capture-p11"))
            CallDeferred(MethodName.CaptureP11);
        else if (args.Contains("--capture-ui"))
            CallDeferred(MethodName.CaptureUi);
        else if (args.Contains("--capture-real"))
            CallDeferred(MethodName.CaptureReal);
        else if (args.Contains("--capture-placement"))
            CallDeferred(MethodName.CapturePlacement);
        else if (args.Contains("--verify-orders"))
            CallDeferred(MethodName.VerifyOrders);
        else
            _ = StartRealMatchAsync();
    }
    /// <summary>Runs the screen from a real engine match. Invoked again after a game over = restart.
    /// Verify and headless paths pass interactiveMulligan: false to keep the automatic keep-all answer.</summary>
    public async Task StartRealMatchAsync(bool interactiveMulligan = true, bool hotseat = false,
        int? deckSeed = null, bool demoOpeningHand = false, bool probe = false, IReadOnlyList<string>? verificationHand = null)
    {
        StopRealMatch();
        var runner = new OrcMatchRunner
        {
            InteractiveMulligan = interactiveMulligan,
            ArtLookup = id => _artById.GetValueOrDefault(id, ""),
            // Production uses the admitted source catalog; only explicit verification uses a probe.
            CatalogCards = probe ? null : _catalog.Cards,
            CatalogSourceDirectory = ProjectSettings.GlobalizePath("res://proto/data/nations"),
            Hotseat = hotseat,
            DeckSeed = deckSeed,
            // Pinning the hand is a demonstration aid, not the default: the verify and capture paths
            // need the engine's own deal, so they leave this off.
            OpeningHand = verificationHand ?? (demoOpeningHand ? new[] { "USG/units/_4", "av76/units/13", "deran/units/_6q", "av76/units/18" } : null),
        };
        BindRunner(runner);
        try { await runner.StartAsync(); }
        catch (OperationCanceledException) { return; }
        catch (Exception error)
        {
            if (_runner != runner) return;
            StopRealMatch(); _battle.ResetDemo();
            _battle.ShowHint($"真实对局未启动：{error.Message}"); GD.PushError(error.ToString());
            return;
        }
        if (_runner != runner || !IsInsideTree()) return;
        if (runner.CardPoolReport is { } report)
            _battle.ShowHint($"可用 {report.Verified} 种卡 · 随机发牌 · 30 张牌组");
        _battle.ShowHotseatControls(hotseat, runner.ActiveSeat,
            () => runner.SwitchSeat(),
            () => runner.ActiveSeatOnTurn);
    }

    private void BindRunner(OrcMatchRunner runner)
    {
        _runner = runner;
        runner.ProjectionReady += (view, actions) => ApplyRunnerProjection(runner, view, actions);
        runner.MulliganRequested += () => { if (_runner == runner) _battle.ShowMulliganPanel(runner.CurrentView); };
        runner.TargetRequested += (request, responder) => Callable.From(() =>
        { if (_runner == runner && runner.TargetPending) _battle.ShowTargetChoice(request, responder); else responder.Cancel(request.RequestId); }).CallDeferred();
        runner.TargetClosed += () => Callable.From(() => { if (_runner == runner) _battle.CloseTargetChoice(); }).CallDeferred();
        runner.PresentationReady += (resolution, actions) =>
        {
            if (_runner != runner) return;
            if (runner.CurrentView is { } view) _battle.UpdateInputProjection(view, runner.CurrentActions);
            _presentations?.Enqueue(resolution, actions);
        };
        runner.CommandRefused += command => { if (_runner == runner) _battle.RefuseCommand(command); };
        runner.ErrorRaised += message => { if (_runner == runner) GD.PushError("[engine] " + message); };
        runner.HintRequested += message => { if (_runner == runner) _battle.ShowHint(message); };
    }

    private void ApplyRunnerProjection(OrcMatchRunner runner, UiMatchView view, UiBattleActions actions)
    {
        if (_runner != runner) return;
        _presentations?.Dispose();
        _battle.ApplyProjection(view, actions);
        _battle.UpdateInputProjection(view, actions);
        _presentations = new PresentationPlayer(runner.MatchId, (resolution, available, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return _runner == runner ? _battle.PresentSequenceAsync(resolution, available) : Task.CompletedTask;
        });
        _presentations.Failed += e => GD.PushError($"[presentation] {e}");
    }

    public override void _Process(double delta)
    {
        var runner = _runner;
        runner?.Pump();
        if (_runner == runner && runner?.CurrentView is { } view)
            _battle.UpdateInputProjection(view, runner.CurrentActions);
    }

    private void StopRealMatch()
    {
        var runner = _runner;
        _runner = null;
        _presentations?.Dispose();
        _presentations = null;
        runner?.Stop();
        _battle?.CloseTargetChoice();
        if (GodotObject.IsInstanceValid(_battle)) _battle.CancelSelection();
    }

    public override void _ExitTree() => StopRealMatch();

    private async Task VerifyBridgeAsync()
    {
        Show("battle");
        // Keep the generic engine lifecycle probe independent of the reviewed catalog's costs.
        // VerifyCardPoolStageAsync separately exercises the production catalog and mouse path.
        await StartRealMatchAsync(interactiveMulligan: false, deckSeed: 2, probe: true);
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
        await AwaitIdleAsync(30);
        if (!_battle.MatchesProjection(settled))
            throw new Exception("Opening presentation restored a stale mulligan board.");

        // Playable loop: submit a real play and a real end-turn; the engine decides both outcomes.
        var actions = _runner!.CurrentActions;
        if (!actions.CanEndTurn || actions.PlayableUids.Count == 0)
            throw new Exception("Engine availability did not reach the UI.");
        // The engine's play entry point takes units; orders have no play path yet, and a card the
        // player cannot afford is correctly refused. Both would look like a broken choreography.
        var uid = settled.SelfHand.FirstOrDefault(c =>
            actions.PlayableUids.Contains(c.Uid)
            && c.Definition.CardType == "unit"
            && c.Definition.Cost <= settled.SelfKredits)?.Uid
            ?? actions.PlayableUids[0];
        var handBefore = settled.SelfHand.Count;
        var deployed = false;
        _runner!.PresentationReady += (resolution, _) => deployed |= resolution.Steps.Any(s => s is UiDeploymentPresentation);
        var phases = new List<string>();
        _battle.PresentationPhase += phase => phases.Add(phase);
        await _runner!.SubmitAsync(new PlayCard(uid));
        await ToSignal(GetTree().CreateTimer(1.8), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[bridge] play uid={uid} hand=[{string.Join(' ', settled.SelfHand.Select(c => $"{c.Definition.CardType}/{c.Definition.Cost}"))}] " +
            $"kredits={settled.SelfKredits} steps fired={deployed}, phases=[{string.Join(' ', phases)}]");
        if (!phases.Contains("deployment-start") || !phases.Any(p => p.StartsWith("deployment-slam")))
            throw new Exception("Deployment choreography never played.");
        var played = _runner!.CurrentView!;
        if (played.SelfHand.Count != handBefore - 1) throw new Exception("Playing a card did not consume it.");
        if (!played.SelfLine.Any(c => c.Uid == uid)) throw new Exception("Played unit never reached the board.");
        if (!deployed) throw new Exception("Deployment produced no presentation.");

        await _runner!.SubmitAsync(new EndTurn());
        await ToSignal(GetTree().CreateTimer(2.2), SceneTreeTimer.SignalName.Timeout);
        // The opponent driver plays the enemy turn through the engine and passes straight back.
        // Whether it deploys is the engine's business: with the shipped pool most hands hold
        // nothing affordable, and a driver that correctly does nothing is still a driver that ran.
        var afterEnemyTurn = _runner!.CurrentView!;
        if (afterEnemyTurn.ActivePlayerSide != "self") throw new Exception("Opponent turn did not come back.");
        if (afterEnemyTurn.Turn <= view.Turn) throw new Exception("The turn counter did not advance past the opponent.");

        // Deployment is adjacency-locked by the engine (only slots next to occupied ones are legal), so a
        // second card must take a *different* slot. The engine decides whether the second play is
        // legal at all — with the shipped pool it often is not — so this only checks the outcome
        // when the card actually landed.
        var second = _runner!.CurrentActions.PlayableUids
            .Where(c => _runner!.CurrentView!.SelfHand.Any(h => h.Uid == c))
            .FirstOrDefault();
        if (second is not null)
        {
            await _runner!.SubmitAsync(new PlayCard(second));
            await ToSignal(GetTree().CreateTimer(1.8), SceneTreeTimer.SignalName.Timeout);
            var board = _runner!.CurrentView!.SelfLine;
            var left = board.FirstOrDefault(c => c.Uid == second);
            var right = board.FirstOrDefault(c => c.Uid == uid);
            if (left is not null && right is not null && left.SlotIndex == right.SlotIndex)
                throw new Exception("Two deployments landed on the same slot.");
        }

        // Real movement: advance a unit to a front slot the board says is free, and require it to land
        // there. The slot is chosen from the projection, not assumed, because the front line is shared.
        await _runner!.SubmitAsync(new EndTurn());
        await ToSignal(GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);
        var mover = _runner!.CurrentActions.Moves.Select(m => m.Uid).FirstOrDefault();
        if (mover is not null)
        {
            var board = _runner!.CurrentView!;
            var occupied = board.SelfLine.Concat(board.EnemyLine)
                .Where(c => c.Zone == "frontline").Select(c => c.SlotIndex).ToHashSet();
            var free = Enumerable.Range(0, board.FrontLineSlotCount).Where(i => !occupied.Contains(i)).ToArray();
            if (free.Length > 0)
            {
                var wanted = free[0];
                await _runner!.SubmitAsync(new MoveUnit(mover, "frontline", wanted));
                await ToSignal(GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);
                var moved = _runner!.CurrentView!.SelfLine.FirstOrDefault(c => c.Uid == mover);
                if (moved is null || moved.Zone != "frontline" || moved.SlotIndex != wanted)
                    throw new Exception($"Real move asked for front slot {wanted} but landed on " +
                        $"{(moved is null ? "<gone>" : $"{moved.Zone}[{moved.SlotIndex}]")}.");
            }
        }

        // Combat choreography: strike the enemy HQ; the engine decides the outcome. The striker is
        // taken from the engine's own availability rather than remembered from earlier in the script:
        // the opponent attacks units too, so a unit named three turns ago may no longer be alive or
        // may have already acted.
        var combatSeen = false;
        _runner!.CombatReady += (impacts, _, _) => combatSeen |= impacts.Count > 0;
        await _runner!.SubmitAsync(new EndTurn());
        await ToSignal(GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);
        var hqUid = _runner!.CurrentView!.EnemyHq!.Uid;
        var strike = _runner!.CurrentActions.AttackPreviews.FirstOrDefault(p => p.DefenderUid == hqUid)
            ?? throw new Exception("No unit can attack the enemy headquarters.");
        await _runner!.SubmitAsync(new AttackUnit(strike.AttackerUid, hqUid));
        await ToSignal(GetTree().CreateTimer(2.4), SceneTreeTimer.SignalName.Timeout);
        if (!combatSeen) throw new Exception("Attack produced no impact presentation.");
        var hqHealth = _runner!.CurrentView!.EnemyHq!.Health;
        if (hqHealth is null || hqHealth >= 20) throw new Exception("Attack did not damage the enemy HQ.");
        GD.Print("BRIDGE_VERIFY_OK real-match play opening-hand=4 opponent-hand-count-only hq=20 pump-stable deploy endturn combat hq-damaged");
    }

    /// <summary>
    /// The mulligan panel end to end: the engine parks the request, the panel opens with the opening
    /// hand, one card is marked, the keep list is submitted, and the match reaches play with the kept
    /// cards intact and the hand size unchanged (replace one = draw one).
    /// </summary>
    private async Task VerifyMulliganPanelAsync()
    {
        Show("battle");
        await StartRealMatchAsync(); // interactive
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_battle.MulliganPanelVisible && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (!_battle.MulliganPanelVisible) throw new Exception("Mulligan panel never opened.");
        var opening = _runner?.CurrentView ?? throw new Exception("Mulligan match produced no projection.");
        if (opening.Phase != "mulligan" || opening.SelfHand.Count == 0) throw new Exception("Mulligan panel has no opening hand.");
        var replaced = opening.SelfHand[0].Uid;
        var kept = opening.SelfHand.Skip(1).Select(c => c.Uid).ToHashSet();
        _battle.MarkMulliganReplacement(replaced);
        _battle.ConfirmMulligan();
        if (_battle.MulliganPanelVisible) throw new Exception("Mulligan panel stayed open after the submit.");
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (_runner is not null && _runner.CurrentView?.Phase != "play" && DateTime.UtcNow < deadline) await Task.Delay(50);
        var after = _runner?.CurrentView ?? throw new Exception("Mulligan submit produced no projection.");
        if (after.Phase != "play") throw new Exception($"Mulligan submit did not reach play: {after.Phase}.");
        if (after.SelfHand.Count != opening.SelfHand.Count) throw new Exception("Mulligan replacement changed the hand size.");
        if (after.SelfDeckCount != opening.SelfDeckCount) throw new Exception("Mulligan replacement changed the deck size.");
        // With an all-identical deck the replacement draw may return the very same instance, so the
        // marked uid cannot be asserted absent; the kept cards and the two counts are the invariant.
        var uids = after.SelfHand.Select(c => c.Uid).ToHashSet();
        if (!uids.IsSupersetOf(kept)) throw new Exception("Mulligan lost a kept card.");
        StopRealMatch();
        _battle.ResetDemo();
        GD.Print("MULLIGAN_VERIFY_OK panel marked=1 replaced keep-3 reach-play hand-size-stable");
    }

    /// <summary>Captures the mulligan panel: untouched, then with one ✕ stamp, from a real match.</summary>
    private async Task CaptureMulliganFramesAsync(string dir)
    {
        Show("battle");
        await StartRealMatchAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_battle.MulliganPanelVisible && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (!_battle.MulliganPanelVisible)
        {
            GD.PushError("[capture] mulligan panel never opened");
            StopRealMatch();
            _battle.ResetDemo();
            return;
        }
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(dir + "/battle-stage5-mulligan.png");
        var first = _runner!.CurrentView!.SelfHand[0].Uid;
        _battle.MarkMulliganReplacement(first);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(dir + "/battle-stage5-mulligan-marked.png");
        _battle.ConfirmMulligan();
        StopRealMatch();
        _battle.ResetDemo();
        Show("battle");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

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
            if (typeof(Main).Assembly.GetReferencedAssemblies().Any(a => a.Name == "Orc"
                || a.Name?.StartsWith("Orc.", StringComparison.Ordinal) == true))
                throw new Exception("UI assembly directly references engine types.");
            GD.Print("UI_BOUNDARY_VERIFY_OK");
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
            await _battle.VerifyFrontPlacementAsync();
            await _battle.VerifyPresentationAsync();
            await _battle.VerifyFeedbackAsync();
            await _battle.VerifyDeploymentVisualsAsync();
            await _battle.VerifyDemoNonBlockingAsync();
            await VerifyMulliganPanelAsync();
            await VerifyBridgeAsync();
            await VerifyCardPoolStageAsync();
            await VerifyOrdersStageAsync();
            await VerifyNonBlockingInputAsync();
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
    /// <summary>Records the P11 assault trajectory and result band as per-frame PNGs.</summary>
    private async void CaptureP11()
    {
        try
        {
            Show("battle");
            _battle.ResetDemo();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var dir = ProjectSettings.GlobalizePath("res://artifacts/p11-film");
            System.IO.Directory.CreateDirectory(dir);
            await _battle.CaptureP11FilmAsync(dir);
        }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
    }
    /// <summary>Records the three-tier deployment slam as per-frame PNGs for video assembly.</summary>
    private async void CaptureSlam()
    {
        try
        {
            Show("battle");
            _battle.ResetDemo();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var dir = ProjectSettings.GlobalizePath("res://artifacts/slam-film-v2");
            System.IO.Directory.CreateDirectory(dir);
            await _battle.CaptureSlamFilmAsync(dir);
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
        await CaptureMulliganFramesAsync(dir);
        OpenGallery();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(dir + "/animation-gallery.png");
        GD.Print("UI_CAPTURE_OK");
        GetTree().Quit();
    }

    // ---------- real-machine audit (--capture-real) ----------
    //
    // Why this exists: --verify-ui answers the engine by calling OrcMatchRunner.SubmitAsync directly.
    // That skips BattleScreen.TryDrop, SlotAfterDrop and OrcMatchSession.AnswerParked — exactly the
    // layer the player touches. The suite stayed green while the board was unusable, so the audit
    // below drives the shipped match through the UI's own input pipeline and saves a frame per action.

    private readonly List<string> _audit = new();
    private readonly List<int> _deploySlots = new(), _moveSlots = new();

    private void Audit(string line)
    {
        _audit.Add(line);
        GD.Print("[audit] " + line);
        try
        {
            var dir = ProjectSettings.GlobalizePath("res://artifacts/real-audit");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllLines(dir + "/audit.txt", _audit);
        }
        catch { /* the log is a convenience; never let it abort the run */ }
    }

    private static string Short(string uid) => uid.Length <= 8 ? uid : uid[..8];

    /// <summary>Waits until no presentation owns the board, or the deadline passes.</summary>
    private async Task<bool> AwaitIdleAsync(double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (!_battle.IsBusy && _presentations?.IsBusy != true) return true;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return false;
    }

    private async Task<bool> AwaitBoardAsync(Func<bool> ready, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (ready()) return true;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return false;
    }

    private async Task AwaitSelfTurnAsync(double seconds) => await AwaitBoardAsync(
        () => _runner?.CurrentView is { Phase: "play", ActivePlayerSide: "self" }
            && !_battle.IsBusy && _presentations?.IsBusy != true, seconds);

    private async Task ShotAsync(string dir, string name)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng($"{dir}/{name}.png");
    }

    private async void CapturePlacement()
    {
        try
        {
            StopRealMatch(); Show("battle");
            var size = GetWindow().Size;
            var dir = ProjectSettings.GlobalizePath($"res://artifacts/stage6a-placement-{size.X}x{size.Y}");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(dir + "/.gdignore", "");
            await _battle.VerifyFrontPlacementAsync(name => ShotAsync(dir, name));
            GD.Print("PLACEMENT_CAPTURE_OK"); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    /// <summary>Runs the shipped real match and drives it with simulated mouse input, frame by frame.</summary>
    private async void CaptureReal()
    {
        var dir = ProjectSettings.GlobalizePath("res://artifacts/real-audit");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(dir + "/.gdignore", "");
            Show("battle");
            await StartRealMatchAsync(interactiveMulligan: false, deckSeed: 2, demoOpeningHand: true);
            if (!await AwaitBoardAsync(() => _runner?.CurrentView?.Phase == "play", 30))
                throw new Exception("Real match never reached play.");
            await AwaitIdleAsync(6);
            // Engine refusals only ever reached the screen as a transient hint; log them, because a drop
            // that is refused and a drop that was never recognised look identical in the frame.
            _runner!.HintRequested += message => Audit("hint=" + message);

            var opening = _runner!.CurrentView!;
            Audit("coords " + _battle.CoordinateReport());
            Audit($"opening turn={opening.Turn} active={opening.ActivePlayerSide} kredits={opening.SelfKredits}/{opening.SelfMaxKredits} " +
                  $"hand={opening.SelfHand.Count} supportSlots={opening.SupportLineSlotCount} frontSlots={opening.FrontLineSlotCount}");
            foreach (var c in opening.SelfHand.OrderBy(c => c.SlotIndex))
                Audit($"  hand {c.Definition.Name} uid={Short(c.Uid)} cost={c.Definition.Cost} def={c.Definition.BaseDefense} " +
                      $"playable={_runner.CurrentActions.PlayableUids.Contains(c.Uid)}");
            Audit($"  board self={opening.SelfLine.Count} enemy={opening.EnemyLine.Count} " +
                  $"playable={_runner.CurrentActions.PlayableUids.Count} moves={_runner.CurrentActions.Moves.Count} " +
                  $"attackPreviews={_runner.CurrentActions.AttackPreviews.Count} canEndTurn={_runner.CurrentActions.CanEndTurn}");
            await ShotAsync(dir, "real-01-opening");
            CheckPaint("opening");

            // Gestures, not abstract ranks: a player drops at the end of a line or next to a named card.
            // Testing what the hand actually does is the only way to catch a mapping that is merely
            // self-consistent.
            var plan = new (string Kind, string Anchor, string Label)[]
            {
                ("idle", "", "let-opponent-advance-1"), ("idle", "", "let-opponent-advance-2"),
                ("deploy", "left", "left-of-hq"), ("deploy", "after-hq", "right-of-hq"),
                // Strikes between deploying and advancing, for two reasons at once. The opponent has had
                // turns to come up, so there is something to strike; and a unit that has moved cannot
                // attack again, so striking before anything advances measures the chain rather than
                // the engine's action gate.
                ("attack", "unit", "unit"), ("attack", "hq", "hq"),
                ("deploy", "right", "row-end"),
                ("move", "middle", "row-middle"),
                ("move", "left", "left-of-it"), ("move", "right", "right-of-it"),
            };
            var index = 0;
            for (var turn = 0; turn < 16 && index < plan.Length; turn++)
            {
                if (_runner?.CurrentView is not { Phase: "play" } before) break;
                var (kind, anchor, label) = plan[index];
                var handled = kind switch
                {
                    "deploy" => await DeployAuditAsync(dir, anchor, label),
                    "move" => await MoveAuditAsync(dir, anchor, label),
                    "attack" => await AttackAuditAsync(dir, anchor == "hq"),
                    "pack" => await PackFrontAsync(dir, label),
                    "idle" => true,   // end the turn and let the opponent build up, nothing to measure
                    _ => false,
                };
                Audit($"-- turn {before.Turn} step={kind}[{label}] handled={handled} " +
                      $"kredits={_runner.CurrentView!.SelfKredits} enemyHq={_runner.CurrentView.EnemyHq?.Health} " +
                      $"selfUnits={_runner.CurrentView.SelfLine.Count(c => !c.IsHq)}");
                if (handled) index++;
                if (_runner.CurrentView.Phase != "play") break;
                await _battle.ClickAsync(_battle.EndTurnPoint());
                Audit($"endturn click channels={_battle.ChannelTrace}");
                await AwaitIdleAsync(12);
                await AwaitSelfTurnAsync(40);
            }

            // Slot identity alone cannot prove visual placement. Slots can also be reused after death.
            Audit($"ENGINE_SLOTS deploy=[{string.Join(',', _deploySlots)}] move=[{string.Join(',', _moveSlots)}]");
            Audit($"PAINT checked={_paintChecked} broken={_paintBroken}");
            Audit($"REALCAPTURE end steps={index}/{plan.Length} enemyHq={_runner?.CurrentView?.EnemyHq?.Health} " +
                  $"selfHq={_runner?.CurrentView?.SelfHq?.Health} phase={_runner?.CurrentView?.Phase}");
            GD.Print("REALCAPTURE_DONE");
        }
        catch (Exception e) { GD.PushError("[capture-real] " + e); }
        finally { GetTree().Quit(); }
    }

    /// <summary>The drop point for a named gesture, in board coordinates.</summary>
    private Vector2 DropFor(bool front, string anchor) => anchor switch
    {
        "left" => _battle.DropAtRowEdge(front, right: false),
        "middle" => _battle.DropAtRowMiddle(front),
        "right" => _battle.DropAtRowEdge(front, right: true),
        "after-hq" => _battle.DropRightOf(_battle.SelfHqUid()),
        "after-self" => _battle.DropRightOf(_battle.SelfFrontUid()),
        _ => _battle.DropAtRowEdge(front, right: true),
    };

    /// <summary>Drops the cheapest affordable unit card at the named place in the support row.</summary>
    private async Task<bool> DeployAuditAsync(string dir, string anchor, string label, bool record = true)
    {
        var view = _runner!.CurrentView!;
        // Units only: the action projection lists every hand card as attemptable, so picking the cheapest
        // "playable" one can pick an order card — which the bridge then refuses as an unknown unit, and
        // the audit would sit on the same step for ever.
        var card = view.SelfHand
            .Where(c => c.Definition.CardType == "unit" && _runner.CurrentActions.PlayableUids.Contains(c.Uid))
            .OrderBy(c => c.Definition.Cost ?? 99).FirstOrDefault();
        if (card is null)
        {
            Audit($"deploy[{label}] SKIPPED kredits={view.SelfKredits} hand=[" +
                  string.Join(' ', view.SelfHand.Select(c => $"{c.Definition.Name}({c.Definition.CardType},{c.Definition.Cost})")) + "]");
            return false;
        }
        var drop = DropFor(front: false, anchor);
        Audit($"deploy[{label}] anchor={anchor} drop=({drop.X:0},{drop.Y:0}) {_battle.AreaHit(drop)} " +
              $"card={card.Definition.Name} cost={card.Definition.Cost} row=[{_battle.RowLayout(front: false)}]");
        Audit($"deploy[{label}] trace {_battle.DropTrace(front: false, drop.X)}");
        await _battle.DragAsync(card.Uid, drop, async () =>
        {
            Audit($"deploy[{label}] overlay {_battle.SlotHintReport()}");
            await ShotAsync(dir, $"real-hold-deploy-{label}");
        });
        Audit($"deploy[{label}] drag={_battle.ChannelTrace} state=[{_battle.Interaction}]");
        await AwaitIdleAsync(8);
        var landed = await AwaitLandedAsync(card.Uid, 30);
        var after = _runner.CurrentView!;
        Audit($"deploy[{label}] landed={(landed is null ? "<still in hand>" : $"{landed.Zone}[{landed.SlotIndex}]")} " +
              $"want={Short(card.Uid)} line=[{string.Join(',', after.SelfLine.Select(c => Short(c.Uid)))}] " +
              $"row=[{_battle.RowLayout(front: false)}]");
        if (record && landed is not null) _deploySlots.Add(landed.SlotIndex);
        await ShotAsync(dir, $"real-deploy-{label}");
        CheckPaint($"deploy-{label}");
        return landed is not null;
    }

    /// <summary>Drags a movable unit to the named place in the shared front line.</summary>
    private async Task<bool> MoveAuditAsync(string dir, string anchor, string label, bool record = true)
    {
        var view = _runner!.CurrentView!;
        var move = _runner.CurrentActions.Moves.FirstOrDefault();
        if (move is null)
        {
            Audit($"move[{label}] NOT_TESTED (the engine offers no movement this turn)");
            return false;
        }
        var from = view.SelfLine.FirstOrDefault(c => c.Uid == move.Uid);
        var drop = DropFor(front: true, anchor);
        var expectedPicture = _battle.RenderedFrontUids().Where(uid => uid != move.Uid).ToList();
        var expectedRank = expectedPicture.Count(uid => _battle.CardPoint(uid).X <= drop.X + .01f);
        expectedPicture.Insert(expectedRank, move.Uid);
        Audit($"move[{label}] anchor={anchor} drop=({drop.X:0},{drop.Y:0}) {_battle.AreaHit(drop)} " +
              $"unit={from?.Definition.Name} from={from?.Zone}[{from?.SlotIndex}] row=[{_battle.RowLayout(front: true)}]");
        Audit($"move[{label}] trace {_battle.DropTrace(front: true, drop.X)}");
        await _battle.DragAsync(move.Uid, drop, async () =>
        {
            Audit($"move[{label}] overlay {_battle.SlotHintReport()}");
            await ShotAsync(dir, $"real-hold-{label}");
        });
        Audit($"move[{label}] drag={_battle.ChannelTrace} state=[{_battle.Interaction}]");
        await AwaitIdleAsync(10);
        // A move is only done when the unit reports the front line; finding it on the board at all proves
        // nothing, because a unit waiting on the support line is on the board too.
        var landed = await AwaitLandedAsync(move.Uid, 30, "frontline")
            ?? _runner.CurrentView!.SelfLine.FirstOrDefault(c => c.Uid == move.Uid);
        Audit($"move[{label}] landed={landed?.Zone}[{landed?.SlotIndex}] row=[{_battle.RowLayout(front: true)}]");
        if (record && landed?.Zone == "frontline") _moveSlots.Add(landed.SlotIndex);
        await ShotAsync(dir, $"real-move-{label}");
        CheckPaint($"move-{label}");
        var actualPicture = _battle.RenderedFrontUids();
        var pictureHonoured = actualPicture.SequenceEqual(expectedPicture);
        Audit($"VISUAL_MOVE[{label}] expected=[{string.Join(',', expectedPicture)}] actual=[{string.Join(',', actualPicture)}] honoured={pictureHonoured}");
        return landed?.Zone == "frontline" && pictureHonoured;
    }

    /// <summary>
    /// Fills the front line as far as one turn allows: deploy, advance, repeat, until four units stand on
    /// it or the hand runs out. Done inside a single turn on purpose — spread over turns the opponent gets
    /// turns in between and kills the units off before the line is ever crowded, which is why an earlier
    /// attempt to reach this state spread over turns never reached it.
    /// </summary>
    private async Task<bool> PackFrontAsync(string dir, string label)
    {
        var any = false;
        for (var i = 0; i < 4; i++)
        {
            if (FrontCount() >= 4) break;
            if (!await DeployAuditAsync(dir, "middle", $"{label}-deploy{i}", record: false)) break;
            await AwaitIdleAsync(6);
            if (!await MoveAuditAsync(dir, "right", $"{label}-move{i}", record: false)) break;
            await AwaitIdleAsync(8);
            any = true;
        }
        Audit($"pack[{label}] front=[{string.Join(' ', _runner!.CurrentView!.EnemyLine.Concat(_runner.CurrentView.SelfLine)
            .Where(c => c.Zone == "frontline").Select(c => $"{c.OwnerSide}:{c.Definition.Name}[{c.SlotIndex}]"))}] " +
            $"slots={_runner.CurrentView.FrontLineSlotCount}");
        return any;
    }

    private int FrontCount() => _runner?.CurrentView is not { } v ? 0 : v.EnemyLine.Count(c => c.Zone == "frontline")
        + v.SelfLine.Count(c => c.Zone == "frontline");

    /// <summary>
    /// Waits until a card shows up on the board, or the frame budget runs out.
    ///
    /// A fixed number of frames is not enough: a submission is asynchronous (the command goes out to the
    /// engine and the projection comes back on a later frame), so reading the board a set number of frames
    /// after the drop can still see the state from before it. That misreads a successful deploy as "still
    /// in hand", and the audit then re-deploys the same card until the line is full.
    /// </summary>
    private async Task<UiCardView?> AwaitLandedAsync(string uid, int frames, string? zone = null)
    {
        for (var i = 0; i < frames; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var found = _runner?.CurrentView?.SelfLine.FirstOrDefault(c => c.Uid == uid);
            if (found is not null && (zone is null || found.Zone == zone)) return found;
        }
        return null;
    }

    private int _paintChecked, _paintBroken;

    /// <summary>
    /// Checks that every drawn line is painted in ascending slot order.
    ///
    /// A packed row has no visible slot positions, so this ordering is the only thing tying a card's place
    /// on screen to the slot the engine holds it in. The audit reading the right slot number back proves
    /// nothing about it: a unit can land on slot 3 and still be painted to the left of a headquarters on
    /// slot 2 — which is what "it only ever deploys to the left" turned out to be.
    /// </summary>
    private void CheckPaint(string label)
    {
        var self = _battle.RowSlotOrder(front: false);
        var front = _battle.RowSlotOrder(front: true);
        _paintChecked++;
        // Two different questions. The support line is anchored — the headquarters holds its slot — so its
        // drawing order and its slot order have to agree. The front line is the player's arrangement, and
        // its numbers deliberately do not ascend. Check membership here; MoveAuditAsync checks the
        // gesture's expected picture order independently of the layout's private ordering list.
        var expectedFront = _runner?.CurrentView?.SelfLine.Concat(_runner.CurrentView.EnemyLine)
            .Where(c => c.Zone == "frontline").Select(c => c.Uid).ToHashSet() ?? new();
        if (Ascending(self) && expectedFront.SetEquals(_battle.RenderedFrontUids())) return;
        _paintBroken++;
        Audit($"PAINT[{label}] BROKEN self=[{string.Join(',', self)}] front=[{string.Join(',', front)}] " +
              $"layout self=[{_battle.RowLayout(front: false)}] front=[{_battle.RowLayout(front: true)}]");

        static bool Ascending(int[] slots)
        {
            for (var i = 1; i < slots.Length; i++) if (slots[i] <= slots[i - 1]) return false;
            return true;
        }
    }

    /// <summary>Drags an attacker onto the target the engine offers, and reports both health bars.</summary>
    private async Task<bool> AttackAuditAsync(string dir, bool preferHq)
    {
        var view = _runner!.CurrentView!;
        var previews = _runner.CurrentActions.AttackPreviews;
        if (previews.Count == 0)
        {
            Audit($"attack[{(preferHq ? "hq" : "unit")}] SKIPPED (nothing in range this turn — reported, not retried)");
            return false;
        }
        var hq = view.EnemyHq;
        var hqPreview = hq is null ? null : previews.FirstOrDefault(p => p.DefenderUid == hq.Uid);
        var unitTarget = view.EnemyLine.FirstOrDefault(u => previews.Any(p => p.DefenderUid == u.Uid));
        var wantsHq = preferHq && hqPreview is not null;
        if (!wantsHq && unitTarget is null) { Audit("attack[unit] NOT_TESTED (no unit target offered)"); return false; }
        if (wantsHq && hq is null) return false;

        var targetUid = wantsHq ? hq!.Uid : unitTarget!.Uid;
        // A unit that has already moved this turn cannot attack again (the engine clears CanAttack in
        // FinalizeMoveAsync), so aiming at one measures the gate rather than the chain. Prefer a unit
        // that has not acted; fall back only when nothing else is offered.
        var attackerUid = previews.FirstOrDefault(p => p.DefenderUid == targetUid
            && view.SelfLine.FirstOrDefault(c => c.Uid == p.AttackerUid)?.CanMoveAndAttack == true)?.AttackerUid
            ?? previews.First(p => p.DefenderUid == targetUid).AttackerUid;
        var attacker = view.SelfLine.FirstOrDefault(c => c.Uid == attackerUid);
        Audit($"attack[{(wantsHq ? "hq" : "unit")}] attacker={attacker?.Definition.Name}@{attacker?.Zone}[{attacker?.SlotIndex}] " +
              $"atk={attacker?.EffectiveAttack} -> {(wantsHq ? "ENEMY-HQ" : unitTarget!.Definition.Name)} " +
              $"targetHp={(wantsHq ? hq!.Health : unitTarget!.Health)} enemyHq={hq?.Health} offered={previews.Count}");
        await _battle.DragAsync(attackerUid, _battle.CardPoint(targetUid), async () =>
        {
            Audit($"attack[{(wantsHq ? "hq" : "unit")}] overlay {_battle.SlotHintReport()}");
            await ShotAsync(dir, $"real-hold-attack-{(wantsHq ? "hq" : "unit")}");
        });
        Audit($"attack channels={_battle.ChannelTrace} state=[{_battle.Interaction}]");
        await AwaitIdleAsync(12);
        var after = _runner.CurrentView!;
        var afterUnit = after.EnemyLine.FirstOrDefault(c => c.Uid == targetUid);
        Audit($"    after target={(wantsHq ? "HQ" : afterUnit?.Definition.Name ?? "<gone>")} " +
              $"targetHp={(wantsHq ? after.EnemyHq?.Health : afterUnit?.Health)} enemyHq={after.EnemyHq?.Health}");
        await ShotAsync(dir, $"real-attack-{(wantsHq ? "hq" : "unit")}");
        var hurt = wantsHq
            ? after.EnemyHq?.Health is not null && hq!.Health is not null && after.EnemyHq.Health < hq.Health
            : afterUnit is null || (unitTarget!.Health is not null && afterUnit.Health is not null && afterUnit.Health < unitTarget.Health);
        return hurt;
    }
}
