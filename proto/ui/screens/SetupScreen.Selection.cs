using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class SetupScreen
{
    private GridContainer _deckGrid = null!;
    private bool _online;
    private Button _practice = null!;
    private Button _friends = null!;
    private VBoxContainer _browser = null!;
    private ScrollContainer _deckScroll = null!;
    private Label _modeHelp = null!;
    private Button _resumeSession = null!;
    private VBoxContainer _pendingNotice = null!;
    private Label _pendingRoom = null!;
    public void SetResume(bool available, string caption) { if (_resumeSession is not null) { _resumeSession.Visible = available; _resumeSession.Text = caption; } }
    private bool OnlineReady => ReadyToPlay && _library.ActiveDeck is { CardIds.Length: 39 } deck && deck.CardIds.All(id => id.StartsWith("pack/", StringComparison.Ordinal));

    private SetupDeckBack _selectedBack = null!;
    private void BuildSelection()
    {
        AddThemeConstantOverride("separation", 0);
        var bar = new PanelContainer(); bar.AddThemeStyleboxOverride("panel", MenuMaterials.Panel(new Color("272a2c"),16)); AddChild(bar);
        var top = new HBoxContainer(); bar.AddChild(top);
        top.AddChild(UiStyles.Label("OrC–KSD", 24, UiStyles.Gold));
        var heading = UiStyles.Label("  /  作战准备", 22, UiStyles.MenuInk); heading.SizeFlagsHorizontal = SizeFlags.ExpandFill; top.AddChild(heading);
        BackButton = UiStyles.Button("返回指挥部", () => BackRequested?.Invoke()); top.AddChild(BackButton);
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddThemeConstantOverride("separation", 0); AddChild(body);
        var left = new PanelContainer { CustomMinimumSize = new(206, 0) }; left.AddThemeStyleboxOverride("panel", MenuMaterials.Panel(new Color("202529"),18)); body.AddChild(left);
        var modes = new VBoxContainer(); modes.AddThemeConstantOverride("separation", 16); left.AddChild(modes);
        modes.AddChild(UiStyles.Label("作战", 25, UiStyles.Gold));
        _resumeSession = UiStyles.Button("返回战场 →", () => ResumeRequested?.Invoke()); _resumeSession.Visible = false; modes.AddChild(_resumeSession);
        _practice = UiStyles.Button("训练  /  电脑对手", () => SelectMode(false)); _practice.ToggleMode = true; modes.AddChild(_practice);
        _friends = UiStyles.Button("好友对战  /  局域网", () => SelectMode(true)); _friends.ToggleMode = true; modes.AddChild(_friends);
        _advanced = new VBoxContainer(); modes.AddChild(_advanced);
        _modeHelp = UiStyles.Label("", 14, UiStyles.MenuDim); _modeHelp.AutowrapMode = TextServer.AutowrapMode.WordSmart; _advanced.AddChild(_modeHelp);
        DetailsButton = UiStyles.Button("连接说明", () =>
        {
            var dialog = new AcceptDialog { Title = "与好友连接", DialogText = "同一局域网内可直接发现房间。异地好友需先连接同一虚拟局域网，再使用房间列表或好友分享的地址。加入与观战均需要房主批准。", MinSize = new(520, 220) };
            AddChild(dialog); dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
        }); modes.AddChild(DetailsButton);
        modes.AddChild(UiStyles.Button("作战记录  /  回放", () => ReplayRequested?.Invoke()));
        modes.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        modes.AddChild(UiStyles.Label("卡组与工坊", 16, UiStyles.Gold));
        ChooseDeckButton = UiStyles.Button("管理卡组", () => ChooseDeckRequested?.Invoke()); modes.AddChild(ChooseDeckButton);
        var hint = UiStyles.Label("带上你构筑的部队，或使用工坊卡牌组建新卡组。", 13, UiStyles.MenuDim); hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; modes.AddChild(hint);
        var table = new SetupSelectionTable { SizeFlagsHorizontal = SizeFlags.ExpandFill }; table.Initialize(_textures); body.AddChild(table);
        var inset = new MarginContainer(); table.AddChild(inset); inset.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach(var edge in new[]{"left","right","top","bottom"}) inset.AddThemeConstantOverride("margin_"+edge, 26);
        var center = new VBoxContainer(); inset.AddChild(center);
        _browser = new VBoxContainer { Visible = false, SizeFlagsVertical = SizeFlags.ExpandFill }; center.AddChild(_browser); BuildRoomBrowser(_browser);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill }; center.AddChild(scroll); _deckScroll = scroll;
        _deckGrid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _deckGrid.AddThemeConstantOverride("h_separation", 24); _deckGrid.AddThemeConstantOverride("v_separation", 24); scroll.AddChild(_deckGrid);
        scroll.Resized += () => _deckGrid.Columns = Math.Clamp((int)(scroll.Size.X / 224), 1, 3);
        var details = new PanelContainer { CustomMinimumSize = new(280, 0) }; details.AddThemeStyleboxOverride("panel", MenuMaterials.Panel(new Color("1f242a"),24)); body.AddChild(details);
        var rail = new VBoxContainer(); rail.AddThemeConstantOverride("separation", 12); details.AddChild(rail);
        _name = UiStyles.Label("", 24, UiStyles.MenuInk); _name.HorizontalAlignment = HorizontalAlignment.Center; _name.AutowrapMode = TextServer.AutowrapMode.WordSmart; rail.AddChild(_name);
        _selectedBack = new SetupDeckBack { CustomMinimumSize = new(220, 320), MouseFilter = MouseFilterEnum.Ignore }; rail.AddChild(_selectedBack);
        _summary = UiStyles.Label("", 14, UiStyles.MenuDim); _summary.HorizontalAlignment = HorizontalAlignment.Center; _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart; rail.AddChild(_summary);
        rail.AddChild(UiStyles.Button("更换卡组", () => ChooseDeckRequested?.Invoke()));
        rail.AddChild(UiStyles.Button("编辑当前卡组", () => EditDeckRequested?.Invoke()));
        rail.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        _pendingNotice = new VBoxContainer { Visible = false }; rail.AddChild(_pendingNotice);
        _pendingRoom = UiStyles.Label("", 13, UiStyles.MenuDim); _pendingRoom.AutowrapMode = TextServer.AutowrapMode.WordSmart; _pendingNotice.AddChild(_pendingRoom);
        _pendingNotice.AddChild(UiStyles.Button("取消加入", () => { _pendingJoin = null; Refresh(); }));
        _status = UiStyles.Label("", 14, UiStyles.Gold); _status.AutowrapMode = TextServer.AutowrapMode.WordSmart; rail.AddChild(_status);
        StartButton = UiStyles.Button("出击", OnStart); StartButton.CustomMinimumSize = new(0, 56); StartButton.AddThemeFontSizeOverride("font_size", 23);
        StartButton.AddThemeStyleboxOverride("normal", UiStyles.Box(new Color("d8d1b9"), new Color("ebe3ca"), 0, 12)); StartButton.AddThemeColorOverride("font_color", new Color("292d2d")); rail.AddChild(StartButton);
        RoomButton = StartButton;
        foreach(var button in new[]{_practice,_friends,ChooseDeckButton,DetailsButton,BackButton}) MenuMaterials.Button(button);
        MenuMaterials.Button(StartButton,true); MenuMaterials.Title(_name); MenuMaterials.Title(heading); SelectMode(false);
    }
    private void SelectMode(bool online)
    {
        if (_online != online) _pendingJoin = null;
        _online = online; _browser.Visible = online; _deckScroll.Visible = !online; _advanced.Visible = true; DetailsButton.Visible = online;
        _practice.ButtonPressed = !online; _friends.ButtonPressed = online;
        _modeHelp.Text = online ? "创建自己的房间，或加入好友的房间。观战无需选择卡组。" : "使用自己的卡组，向电脑对手练习。";
        if (StartButton is not null) Refresh();
    }

    private void RefreshSelection()
    {
        StartButton.Text = !ReadyToPlay || (_online && !OnlineReady) ? "选择卡组  →" : _online ? "创建房间  →" : "开始训练  →";
        if (_pendingJoin.HasValue && OnlineReady) StartButton.Text = "加入选中的房间 →";
        _pendingNotice.Visible = _pendingJoin.HasValue;
        if (_pendingJoin is { } pending) _pendingRoom.Text = $"准备加入 {pending.Host}:{pending.Port}";
        if (_online && !OnlineReady) _status.Text = "这副卡组暂不能用于好友对战。请选择 39 张联机可用卡牌组成的卡组，另含总部。";
        _selectedBack.Bind(_library.ActiveDeck, _textures);
        if(_library.ActiveDeck is {} current) _summary.Text=$"{current.CardIds.Length+1} 张（含总部） · "+(current.Options.UnrestrictedNations?"混编":SetupDeckBack.NationLabel(current.Options.MainNation));
        UiStyles.Clear(_deckGrid);
        foreach (var deck in _library.Decks)
        {
            var selected = deck.Id == _library.ActiveId;
            var button = new Button { CustomMinimumSize = new(200, 362), TooltipText = deck.Name, ClipContents = false };
            foreach(var state in new[]{"normal","hover","pressed","focus"}) button.AddThemeStyleboxOverride(state, new StyleBoxEmpty());
            _deckGrid.AddChild(button);
            var back = new SetupDeckBack { CustomMinimumSize = new(200, 292), MouseFilter = MouseFilterEnum.Ignore, Selected = selected }; button.AddChild(back);
            back.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide); back.OffsetBottom = 292; back.Bind(deck, _textures);
            var plaque = new Panel { Position=new(0,296), Size=new(200,66), MouseFilter=MouseFilterEnum.Ignore };
            plaque.AddThemeStyleboxOverride("panel",MenuMaterials.Panel(new Color("23272a"),2)); button.AddChild(plaque);
            var caption = new Label { Text = deck.Name, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Position = new(0,298), Size = new(200,36), MouseFilter = MouseFilterEnum.Ignore, MaxLinesVisible = 1, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
            MenuMaterials.Title(caption); caption.AddThemeFontSizeOverride("font_size",20); button.AddChild(caption);
            var nation = new Label { Text = deck.Options.UnrestrictedNations ? "混编部队" : SetupDeckBack.NationLabel(deck.Options.MainNation),
                HorizontalAlignment = HorizontalAlignment.Center, Position = new(0,334), Size = new(200,24), MouseFilter = MouseFilterEnum.Ignore };
            nation.AddThemeColorOverride("font_color",UiStyles.Gold); nation.AddThemeFontSizeOverride("font_size",14); button.AddChild(nation);
            button.Pressed += () =>
            {
                try { _library.Activate(deck.Id); Refresh(); }
                catch(Exception e) when(e is System.IO.IOException or InvalidOperationException) { _status.Text=e.Message; }
            };
            button.MouseEntered += () => { back.Hovered = true; back.QueueRedraw(); };
            button.MouseExited += () => { back.Hovered = false; back.QueueRedraw(); };
            button.FocusEntered += () => { back.Hovered = true; back.QueueRedraw(); };
            button.FocusExited += () => { back.Hovered = false; back.QueueRedraw(); };
        }
        if(_library.Decks.Count == 0) _deckGrid.AddChild(UiStyles.Button("＋ 创建第一副卡组", () => ChooseDeckRequested?.Invoke()));
    }
}
