using Godot;
using Kards.Ui.OrcBridge;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>房间申请与房主审批界面；生命周期由管理器控制，普通业务连接没有管理权限。</summary>
public partial class RoomScreen : VBoxContainer
{
    public CosmeticLibrary? Cosmetics { get; set; }
    private string _packsRoot = "", _sandbox = "";
    private ServerLauncher? _server;
    private RemoteMatchFeed? _feed;
    private LobbyDeck.Result? _deck;
    private ConstructedDeck? _deckPresentation;
    private bool _starting;
    private int _lifecycle;
    private bool _connecting;
    internal bool AllowSampleDeck { get; set; }
    internal bool FixedDeckForVerification { get; set; }
    private int? _aiSeat;
    private bool _reconnectObserver;
    private Button? _admissions;
    private double _poll;
    private int _guestPort;
    private bool _guest;
    private string? _lastError;

    // ── 断线重连（M3）──────────────────────────────────────────────
    // 服务端语义：对局态座位不因断线释放（MarkDisconnected 只翻 Connected），
    // room.resume 凭令牌重挂、不限来源；同令牌重复 room.claim 也幂等。
    // 所以 UI 只需记住 {地址, 座位, seatToken}，断线后循环 重连→resume→RefreshView。
    private bool _reconnecting;
    private bool _closing;
    private Task? _closeTask;
    private string _reconnectHost = "";
    private int _reconnectPort, _reconnectSeat;
    private string? _reconnectToken;
    private int _reconnectAttempts;
    private double _retryClock;
    /// <summary>重连单飞守卫：探针 await 一次 + _Process 定时器一次会并发进来，后写的 feed 会覆盖先写的（M3 flake 真凶）。</summary>
    private int _reconnectInFlight;

    // ── 大厅发现（M4）──────────────────────────────────────────────
    // 房主起服后宣告（RoomAnnouncer），客人监听 UDP 去重渲染（RoomListener）。
    private bool _exiting;
    private RoomAnnouncer? _announcer;
    private string? _ownRoomId;

    /// <summary>验证用：保证这张卡在卡组里（等随机发牌等到天荒地老时可预期地触发选靶）。</summary>
    private string? _requiredCard;
    /// <summary>座位占用（由快照更新；读它的地方可能在非主线程，别直接问控件）。</summary>
    private readonly bool[] _occupied = new bool[2];

    private Label _status = null!, _endpoint = null!, _pool = null!, _room = null!, _ping = null!;
    private Label _checkSeat0 = null!, _checkSeat1 = null!, _checkPool = null!, _hint = null!;
    private RoomSeatCard _seat0 = null!, _seat1 = null!;
    private Button _addAi = null!, _invite = null!, _start = null!, _back = null!;
    private string _joinEndpoint = "";

    /// <summary>请求返回上一屏（需要释放服务器实例）。</summary>
    public event Action? BackRequested;

    /// <summary>开局成功，请求切到战场。</summary>
    public event Action? MatchStarted;

    /// <summary>当前连接（未就绪＝null）。</summary>
    public RemoteMatchFeed? Feed => _feed;

    /// <summary>本机监听端口（作为客人加入时＝对方端口；未连接＝0）。</summary>
    public int Port => _server?.Port ?? _guestPort;

    /// <summary>本端是房主（起服务器）还是客人（连别人的）。</summary>
    public bool IsGuest => _guest;
    internal Func<ConstructedDeck?>? DeckPresentationSource { get; set; }
    internal string? HostedRoomId => _ownRoomId;

    public void Initialize(string packsRoot, string sandboxDirectory)
    {
        _packsRoot = packsRoot;
        _sandbox = sandboxDirectory;

        AddThemeConstantOverride("separation", 14);
        BuildHeader();
        BuildBody();
        BuildFooter();
        foreach(var panel in FindChildren("*", "PanelContainer", true, false).OfType<PanelContainer>())
            panel.AddThemeStyleboxOverride("panel",MenuMaterials.Panel(new Color("22272a"),12));
        foreach(var button in FindChildren("*", "Button", true, false).OfType<Button>())
            MenuMaterials.Button(button,ReferenceEquals(button,_start),6);
    }

    // ── 布局 ─────────────────────────────────────────────────────────

    private void BuildHeader()
    {
        var header = new HBoxContainer();
        AddChild(header);

        var title = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeConstantOverride("separation", 2);
        header.AddChild(title);
        // 眉标沿用项目语言：双斜线 + 宽的英文小字。
        title.AddChild(UiStyles.Label("//  ROOM  ·  局域网对局", 12, UiStyles.Accent));
        title.AddChild(UiStyles.Label("作战房间", 30, UiStyles.MenuInk));
        title.AddChild(UiStyles.Label("确认双方准备情况，邀请好友并开始对战。", 14, UiStyles.MenuDim));

        _back = UiStyles.Button("离开房间", () => BackRequested?.Invoke());
        _back.CustomMinimumSize = new(160, 44);
        _back.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        header.AddChild(_back);
    }

    private void BuildBody()
    {
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 16);
        AddChild(body);

