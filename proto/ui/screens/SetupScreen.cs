using Godot;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>Pre-match presentation only. Match rules and seat assignment remain engine-owned.</summary>
public partial class SetupScreen : VBoxContainer
{
    private DeckLibrary _library = null!;
    private DeckConstructionService _service = null!;
    private TextureCache _textures = null!;
    private Label _name = null!, _summary = null!, _status = null!;
    private SetupDeckArtwork _self = null!;
    private VBoxContainer _advanced = null!;
    internal Button StartButton = null!;
    internal Button DetailsButton = null!;
    internal Button BackButton = null!, ChooseDeckButton = null!, RoomButton = null!;
    public event Action? StartRequested;
    public event Action? ChooseDeckRequested;
    public event Action? BackRequested;

    /// <summary>进入作战房间（拉起服务器实例，拉人分配座位）。</summary>
    public event Action? RoomRequested;

    /// <summary>从局域网房间列表点「加入」（M4 主流程入口）。</summary>
    public event Action<string, int>? RoomJoinRequested;
    private static readonly Color Paper = new("dbd2bb"), PaperInk = new("30382f"), PaperDim = new("58604e");
    private bool ReadyToPlay => _library.ActiveDeck is { } deck && _library.LoadError is null && _service.Validate(deck).Count == 0;

    // ── 局域网房间发现（M4）：监听常开，列表实时刷新 ──────────────
    private RoomListener? _listener;
    private readonly Dictionary<string, DiscoveredRoom> _discovered = new(StringComparer.Ordinal);
    private VBoxContainer _roomList = null!;
    private double _roomClock;

