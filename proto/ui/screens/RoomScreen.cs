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
    /// <summary>验证用：保证这张卡在卡组里（等随机发牌等到天荒地老时可预期地触发选靶）。</summary>
    private string? _requiredCard;
    /// <summary>座位占用（由快照更新；读它的地方可能在非主线程，别直接问控件）。</summary>
    private readonly bool[] _occupied = new bool[2];

    private Label _status = null!, _endpoint = null!, _pool = null!, _room = null!;
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
            _deck = LobbyDeck.Build(_packsRoot, requiredLocalId: _requiredCard);

            Ui(() => Say("○ 启动服务器实例…"));
            var packs = Directory.EnumerateDirectories(_packsRoot)
                .Where(directory => File.Exists(Path.Combine(directory, "manifest.json")))
                .OrderBy(directory => directory, StringComparer.Ordinal)
                .ToArray();
            _server = await ServerLauncher.StartAsync(packs, _sandbox);
            Ui(() => Say($"● 就绪 · 端口 {_server.Port}"));

            _feed = await RemoteMatchFeed.ConnectAsync("127.0.0.1", _server.Port);
            // 回调来自协议线程，控件只能主线程写——统一 marshal。
            _feed.ErrorRaised += message => { _lastError = message; Ui(() => Say("✗ " + message)); };

            // 房主＝local 座位；服务端只认 Kind==Local 的房间主人，否则开局被拒。
            var token = await _feed.ClaimSeatAsync(0, "local", "房主", _deck.CardIds);
            if (token is null) { Ui(() => Say("✗ 占座失败（见上方错误）")); return; }

            await RefreshAsync();
        }
        catch (Exception error)
        {
            Ui(() => { Say("✗ " + error.Message); _hint.Text = error.Message; });
        }
    }

    /// <summary>离开房间：释放连接与实例（终局后服务器不会自己退出）。</summary>
    public async void Close()
    {
        var feed = _feed; _feed = null;
        var server = _server; _server = null;
        if (feed is not null) { try { await feed.DisposeAsync(); } catch (Exception) { } }
        if (server is not null) { try { await server.DisposeAsync(); } catch (Exception) { } }
        Ui(() => Say("○ 已停止"));
    }

    public override void _Process(double delta)
    {
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
        _pool.Text = $"卡池 {_deck?.PackageId ?? "—"} · {snapshot.Policy}" +
                     (snapshot.Fingerprint.Length >= 8 ? $" · {snapshot.Fingerprint[..8]}" : "");
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
            _deck ??= LobbyDeck.Build(_packsRoot);

            Ui(() => Say($"○ 连接 {host}:{port}…"));
            _guestPort = port;
            var feed = await RemoteMatchFeed.ConnectAsync(host, port, seat: 1);
            feed.ErrorRaised += message => Ui(() => Say("✗ " + message));

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

            // 房主开局后服务端会推视图；客人在这一刻自动切战场。
            // 房主自己走"开始对局"按钮，不依赖这条。
            var fired = false;
            feed.ProjectionReady += (_, _) =>
            {
                if (fired) return;
                fired = true;
                Ui(() => MatchStarted?.Invoke());
            };

            // 客人不能添 AI、也不能开局——那是房主的权限。
            Ui(() => { _join.Disabled = true; _addAi.Disabled = true; _joinField.Editable = false; });
            await RefreshAsync();
        }
        catch (Exception error)
        {
            Ui(() => Say("✗ " + error.Message));
        }
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