        body.AddChild(BuildInstancePanel());

        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 14);
        body.AddChild(right);

        var seats = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        seats.AddThemeConstantOverride("separation", 14);
        right.AddChild(seats);

        _seat0 = new RoomSeatCard();
        _seat0.Initialize(0);
        _seat0.SetAccess(false);
        _seat0.FillRequested += () => _ = FillSeatAsync(0);
        seats.AddChild(_seat0);

        _seat1 = new RoomSeatCard();
        _seat1.Initialize(1);
        _seat1.SetAccess(false);
        _addAi = _seat1.FillButton;
        _seat1.FillRequested += () => _ = FillSeatAsync(1);
        seats.AddChild(_seat1);

        right.AddChild(BuildChecks());
    }

    private Button _reapply = null!;
    private PanelContainer BuildInstancePanel()
    {
        var panel = new PanelContainer { CustomMinimumSize = new(300, 0) };
        panel.AddThemeStyleboxOverride("panel", MenuMaterials.Panel(new Color("22272a"), 20));
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 14); panel.AddChild(column);
        column.AddChild(UiStyles.Label("与好友一起作战", 22, UiStyles.Gold));
        _status = UiStyles.Label("正在准备房间…", 14, UiStyles.MenuInk); _status.AutowrapMode = TextServer.AutowrapMode.WordSmart; column.AddChild(_status);
        _invite = UiStyles.Button("复制邀请地址", CopyInvite); column.AddChild(_invite);
        _admissions = UiStyles.Button("入场申请", () => _ = ShowAdmissionsAsync()); column.AddChild(_admissions);
        _reapply = UiStyles.Button("重新申请入场", () => _ = ReapplyAsync()); _reapply.Visible = false; column.AddChild(_reapply);
        var help = UiStyles.Label("邀请好友加入，批准入场后即可开始。也可以在对手位置添加电脑对手。", 14, UiStyles.MenuDim);
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart; column.AddChild(help);
        column.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        var details = new VBoxContainer { Visible = false }; column.AddChild(UiStyles.Button("连接详情", () => details.Visible = !details.Visible)); column.AddChild(details);
        _endpoint = UiStyles.Label("—", 14, UiStyles.MenuInk); _endpoint.AutowrapMode = TextServer.AutowrapMode.WordSmart; details.AddChild(_endpoint);
        _pool = UiStyles.Label("", 12, UiStyles.MenuDim); _pool.AutowrapMode = TextServer.AutowrapMode.WordSmart; details.AddChild(_pool);
        _room = UiStyles.Label("", 12, UiStyles.MenuDim); details.AddChild(_room);
        _ping = UiStyles.Label("延迟 —", 12, UiStyles.MenuDim); details.AddChild(_ping);
        _hint = UiStyles.Label("", 14, UiStyles.Gold); _hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; column.AddChild(_hint);
        return panel;
    }

    private PanelContainer BuildChecks()
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyles.Box(new Color("232b23"), UiStyles.MenuLine, 10, 16));

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        panel.AddChild(column);
        column.AddChild(UiStyles.Label("开局检查", 12, UiStyles.Accent));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 28);
        column.AddChild(row);
        _checkSeat0 = UiStyles.Label("等待房主", 13, UiStyles.MenuDim); row.AddChild(_checkSeat0);
        _checkSeat1 = UiStyles.Label("等待对手", 13, UiStyles.MenuDim); row.AddChild(_checkSeat1);
        _checkPool = UiStyles.Label("等待卡组", 13, UiStyles.MenuDim); row.AddChild(_checkPool);

        return panel;
    }

    private void BuildFooter()
    {
        var footer = new HBoxContainer();
        AddChild(footer);

        var note = UiStyles.Label("双方就绪后，由房主开始对战。", 12, UiStyles.MenuDim);
        note.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        note.VerticalAlignment = VerticalAlignment.Center;
        footer.AddChild(note);

        _start = UiStyles.Button("开始对局  →", () => _ = StartAsync());
        UiStyles.Primary(_start);
        _start.CustomMinimumSize = new(236, 52);
        _start.AddThemeFontSizeOverride("font_size", 20);
        _start.Disabled = true;
        footer.AddChild(_start);
    }

    // ── 生命周期 ─────────────────────────────────────────────────────

    /// <summary>进入房间：起实例 → 连接 → 自动占本座 → 读快照。可重复调用（已就绪则只刷新）。</summary>
    public async void Open() => await OpenAsync();

    /// <summary>同上，可 await 版本（验证探针用）。</summary>
    public async Task OpenAsync()
    {
        if (_closing || _connecting) return;
        if (_feed is not null)
        {
            await RefreshAsync();
            return;
        }

        var lifecycle = _lifecycle; _connecting = true;
        try
        {
            Ui(() => Say("正在准备卡组…"));
            // 有必带卡（验证探针）时强制走自动组牌，否则优先用玩家自己构筑的卡组。
            _deck = (string.IsNullOrEmpty(_requiredCard) ? TryActiveDeck() : null)
                    ?? (AllowSampleDeck
                        || !string.IsNullOrEmpty(_requiredCard) ? LobbyDeck.Build(_packsRoot, requiredLocalId: _requiredCard)
                        : throw new InvalidOperationException(_deckNote ?? "请先在作战准备页选择可用的对战卡组。"));

            _deckPresentation = _deck.PackageId == "player" ? DeckPresentationSource?.Invoke()?.Snapshot() : null;
            Ui(() => Say("正在创建房间…"));
            var packs = Directory.EnumerateDirectories(_packsRoot)
                .Where(directory => File.Exists(Path.Combine(directory, "manifest.json")))
                .OrderBy(directory => directory, StringComparer.Ordinal)
                .ToArray();
            var server = await ServerLauncher.StartAsync(packs, _sandbox, aiSeat: _aiSeat, aiDeck: _aiSeat.HasValue ? _deck.CardIds : null,
                fixedDeckForVerification: FixedDeckForVerification);
            if (lifecycle != _lifecycle) { await server.DisposeAsync(); return; }
            _server = server;
            Ui(() => Say($"● 就绪 · 端口 {_server.Port}"));

            var feed = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
            if (lifecycle != _lifecycle) { await feed.DisposeAsync(); return; }
            _feed = feed;
            _server.Configure(_feed);
            // 回调来自协议线程，控件只能主线程写——统一 marshal。
            feed.ErrorRaised += message => Ui(() => { if (_feed == feed) { _lastError = message; NoteError(message); } });
            HookPing(_feed);

            // 房主＝local 座位；服务端只认 Kind==Local 的房间主人，否则开局被拒。
            var token = await _feed.ClaimSeatAsync(0, "local", "房主", _deck.CardIds);
            if (lifecycle != _lifecycle) return;
            if (token is null) throw new InvalidOperationException("未能加入自己的房间，请重试。");

            var admitted = await _server.AdmitAsync(_feed.ConnectionId);
            if (!admitted.Ok) throw new InvalidOperationException(admitted.Message ?? admitted.Code);
            if (!await _feed.WaitForAdmissionAsync()) throw new InvalidOperationException("未收到房主入场许可。");
            if (lifecycle != _lifecycle) return;
            RememberSession("127.0.0.1", _server.Port, 0, _feed.SeatToken);
            await RefreshAsync();
        }
        catch (Exception error)
        {
            GD.PushError("[room] 进入房间失败：" + error.Message);
            if (lifecycle == _lifecycle)
            {
                await CloseAsync();
                Ui(() => { Say("✗ " + error.Message); _hint.Text = error.Message; });
            }
        }
        finally { if (lifecycle == _lifecycle) _connecting = false; }
    }

    /// <summary>离开房间：释放连接与实例（终局后服务器不会自己退出）。</summary>
    public async void Close() => await CloseAsync();
    public Task CloseAsync() => _closeTask is { IsCompleted: false } pending ? pending : _closeTask = CloseCoreAsync();
    private async Task CloseCoreAsync()
    {
        _lifecycle++;
        _connecting = false;
        _closing = true;
        _reconnecting = false;
        _announcer?.Dispose();
        _announcer = null;
        _ownRoomId = null;
        var feed = _feed; _feed = null;
        var server = _server; _server = null;
        if (feed is not null) { try { await feed.DisposeAsync(); } catch (Exception) { } }
        if (server is not null) { try { await server.DisposeAsync(); } catch (Exception) { } }
        _aiSeat = null; _guest = false; _guestPort = 0;
        _deck = null; _deckPresentation = null; _deckNote = null; _lastError = null; _starting = false;
        Array.Clear(_occupied);
        _reconnectToken = null; _reconnectObserver = false;
        _closing = false;
        Ui(() => Say("已离开房间"));
    }

    public override void _ExitTree()
    {
        _lifecycle++; _exiting = true; _closing = true;
        _announcer?.Dispose();
        if (_feed is { } feed) _ = feed.DisposeAsync();
        if (_server is { } server) _ = server.DisposeAsync();
        _feed = null; _server = null;
    }
    public override void _Process(double delta)
    {
        if (_reconnecting)
        {
            _retryClock += delta;
            if (_retryClock < 2.5) return;
            _retryClock = 0;
            _ = TryReconnectAsync();
            return;
        }

        if (_feed is null) return;
        _poll += delta;
        if (_poll < 1.5) return;
        _poll = 0;
        _ = RefreshAsync();
    }

    // ── 操作 ─────────────────────────────────────────────────────────

    /// <summary>主线程执行（协议回调在别的线程，控件只能在主线程写）。</summary>
    private void Ui(Action action)
    {
        if (_exiting) return;
        if (!IsInsideTree()) { action(); return; }
        Callable.From(action).CallDeferred();
    }

    private sealed record RoomSnapshot(
        string RoomId, string Policy, bool PolicyKnown, string Fingerprint, IReadOnlyList<string> Addresses,
        IReadOnlyList<(int Seat, string Kind, string? Name, bool Connected)> Seats);

    private async Task RefreshAsync()
    {
        if (_feed is not { } feed) return;
        var lifecycle = _lifecycle;
        try
        {
            var info = await feed.RoomInfoAsync();
            if (_feed != feed || lifecycle != _lifecycle) return;
            if (!info.Ok) { Ui(() => Say("✗ " + info.Code)); return; }
            if (info.Details is not System.Text.Json.Nodes.JsonObject details) return;

            // 解析在任意线程做，写控件统一回主线程。
            var snapshot = Parse(details);
            Ui(() => { if (_feed == feed && lifecycle == _lifecycle) Apply(snapshot); });
            if (_server is not null && !_guest)
            {
                var applicants = await _server.AdminAsync("connections.list");
                var count = (applicants.Details?["connections"] as System.Text.Json.Nodes.JsonArray)?.Count(c => c?["state"]?.GetValue<string>() == "pending") ?? 0;
                Ui(() => { if (_admissions is not null) _admissions.Text = count > 0 ? $"入场申请  ·  {count} 人待批准" : "入场申请"; });
            }
        }
        catch (Exception error)
        {
            if (_feed != feed || lifecycle != _lifecycle) return;
            // 轮询异常＝连接层问题（服务端错误走 result.Ok=false 分支），转入自动重连。
            if (OnConnectionIssue(error.Message))
            {
                Ui(() => Say("✗ 连接断开 · 正在自动重连…"));
                return;
            }

            Ui(() => Say("✗ " + error.Message));
        }
    }

    private RoomSnapshot Parse(System.Text.Json.Nodes.JsonObject details)
    {
        var addresses = new List<string>();
        if (details["addresses"] is System.Text.Json.Nodes.JsonArray list)
            foreach (var entry in list)
            {
                var ip = entry?["ip"]?.GetValue<string>();
                var port = entry?["port"]?.GetValue<int>() ?? Port;
                if (!string.IsNullOrWhiteSpace(ip)) addresses.Add($"{ip}:{port}");
            }

        var seats = new List<(int, string, string?, bool)>();
        if (details["seats"] is System.Text.Json.Nodes.JsonArray seatList)
            foreach (var node in seatList)
                seats.Add((
                    node?["seat"]?.GetValue<int>() ?? -1,
                    node?["occupied"]?.GetValue<bool>() == true ? node?["kind"]?.GetValue<string>() ?? "remote" : "",
                    node?["displayName"]?.GetValue<string>(),
                    node?["state"]?.GetValue<string>() == "admitted"));

        // 卡池状态：上游 §6.1 是 pool{generation,fingerprint,packPolicy}，老引擎只有顶层 packFingerprint。
        // 都没有时不猜——PolicyKnown=false 让开局检查保持乐观（与旧行为一致），但界面显示"—"而不是假话。
        var pool = details["pool"];
        var policy = pool?["packPolicy"]?.GetValue<string>()
                     ?? details["packPolicy"]?.GetValue<string>()
                     ?? details["policy"]?.GetValue<string>();
        var fingerprint = pool?["fingerprint"]?.GetValue<string>()
                          ?? details["packFingerprint"]?.GetValue<string>()
                          ?? "";

        return new RoomSnapshot(
            details["roomId"]?.GetValue<string>() ?? "—",
            string.IsNullOrWhiteSpace(policy) ? "loaded" : policy!,
            !string.IsNullOrWhiteSpace(policy),
            fingerprint,
            addresses, seats);
    }

    private void Apply(RoomSnapshot snapshot)
    {
        _room.Text = $"房间 {snapshot.RoomId}";
        // 房主：把"我这有房间"广播出去（M4）；座位占用随快照刷新。
        if (!_guest && snapshot.RoomId.Length > 0 && snapshot.RoomId != "—")
        {
            if (_ownRoomId != snapshot.RoomId)
            {
                _ownRoomId = snapshot.RoomId;
                _announcer?.Dispose();
                _announcer = new RoomAnnouncer(snapshot.RoomId, _server?.Port ?? Port, "OrC 作战房间");
            }
            _announcer?.UpdateSeats(snapshot.Seats.Count(s => s.Kind.Length > 0), 2);
        }

        // 服务端没报卡池状态时显示"—"（不编一个"loaded"出来）；指纹只在真拿到 8 位以上时才显示。
        var poolParts = new List<string> { $"卡池 {_deck?.PackageId ?? "—"}" };
        if (snapshot.PolicyKnown && snapshot.Policy.Length > 0) poolParts.Add(snapshot.Policy);
        if (snapshot.Fingerprint.Length >= 8) poolParts.Add(snapshot.Fingerprint[..8]);
        if (_deckNote is { Length: > 0 } note) poolParts.Add(note);
        _pool.Text = string.Join(" · ", poolParts);
        // 客人（房主＝false）看不到对方网卡列表——那对"加入方"没意义，显示自己连的是哪。
        _endpoint.Text = _guest
            ? $"已连接 {_joinEndpoint.Trim()}"
            : snapshot.Addresses.Count > 0
                ? FormatAddresses(snapshot.Addresses)
                : $"127.0.0.1:{Port}";

        foreach (var (seat, kind, name, connected) in snapshot.Seats)
        {
            if (seat is not (0 or 1)) continue;
            _occupied[seat] = kind.Length > 0;
            var deckLabel = _deck is null || _feed is null || seat != _feed.Seat || _feed.IsObserver ? null : $"卡组 {_deck.CardIds.Count} 张 · {_deck.Faction}";
            var card = seat == 0 ? _seat0 : _seat1;
            card.SetState(kind, kind == "ai" ? "电脑对手" : name, connected, deckLabel);
            var self = _feed is { IsObserver: false } && seat == _feed.Seat;
            card.SetDeck(self ? _deckPresentation : null, self);
        }

        var ready0 = snapshot.Seats.Any(s => s.Seat == 0 && s.Connected);
        var ready1 = snapshot.Seats.Any(s => s.Seat == 1 && s.Connected);
        // 服务端没报卡池状态＝未知，不因此拦住开局（旧行为）；报告了才按 loaded 判定。
        var poolOk = !snapshot.PolicyKnown || snapshot.Policy == "loaded";

        SetCheck(_checkSeat0, ready0, "房主就绪");
        SetCheck(_checkSeat1, ready1, "对手就绪");
        SetCheck(_checkPool, poolOk, "卡组已提交");

        _start.Disabled = _guest || !(ready0 && ready1 && poolOk);
        _start.Text = _guest ? "等待房主开局…" : "开始对局  →";
        _addAi.Disabled = _guest || (ready0 && ready1);
        _seat0.SetAccess(false); _seat1.SetAccess(!_guest && !ready1);
        _invite.Visible = !_guest; _admissions!.Visible = !_guest;
        _reapply.Visible = _feed?.AdmissionState is "rejected" or "revoked";
        _start.Visible = _feed?.IsObserver != true;
        Say(_guest ? (_feed?.AdmissionState == "pending" ? "等待房主批准入场…" : _feed?.IsObserver == true ? "正在观战" : "已加入好友的房间") : ready0 && ready1 ? "双方已就绪，可以开始对战" : "等待好友加入");
    }

    /// <summary>
    /// 地址只展示前 3 条：已知虚拟局域网段（Radmin 26. / ZeroTier 28-30. 等）优先，其余折成"+N"。
    /// 一屏列 7 个 IP 会把左栏撑满，而"发给对方"实际只需要一个能用的。
    /// 换组网软件无需改这里——新软件的虚拟网段只要加进 <see cref="AddressPriority"/> 即可。
    /// </summary>
    private static string FormatAddresses(IReadOnlyList<string> addresses)
    {
        var ordered = addresses
            .OrderBy(AddressPriority)
            .ThenBy(address => address.StartsWith("192.168.", StringComparison.Ordinal) ? 1 : 0)
            .ToList();
        var lines = ordered.Take(3).ToList();
        if (ordered.Count > 3) lines.Add($"+{ordered.Count - 3} 个其它网卡");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// 地址优先级：0=虚拟局域网段，1=其它私有网段，2=公网，9=链路本地/环回（不可用）。
    /// 这样无论底层是 Radmin、ZeroTier 还是 n2n，"发给对方"都优先推虚拟局域网地址。
    /// </summary>
    private static int AddressPriority(string address)
    {
        if (address.StartsWith("169.254.", StringComparison.Ordinal) ||
            address.StartsWith("127.", StringComparison.Ordinal)) return 9;
        if (address.StartsWith("26.", StringComparison.Ordinal) ||
            address.StartsWith("28.", StringComparison.Ordinal) ||
            address.StartsWith("29.", StringComparison.Ordinal) ||
            address.StartsWith("30.", StringComparison.Ordinal)) return 0;
        if (address.StartsWith("10.", StringComparison.Ordinal) ||
            address.StartsWith("172.", StringComparison.Ordinal) ||
            address.StartsWith("192.168.", StringComparison.Ordinal)) return 1;
        return 2;
    }

    private static void SetCheck(Label label, bool ok, string what)
    {
        label.Text = ok ? $"✓ {what}" : $"○ 等待{what.Replace("就绪", "").Replace("已提交", "")}";
        label.AddThemeColorOverride("font_color", ok ? UiStyles.Green : UiStyles.MenuDim);
    }

    /// <summary>
    /// 以客人身份加入别人的房间：连对方地址 → 占一个空座（remote）。
    /// 不启动本地服务器——这是"拉人"的另一半，此前只有房主侧。
    /// </summary>
    private async Task JoinAsync()
    {
        if (_closing || _connecting) return;
        if (_feed is not null) { _hint.Text = "已经在房间里了。"; return; }
        if (!TryParseEndpoint(_joinEndpoint, out var host, out var port))
        {
            _hint.Text = "地址格式：IP:端口，例如 10.147.20.5:4717";
            return;
        }

        var lifecycle = _lifecycle; _connecting = true;
        RemoteMatchFeed? connectingFeed = null;
        try
        {
            Ui(() => Say("正在准备卡组…"));
            _deck = TryActiveDeck() ?? (AllowSampleDeck
                ? LobbyDeck.Build(_packsRoot) : throw new InvalidOperationException(_deckNote ?? "请先选择可用卡组。"));
            _deckPresentation = _deck.PackageId == "player" ? DeckPresentationSource?.Invoke()?.Snapshot() : null;

            Ui(() => Say($"○ 连接 {host}:{port}…"));
            _guestPort = port;
            var feed = await RemoteMatchFeed.ConnectAsync(host, port, seat: 1);
            connectingFeed = feed;
            if (lifecycle != _lifecycle) return;
            feed.ErrorRaised += message => Ui(() => { if (_feed == feed) NoteError(message); });
            // 客人也要说真话：把本机实际装载的包报上去，房主侧才有东西可对账。
            // 这不影响装载（服务端只认自己的包集合），只是让"两边卡池不一样"在开局前可见。
            feed.DeclareLocalPackages(PackDeclarations.Scan(_packsRoot));
            HookPing(feed);

            // 两个座位都可能被占：房主通常坐 0，所以先试 1 再试 0。
            string? token = null;
            foreach (var candidate in new[] { 1, 0 })
            {
                token = await feed.ClaimSeatAsync(candidate, "remote", "远端玩家", _deck.CardIds);
                if (lifecycle != _lifecycle) return;
                if (token is not null) break;
            }

            if (token is null)
            {
                await feed.DisposeAsync();
                Ui(() => Say("✗ 加入失败（房间已满或已开局）"));
                return;
            }

            _feed = feed;
            connectingFeed = null;
            _guest = true;
            RememberSession(host, port, feed.Seat, feed.SeatToken);
            feed.AdmissionChanged += state => Ui(() => {
                if (_feed != feed) return;
                if (state == "admitted") RememberSession(host, port, feed.Seat, feed.SeatToken);
                Say(state == "admitted" ? "● 房主已批准加入" : state == "rejected" ? "加入申请已被拒绝" : "入场许可已撤销");
            });

            // 房主开局后服务端会推视图；客人在这一刻自动切战场。
            HookGuestAutoEnter(feed);

            // 客人不能添 AI、也不能开局——那是房主的权限。
            Ui(() => _addAi.Disabled = true);
            await RefreshAsync();
        }
        catch (Exception error)
        {
            Ui(() => { if (lifecycle == _lifecycle) Say("✗ " + error.Message); });
        }
        finally
        {
            if (connectingFeed is not null) await connectingFeed.DisposeAsync();
            if (lifecycle == _lifecycle) _connecting = false;
        }
    }

    /// <summary>从主流程入口（作战配置页的房间列表）直接加入：填地址走同一条界面路径（M4）。</summary>
    public Task JoinAsync(string host, int port)
    {
        _joinEndpoint = $"{host}:{port}";
        return JoinAsync();
    }

    /// <summary>解析好友分享的主机地址与端口。</summary>
    internal static bool TryParseEndpoint(string text, out string host, out int port)
    {
        host = "";
        port = 0;
        var trimmed = text.Trim();
        var colon = trimmed.LastIndexOf(':');
        if (colon <= 0) return false;
        host = trimmed[..colon].Trim();
        return host.Length > 0
               && int.TryParse(trimmed[(colon + 1)..].Trim(), out port)
               && port is > 0 and < 65536;
    }

    /// <summary>把 AI 放进指定座位（座位卡上的按钮与左栏入口共用）。</summary>
    private async Task FillSeatAsync(int seat)
    {
        if (_feed is null || _deck is null) { _hint.Text = "房间尚未就绪。"; return; }
        if (_occupied[seat]) { _hint.Text = $"座位 {seat} 已经有人了。"; return; }

        _addAi.Disabled = true;
        try
        {
            _hint.Text = "正在添加电脑对手…";
            var ok = await AddAiToSeatAsync(seat);
            Ui(() => _hint.Text = ok ? "电脑对手已准备就绪。" : "未能添加电脑对手，请重试。");
            await RefreshAsync();
        }
        catch (Exception error) { Ui(() => _hint.Text = error.Message); }
        finally { _addAi.Disabled = _guest || _occupied[1]; }
    }

    /// <summary>把 AI 放到指定座位（验证探针直接用，跳过"自动找空座"）。</summary>
    internal async Task<bool> AddAiToSeatAsync(int seat)
    {
        if (_feed is null || _deck is null || _server is null || _guest || seat != 1) return false;
        var list = await _server.AdminAsync("connections.list");
        if (list.Details?["connections"] is System.Text.Json.Nodes.JsonArray connections &&
            connections.Any(c => c?["connectionId"]?.GetValue<string>() != _feed.ConnectionId))
            throw new InvalidOperationException("已有其他玩家或观战连接，请创建新房间后配置 AI。");
        await _feed.DisposeAsync(); _feed = null;
        await _server.DisposeAsync(); _server = null;
        _announcer?.Dispose(); _announcer = null; _ownRoomId = null;
        _aiSeat = seat;
        await OpenAsync();
        return _feed is not null;
    }

    /// <summary>开局（验证探针用；UI 按钮走 <see cref="StartAsync"/>）。</summary>
    internal async Task<bool> StartForVerificationAsync()
    {
        if (_feed is null) return false;
        return await _feed.StartMatchAsync();
    }

    /// <summary>卡组构建结果（验证探针用来核对张数与包名）。</summary>
    internal LobbyDeck.Result? Deck => _deck;

    /// <summary>
    /// 玩家当前构筑卡组（<c>DeckLibrary.ActiveDeck.CardIds</c>）。返回 null 表示没有可用卡组。
    ///
    /// <para>
    /// 联机只认应用层 id（<c>pack/{包}/{局部id}</c>）。卡库分两套 id：原始 catalog（<c>av76/...</c>）
    /// 与卡包投影（<c>pack/...</c>），而**两者之间没有任何溯源字段**——不是"还没打通映射"，
    /// 是数据本身毫无关联（实测 219 张包卡按国别+卡名只能对上 2 张）。所以这里只接受 <c>pack/</c> 前缀，
    /// 混入源卡时阻止加入并提示更换卡组；产品入口不替玩家更换牌组。
    /// </para>
    /// </summary>
    internal Func<IReadOnlyList<string>?>? ActiveDeckSource { get; set; }

    /// <summary>卡组来源说明与不可用原因。</summary>
    private string? _deckNote;

    /// <summary>尝试用玩家自己的卡组；不可用返回 null 并记下原因。</summary>
    private LobbyDeck.Result? TryActiveDeck()
    {
        var ids = ActiveDeckSource?.Invoke();
        if (ids is null || ids.Count == 0) return null;

        var invalid = ids.FirstOrDefault(id => !id.StartsWith("pack/", StringComparison.Ordinal));
        if (invalid is not null)
        {
            _deckNote = $"卡组含联机不支持的卡（{invalid}）";
            return null;
        }

        if (ids.Count != LobbyDeck.DefaultSize)
        {
            _deckNote = $"卡组 {ids.Count} 张（联机要求 {LobbyDeck.DefaultSize} 张）";
            return null;
        }

        _deckNote = "你的构筑卡组";
        return new LobbyDeck.Result("player", "自建", ids.ToArray());
    }

    /// <summary>最后一条服务端错误（验证探针用来报告占座/开局失败的真实原因）。</summary>
    internal string? LastError => _lastError;

    /// <summary>立刻拉一次快照（验证/截图用；平时靠 _Process 每 1.5 秒轮询）。</summary>
    internal Task<RemoteToolResult> AdminForVerificationAsync(string op, System.Text.Json.Nodes.JsonObject args) => _server!.AdminAsync(op, args);
    internal Task<RemoteToolResult> AdmitForVerificationAsync(string connectionId) => _server!.AdmitAsync(connectionId);

    internal Task RefreshForVerificationAsync() => RefreshAsync();

    /// <summary>按"客人"路径加入（验证探针用，等价于在输入框填地址后点"加入"）。</summary>
    internal async Task<bool> JoinForVerificationAsync(string host, int port)
    {
        _joinEndpoint = $"{host}:{port}";
        await JoinAsync();
        return _feed is not null;
    }

    /// <summary>重连状态（验证探针用）。</summary>
    internal bool IsReconnecting => _reconnecting;

    /// <summary>
    /// 验证用：模拟连接层故障。真实断线（心跳判死/传输硬断）走同一个入口
    /// <see cref="BeginReconnect"/>，这里只是把"等心跳"换成"立刻触发"。
    /// 返回前完成一次重连尝试；调用方随后轮询 <see cref="Feed"/> 是否恢复。
    /// </summary>
    internal async Task SimulateConnectionLossForVerificationAsync()
    {
        BeginReconnect();
        await TryReconnectAsync();
    }

    /// <summary>验证用：指定必须出现在卡组里的一张卡（须在 <see cref="OpenAsync"/> 之前调用）。</summary>
    internal void RequireCardForVerification(string localId) => _requiredCard = localId;

    private async Task StartAsync()
    {
        if (_feed is not { } feed || _starting) return;
        var lifecycle = _lifecycle;
        _starting = true;
        _start.Disabled = true;
        try
        {
            var started = await feed.StartMatchAsync();
            if (_feed != feed || lifecycle != _lifecycle) return;
            if (!started) { _hint.Text = "开局失败（见上方错误）。"; return; }
            MatchStarted?.Invoke();
        }
        catch (Exception error) { _hint.Text = "未能开始对战：" + error.Message; }
        finally
        {
            _starting = false;
            _start.Disabled = false;
        }
    }

    private void CopyInvite()
    {
        var text = _endpoint.Text.Contains(':') ? _endpoint.Text.Split('\n')[0] : _endpoint.Text;
        DisplayServer.ClipboardSet(text);
        _hint.Text = $"已复制 {text}。好友可在作战准备页使用地址加入。";
    }

    /// <summary>判定是否连接层故障；是则转入重连状态（幂等）。返回 false 表示普通业务错误。</summary>
    internal string PingText => _ping.Text;
    private void NoteError(string message)
    {
        if (OnConnectionIssue(message)) Say("✗ 连接断开 · 正在自动重连…");
        else Say("✗ " + message);
    }
    private bool OnConnectionIssue(string message)
    {
        if (_closing || _reconnecting || _feed is null || _feed.AdmissionState is "revoked" or "rejected") return false;
        // OrcClient 心跳判死/传输硬断时，挂起请求统一以该文案失败。
        if (!message.Contains("连接已关闭")) return false;
        BeginReconnect();
        return true;
    }

    /// <summary>
    /// 进入重连状态：丢掉旧传输（房主的服务器实例**留着**），记住凭据，
    /// 由 <see cref="_Process"/> 每 2.5 秒驱动 <see cref="TryReconnectAsync"/>。
    /// </summary>
    private void BeginReconnect()
    {
        if (_reconnecting || _closing || _feed is null) return;
        GD.Print($"[room] reconnect begin (guest={_guest}, port={(_guest ? _guestPort : _server?.Port ?? 0)})");
        _reconnectObserver = _feed.IsObserver;
        _reconnectToken = _feed.SeatToken;
        _reconnectSeat = _feed.Seat;
        if (_reconnectPort == 0 && !_guest) { _reconnectHost = "127.0.0.1"; _reconnectPort = _server?.Port ?? 0; }
        var feed = _feed; _feed = null;
        _ = feed.DisposeAsync();
        _reconnecting = true;
        _reconnectAttempts = 0;
        _retryClock = 2.5; // 下一帧立刻试第一次
    }

    /// <summary>重连＝连接 → room.resume →（对局态）RefreshView 补投影。失败自动重试，直到成功或用户返回。
    /// **单飞**：同时只允许一个尝试在跑（探针直调与 _Process 定时器会并发）。</summary>
    private async Task TryReconnectAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _reconnectInFlight, 1) == 1) return;
        try
        {
            await TryReconnectCoreAsync();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _reconnectInFlight, 0);
        }
    }

    private async Task TryReconnectCoreAsync()
    {
        if (!_reconnecting || _closing) return;
        var lifecycle = _lifecycle;
        _reconnectAttempts++;
        RemoteMatchFeed? connectingFeed = null;
        try
        {
            var feed = await RemoteMatchFeed.ConnectAsync(_reconnectHost, _reconnectPort, seat: _reconnectSeat);
            connectingFeed = feed;
            if (lifecycle != _lifecycle) return;
            feed.ErrorRaised += message => { _lastError = message; Ui(() => NoteError(message)); };
            HookPing(feed);
            _server?.Configure(feed);
            var resumed = _reconnectObserver ? await feed.AttachViewerAsync() :
                !string.IsNullOrEmpty(_reconnectToken) && await feed.ResumeSeatAsync(_reconnectToken);
            if (lifecycle != _lifecycle) return;
            if (!resumed)
            {
                _feed = feed; connectingFeed = null; _reconnecting = false; _reconnectToken = null;
                Say("入场凭据已失效，请重新申请入场。");
                return;
            }

            _feed = feed;
            connectingFeed = null;
            _reconnecting = false;
            if (_guest) HookGuestAutoEnter(feed);
            GD.Print($"[room] reconnect success (attempt {_reconnectAttempts}, feed#{feed.GetHashCode():x})");
            Say(_reconnectObserver ? "○ 已重新连接 · 等待房主批准观战" : "● 重连成功 · 座位已恢复");
            if (_reconnectObserver) { await RefreshAsync(); return; }
            // 补投影：服务端瞬时态可能拒一两次，拉不到就重试（M3 排障教训：一次失败不该留白屏）。
            try
            {
                for (var i = 0; i < 3 && feed.CurrentView is null; i++)
                {
                    await feed.RefreshViewAsync();
                    if (feed.CurrentView is not null) break;
                    await Task.Delay(500);
                }
            }
            catch (Exception) { /* 房间态没有对局视图，可忽略 */ }
            await RefreshAsync();
        }
        catch (Exception)
        {
            if (lifecycle == _lifecycle) Say($"✗ 重连失败（第 {_reconnectAttempts} 次）· 2.5 秒后再试");
        }
        finally { if (connectingFeed is not null) await connectingFeed.DisposeAsync(); }
    }

    /// <summary>客人：房主开局（或重连后补到投影）自动切战场。每条连接只触发一次。</summary>
    private async Task ReapplyAsync()
    {
        if (_feed is not { } previous || previous.AdmissionState is "pending" or "admitted") return;
        try
        {
            if (previous.IsObserver)
            {
                var port = Port; _feed = null; await previous.DisposeAsync();
                await SpectateAsync(_reconnectHost, port); return;
            }
            if (_guest)
            {
                _feed = null; await previous.DisposeAsync(); await JoinAsync(); return;
            }
            if (_server is null || _deck is null) return;
            _feed = null; await previous.DisposeAsync();
            var feed = await RemoteMatchFeed.ConnectAsync("127.0.0.1", _server.Port);
            _server.Configure(feed); _feed = feed; HookPing(feed);
            await previous.DisposeAsync();
            if (await feed.ClaimSeatAsync(0, "local", "房主", _deck.CardIds) is null) return;
            var result = await _server.AdmitAsync(feed.ConnectionId);
            if (!result.Ok) throw new InvalidOperationException(result.Message ?? result.Code);
            await feed.WaitForAdmissionAsync();
            RememberSession("127.0.0.1", Port, 0, feed.SeatToken);
            await RefreshAsync();
        }
        catch (Exception e) { Ui(() => NoteError(e.Message)); }
    }

    private async Task ShowAdmissionsAsync()
    {
        if (_server is null || _guest) { _hint.Text = "请由房主处理入场申请。"; return; }
        try
        {
            var result = await _server.AdminAsync("connections.list");
            Ui(() => {
                var dialog = new AcceptDialog { Title = "入场申请", MinSize = new Vector2I(600, 280) };
                var rows = new VBoxContainer(); dialog.AddChild(rows); AddChild(dialog);
                var pending = (result.Details?["connections"] as System.Text.Json.Nodes.JsonArray)?
                    .Where(c => c?["state"]?.GetValue<string>() == "pending").ToArray() ?? [];
                if (pending.Length == 0) rows.AddChild(UiStyles.Label("暂无待处理申请。", 18, UiStyles.MenuInk));
                foreach (var connection in pending)
                {
                    var id = connection!["connectionId"]!.GetValue<string>();
                    var row = new HBoxContainer(); rows.AddChild(row);
                    var role = connection["seat"] is null ? "观战申请 · 可查看双方手牌" : $"玩家申请 · 座位 {connection["seat"]}";
                    row.AddChild(UiStyles.Label($"{connection["label"]} · {role}\n{connection["remote"]}", 14, UiStyles.MenuInk));
                    row.AddChild(UiStyles.Button("批准", () => _ = DecideAdmissionAsync(id, true, dialog)));
                    row.AddChild(UiStyles.Button("拒绝", () => _ = DecideAdmissionAsync(id, false, dialog)));
                }
                dialog.CloseRequested += dialog.QueueFree; dialog.Confirmed += dialog.QueueFree;
                dialog.PopupCentered();
            });
        }
        catch (Exception e) { Ui(() => NoteError(e.Message)); }
    }
    private async Task DecideAdmissionAsync(string id, bool accept, AcceptDialog dialog)
    {
        if (_server is null) return;
        var result = await _server.AdminAsync(accept ? "conn.admit" : "conn.reject",
            new System.Text.Json.Nodes.JsonObject { ["connectionId"] = id });
        Ui(() => { if (result.Ok) dialog.QueueFree(); else NoteError(result.Message ?? result.Code); });
        await RefreshAsync();
    }
    public async Task SpectateAsync(string host, int port)
    {
        if (_closing || _connecting || _feed is not null) return;
        var lifecycle = _lifecycle; _connecting = true;
        RemoteMatchFeed? connectingFeed = null;
        try
        {
            if (_server is not null && port == _server.Port) throw new InvalidOperationException("房主无需通过自己的地址观战，请由另一客户端加入。");
            var feed = await RemoteMatchFeed.ConnectAsync(host, port);
            connectingFeed = feed;
            if (lifecycle != _lifecycle) return;
            feed.ErrorRaised += message => Ui(() => { if (_feed == feed) NoteError(message); });
            if (!await feed.AttachViewerAsync() || lifecycle != _lifecycle) return;
            _feed = feed; _guest = true; _guestPort = port; _joinEndpoint = $"{host}:{port}";
            connectingFeed = null;
            _reconnectHost = host; _reconnectPort = port; _reconnectSeat = 0; _reconnectToken = null;
            HookGuestAutoEnter(feed); HookPing(feed);
            feed.AdmissionChanged += state => Ui(() => { if (_feed == feed) Say(state == "admitted" ? "● 观战已获批准" : state == "pending" ? "等待房主批准观战" : "观战申请未获批准"); });
            await RefreshAsync();
        }
        catch (Exception e) { Ui(() => { if (lifecycle == _lifecycle) NoteError(e.Message); }); }
        finally
        {
            if (connectingFeed is not null) await connectingFeed.DisposeAsync();
            if (lifecycle == _lifecycle) _connecting = false;
        }
    }

    private void HookGuestAutoEnter(RemoteMatchFeed feed)
    {
        // 主信号＝match.started：先切战场、先订阅，换牌阶段才不会因为"切屏晚了一帧"被跳过。
        // 兜底＝第一帧投影：重连时服务端不会再发 match.started，只能靠投影补入场。
        var fired = false;
        void Enter(string reason)
        {
            if (fired) return;
            fired = true;
            GD.Print($"[room] guest enter battle via {reason}");
            Ui(() => { if (_feed == feed) MatchStarted?.Invoke(); });
        }

        feed.MatchStarted += () => Enter("match.started");
        feed.ProjectionReady += (_, _) => Enter("first-projection");
    }

    /// <summary>挂延迟显示（M5）：启动 ping 循环并把结果写进状态栏。</summary>
    private void HookPing(RemoteMatchFeed feed)
    {
        if(Cosmetics is not null)feed.ConfigureCosmetics(Cosmetics,_deckPresentation?.Id,_deckPresentation?.Options.MainNation);
        feed.StartPingLoop();
        feed.AdmissionChanged += state => Ui(() => {
            if (_feed != feed) return;
            _reapply.Visible = state is "revoked" or "rejected";
            _reconnectToken = feed.SeatToken;
            if (state is "revoked" or "rejected")
            {
                _hint.Text = state == "rejected" ? "房主拒绝了入场申请。" : "房间已更新或许可已撤销，请重新申请入场。";
                _start.Disabled = true; _seat1.SetAccess(false);
                Say(_hint.Text);
            }
        });
        feed.PingUpdated += ms => Ui(() => { if (_feed == feed) _ping.Text = ms is null ? "延迟 —"
            : $"延迟 {ms} ms" + (ms > 250 ? "（高）" : ""); });
    }

    /// <summary>记住本端会话凭据（重连的唯一依据），并落盘 user://net-room/last-session.json。</summary>
    private void RememberSession(string host, int port, int seat, string? token)
    {
        _reconnectHost = host; _reconnectPort = port; _reconnectSeat = seat; _reconnectToken = token;
        try
        {
            Directory.CreateDirectory(_sandbox);
            var payload = System.Text.Json.JsonSerializer.Serialize(new SessionRecord(host, port, seat, token));
            File.WriteAllText(Path.Combine(_sandbox, "last-session.json"), payload, new System.Text.UTF8Encoding(false));
        }
        catch (Exception) { /* 落盘失败只影响"下次还能不能续"，不影响本局 */ }
    }

    private sealed record SessionRecord(string Host, int Port, int Seat, string? SeatToken);

    // ── 渲染 ─────────────────────────────────────────────────────────

    private void Say(string text) => _status.Text = text;
}