    public void Initialize(DeckLibrary library, DeckConstructionService service, TextureCache textures)
    {
        _library = library; _service = service; _textures = textures;
        AddThemeConstantOverride("separation", 16);
        var header = new HBoxContainer(); AddChild(header);
        var title = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeConstantOverride("separation", 2); header.AddChild(title);
        title.AddChild(UiStyles.Label("作战准备", 32, UiStyles.MenuInk));
        title.AddChild(UiStyles.Label("确认卡组，准备出击。", 14, new Color("c1bca9")));
        var back = UiStyles.Button("← 返回指挥部", () => BackRequested?.Invoke());
        BackButton = back;
        back.CustomMinimumSize = new(160, 44); back.SizeFlagsVertical = SizeFlags.ShrinkCenter; header.AddChild(back);

        var field = new SetupMapArtwork(); field.Initialize(textures);
        field.SizeFlagsVertical = SizeFlags.ExpandFill; field.CustomMinimumSize = new(0, 300); AddChild(field);
        var inset = new MarginContainer(); field.AddChild(inset); inset.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var edge in new[] { "left", "right" }) inset.AddThemeConstantOverride("margin_" + edge, 44);
        foreach (var edge in new[] { "top", "bottom" }) inset.AddThemeConstantOverride("margin_" + edge, 18);
        var versus = new HBoxContainer(); versus.AddThemeConstantOverride("separation", 24); inset.AddChild(versus);
        var selfColumn = CardColumn(versus, "你的部队", false, out _self);
        _name = CenterLabel("", 24, UiStyles.MenuInk); _name.MaxLinesVisible = 2;
        _name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; selfColumn.AddChild(_name);
        _summary = CenterLabel("", 14, new Color("c1c4b0")); selfColumn.AddChild(_summary);
        var separator = new VBoxContainer { CustomMinimumSize = new(76, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        separator.AddChild(CenterLabel("对阵", 20, UiStyles.Gold));
        versus.AddChild(separator);
        var enemyColumn = CardColumn(versus, "对手", true, out _);
        enemyColumn.AddChild(CenterLabel("内置对手", 24, UiStyles.MenuInk));
        enemyColumn.AddChild(CenterLabel("本地对局 · 自动行动", 14, new Color("c1c4b0")));

        var briefing = new PanelContainer(); AddChild(briefing);
        var paperStyle = UiStyles.Box(Paper, new Color("a69d83"), 0, 22);
        paperStyle.BorderWidthTop = 3; briefing.AddThemeStyleboxOverride("panel", paperStyle);
        var columns = new HBoxContainer(); columns.AddThemeConstantOverride("separation", 32); briefing.AddChild(columns);
        var notes = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        notes.AddThemeConstantOverride("separation", 8); columns.AddChild(notes);
        notes.AddChild(UiStyles.Label("出击简报", 20, PaperInk));
        notes.AddChild(UiStyles.Label("先手随机决定  /  使用当前卡组进入战场", 15, PaperDim));
        _status = UiStyles.Label("", 15, PaperInk); _status.AutowrapMode = TextServer.AutowrapMode.WordSmart; notes.AddChild(_status);
        var details = UiStyles.Button("对局详情  ＋", () => { }); details.ToggleMode = true;
        DetailsButton = details;
        details.Alignment = HorizontalAlignment.Left; details.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        details.AddThemeColorOverride("font_color", PaperDim); details.AddThemeColorOverride("font_hover_color", PaperInk);
        details.AddThemeColorOverride("font_pressed_color", PaperInk);
        details.AddThemeColorOverride("font_hover_pressed_color", PaperInk);
        details.AddThemeColorOverride("font_focus_color", PaperInk);
        foreach (var state in new[] { "normal", "hover", "pressed" })
            details.AddThemeStyleboxOverride(state, UiStyles.Box(Colors.Transparent, Colors.Transparent, 0, 2));
        notes.AddChild(details);
        _advanced = new VBoxContainer { Visible = false }; notes.AddChild(_advanced);
        var parameters = UiStyles.Label($"内置对手（引擎启发式 AI）：每回合最多 {OrcOpponentDriver.MaxActions} 步，单步预算 {OrcOpponentDriver.ActionBudget.TotalSeconds:0.#} 秒。", 13, PaperDim);
        parameters.AutowrapMode = TextServer.AutowrapMode.WordSmart; _advanced.AddChild(parameters);
        details.Toggled += open => { _advanced.Visible = open; details.Text = open ? "收起详情  －" : "对局详情  ＋"; };
        var actions = new VBoxContainer { CustomMinimumSize = new(256, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        actions.AddThemeConstantOverride("separation", 10); columns.AddChild(actions);
        StartButton = UiStyles.Button("进入战场  →", OnStart); UiStyles.Primary(StartButton);
        StartButton.CustomMinimumSize = new(256, 56); StartButton.AddThemeFontSizeOverride("font_size", 21); actions.AddChild(StartButton);
        var choose = UiStyles.Button("更换卡组", () => ChooseDeckRequested?.Invoke());
        ChooseDeckButton = choose; choose.CustomMinimumSize = new(0, 38); actions.AddChild(choose);
        // 联机入口：拉起服务器实例、拉人、分配座位。与"进入战场"并列，但用描边区分主次。
        RoomButton = UiStyles.Button("组建房间  ＋", () => RoomRequested?.Invoke());
        RoomButton.CustomMinimumSize = new(0, 38);
        RoomButton.AddThemeStyleboxOverride("normal", UiStyles.Box(new("2b3229"), UiStyles.Accent, 12, 10));
        actions.AddChild(RoomButton);
        BuildRoomBrowser();
        Refresh();
    }

    /// <summary>
    /// 局域网房间列表（M4 主流程入口）：监听在进屏起就常开，
    /// 有房就显示"谁开了房、还剩几个座"，点「加入」直接跳房间屏并进房——
    /// 不必先知道 IP，也不必先进房间屏。
    /// </summary>
    private void BuildRoomBrowser()
    {
        _listener = new RoomListener();
        _listener.Received += room => Callable.From(() => NoteDiscoveredRoom(room)).CallDeferred();
        _listener.Start();

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyles.Box(new Color("232b23"), UiStyles.MenuLine, 10, 14));
        AddChild(panel);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        panel.AddChild(column);
        column.AddChild(UiStyles.Label("//  LAN  ·  局域网房间", 12, UiStyles.Accent));
        _roomList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _roomList.AddThemeConstantOverride("separation", 4);
        column.AddChild(_roomList);
        var note = UiStyles.Label("同一网络（Radmin 亦可）里开着的房间会自动出现；也可以进房间后手填地址。", 11, UiStyles.MenuDim);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(note);
        RenderRoomList();
    }

    private void NoteDiscoveredRoom(DiscoveredRoom room)
    {
        _discovered[room.RoomId] = room;
        RenderRoomList();
    }

    public override void _Process(double delta)
    {
        _roomClock += delta;
        if (_roomClock < 1.0) return;
        _roomClock = 0;
        var now = System.Environment.TickCount64;
        var expired = _discovered.Values
            .Where(room => now - room.SeenMilliseconds > RoomDiscovery.Expiry.TotalMilliseconds)
            .Select(room => room.RoomId).ToArray();
        foreach (var id in expired) _discovered.Remove(id);
        if (expired.Length > 0) RenderRoomList();
    }

    private void RenderRoomList()
    {
        if (_roomList is null) return;
        UiStyles.Clear(_roomList);
        if (_discovered.Count == 0)
        {
            _roomList.AddChild(UiStyles.Label("暂时没有发现的房间——让房主先进「作战房间」。", 12, UiStyles.MenuDim));
            return;
        }

        foreach (var room in _discovered.Values.OrderByDescending(room => room.SeenMilliseconds))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var label = UiStyles.Label(
                $"{room.Host}:{room.Port} · 座位 {room.SeatsTaken}/{room.SeatsTotal}", 13, UiStyles.MenuInk);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.ClipText = true;
            row.AddChild(label);
            var join = UiStyles.Button("加入  →", () => RoomJoinRequested?.Invoke(room.Host, room.Port));
            join.CustomMinimumSize = new(84, 30);
            row.AddChild(join);
            _roomList.AddChild(row);
        }
    }

    /// <summary>当前发现列表（验证探针用）。</summary>
    internal DiscoveredRoom[] DiscoveredRooms => [.. _discovered.Values];
    private VBoxContainer CardColumn(HBoxContainer row, string heading, bool enemy, out SetupDeckArtwork art)
    {
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 7); row.AddChild(column);
        column.AddChild(CenterLabel(heading, 14, UiStyles.Gold));
        art = new SetupDeckArtwork { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 170) };
        art.Initialize(_textures, enemy); column.AddChild(art); return column;
    }
    private static Label CenterLabel(string text, int size, Color color) => new Label
    {
        Text = text, HorizontalAlignment = HorizontalAlignment.Center,
        LabelSettings = new LabelSettings { FontSize = size, FontColor = color, ShadowColor = new Color("101710b0"), ShadowSize = 2, ShadowOffset = new(0, 2) }
    };
    public void Refresh()
    {
        var deck = _library.ActiveDeck;
        _name.Text = deck?.Name ?? "尚未选择卡组"; _name.TooltipText = _name.Text;
        _summary.Text = deck is null ? "选择一副卡组后出击" : $"{deck.CardIds.Length + 1} 张（含总部） · {(deck.Options.UnrestrictedNations ? "混编" : deck.Options.MainNation)}";
        var errors = deck is null ? null : _service.Validate(deck);
        _status.Text = _library.LoadError ?? (deck is null ? "请先选择对战卡组。" : errors!.Count == 0 ? "● 卡组准备就绪" : errors[0]);
        StartButton.Text = ReadyToPlay ? "进入战场  →" : "选择卡组  →";
        _self.Modulate = deck is null ? new Color(.65f, .65f, .65f) : Colors.White;
    }
    private void OnStart() { if (ReadyToPlay) StartRequested?.Invoke(); else ChooseDeckRequested?.Invoke(); }
}

