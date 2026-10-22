using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>
/// 作战房间：进屏即**拉起服务器实例**，然后拉人（AI / 远端玩家）、分配座位，最后开局。
///
/// <para>
/// **形态依据**：协议 2.0 预定稿里"座位 + controller + 管理面"的语义，
/// 但引擎当前仍是协议 1.0（<c>room.claim</c> / <c>room.add_ai</c> / <c>room.start{seatToken}</c>），
/// 所以这里按 1.0 的调用实现，UI 结构按 2.0 摆位——升级协议时只换调用层，不动布局。
/// </para>
///
/// <para>
/// **卡组**：玩家自建卡组是 catalog id，服务端要应用层全名，两套无映射，
/// 因此房间内先用 <see cref="LobbyDeck"/> 从卡包现场组卡。这是临时通道，不是最终形态。
/// </para>
/// </summary>
public partial class RoomScreen : VBoxContainer
{
    private string _packsRoot = "", _sandbox = "";
    private ServerLauncher? _server;
    private RemoteMatchFeed? _feed;
    private LobbyDeck.Result? _deck;
    private bool _starting;
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
    private string _reconnectHost = "";
    private int _reconnectPort, _reconnectSeat;
    private string? _reconnectToken;
    private int _reconnectAttempts;
    private double _retryClock;
    /// <summary>重连单飞守卫：探针 await 一次 + _Process 定时器一次会并发进来，后写的 feed 会覆盖先写的（M3 flake 真凶）。</summary>
    private int _reconnectInFlight;

    // ── 大厅发现（M4）──────────────────────────────────────────────
    // 房主起服后宣告（RoomAnnouncer），客人监听 UDP 去重渲染（RoomListener）。
    private RoomListener? _listener;
    private RoomAnnouncer? _announcer;
    private string? _ownRoomId;
    private readonly Dictionary<string, DiscoveredRoom> _discovered = new(StringComparer.Ordinal);
    private VBoxContainer _discoveredBox = null!;
    private double _discoveryClock;

    /// <summary>验证用：保证这张卡在卡组里（等随机发牌等到天荒地老时可预期地触发选靶）。</summary>
    private string? _requiredCard;
    /// <summary>座位占用（由快照更新；读它的地方可能在非主线程，别直接问控件）。</summary>
    private readonly bool[] _occupied = new bool[2];

    private Label _status = null!, _endpoint = null!, _pool = null!, _room = null!, _ping = null!;
    private Label _checkSeat0 = null!, _checkSeat1 = null!, _checkPool = null!, _hint = null!;
    private RoomSeatCard _seat0 = null!, _seat1 = null!;
    private Button _addAi = null!, _invite = null!, _addAgent = null!, _start = null!, _back = null!, _join = null!;
    private LineEdit _joinField = null!;

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

    public void Initialize(string packsRoot, string sandboxDirectory)
    {
        _packsRoot = packsRoot;
        _sandbox = sandboxDirectory;

        // 大厅发现（M4）：监听只要进屏就一直开着——浏览房间不依赖自己先起服务器。
        _listener = new RoomListener();
        _listener.Received += room => Ui(() => NoteDiscovered(room));
        _listener.Start();

        AddThemeConstantOverride("separation", 14);
        BuildHeader();
        BuildBody();
        BuildFooter();
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
        title.AddChild(UiStyles.Label("拉起服务器实例，分配座位，然后开局。", 14, UiStyles.MenuDim));

        _back = UiStyles.Button("← 返回指挥部", () => BackRequested?.Invoke());
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
        _seat0.FillRequested += () => _ = FillSeatAsync(0);
        seats.AddChild(_seat0);

        _seat1 = new RoomSeatCard();
        _seat1.Initialize(1);
        _seat1.FillRequested += () => _ = FillSeatAsync(1);
        seats.AddChild(_seat1);

        right.AddChild(BuildChecks());
    }

    private PanelContainer BuildInstancePanel()
    {
        var panel = new PanelContainer { CustomMinimumSize = new(348, 0) };
        panel.AddThemeStyleboxOverride("panel", UiStyles.Box(UiStyles.MenuPanel, UiStyles.MenuLine, 12, 20));

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        panel.AddChild(column);

        column.AddChild(UiStyles.Label("本机服务器", 18, UiStyles.MenuInk));
        _status = UiStyles.Label("○ 启动中…", 14, UiStyles.Accent);
        column.AddChild(_status);
        _ping = UiStyles.Label("延迟 —", 12, UiStyles.MenuDim);
        column.AddChild(_ping);

        column.AddChild(UiStyles.Label("发给对方", 12, UiStyles.Accent));
        _endpoint = UiStyles.Label("—", 15, UiStyles.MenuInk);
        _endpoint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_endpoint);

