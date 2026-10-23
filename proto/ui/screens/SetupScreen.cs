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
    public event Action? EditDeckRequested;
    public event Action? ResumeRequested;
    public event Action? BackRequested;

    /// <summary>进入作战房间（拉起服务器实例，拉人分配座位）。</summary>
    public event Action? RoomRequested;
    public event Action? ReplayRequested;

    /// <summary>从局域网房间列表点「加入」（M4 主流程入口）。</summary>
    public event Action<string, int>? RoomJoinRequested;
    public event Action<string, int>? RoomSpectateRequested;
    private static readonly Color Paper = new("dbd2bb"), PaperInk = new("30382f"), PaperDim = new("58604e");
    private bool ReadyToPlay => _library.ActiveDeck is { } deck && _library.LoadError is null && _service.Validate(deck).Count == 0;

    // ── 局域网房间发现（M4）：监听常开，列表实时刷新 ──────────────
    private RoomListener? _listener;
    private readonly System.Collections.Concurrent.ConcurrentQueue<DiscoveredRoom> _incomingRooms = new();
    private readonly Dictionary<string, DiscoveredRoom> _discovered = new(StringComparer.Ordinal);
    private VBoxContainer _roomList = null!;
    private double _roomClock;
    internal Func<string?>? HiddenRoomId { get; set; }

    public void Initialize(DeckLibrary library, DeckConstructionService service, TextureCache textures)
    {
        _library = library; _service = service; _textures = textures;
        BuildSelection(); Refresh();
    }
    /// <summary>
    /// 局域网房间列表（M4 主流程入口）：监听在进屏起就常开，
    /// 有房就显示"谁开了房、还剩几个座"，点「加入」直接跳房间屏并进房——
    /// 不必先知道 IP，也不必先进房间屏。
    /// </summary>
    private void BuildRoomBrowser(VBoxContainer parent)
    {
        _listener = new RoomListener();
        _listener.Received += room => _incomingRooms.Enqueue(room);
        _listener.Start();

        var panel = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", MenuMaterials.Panel(new Color("23282a"),20));
        parent.AddChild(panel);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        panel.AddChild(column);
        var heading = new HBoxContainer(); column.AddChild(heading);
        var title = UiStyles.Label("好友的房间", 24, UiStyles.Gold); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; heading.AddChild(title);
        heading.AddChild(UiStyles.Button("使用房间地址…", OpenAddressDialog));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; column.AddChild(scroll);
        _roomList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _roomList.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_roomList);
        var note = UiStyles.Label("同一局域网中的房间会出现在这里。找不到好友时，可使用房间地址连接。", 11, UiStyles.MenuDim);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(note);
        RenderRoomList();
    }

    private void NoteDiscoveredRoom(DiscoveredRoom room)
    {
        _discovered[room.RoomId] = room;
        RenderRoomList();
    }

    public override void _ExitTree() { _listener?.Dispose(); _listener = null; }
    public override void _Process(double delta)
    {
        while (_incomingRooms.TryDequeue(out var discovered)) NoteDiscoveredRoom(discovered);
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
        var rooms = _discovered.Values.Where(room => room.RoomId != HiddenRoomId?.Invoke()).OrderByDescending(room => room.SeenMilliseconds).ToArray();
        if (rooms.Length == 0)
        {
            var empty = new VBoxContainer { CustomMinimumSize = new(0, 280), Alignment = BoxContainer.AlignmentMode.Center }; empty.AddThemeConstantOverride("separation",16); _roomList.AddChild(empty);
            var title = UiStyles.Label("等待好友的房间", 25, UiStyles.MenuInk); title.HorizontalAlignment = HorizontalAlignment.Center; empty.AddChild(title);
            var help = UiStyles.Label("好友创建房间后，会自动出现在这里。\n你也可以在右侧创建房间，或使用好友分享的地址。", 15, UiStyles.MenuDim);
            help.HorizontalAlignment = HorizontalAlignment.Center; help.AutowrapMode = TextServer.AutowrapMode.WordSmart; empty.AddChild(help);
            return;
        }

        foreach (var room in rooms)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var label = UiStyles.Label(
                $"{(string.IsNullOrWhiteSpace(room.Name) ? "好友的房间" : room.Name)} · {room.SeatsTaken}/{room.SeatsTotal} 人", 13, UiStyles.MenuInk);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.ClipText = true;
            row.AddChild(label);
            var join = UiStyles.Button(room.SeatsTaken >= room.SeatsTotal ? "已满" : "加入 →", () => RequestJoin(room.Host, room.Port));
            join.Disabled = room.SeatsTaken >= room.SeatsTotal;
            join.CustomMinimumSize = new(84, 30);
            row.AddChild(join);
            row.AddChild(UiStyles.Button("观战", () => RoomSpectateRequested?.Invoke(room.Host, room.Port)));
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
        RefreshSelection();
    }
    private (string Host, int Port)? _pendingJoin;
    internal bool OnlineMode => _online;
    internal bool HasPendingJoin => _pendingJoin.HasValue;
    internal void RequestJoin(string host, int port)
    {
        if (OnlineReady) { _pendingJoin = null; RoomJoinRequested?.Invoke(host, port); }
        else { _pendingJoin = (host, port); ChooseDeckRequested?.Invoke(); }
    }
    private void OnStart()
    {
        if (!ReadyToPlay || (_online && !OnlineReady)) { ChooseDeckRequested?.Invoke(); return; }
        if (_pendingJoin is { } pending) { RequestJoin(pending.Host, pending.Port); return; }
        if (_online) RoomRequested?.Invoke(); else StartRequested?.Invoke();
    }
    private void OpenAddressDialog()
    {
        var dialog = new ConfirmationDialog { Title = "连接好友房间", OkButtonText = "加入房间", CancelButtonText = "取消", DialogHideOnOk = false, MinSize = new(460, 180) }; AddChild(dialog);
        var form = new VBoxContainer(); dialog.AddChild(form);
        form.AddChild(UiStyles.Label("粘贴好友分享的地址", 16, UiStyles.MenuInk));
        var address = new LineEdit { PlaceholderText = "例如 10.147.20.5:4717" }; form.AddChild(address);
        var error = UiStyles.Label("", 13, UiStyles.Gold); form.AddChild(error);
        void Connect(bool watch)
        {
            if (!RoomScreen.TryParseEndpoint(address.Text, out var host, out var port)) { error.Text = "请输入有效的地址与端口。"; return; }
            dialog.QueueFree();
            if (watch) RoomSpectateRequested?.Invoke(host, port); else RequestJoin(host, port);
        }
        dialog.AddButton("观战", true, "watch"); dialog.CustomAction += action => { if (action == "watch") Connect(true); };
        dialog.Confirmed += () => Connect(false); dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered(); address.GrabFocus();
    }
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