public partial class SetupMapArtwork : Control
{
    private Texture2D? _map;
    public void Initialize(TextureCache textures)
    {
        _map = textures.Get("res://proto/assets/battle/hq-map-v2.png");
        MouseFilter = MouseFilterEnum.Ignore; Resized += QueueRedraw;
    }
    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        DrawRect(rect, new("28382d"));
        if (_map is not null) DrawTextureRectRegion(_map, rect, new Rect2(0, 250, 1086, 700), new Color(.47f, .51f, .38f));
        DrawRect(rect, new Color(.055f, .11f, .075f, .72f));
        for (var x = 36f; x < Size.X; x += 80) DrawLine(new(x, 0), new(x, Size.Y), new("ced4a609"));
        for (var y = 36f; y < Size.Y; y += 80) DrawLine(new(0, y), new(Size.X, y), new("ced4a609"));
        DrawRect(rect, new("8c937650"), false, 1);
        var center = Size / 2;
        DrawArc(center, 60, 0, Mathf.Tau, 80, new("b5ad7b30"), 1, true);
        DrawLine(new(center.X, 24), new(center.X, center.Y - 76), new("b5ad7b30"), 1, true);
        DrawLine(new(center.X, center.Y + 76), new(center.X, Size.Y - 24), new("b5ad7b30"), 1, true);
    }
}

public partial class SetupDeckArtwork : Control
{
    private Texture2D? _back;
    private bool _enemy;
    public void Initialize(TextureCache textures, bool enemy)
    {
        _back = textures.Get("res://proto/assets/battle/card-back-v2.png"); _enemy = enemy;
        MouseFilter = MouseFilterEnum.Ignore; Resized += QueueRedraw;
    }
    public override void _Draw()
    {
        var height = Math.Min(295, Size.Y - 24); var width = height / 1.45f;
        if (height <= 0) return;
        DrawSetTransform(Size / 2, _enemy ? .065f : -.065f);
        var card = new Rect2(-width / 2, -height / 2, width, height);
        DrawRect(new(card.Position + new Vector2(10, 14), card.Size), new("050b08a0"));
        for (var i = 3; i >= 1; i--) DrawRect(new(card.Position + new Vector2(i * 2, i * 3), card.Size), new("948c71"));
        if (_back is not null) DrawTextureRect(_back, card, false, _enemy ? new Color(.68f, .73f, .67f) : new Color(.96f, .96f, .86f));
        else DrawRect(card, new("48583c"));
        DrawSetTransform(Vector2.Zero);
    }
}