/// <summary>一个座位的状态卡：空座＝暗框，有 controller＝金框。</summary>
public partial class RoomSeatCard : PanelContainer
{
    private Label _seat = null!, _who = null!, _state = null!, _deck = null!;
    private Button _fill = null!;
    private int _index;
    private readonly TextureCache _textures = new();
    private SetupDeckBack _ownCover = null!;
    private SetupDeckArtwork _hiddenCover = null!;
    private string? _coverSignature;
    public void SetDeck(ConstructedDeck? deck, bool self)
    {
        _ownCover.Visible = self && deck is not null; _hiddenCover.Visible = !_ownCover.Visible;
        var signature = deck is null ? null : deck.Id + "/" + deck.Options.MainNation + "/" + deck.Options.UnrestrictedNations;
        if (signature != _coverSignature) { _coverSignature = signature; if (deck is not null) _ownCover.Bind(deck, _textures); }
        if (_ownCover.Visible) _deck.Text = deck!.Name;
    }

    /// <summary>空座位的"放 AI"被点：由房间屏决定实际调用。</summary>
    public event Action? FillRequested;

    /// <summary>该座是否已有 controller（AI 或玩家）。</summary>
    public bool HasController { get; private set; }

    /// <summary>座位号。</summary>
    public int Seat => _index;
    internal Button FillButton => _fill;
    public void SetAccess(bool canAddAi) { _fill.Visible = !HasController && canAddAi; }