        column.AddChild(new HSeparator());
        _pool = UiStyles.Label("卡池 —", 13, UiStyles.MenuDim);
        column.AddChild(_pool);
        _room = UiStyles.Label("房间 —", 13, UiStyles.MenuDim);
        column.AddChild(_room);

        column.AddChild(new HSeparator());
        column.AddChild(UiStyles.Label("加入别人的房间", 12, UiStyles.Accent));
        _joinField = new LineEdit { PlaceholderText = "26.x.x.x:端口" };
        _joinField.CustomMinimumSize = new(0, 36);
        _joinField.TextSubmitted += text => { _ = JoinAsync(); };
        column.AddChild(_joinField);
        _join = UiStyles.Button("加入  →", () => _ = JoinAsync());
        _join.CustomMinimumSize = new(0, 36);
        column.AddChild(_join);

        column.AddChild(new HSeparator());
        column.AddChild(UiStyles.Label("发现的房间", 12, UiStyles.Accent));
        _discoveredBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _discoveredBox.AddThemeConstantOverride("separation", 4);
        column.AddChild(_discoveredBox);
        var discoveryNote = UiStyles.Label("同一局域网（Radmin 亦可）内开过房间的会自动出现在这里。", 11, UiStyles.MenuDim);
        discoveryNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(discoveryNote);

        column.AddChild(new HSeparator());
        column.AddChild(UiStyles.Label("拉入", 12, UiStyles.Accent));

        _addAi = UiStyles.Button("＋  引擎 AI", () => _ = AddAiAsync());
        _addAi.CustomMinimumSize = new(0, 40);
        column.AddChild(_addAi);

        _invite = UiStyles.Button("＋  远端玩家", CopyInvite);
        _invite.CustomMinimumSize = new(0, 40);
        column.AddChild(_invite);

        _addAgent = UiStyles.Button("＋  Agent（未开放）", () => { });
        _addAgent.CustomMinimumSize = new(0, 40);
        _addAgent.Disabled = true;
        column.AddChild(_addAgent);

