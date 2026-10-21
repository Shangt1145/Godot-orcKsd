using Kards.Ui.OrcBridge;
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
    private SetupScreen _setup = null!;
    private TabContainer _helpTabs = null!;
    private Label _pool = null!, _speedLabel = null!;
    private PanelContainer _shellBar = null!, _shellFooter = null!;
    private MarginContainer _contentMargin = null!;
    private string _active = "home";
    private UiMatchView? _match;
    /// <summary>本地对局夹具：验证代码依赖 OrcMatchRunner 的测试专用成员（换视角/座椅/卡池报告）。</summary>
    private OrcMatchRunner? _runner;

    /// <summary>正式对局的数据源。单机指向同一个 runner，联机指向远端实现；接线逻辑只认这个。</summary>
    private IMatchFeed? _feed;
    private PresentationPlayer? _presentations;
    private DeckConstructionService _deckService = null!;
    private DeckLibrary _decks = null!;
    private DeckEditorScreen _deckEditor = null!;
    private DeckLibraryScreen _deckLibrary = null!;
    private DeckPackPicker _deckPackPicker = null!;
    private DiyCardLibrary _diy = null!;
    private DiyEditorScreen _diyEditor = null!;
    private DiyEffectEditorScreen _diyEffects = null!;
    private OrcImportLibrary _orcImports = null!;
    private IReadOnlyList<OrcImportOutcome> _importedOutcomes = [];
    private CollectionScreen _collection = null!;
    private ConstructedDeck? _trialDeck;
    private string _diyPath = "";
    private bool _verificationRun;
    private string _deckPath = "";
    private DiyWorkshopLibrary _workshopLibrary=null!;
    private DiyWorkshopScreen _workshop=null!;

    public override async void _Ready()
    {
        if(ResumeInterruptedUpdate())return;
#if DEBUG
        const string managedConfiguration = "Debug";
#else
        const string managedConfiguration = "Release";
#endif
        NativeScriptHost.ConfigureAssemblyDirectory(ProjectSettings.GlobalizePath($"res://.godot/mono/temp/bin/{managedConfiguration}"));
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
        _verificationRun = OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--verify-") || a.StartsWith("--capture-"));
        _splash = new MenuSplash(); _splash.Initialize(); AddChild(_splash); _splash.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        try { await InitializeInterfaceAsync(sfx); }
        catch (Exception error)
        {
            GD.PushError(error.ToString()); _splash?.Fail(error.Message);
            if (_verificationRun) GetTree().Quit(1);
        }
    }
    private async Task InitializeInterfaceAsync(SfxPlayer sfx)
    {
        await StartupStageAsync(.08f, "整理卡牌收藏…");
        try
        {
            _catalog.Load(RuntimeGameData.Directory("nations"), CardCatalog.ResolveArtResource);
        }
        catch (Exception e) { throw new InvalidDataException("卡池加载失败，请先运行 tools/prepare.ps1", e); }
        // The engine knows nothing about art, so the catalog is the single place a card id becomes
        // a texture path. A real match reads its art through this table, same as the collection does.
        _artById = _catalog.Cards.Where(c => c.ArtPath.Length > 0)
            .GroupBy(c => c.CardId).ToDictionary(g => g.Key, g => g.First().ArtPath, StringComparer.Ordinal);
        await StartupStageAsync(.32f, "检查已保存卡组…");
        _deckPath = ProjectSettings.GlobalizePath(_verificationRun
            ? $"res://artifacts/decks/session-{Guid.NewGuid():N}.json" : "user://decks.json");
        // 迁移 S8：内建词条注册面是进程级静态（orc.builtin.core 进程相内容），必须先于任何
        // CardDefinition 解析装配（ResolveKeywords 对未注册标识 fail-fast）；此处一并完成卡包发现，
        // ReloadPacks 的 LoadPackCards 幂等复用同一宿主。
        OrcEngineHost.WarmUp(RuntimeGameData.Directory("kards"));
        _nativeWorkshop = new NativeWorkshop(ProjectSettings.GlobalizePath(_verificationRun
            ? $"res://artifacts/workshop/{Guid.NewGuid():N}" : "user://workshop"), RuntimeGameData.Directory("kards"), OrcEngineHost.SetWorkshopRoots);
        _nativeWorkshop.Activate();
        // 卡图对齐参数（user:// 下）在宿主就绪后读回，之后所有 CardFace 都按它排布。
        if (!_verificationRun) CardFaceTuner.LoadSaved();
        _deckService = new DeckConstructionService(_catalog.Cards, RuntimeGameData.Directory("nations"));
        _diyPath = ProjectSettings.GlobalizePath(_verificationRun
            ? $"res://artifacts/diy/session-{Guid.NewGuid():N}.json" : "user://diy-cards.json");
        if (OS.GetCmdlineUserArgs().Contains("--verify-workshop-routes"))
        {
            var source = ProjectSettings.GlobalizePath("user://diy-cards.json");
            if (System.IO.File.Exists(source)) { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_diyPath)!); System.IO.File.Copy(source, _diyPath); }
        }
        _diy = new DiyCardLibrary(_diyPath);
        var workshopPath = ProjectSettings.GlobalizePath(_verificationRun?$"res://artifacts/diy/packs-{Guid.NewGuid():N}.json":"user://diy-packs.json");
        if (OS.GetCmdlineUserArgs().Contains("--verify-workshop-routes"))
        {
            var source = ProjectSettings.GlobalizePath("user://diy-packs.json");
            if (System.IO.File.Exists(source)) System.IO.File.Copy(source, workshopPath);
        }
        _workshopLibrary=new DiyWorkshopLibrary(workshopPath);
        if (!_verificationRun)
            DemoMechanicsUpgrade.Apply(_workshopLibrary, ProjectSettings.GlobalizePath("user://diy-packs.json"));
        if(_diy.LoadError is null&&_workshopLibrary.LoadError is null&&_diy.Cards.Any(c=>c.PackId.Length==0))
        {
            const string legacy="pack/diy-legacy";
            if(!_workshopLibrary.Packs.Any(p=>p.Id==legacy))_workshopLibrary.Save(new(){Id=legacy,Name="我的 DIY 卡包"});
            foreach(var card in _diy.Cards.Where(c=>c.PackId.Length==0))_diy.Save(card with{PackId=legacy});
        }
        WorkshopAuthoring.Configure(_workshopLibrary,_diy.Cards);
        // 自导入卡图：art/ 目录与卡库同生命周期；缩放表随卡库刷新（RefreshImportedDiyArt）。
        DiyArtStore.Configure(Path.Combine(Path.GetDirectoryName(_diyPath)!, "art"),
            _diy.Cards.Where(c => c.ArtFile.Length > 0).Select(c => (c.RuntimeId, c.ArtScale)));
        _deckService.UpdateCustom(_diy.Cards);
        _catalog.SetCustomCards(_deckService.CustomCards);
        // OrC 批量导入：装载持久化行并重编译（解析器演进时旧导入自动跟随新面）；
        // 装载失败＝隔离并警告，绝不抛到启动链上。
        _orcImports = new OrcImportLibrary(ProjectSettings.GlobalizePath(_verificationRun
            ? $"res://artifacts/orc-imports/session-{Guid.NewGuid():N}.json" : "user://orc-imports.json"));
        if (_orcImports.LoadError is { } orcError) GD.PushWarning(orcError);
        RefreshImported();
        ReloadPacks();
        _artById = _catalog.Cards.Where(c => c.ArtPath.Length > 0).ToDictionary(c => c.CardId, c => c.ArtPath);
        _decks = new DeckLibrary(_deckPath);
        if (_verificationRun && _decks.Decks.Count == 0 && _decks.LoadError is null)
        {
            try { var starter = _deckService.Starter(); _decks.Save(starter); _decks.Activate(starter.Id); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { GD.PushError(e.Message); }
        }
        await StartupStageAsync(.64f, "布置指挥部…");
        var root = new VBoxContainer();
        AddChild(root);
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        var content = BuildMenuShell(root);
        var collection = new CollectionScreen();
        _collection = collection;
        collection.Initialize(_catalog, _textures, id => _deckService.PlayableIds.Contains(id));
        AddScreen(content, "collection", collection);
        _battle = new BattleScreen();
        _battle.Theme = UiStyles.CreateTheme();
        _battle.Initialize(_catalog, _textures, _clock, sfx);
        _battle.CommandRequested += command =>
        {
            // 有对局就转发；SubmitAsync 内部对"会话已结束"已有 null 保护，故不必再问 IsRunning。
            if (_feed is { } feed)
            {
                _ = feed.SubmitAsync(command);
                if (feed.CurrentView is { } view) _battle.UpdateInputProjection(view, feed.CurrentActions);
            }
            CommandRequested?.Invoke(command);
        };
        // 验证环境里重开沿用同一套固定条件（人工对手、玩家先手），产品环境仍按种子随机。
        _battle.RealMatchRequested += () => _ = StartRealMatchAsync(trialDeck: _trialDeck,
            manualOpponentForVerification: _verificationRun, firstPlayerIndex: _verificationRun ? 0 : null);
        _battle.NavigationRequested += id => {
            if (id == "deck" && _deckEditor.Draft.PackIds is { Length: 0 }) _deckEditor.CreateNew();
            else Show(id);
        };
        AddScreen(content, "battle", _battle);
        _deckEditor = new DeckEditorScreen(); _deckEditor.Initialize(_deckService, _decks, _textures);
        _deckLibrary = new DeckLibraryScreen(); _deckLibrary.Initialize(_decks, _deckService, _textures);
        _deckPackPicker = new DeckPackPicker(); AddChild(_deckPackPicker); _deckPackPicker.Initialize(_deckService);
        void NewDeck() => _deckPackPicker.Open([], packs => {
            var countries = _deckService.PlayableCards.Where(c => packs.Contains(DeckCardPacks.Of(c.CardId)))
                .Select(c => c.Set).Where(ConstructionRules.MainNations.Contains).Distinct().ToArray();
            var options = new DeckOptions { MainNation = countries.FirstOrDefault() ?? "av76",
                AllyNation = countries.Skip(1).FirstOrDefault() ?? ConstructionRules.MainNations.First(n => n != (countries.FirstOrDefault() ?? "av76")) };
            _deckEditor.Open(new ConstructedDeck { PackIds = packs, Options = options }); Show("deck");
        });
        _deckLibrary.NewRequested += NewDeck; _deckEditor.NewRequested += NewDeck;
        _deckEditor.PacksRequested += () => _deckPackPicker.Open(DeckCardPacks.Selected(_deckEditor.Draft), _deckEditor.ApplyPacks, creating: false);
        _deckEditor.Saved += RefreshMenuData;
        _deckEditor.StartRequested += () => { Show("battle"); _ = StartRealMatchAsync(useConstructedDeck: true); };
        _deckLibrary.EditRequested += deck => { _deckEditor.Open(deck); Show("deck"); };
        _deckLibrary.StartRequested += () => { Show("battle"); _ = StartRealMatchAsync(useConstructedDeck: true); };
        AddScreen(content, "deck", _deckEditor);
        AddScreen(content, "decklib", _deckLibrary);
        _home = new HomeScreen(); _home.Initialize(_decks, _deckService, _textures);
        _home.PlayRequested += OpenPlaySelection;
        _home.StartRequested += () => { Show("battle"); _ = StartRealMatchAsync(useConstructedDeck: true); };
        _home.NavigationRequested += NavigateMenu;
        _deckLibrary.Changed += RefreshMenuData;
        AddScreen(content, "home", _home);
        _setup = new SetupScreen(); _setup.Initialize(_decks, _deckService, _textures);
        _setup.StartRequested += () => { Show("battle"); _ = StartRealMatchAsync(useConstructedDeck: true); };
        _setup.ChooseDeckRequested += () => Show("decklib");
        _setup.BackRequested += () => Show("home");
        AddScreen(content, "setup", _setup);
        _diyEditor = new DiyEditorScreen();
        _diyEditor.Initialize(_diy, () => _catalog.Cards, _textures, id => _decks.Decks.Count(d => d.CardIds.Contains(id)),
            id => _deckService.CustomErrors.GetValueOrDefault(id), _orcImports, () => _importedOutcomes);
        _diyEditor.Saved += RefreshDiy;
        _diyEditor.ImportedChanged += RefreshImported;
        _diyEditor.ImportedTrialRequested += outcome =>
        {
            var starter = _deckService.Starter(12);
            var copies = Math.Min(2, ConstructionRules.CopyLimit(outcome.Card!.Rarity));
            var trial = starter with { Name = outcome.Name + " · 试打",
                CardIds = Enumerable.Repeat("orc/" + outcome.Id, copies).Concat(starter.CardIds.Take(12 - copies)).ToArray() };
            // 试打固定玩家先手：它的目的是立刻看到这张卡上场，不是打一场正式对局。
            Show("battle"); _ = StartRealMatchAsync(deckSeed: 37, trialDeck: trial, firstPlayerIndex: 0);
        };
        _diyEditor.ConstructRequested += id =>
        {
            _deckEditor.Search.Text = _catalog.Cards.First(c => c.CardId == id).Name;
            if (!DeckCardPacks.Allows(_deckEditor.Draft, id))
                _deckPackPicker.Open(DeckCardPacks.Selected(_deckEditor.Draft), packs => { _deckEditor.ApplyPacks(packs); Show("deck"); }, creating: false);
            else Show("deck");
        };
        _diyEditor.TrialRequested += card =>
        {
            var starter = _deckService.Starter(12);
            var copies = Math.Min(2, ConstructionRules.CopyLimit(card.Rarity));
            var trial = starter with { Name = card.Name + " · 试打",
                CardIds = Enumerable.Repeat(card.RuntimeId, copies).Concat(starter.CardIds.Take(12 - copies)).ToArray() };
            // 试打固定玩家先手，同导入卡的试打路径。
            Show("battle"); _ = StartRealMatchAsync(deckSeed: 37, trialDeck: trial, firstPlayerIndex: 0);
        };
        _diyEditor.ConfigureWorkshop(_workshopLibrary,RefreshDiy);
        _workshop=new DiyWorkshopScreen();_workshop.Initialize(_workshopLibrary,_diy,_diyEditor,_deckService,_textures,RefreshDiy,()=>_clock.ReducedMotion);
        _workshop.ConfigureNative(_nativeWorkshop, () => { _textures.Clear(); ReloadPacks(); RefreshDiy(); });
        AddScreen(content, "editor", _workshop);
        // 效果编辑器（独立屏幕）：主编辑器入口按钮进入，编辑实时写回草稿，完成后返回。
        _diyEffects = new DiyEffectEditorScreen(); _diyEffects.Initialize();
        _diyEffects.Catalog=()=>_catalog.Cards;
        _diyEffects.Drafts=()=>_diyEditor.AuthoredCards;
        _diyEffects.InspectorSaved+=draft=>_diyEditor.AcceptInspectorDraft(draft);
        _diyEffects.DraftEdited+=draft=>_diyEditor.ApplyInspectorDraft(draft);
        _diyEffects.DraftTouched+=()=>_diyEditor.MarkInspectorDirty();
        _diyEditor.EffectsEditorRequested += () => { _diyEffects.Open(_diyEditor.Draft); Show("diyfx"); };
        _diyEffects.Applied += (orc, legacy) => _diyEditor.ApplyEffects(orc, legacy);
        _diyEffects.Finished += () => Show("editor");
        AddScreen(content, "diyfx", _diyEffects);
        _helpTabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _gallery = new AnimationGallery { Name = "动画演示" };
        _helpTabs.AddChild(_gallery);
        _gallery.Initialize(_catalog, _textures, _fx);
        var bridge = new BridgeDemo { Name = "交互与事件" };
        _helpTabs.AddChild(bridge);
        bridge.Initialize(_fx, _catalog, _textures);
        var help = Guide("使用说明", "收藏支持按国别、卡类、兵种、稀有度、词条筛选，也可以搜索卡名和卡面文本。滚轮下翻连续浏览所有卡牌。\n\n点击或长按卡牌查看大图。动画演示页可选择兵种与防御值，并随时打断或复位。\n\n设置中的减弱动效保留声音、卡面揭示与动作时序，同时关闭菜单位移、卡牌展开和鼠标跟随。");
        help.Name = "使用说明";
        _helpTabs.AddChild(help);
        _helpTabs.TabChanged += _ => _fx.InterruptAll();
        AddScreen(content, "help", _helpTabs);
        AddScreen(content, "settings", Settings());
        var footer = new PanelContainer();
        _shellFooter = footer;
        footer.AddThemeStyleboxOverride("panel", MenuStyles.Box(new("141c17"), new("354032"), 0, 6));
        root.AddChild(footer);
        var footRow = new HBoxContainer();
        footer.AddChild(footRow);
        footRow.AddChild(UiStyles.Label("OrC-KSD" + VersionSuffix(), 12, UiStyles.MenuDim));
        var gap = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        footRow.AddChild(gap);
        _speedLabel = UiStyles.Label("", 12, UiStyles.Dim);
        footRow.AddChild(_speedLabel);
        _clock.Changed += UpdateStatus;
        _clock.Changed += RefreshMenuMotion;
        UpdateStatus();
        await StartupStageAsync(1, "准备就绪");
        Show("home");
        await FinishStartupAsync();
        var args = OS.GetCmdlineUserArgs();
        if (args.Contains("--net-probe")) Callable.From(NetProbe).CallDeferred();
        else if (args.Contains("--verify-menu") || args.Contains("--capture-menu")) CallDeferred(MethodName.VerifyMenu);
        else if (args.Contains("--capture-setup")) Callable.From(CaptureSetup).CallDeferred();
        else if (args.Contains("--verify-export")) CallDeferred(MethodName.VerifyExport);
        else if (args.Contains("--capture-official-art")) CallDeferred(MethodName.CaptureOfficialArt);
        else if (args.Contains("--verify-inflight-draw")) CallDeferred(MethodName.VerifyInFlightDrawOnly);
        else if (args.Contains("--verify-action-portals") || args.Contains("--capture-action-portals"))
            CallDeferred(MethodName.VerifyActionPortals);
        else if (args.Contains("--verify-beta4-p0"))
            Callable.From(VerifyBeta4P0).CallDeferred();
        else if (args.Contains("--verify-native-workshop"))
            Callable.From(VerifyNativeWorkshop).CallDeferred();
        else if (args.Contains("--verify-workshop-routes"))
            Callable.From(VerifyWorkshopRoutes).CallDeferred();
        else if (args.Contains("--verify-inspector-composition") || args.Contains("--capture-inspector-composition"))
            Callable.From(VerifyInspectorComposition).CallDeferred();
        else if (args.Contains("--verify-desktop-authoring") || args.Contains("--capture-desktop-authoring"))
            CallDeferred(MethodName.VerifyDesktopAuthoring);
        else if(args.Contains("--verify-workshop")||args.Contains("--capture-workshop"))
            CallDeferred(MethodName.VerifyWorkshop);
        else if (args.Contains("--verify-diy-layout") || args.Contains("--capture-diy-layout"))
            CallDeferred(MethodName.VerifyDiyLayout);
        else if (args.Contains("--verify-cardface") || args.Contains("--capture-cardface"))
            CallDeferred(MethodName.VerifyCardFace);
        else if (args.Contains("--tune-cardface"))
            CallDeferred(MethodName.OpenCardFaceTuner);
        else if (args.Contains("--verify-diy") || args.Contains("--capture-diy"))
            CallDeferred(MethodName.VerifyDiy);
        else if (args.Contains("--verify-decks") || args.Contains("--capture-decks"))
            CallDeferred(MethodName.VerifyDecks);
        else if (args.Contains("--verify-ui"))
            CallDeferred(MethodName.VerifyUi);
        else if (args.Contains("--verify-stage6c2"))
            CallDeferred(MethodName.VerifyStage6C2);
        else if (args.Contains("--verify-native-feedback") || args.Contains("--capture-native-feedback"))
            CallDeferred(MethodName.VerifyNativeFeedback);
        else if (args.Contains("--verify-causal-draw"))
            CallDeferred(MethodName.VerifyCausalDraw);
        else if (args.Contains("--verify-input-scheduling"))
            CallDeferred(MethodName.VerifyInputScheduling);
        else if (args.Contains("--verify-card-pool"))
            CallDeferred(MethodName.VerifyCardPool);
        else if (args.Contains("--verify-deployment"))
            CallDeferred(MethodName.VerifyDeployment);
        else if (args.Contains("--capture-slam"))
            CallDeferred(MethodName.CaptureSlam);
        else if (args.Contains("--capture-orders"))
            CallDeferred(MethodName.CaptureOrders);
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
        else if (args.Contains("--verify-target-drag"))
            CallDeferred(MethodName.VerifyTargetDrag);
        else if (args.Contains("--verify-keyword-movement"))
            VerifyKeywordMovement();
        else if (args.Contains("--verify-demo-pack-targeting"))
            CallDeferred(MethodName.VerifyDemoPackTargeting);
        else if (args.Contains("--verify-discard"))
            CallDeferred(MethodName.VerifyDiscardAnimation);
        else if (args.Contains("--verify-opponent-turn"))
            CallDeferred(MethodName.VerifyOpponentTurnLiveness);
        else if (args.Contains("--verify-deploy-card"))
            CallDeferred(MethodName.VerifyDeployCard);
    }
    /// <summary>Runs the screen from a real engine match. Invoked again after a game over = restart.
    /// Verify and headless paths pass interactiveMulligan: false to keep the automatic keep-all answer.</summary>
    public async Task StartRealMatchAsync(bool interactiveMulligan = true, bool manualOpponentForVerification = false,
        int? deckSeed = null, bool demoOpeningHand = false, bool probe = false, IReadOnlyList<string>? verificationHand = null, bool useConstructedDeck = false,
        ConstructedDeck? trialDeck = null, int? firstPlayerIndex = null)
    {
        ConstructedDeck? selected = null;
        if (trialDeck is not null)
        {
            selected = trialDeck.Snapshot();
            if (_deckService.Validate(selected).Count > 0) { Show("editor"); return; }
        }
        else if ((!_verificationRun || useConstructedDeck) && !probe)
        {
            selected = _decks.ActiveDeck;
            if (selected is null || _deckService.Validate(selected).Count > 0)
            {
                Show("decklib");
                _battle.ShowHint("请先选择一副可用于对战的已保存卡组");
                return;
            }
        }
        StopRealMatch();
        _trialDeck = trialDeck?.Snapshot();
        var matchArt = _artById.ToDictionary(p => p.Key, p => p.Value);
        var runner = new OrcMatchRunner
        {
            InteractiveMulligan = interactiveMulligan,
            ConstructedDeck = selected,
            ArtLookup = id => matchArt.GetValueOrDefault(id, ""),
            // Production uses the admitted source catalog; only explicit verification uses a probe.
            CatalogCards = probe ? null : _deckService.Cards.ToArray(),
            CustomCards = probe ? null : _diy.Cards.Where(c => _deckService.PlayableIds.Contains(c.RuntimeId) && !_deckService.CustomErrors.ContainsKey(c.Id)).Select(c => c.Snapshot()).ToArray(),
            ImportedCards = probe ? null : _importedOutcomes.Where(o => o.Complete).ToArray(),
            CatalogSourceDirectory = RuntimeGameData.Directory("nations"),
            ManualOpponentForVerification = manualOpponentForVerification,
            DeckSeed = deckSeed,
            // 先手默认随机。人工对手夹具由验证脚本手动推进双方，前提是玩家先手，所以它默认落座位 0；
            // 产品入口不走这条分支，玩家无法指定先后手。
            FirstPlayerIndex = firstPlayerIndex ?? (manualOpponentForVerification ? 0 : null),
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
            _battle.ShowHint(selected is null ? $"可用 {report.Verified} 种卡 · 验证用 30 张牌组" : $"{selected.Name} · {selected.CardIds.Length} 张卡牌 + 总部 · 随机发牌");

    }

    /// <summary>绑定本地对局。强类型保留给验证夹具；正式接线走 <see cref="BindFeed"/>。</summary>
    private void BindRunner(OrcMatchRunner runner)
    {
        _runner = runner;
        BindFeed(runner);
    }

    /// <summary>
    /// 绑定一个对局数据源。**只依赖 IMatchFeed**——单机传 runner，联机传远端实现，
    /// 这条路径上不出现任何 OrcMatchRunner 专有成员。
    /// </summary>
    private void BindFeed(IMatchFeed feed)
    {
        _feed = feed;
        feed.InteractionStateReader = _battle.ReadInteractionState;
        feed.ProjectionReady += (view, actions) => ApplyRunnerProjection(feed, view, actions);
        feed.MulliganRequested += () => Callable.From(() =>
        { if (_feed == feed && feed.MulliganPending) _battle.ShowMulliganPanel(feed.CurrentView, feed.MulliganRequestId); }).CallDeferred();
        feed.TargetRequested += (request, responder) => Callable.From(() =>
        { if (_feed == feed && feed.TargetPending) _battle.ShowTargetChoice(request, responder); else responder.Cancel(request.RequestId); }).CallDeferred();
        feed.TargetClosed += () => Callable.From(() =>
        { if (_feed == feed && !feed.TargetPending) _battle.CloseTargetChoice(); }).CallDeferred();
        feed.PresentationReady += (resolution, actions) =>
        {
            if (_feed != feed) return;
            _battle.ObservePresentationTurn(resolution);
            if (feed.CurrentView is { } view) _battle.UpdateInputProjection(view, feed.CurrentActions);
            _presentations?.Enqueue(resolution, actions);
        };
        feed.CommandRefused += command => { if (_feed == feed) _battle.RefuseCommand(command); };
        feed.ErrorRaised += message => { if (_feed == feed) GD.PushError("[engine] " + message); };
        feed.HintRequested += message => { if (_feed == feed) _battle.ShowHint(message); };
    }

    private void ApplyRunnerProjection(IMatchFeed feed, UiMatchView view, UiBattleActions actions)
    {
        if (_feed != feed) return;
        _presentations?.Dispose();
        _battle.ApplyProjection(view, actions);
        // Applying a new phase also changes the state pulled by native input judicators.
        // Query again after that transition so a new match does not inherit the old UI lock.
        _battle.UpdateInputProjection(view, feed.CurrentActions);
        _presentations = new PresentationPlayer(feed.MatchId, (resolution, available, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return _feed == feed ? _battle.PresentSequenceAsync(resolution, available) : Task.CompletedTask;
        }, view);
        _presentations.Failed += e => GD.PushError($"[presentation] {e}");
    }

    public override void _Process(double delta)
    {
        var feed = _feed;
        feed?.Advance(delta);
        if (_feed == feed && feed?.CurrentView is { } view)
            _battle.UpdateInputProjection(view, feed.CurrentActions);
    }

    private void StopRealMatch()
    {
        var runner = _runner;
        _runner = null;
        _feed = null;
        _presentations?.Dispose();
        _presentations = null;
        runner?.Stop();
        _battle?.CloseTargetChoice();
        if (GodotObject.IsInstanceValid(_battle)) _battle.CancelSelection();
        if (GodotObject.IsInstanceValid(_battle)) _battle.SuspendMatchInput();
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

        async Task SettlePlayback()
        {
            _runner!.Pump();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!await AwaitIdleAsync(30)) throw new Exception("Native bridge presentation did not settle.");
        }

        // Playable loop: submit a real play and a real end-turn; the engine decides both outcomes.
        var actions = _runner!.CurrentActions;
        if (!actions.CanEndTurn || actions.PlayableUids.Count == 0)
            throw new Exception("Engine availability did not reach the UI.");
        // This native lifecycle probe uses affordable units; source-assembled orders have their
        // own end-to-end verification below.
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
        await SettlePlayback();
        GD.Print($"[bridge] play uid={uid} hand=[{string.Join(' ', settled.SelfHand.Select(c => $"{c.Definition.CardType}/{c.Definition.Cost}"))}] " +
            $"kredits={settled.SelfKredits} steps fired={deployed}, phases=[{string.Join(' ', phases)}]");
        if (!phases.Contains("deployment-start") || !phases.Any(p => p.StartsWith("deployment-slam")))
            throw new Exception("Deployment choreography never played.");
        var played = _runner!.CurrentView!;
        if (played.SelfHand.Count != handBefore - 1) throw new Exception("Playing a card did not consume it.");
        if (!played.SelfLine.Any(c => c.Uid == uid)) throw new Exception("Played unit never reached the board.");
        if (!deployed) throw new Exception("Deployment produced no presentation.");

        await _runner!.SubmitAsync(new EndTurn());
        await SettlePlayback();
        // The opponent driver plays the enemy turn through the engine and passes straight back.
        // Whether it deploys is the engine's business: with the shipped pool most hands hold
        // nothing affordable, and a driver that correctly does nothing is still a driver that ran.
        var afterEnemyTurn = _runner!.CurrentView!;
        if (afterEnemyTurn.ActivePlayerSide != "self") throw new Exception("Opponent turn did not come back.");
        if (afterEnemyTurn.Turn <= view.Turn) throw new Exception("The turn counter did not advance past the opponent.");

        // Native insertion leaves distinct occupied positions. This lifecycle probe waits for
        // playback before querying the next action; separate tests cover concurrent input.
        var second = _runner!.CurrentActions.PlayableUids
            .Where(c => _runner!.CurrentView!.SelfHand.Any(h => h.Uid == c))
            .FirstOrDefault();
        if (second is not null)
        {
            await _runner!.SubmitAsync(new PlayCard(second));
            await SettlePlayback();
            var board = _runner!.CurrentView!.SelfLine;
            var left = board.FirstOrDefault(c => c.Uid == second);
            var right = board.FirstOrDefault(c => c.Uid == uid);
            if (left is not null && right is not null && left.SlotIndex == right.SlotIndex)
                throw new Exception("Two deployments landed on the same slot.");
        }

        // Real movement: advance a unit to a front slot the board says is free, and require it to land
        // there. The slot is chosen from the projection, not assumed, because the front line is shared.
        await _runner!.SubmitAsync(new EndTurn());
        await SettlePlayback();
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
                await SettlePlayback();
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
        await SettlePlayback();
        var hqUid = _runner!.CurrentView!.EnemyHq!.Uid;
        var strike = _runner!.CurrentActions.AttackPreviews.FirstOrDefault(p => p.DefenderUid == hqUid)
            ?? throw new Exception("No unit can attack the enemy headquarters.");
        await _runner!.SubmitAsync(new AttackUnit(strike.AttackerUid, hqUid));
        await SettlePlayback();
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
        // This probe isolates one-for-one replacement. Going second also draws at turn start;
        // that separate no-input path is covered by --verify-beta4-p0.
        await StartRealMatchAsync(firstPlayerIndex: 0); // interactive
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
    private static VBoxContainer Guide(string title, string description)
    {
        var v = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        v.AddChild(UiStyles.Label(title, 34, UiStyles.Gold));
        var p = UiStyles.Label(description, 18, UiStyles.Dim);
        p.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(p);
        return v;
    }
    private Control Settings()
    {
        var screen = new VBoxContainer(); screen.AddThemeConstantOverride("separation", 20);
        screen.AddChild(MenuStyles.Label("设置", 32, MenuStyles.Ink));
        screen.AddChild(MenuStyles.Label("调整演出与声音，偏好自动保存。", 15, MenuStyles.Dim));
        var body = new HBoxContainer(); body.AddThemeConstantOverride("separation", 24); screen.AddChild(body);
        VBoxContainer Section(string title)
        {
            var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 370) }; body.AddChild(panel);
            var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 18); panel.AddChild(box);
            box.AddChild(MenuStyles.Label(title, 24, MenuStyles.Ink)); return box;
        }
        var motionSection = Section("演出");
        motionSection.AddChild(MenuStyles.Label("动画速度", 14, MenuStyles.Gold));
        var speed = new OptionButton();
        speed.AddItem("正常 · 1.00"); speed.AddItem("快 · 0.60"); speed.AddItem("极快 · 0.32");
        speed.Select((int)_clock.Current); speed.ItemSelected += i => _clock.SetSpeed((AnimClock.Speed)i); motionSection.AddChild(speed);
        var motion = new CheckButton { Text = "减弱动效", ButtonPressed = _clock.ReducedMotion };
        motion.Toggled += on => { _clock.SetReducedMotion(on); _fx.InterruptAll(); }; motionSection.AddChild(motion);
        var description = MenuStyles.Label("减少菜单位移、卡牌展开与鼠标跟随，保留卡面揭示和必要的操作反馈。", 14, MenuStyles.Dim);
        description.AutowrapMode = TextServer.AutowrapMode.WordSmart; motionSection.AddChild(description);
        var audio = Section("声音");
        var mute = new CheckButton { Text = "静音", ButtonPressed = _clock.Muted }; mute.Toggled += on => _clock.SetMuted(on); audio.AddChild(mute);
        audio.AddChild(MenuStyles.Label("音效音量", 14, MenuStyles.Gold));
        var level = MenuStyles.Label($"{_clock.Volume:P0}", 18, MenuStyles.Ink);
        var volume = new HSlider { MinValue = 0, MaxValue = 1, Step = .01, Value = _clock.Volume, CustomMinimumSize = new(0, 32) };
        volume.ValueChanged += x => { _clock.SetVolume((float)x); level.Text = $"{x:P0}"; }; audio.AddChild(volume); audio.AddChild(level);
        var hintPanel = new PanelContainer(); screen.AddChild(hintPanel);
        var hint = MenuStyles.Label("从左侧切换卡组、收藏与工坊。对局中可从右上角菜单返回指挥部，再选择“返回战场”继续。", 14, MenuStyles.Dim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; hintPanel.AddChild(hint);
        return screen;
    }
    private void UpdateStatus()
    {
        _speedLabel.Text = $"演出 {_clock.Scale:0.00}×" + (_clock.ReducedMotion ? " · 减弱动效" : "");
    }
    private void Show(string id)
    {
        if (_active == "editor" && id != "editor" && id != "diyfx") { _workshop.GuardLeaving(() => ShowCore(id)); return; }
        ShowCore(id);
    }
    private void ShowCore(string id)
    {
        if (!_screens.ContainsKey(id))
            return;
        var changed = id != _active || !_screens[id].Visible;
        if (changed) { _fx.InterruptAll(); ResetMenuTransition(); }
        _active = id;
        if (id == "decklib") _deckLibrary.Refresh();
        if (id == "home") _home.Refresh();
        if (id == "setup") _setup.Refresh();
        // 换屏统一走快门：盖住 → 切可见性 → 揭开。战场与菜单同一套过场（此前战场是硬切）。
        var curtained = changed && !_clock.ReducedMotion;
        if (curtained) CoverScreen();
        foreach (var (key, screen) in _screens)
            screen.Visible = key == id;
        foreach (var (key, b) in _nav)
            b.SetPressedNoSignal(key == id);
        SetBattlePresentation(id == "battle");
        UpdateMenuNavigation();
        if (changed) EnterMenuScreen(id);
        if (curtained) RevealScreen();
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
        _shellBar.Visible = _shellFooter.Visible = !activeMatch && _active != "setup";
        _menuBackdrop.Visible = !activeMatch;
        foreach (var edge in new[] { "left", "right", "top", "bottom" })
            _contentMargin.AddThemeConstantOverride("margin_" + edge, activeMatch ? 0 : 28);
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
            // 迁移 S8：中立视图契约上移到 Orc.UI.Contracts（零引擎依赖），proto/ui 经全局 Using 消费
            // 它不算越界；引擎运行时程序集（Orc / Orc.Game / Orc.Packages / ...）仍然一律禁止。
            var engineRefs = typeof(Main).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name).OfType<string>()
                .Where(n => n == "Orc" || (n.StartsWith("Orc.", StringComparison.Ordinal) && n != "Orc.UI.Contracts"))
                .ToList();
            if (engineRefs.Count > 0)
                throw new Exception($"UI assembly directly references engine types: {string.Join("、", engineRefs!)}");
            GD.Print("UI_BOUNDARY_VERIFY_OK");
            await VerifyMenuAsync();
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
            var sourceCount = _catalog.Cards.Count(c => !c.CardId.StartsWith("pack/", StringComparison.Ordinal)
                && !c.CardId.StartsWith("diy/", StringComparison.Ordinal));
            if (sourceCount != 315)
                throw new Exception("Wrong catalogue size.");
            var originalCards = _catalog.Cards.Where(c => c.CardId.StartsWith("pack/", StringComparison.Ordinal)).ToArray();
            var expectedOriginal = System.IO.Directory.GetFiles(RuntimeGameData.Directory("kards"), "*.card.json", System.IO.SearchOption.AllDirectories).Length;
            // 新引擎会按资产规则过滤非法卡（典型：指令/反制卡必须有内置效果——只有卡面文本、
            // 没有实现效果的空壳卡会被丢弃）。所以「卡池数 == 文件数」不再成立；
            // 正确的不变式是「卡池数 + 被过滤数 == 文件数」，且被过滤的卡必须能点名。
            var rejectedPacks = Kards.Ui.OrcBridge.OrcEngineHost.RejectedPackCards;
            if (originalCards.Length + rejectedPacks.Count != expectedOriginal)
                throw new Exception($"Pack card count mismatch: pool={originalCards.Length} rejected={rejectedPacks.Count} files={expectedOriginal}.");
            if (rejectedPacks.Count > 0)
                GD.Print($"PACK_REJECTED cards={rejectedPacks.Count} {string.Join(",", rejectedPacks)}");
            var artless = originalCards.Where(c => c.ArtPath.Length == 0 || _textures.Get(c.ArtPath) is null)
                .Select(c => c.CardId + "@" + (c.ArtPath.Length == 0 ? "<no art path>" : c.ArtPath)).Take(6).ToArray();
            if (artless.Length > 0)
                throw new Exception("Pack artwork missing or cannot be decoded: " + string.Join(" | ", artless));
            GD.Print($"OFFICIAL_ART_VERIFY_OK cards={originalCards.Length}");
            foreach (var c in _catalog.Cards.Where(c => c.ArtPath.Length > 0 && !c.ArtPath.StartsWith("diyart://", StringComparison.Ordinal)))
                if (!ResourceLoader.Exists(c.ArtPath))
                    throw new Exception($"Missing texture: {c.CardId}");
            // diyart:// 自导入卡图走 DiyArtStore 运行时加载（不经资源导入管线），单独核验文件存在。
            foreach (var c in _catalog.Cards.Where(c => c.ArtPath.StartsWith("diyart://", StringComparison.Ordinal)))
                if (!DiyArtStore.Has(c.ArtPath[9..]))
                    throw new Exception($"Missing DIY art: {c.CardId}");
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
            await VerifyStage6C2Async();
            await VerifyBridgeAsync();
            await VerifyCardPoolStageAsync();
            await VerifyOrdersStageAsync();
            await VerifyNonBlockingInputAsync();
            await _battle.VerifyTurnAndPriorityAsync();
            await VerifyCausalDrawOrderAsync();
            await VerifyNativeFeedbackAsync();
            // 旧编辑器（DiyEditorScreen）的 UI 夹具已摘除。工坊重构为「卡包列表 → 单卡制作（分页＋弹窗＋聚焦）」
            // 之后，原夹具 18 处坐标点击全部落在不可见控件上；继续沿点击链修补没有收敛点。
            // 覆盖并未丢失：数据层由 DiyCard/DiyInspector/DeckPack 单测覆盖，
            // 工坊 UI 由发布门的 --verify-workshop（NativeWorkshop）覆盖。
            // 恢复条件：按现结构（先 OpenCompatibleCardForVerification 进单卡页）重写该夹具。
            // await VerifyDiyAsync();
            await VerifyDecksAsync();
            OpenGallery();
            await MenuSettledAsync();
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
            var dir = ProjectSettings.GlobalizePath("res://artifacts/deployment-reference-20261008");
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