    public void Initialize(int index)
    {
        _index = index;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        AddChild(column);

        _seat = UiStyles.Label(index == 0 ? "房主" : "对手", 12, UiStyles.Accent);
        column.AddChild(_seat);
        _who = UiStyles.Label("空座", 24, UiStyles.MenuInk);
        MenuMaterials.Title(_who);
        column.AddChild(_who);
        _state = UiStyles.Label("等待加入", 14, UiStyles.MenuDim);
        _state.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_state);

        column.AddChild(new HSeparator());
        var artwork = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 240), MouseFilter = MouseFilterEnum.Ignore }; column.AddChild(artwork);
        _ownCover = new SetupDeckBack { MouseFilter = MouseFilterEnum.Ignore }; artwork.AddChild(_ownCover); _ownCover.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _ownCover.Hide();
        _hiddenCover = new SetupDeckArtwork(); _hiddenCover.Initialize(_textures, true); artwork.AddChild(_hiddenCover); _hiddenCover.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _deck = UiStyles.Label("卡组 —", 15, UiStyles.MenuDim); _deck.HorizontalAlignment = HorizontalAlignment.Center; _deck.ClipText = true;
        column.AddChild(_deck);
        _fill = UiStyles.Button("＋ 添加电脑对手", () => FillRequested?.Invoke());
        _fill.CustomMinimumSize = new(0, 36);
        _fill.AddThemeStyleboxOverride("normal", UiStyles.Box(new("2b3229"), UiStyles.Accent, 10, 8));
        column.AddChild(_fill);

        SetState("", null, false, null);
    }

    public void SetState(string controllerKind, string? displayName, bool connected, string? deckLabel)
    {
        HasController = controllerKind.Length > 0;
        _fill.Visible = !HasController;

        if (!HasController)
        {
            _who.Text = "空座";
            _state.Text = "邀请好友，或添加电脑对手";
            _state.AddThemeColorOverride("font_color", UiStyles.MenuDim);
            _deck.Text = "卡组 —";
            _hiddenCover.Modulate = new Color(1,1,1,.35f);
            AddThemeStyleboxOverride("panel", MenuMaterials.Panel(new Color("202529"),20));
            return;
        }

        var isAi = controllerKind == "ai";
        _who.Text = string.IsNullOrWhiteSpace(displayName) ? (isAi ? "电脑对手" : "玩家") : displayName;
        _state.Text = isAi ? "电脑控制 · 已就绪" : connected ? "玩家 · 已连接" : "玩家 · 离线";
        _state.AddThemeColorOverride("font_color", connected || isAi ? UiStyles.MenuInk : UiStyles.Accent);
        _deck.Text = deckLabel ?? (isAi ? "电脑对手卡组" : "卡组 · 已提供");
        _hiddenCover.Modulate = Colors.White;
        AddThemeStyleboxOverride("panel", MenuMaterials.Panel(new Color("29302e"),20));
    }
}