        _hint = UiStyles.Label("", 12, UiStyles.MenuDim);
        _hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_hint);

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
        _checkSeat0 = UiStyles.Label("✗ 座位 0", 13, UiStyles.MenuDim); row.AddChild(_checkSeat0);
        _checkSeat1 = UiStyles.Label("✗ 座位 1", 13, UiStyles.MenuDim); row.AddChild(_checkSeat1);
        _checkPool = UiStyles.Label("✗ 卡池", 13, UiStyles.MenuDim); row.AddChild(_checkPool);

        return panel;
    }

    private void BuildFooter()
    {
        var footer = new HBoxContainer();
        AddChild(footer);

        var note = UiStyles.Label("座位齐备即可开局；远端玩家用上方地址自行加入。", 12, UiStyles.MenuDim);
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
        if (_feed is not null)
        {
            await RefreshAsync();
            return;
        }

        try
        {
            Ui(() => Say("○ 组装卡组…"));
            // 有必带卡（验证探针）时强制走自动组牌，否则优先用玩家自己构筑的卡组。
            _deck = (string.IsNullOrEmpty(_requiredCard) ? TryActiveDeck() : null)
                    ?? LobbyDeck.Build(_packsRoot, requiredLocalId: _requiredCard);

            Ui(() => Say("○ 启动服务器实例…"));
            var packs = Directory.EnumerateDirectories(_packsRoot)
                .Where(directory => File.Exists(Path.Combine(directory, "manifest.json")))
                .OrderBy(directory => directory, StringComparer.Ordinal)
                .ToArray();
            _server = await ServerLauncher.StartAsync(packs, _sandbox);
            Ui(() => Say($"● 就绪 · 端口 {_server.Port}"));

            _feed = await RemoteMatchFeed.ConnectAsync("127.0.0.1", _server.Port);
            // 回调来自协议线程，控件只能主线程写——统一 marshal。
            _feed.ErrorRaised += message => { _lastError = message; Ui(() => NoteError(message)); };
            HookPing(_feed);

            // 房主＝local 座位；服务端只认 Kind==Local 的房间主人，否则开局被拒。
            var token = await _feed.ClaimSeatAsync(0, "local", "房主", _deck.CardIds);
            if (token is null) { Ui(() => Say("✗ 占座失败（见上方错误）")); return; }

            RememberSession("127.0.0.1", _server.Port, 0, token);
            await RefreshAsync();
        }
        catch (Exception error)
        {
            GD.PushError("[room] 进入房间失败：" + error.Message);
            Ui(() => { Say("✗ " + error.Message); _hint.Text = error.Message; });
        }
    }

    /// <summary>离开房间：释放连接与实例（终局后服务器不会自己退出）。</summary>
    public async void Close()
    {
        _closing = true;
        _reconnecting = false;
        _announcer?.Dispose();
        _announcer = null;
        _ownRoomId = null;
        var feed = _feed; _feed = null;
        var server = _server; _server = null;
        if (feed is not null) { try { await feed.DisposeAsync(); } catch (Exception) { } }
        if (server is not null) { try { await server.DisposeAsync(); } catch (Exception) { } }
        _closing = false;
        Ui(() => Say("○ 已停止"));
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

        if (_feed is null)
        {
            _discoveryClock += delta;
            if (_discoveryClock >= 1.0) { _discoveryClock = 0; PruneDiscovery(); RenderDiscovery(); }
            return;
        }
        _poll += delta;
        _discoveryClock += delta;
        if (_discoveryClock >= 1.0) { _discoveryClock = 0; PruneDiscovery(); RenderDiscovery(); }
        if (_poll < 1.5) return;
        _poll = 0;
        _ = RefreshAsync();
    }

    // ── 操作 ─────────────────────────────────────────────────────────

    /// <summary>主线程执行（协议回调在别的线程，控件只能在主线程写）。</summary>
    private void Ui(Action action)
    {
        if (!IsInsideTree()) { action(); return; }
        Callable.From(action).CallDeferred();
    }

    private sealed record RoomSnapshot(
        string RoomId, string Policy, string Fingerprint, IReadOnlyList<string> Addresses,
        IReadOnlyList<(int Seat, string Kind, string? Name, bool Connected)> Seats);

    private async Task RefreshAsync()
    {
        if (_feed is null) return;
        try
        {
            var info = await _feed.RoomInfoAsync();
            if (!info.Ok) { Ui(() => Say("✗ " + info.Code)); return; }
            if (info.Details is not System.Text.Json.Nodes.JsonObject details) return;

            // 解析在任意线程做，写控件统一回主线程。
            var snapshot = Parse(details);
            Ui(() => Apply(snapshot));
        }
        catch (Exception error)
        {
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
                    node?["controllerKind"]?.GetValue<string>() ?? "",
                    node?["displayName"]?.GetValue<string>(),
                    node?["connected"]?.GetValue<bool>() ?? false));

        return new RoomSnapshot(
            details["roomId"]?.GetValue<string>() ?? "—",
            details["packPolicy"]?.GetValue<string>() ?? "—",
            details["packFingerprint"]?.GetValue<string>() ?? "",
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
            _announcer.UpdateSeats(snapshot.Seats.Count(s => s.Kind.Length > 0), 2);
        }

        _pool.Text = $"卡池 {_deck?.PackageId ?? "—"} · {snapshot.Policy}"
                     + (snapshot.Fingerprint.Length >= 8 ? $" · {snapshot.Fingerprint[..8]}" : "")
                     + (_deckNote is { Length: > 0 } note ? $" · {note}" : "");
        // 客人（房主＝false）看不到对方网卡列表——那对"加入方"没意义，显示自己连的是哪。
        _endpoint.Text = _guest
            ? $"已连接 {_joinField.Text.Trim()}"
            : snapshot.Addresses.Count > 0
                ? FormatAddresses(snapshot.Addresses)
                : $"127.0.0.1:{Port}";

        foreach (var (seat, kind, name, connected) in snapshot.Seats)
        {
            if (seat is not (0 or 1)) continue;
            _occupied[seat] = kind.Length > 0;
            var deckLabel = _deck is null ? null : $"卡组 {_deck.CardIds.Count} 张 · {_deck.Faction}";
            (seat == 0 ? _seat0 : _seat1).SetState(kind, name, connected, deckLabel);
        }

        var ready0 = _occupied[0];
        var ready1 = _occupied[1];
        var poolOk = snapshot.Policy == "loaded";

        SetCheck(_checkSeat0, ready0, "座位 0");
        SetCheck(_checkSeat1, ready1, "座位 1");
        SetCheck(_checkPool, poolOk, "卡池");

        _start.Disabled = _guest || !(ready0 && ready1 && poolOk);
        _start.Text = _guest ? "等待房主开局…" : "开始对局  →";
        _addAi.Disabled = _guest || (ready0 && ready1);
        Say(_guest ? $"● 已在房间 · 座位 {_feed?.Seat}" : $"● 就绪 · 端口 {Port}");
    }

    /// <summary>
    /// 地址只展示前 3 条：Radmin 虚拟网卡优先，其余折成"+N"。
    /// 一屏列 7 个 IP 会把左栏撑满，而"发给对方"实际只需要一个能用的。
    /// </summary>
    private static string FormatAddresses(IReadOnlyList<string> addresses)
    {
        var ordered = addresses
            .OrderBy(address => address.StartsWith("26.", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(address => address.StartsWith("192.168.", StringComparison.Ordinal) ? 1 : 0)
            .ToList();
        var lines = ordered.Take(3).ToList();
        if (ordered.Count > 3) lines.Add($"+{ordered.Count - 3} 个其它网卡");
        return string.Join('\n', lines);
    }

    private static void SetCheck(Label label, bool ok, string what)
    {
        label.Text = ok ? $"✓ {what} 就位" : $"✗ {what} 空缺";
        label.AddThemeColorOverride("font_color", ok ? UiStyles.Green : UiStyles.MenuDim);
    }

    /// <summary>
    /// 以客人身份加入别人的房间：连对方地址 → 占一个空座（remote）。
    /// 不启动本地服务器——这是"拉人"的另一半，此前只有房主侧。
    /// </summary>
    private async Task JoinAsync()
    {
        if (_feed is not null) { _hint.Text = "已经在房间里了。"; return; }
        if (!TryParseEndpoint(_joinField.Text, out var host, out var port))
        {
            _hint.Text = "地址格式：IP:端口，例如 26.109.28.81:4717";
            return;
        }

        try
        {
            Ui(() => Say("○ 组装卡组…"));
            _deck ??= TryActiveDeck() ?? LobbyDeck.Build(_packsRoot);

            Ui(() => Say($"○ 连接 {host}:{port}…"));
            _guestPort = port;
            var feed = await RemoteMatchFeed.ConnectAsync(host, port, seat: 1);
            feed.ErrorRaised += message => Ui(() => NoteError(message));
            HookPing(feed);

            // 两个座位都可能被占：房主通常坐 0，所以先试 1 再试 0。
            string? token = null;
            foreach (var candidate in new[] { 1, 0 })
            {
                token = await feed.ClaimSeatAsync(candidate, "remote", "远端玩家", _deck.CardIds);
                if (token is not null) break;
            }

            if (token is null)
            {
                await feed.DisposeAsync();
                Ui(() => Say("✗ 加入失败（房间已满或已开局）"));
                return;
            }

            _feed = feed;
            _guest = true;
            RememberSession(host, port, feed.Seat, token);

            // 房主开局后服务端会推视图；客人在这一刻自动切战场。
            HookGuestAutoEnter(feed);

            // 客人不能添 AI、也不能开局——那是房主的权限。
            Ui(() => { _join.Disabled = true; _addAi.Disabled = true; _joinField.Editable = false; });
            await RefreshAsync();
        }
        catch (Exception error)
        {
            Ui(() => Say("✗ " + error.Message));
        }
    }

    /// <summary>从主流程入口（作战配置页的房间列表）直接加入：填地址走同一条界面路径（M4）。</summary>
    public Task JoinAsync(string host, int port)
    {
        _joinField.Text = $"{host}:{port}";
        return JoinAsync();
    }

    /// <summary>解析 "IP:端口"（也接受只给 IP，默认端口见调用处提示）。</summary>
    private static bool TryParseEndpoint(string text, out string host, out int port)
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

    private async Task AddAiAsync()
    {
        // 左栏入口＝"拉一个人进来，然后分配座位"：自动挑第一个空座。
        var seat = _occupied[0] ? (_occupied[1] ? -1 : 1) : 0;
        if (seat < 0) { _hint.Text = "两个座位都有人了。"; return; }
        await FillSeatAsync(seat);
    }

    /// <summary>把 AI 放进指定座位（座位卡上的按钮与左栏入口共用）。</summary>
    private async Task FillSeatAsync(int seat)
    {
        if (_feed is null || _deck is null) { _hint.Text = "房间尚未就绪。"; return; }
        if (_occupied[seat]) { _hint.Text = $"座位 {seat} 已经有人了。"; return; }

        _hint.Text = $"正在把 AI 放到座位 {seat}…";
        var ok = await AddAiToSeatAsync(seat);
        Ui(() => _hint.Text = ok ? $"座位 {seat} 已交给引擎 AI。" : $"座位 {seat} 添加 AI 失败（见上方错误）。");
        await RefreshAsync();
    }

    /// <summary>把 AI 放到指定座位（验证探针直接用，跳过"自动找空座"）。</summary>
    internal async Task<bool> AddAiToSeatAsync(int seat)
    {
        if (_feed is null || _deck is null) return false;
        return await _feed.AddAiAsync(seat, _deck.CardIds, "引擎 AI");
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
    /// 混进源卡就退回自动组牌，并在卡池标签上写明原因。
    /// </para>
    /// </summary>
    internal Func<IReadOnlyList<string>?>? ActiveDeckSource { get; set; }

    /// <summary>卡组来源说明（自建卡组 / 为什么要退回自动组牌）。</summary>
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
    internal Task RefreshForVerificationAsync() => RefreshAsync();

    /// <summary>按"客人"路径加入（验证探针用，等价于在输入框填地址后点"加入"）。</summary>
    internal async Task<bool> JoinForVerificationAsync(string host, int port)
    {
        _joinField.Text = $"{host}:{port}";
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
        if (_feed is null || _starting) return;
        _starting = true;
        _start.Disabled = true;
        try
        {
            var started = await _feed.StartMatchAsync();
            if (!started) { _hint.Text = "开局失败（见上方错误）。"; return; }
            MatchStarted?.Invoke();
        }
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
        _hint.Text = $"已复制 {text}，发给对方后在联机页填入。";
    }

    // ── 大厅发现（M4）──────────────────────────────────────────────

    /// <summary>看到一条房间宣告（协议线程回调经 Ui 投递到主线程）。自己的房间不进列表。</summary>
    private void NoteDiscovered(DiscoveredRoom room)
    {
        if (room.RoomId == _ownRoomId) return;
        _discovered[room.RoomId] = room;
        RenderDiscovery();
    }

    /// <summary>超过 <see cref="RoomDiscovery.Expiry"/> 没听到的房间从列表剔除。</summary>
    private void PruneDiscovery()
    {
        var now = System.Environment.TickCount64;
        var expired = _discovered.Values
            .Where(room => now - room.SeenMilliseconds > RoomDiscovery.Expiry.TotalMilliseconds)
            .Select(room => room.RoomId).ToArray();
        foreach (var id in expired) _discovered.Remove(id);
        if (expired.Length > 0) RenderDiscovery();
    }

    private void RenderDiscovery()
    {
        if (_discoveredBox is null) return;
        UiStyles.Clear(_discoveredBox);
        foreach (var room in _discovered.Values.OrderByDescending(room => room.SeenMilliseconds))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var label = UiStyles.Label(
                $"{room.Host}:{room.Port} · 座位 {room.SeatsTaken}/{room.SeatsTotal}", 13, UiStyles.MenuInk);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.ClipText = true;
            row.AddChild(label);
            var join = UiStyles.Button("加入", () =>
            {
                _joinField.Text = $"{room.Host}:{room.Port}";
                _ = JoinAsync();
            });
            join.CustomMinimumSize = new(64, 30);
            row.AddChild(join);
            _discoveredBox.AddChild(row);
        }
    }

    /// <summary>当前发现列表（验证探针用）。</summary>
    internal DiscoveredRoom[] DiscoveredRooms => [.. _discovered.Values];

    /// <summary>延迟显示文本（验证探针用）。</summary>
    internal string PingText => _ping.Text;

    // ── 断线重连（M3）──────────────────────────────────────────────

    /// <summary>统一错误入口：连接层故障触发自动重连，其余只展示。</summary>
    private void NoteError(string message)
    {
        if (OnConnectionIssue(message)) Say("✗ 连接断开 · 正在自动重连…");
        else Say("✗ " + message);
    }

    /// <summary>判定是否连接层故障；是则转入重连状态（幂等）。返回 false 表示普通业务错误。</summary>
    private bool OnConnectionIssue(string message)
    {
        if (_closing || _reconnecting || _feed is null) return false;
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
        _reconnectToken ??= _feed.SeatToken;
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
        _reconnectAttempts++;
        try
        {
            var feed = await RemoteMatchFeed.ConnectAsync(_reconnectHost, _reconnectPort, seat: _reconnectSeat);
            feed.ErrorRaised += message => { _lastError = message; Ui(() => NoteError(message)); };
            HookPing(feed);
            var resumed = !string.IsNullOrEmpty(_reconnectToken) && await feed.ResumeSeatAsync(_reconnectToken);
            if (!resumed)
            {
                await feed.DisposeAsync();
                Say($"✗ 重连失败（第 {_reconnectAttempts} 次）· 令牌无效或座位已释放");
                return;
            }

            _feed = feed;
            _reconnecting = false;
            if (_guest) HookGuestAutoEnter(feed);
            GD.Print($"[room] reconnect success (attempt {_reconnectAttempts}, feed#{feed.GetHashCode():x})");
            Say("● 重连成功 · 座位已恢复");
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
            Say($"✗ 重连失败（第 {_reconnectAttempts} 次）· 2.5 秒后再试");
        }
    }

    /// <summary>客人：房主开局（或重连后补到投影）自动切战场。每条连接只触发一次。</summary>
    private void HookGuestAutoEnter(RemoteMatchFeed feed)
    {
        var fired = false;
        feed.ProjectionReady += (_, _) =>
        {
            if (fired) return;
            fired = true;
            Ui(() => MatchStarted?.Invoke());
        };
    }

    /// <summary>挂延迟显示（M5）：启动 ping 循环并把结果写进状态栏。</summary>
    private void HookPing(RemoteMatchFeed feed)
    {
        feed.StartPingLoop();
        feed.PingUpdated += ms => Ui(() => _ping.Text = ms is null ? "延迟 —"
            : $"延迟 {ms} ms" + (ms > 250 ? "（高）" : ""));
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

    /// <summary>空座位的"放 AI"被点：由房间屏决定实际调用。</summary>
    public event Action? FillRequested;

    /// <summary>该座是否已有 controller（AI 或玩家）。</summary>
    public bool HasController { get; private set; }

    /// <summary>座位号。</summary>
    public int Seat => _index;

    public void Initialize(int index)
    {
        _index = index;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        AddChild(column);

        _seat = UiStyles.Label($"SEAT {index}", 12, UiStyles.Accent);
        column.AddChild(_seat);
        _who = UiStyles.Label("空座", 24, UiStyles.MenuInk);
        column.AddChild(_who);
        _state = UiStyles.Label("等待加入", 14, UiStyles.MenuDim);
        _state.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_state);

        column.AddChild(new HSeparator());
        _deck = UiStyles.Label("卡组 —", 13, UiStyles.MenuDim);
        column.AddChild(_deck);

        // 撑开中段，让"放 AI"落在卡片底部，而不是紧贴文字。
        column.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        _fill = UiStyles.Button("＋  放引擎 AI 到此座", () => FillRequested?.Invoke());
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
            _state.Text = "等待玩家接入，或直接放一个 AI";
            _state.AddThemeColorOverride("font_color", UiStyles.MenuDim);
            _deck.Text = "卡组 —";
            AddThemeStyleboxOverride("panel", UiStyles.Box(new Color("1a211b"), UiStyles.MenuLine, 12, 18));
            return;
        }

        var isAi = controllerKind == "ai";
        _who.Text = string.IsNullOrWhiteSpace(displayName) ? (isAi ? "引擎 AI" : "玩家") : displayName;
        _state.Text = isAi ? "引擎驱动 · 自动行动" : connected ? "玩家 · 已连接" : "玩家 · 离线";
        _state.AddThemeColorOverride("font_color", connected || isAi ? UiStyles.MenuInk : UiStyles.Accent);
        _deck.Text = deckLabel ?? (isAi ? "卡组 · 服务端托管" : "卡组 · 已提供");
        AddThemeStyleboxOverride("panel", UiStyles.Box(UiStyles.MenuPanel, UiStyles.Gold, 12, 18));
    }
}
