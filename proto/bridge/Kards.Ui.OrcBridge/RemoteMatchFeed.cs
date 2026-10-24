using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Kards.Ui.Contracts;
using Orc.Client;
using Orc.Protocol;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 工具调用结果的 UI 侧视图。
/// <para>
/// **存在的唯一理由**：UI 程序集（<c>Kards.Ui</c>）不许出现引擎类型——直接用
/// <c>Orc.Protocol.ToolResultPayload</c> 当公开返回类型，会把 <c>Orc.Protocol</c> 写进
/// <c>Kards.Ui.dll</c> 的引用表，被 <c>UI_BOUNDARY_VERIFY_OK</c> 拦下。这里只保留 BCL 类型。
/// </para>
/// </summary>
public sealed record RemoteToolResult(bool Ok, string Code, string? Message, JsonNode? Details);

/// <summary>Remote projection and command adapter. Sessions require approval; no-seat viewers stay read-only.</summary>
public sealed partial class RemoteMatchFeed : IMatchFeed, IAsyncDisposable
{
    private readonly OrcClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _seatToken;
    private string? _targetSessionId;
    /// <summary>选靶候选：UI 侧的 choice id（序号字符串）→ 引擎要的 picks 序号。</summary>
    private readonly Dictionary<string, int> _picksByChoiceId = new(StringComparer.Ordinal);
    /// <summary>选靶候选：choice id → 引擎引用键（用于把"点谁"对到候选）。</summary>
    private readonly Dictionary<string, string> _refsByChoiceId = new(StringComparer.Ordinal);
    /// <summary>下一条选靶请求要自动命中的目标引用键（攻击/指向性指挥用）。</summary>
    private string? _desiredTargetRef;
    private bool _desiredGap;
    private HashSet<string>? _mulliganReplacements;
    public Func<string, string?>? ArtPathResolver { get; set; }

    /// <summary>
    /// 视图反序列化专用配置：服务端把枚举写成**字符串**（<c>"None"</c>、<c>"Mulligan"</c>…），
    /// 而 <see cref="ProtocolJson.Options"/> 没挂枚举转换器——直接复用会报
    /// 「JSON value could not be converted to UiMatchOutcome」。这里在协议配置上补转换器。
    /// </summary>
    private static readonly JsonSerializerOptions ViewJson = CreateViewJson();

    private static JsonSerializerOptions CreateViewJson()
    {
        var options = new JsonSerializerOptions(ProtocolJson.Options);
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: true));
        return options;
    }

    private UiMatchView? _view;
    private UiBattleActions _actions = new();
    private bool _mulliganPending;
    private string? _mulliganRequestId;
    private bool _targetPending;

    /// <summary>发起当前这次选靶请求的命令。第二段失败时要拿它去判被拒，否则该单位会永久"待定"。</summary>
    private UiCommand? _targetCommand;
    private readonly SortedDictionary<long, JsonNode> _pendingSegments = new();
    private readonly object _projectionGate = new();
    private long _lastSegmentSequence;
    private bool _disposed;
    private Task? _pingLoop;

    /// <summary>最近一次往返延迟（毫秒）；null＝尚未测到或连接不可用（M5 延迟显示）。</summary>
    public int? LastPingMs { get; private set; }

    /// <summary>每次测得新延迟时触发（null＝连接不可用）。协议线程回调，UI 使用处自行 marshal。</summary>
    public event Action<int?>? PingUpdated;

    /// <summary>启动延迟测量循环：每 3 秒发一次 <c>ping</c> 测 RTT。重连后对新 feed 再调一次即可。</summary>
    public void StartPingLoop()
    {
        if (_pingLoop is not null) return;
        _pingLoop = Task.Run(PingLoopAsync);
    }

    private async Task PingLoopAsync()
    {
        var first = true;
        while (true)
        {
            if (!first)
            {
                try { await Task.Delay(3000).ConfigureAwait(false); }
                catch (Exception) { return; }
            }
            first = false;
            if (_disposed) return;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await _client.CallAsync("ping", new JsonObject(), CancellationToken.None).ConfigureAwait(false);
                LastPingMs = (int)clock.ElapsedMilliseconds;
                PingUpdated?.Invoke(LastPingMs);
            }
            catch (Exception)
            {
                LastPingMs = null;
                PingUpdated?.Invoke(null);
                return; // 传输已断，重连由 RoomScreen 的重连状态机负责
            }
        }
    }

    private RemoteMatchFeed(OrcClient client, int seat)
    {
        _client = client;
        Seat = seat;
        _client.NotificationReceived += OnNotification;
    }

    /// <summary>当前显示视角；是否真正绑定座位由 BoundSeat 表达。</summary>
    public int Seat { get; private set; }
    public int? BoundSeat => IsObserver || AdmissionState != "admitted" ? null : Seat;

    public string MatchId { get; private set; } = "";
    public UiMatchView? CurrentView => _view;
    public UiBattleActions CurrentActions => IsObserver || AdmissionState != "admitted" || _targetPending ? new() : _actions;
    public bool IsSubmitting { get; private set; }
    public bool MulliganPending => _mulliganPending;
    public bool TargetPending => _targetPending;
    public string? MulliganRequestId => _mulliganRequestId;
    public Func<UiActionInteractionState>? InteractionStateReader { get; set; }

    public event Action<UiMatchView, UiBattleActions>? ProjectionReady;
    public event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;
    public event Action<IReadOnlyList<UiOrderImpact>, UiMatchView, UiBattleActions>? CombatReady;
    public event Action? MulliganRequested;

    /// <summary>
    /// 对局开始（<c>notify/match.started</c>）。界面必须**在这个信号上切战场**，
    /// 不能等第一帧投影：投影到达的时机取决于服务端推送，晚半拍就会跳过换牌阶段。
    /// </summary>
    public event Action? MatchStarted;
    public event Action<UiTargetRequest, IUiTargetResponder>? TargetRequested;
    public event Action? TargetClosed;
    public event Action<UiCommand>? CommandRefused;
    public event Action<string>? ErrorRaised;
    public event Action<string>? HintRequested;

    /// <summary>连接远端服务端并完成握手。不建局、不占座——那是 <see cref="ClaimSeatAsync"/> 的事。</summary>
    /// <param name="seat">本端打算占的座位（决定视图按哪一方过滤）；占座仍由 claim 决定。</param>
    public static async Task<RemoteMatchFeed> ConnectAsync(
        string host, int port, CancellationToken ct = default, int seat = 0)
    {
        var client = await OrcClient.ConnectAsync(new ClientOptions { Host = host, Port = port }, ct)
            .ConfigureAwait(false);
        var supportsCosmetics = false;
        try
        {
            var initialized = await client.InitializeAsync(new InitializeParams { ClientName = "godot" }, ct).ConfigureAwait(false);
            supportsCosmetics = initialized.Capabilities.Contains("cosmetics-v1");
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new RemoteMatchFeed(client, seat) { SupportsCosmetics = supportsCosmetics };
    }

    /// <summary>读房间快照（座位、可连地址、卡包状态）。</summary>
    public async Task<RemoteToolResult> RoomInfoAsync(CancellationToken ct = default)
    {
        var result = await _client.CallAsync("room.info", new JsonObject(), ct).ConfigureAwait(false);
        return new RemoteToolResult(result.Ok, result.Code, result.Message, result.Details);
    }

    public string ConnectionId => _client.ConnectionId ?? "";
    public string AdmissionState { get; private set; } = "idle";
    public bool IsObserver { get; private set; }
    public event Action<string>? AdmissionChanged;
    internal IReadOnlyList<(string Id, string? Version, string? Guid)> PackageDeclaration { get; set; } = [];

    /// <summary>
    /// 客人侧声明本机卡池（对账用）。装载仍以房主磁盘为唯一权威——声明只影响"能不能放行"和界面展示，
    /// 不会让客人的包进入 <c>PackDirectories</c>。
    /// </summary>
    public void DeclareLocalPackages(IReadOnlyList<(string Id, string? Version, string? Guid)> packages)
        => PackageDeclaration = packages;
    internal Func<CancellationToken, Task<bool>>? StartHostMatch { get; set; }
    private JsonNode? _observerFrame;
    public event Action<int, UiMatchView>? ObserverProjectionReady;
    public async Task<string?> ClaimSeatAsync(int seat, string kind, string? name, IReadOnlyList<string> deck, CancellationToken ct = default)
    {
        var result = await _client.AttachAsync(new SessionAttachParams {
            Roles = ["controller", "viewer"], Seat = seat, DisplayName = name, Deck = deck,
            Packages = PackageDeclaration.Select(p => new PackageRef(p.Id, p.Version, p.Guid)).ToArray()
        }, ct).ConfigureAwait(false);
        Seat = seat;
        if (AdmissionState != "admitted") AdmissionState = result.State;
        if (result.Code != "ok") { ErrorRaised?.Invoke(result.Message ?? result.Code); return null; }
        return result.ConnectionId;
    }
    public async Task<bool> WaitForAdmissionAsync(CancellationToken ct = default)
    {
        for (var i = 0; i < 100 && AdmissionState is "pending" or "idle"; i++)
            await Task.Delay(50, ct).ConfigureAwait(false);
        return AdmissionState == "admitted";
    }
    public async Task<bool> AttachViewerAsync(string name = "观战者", CancellationToken ct = default)
    {
        IsObserver = true;
        var result = await _client.AttachAsync(new SessionAttachParams { Roles = ["viewer"], DisplayName = name }, ct).ConfigureAwait(false);
        AdmissionState = result.State;
        return result.Code == "ok";
    }
    public void SetObserverPerspective(int seat)
    {
        if (!IsObserver || seat is not (0 or 1)) return;
        lock (_projectionGate)
        {
            if (Seat == seat) return;
            Seat = seat;
            _view = null;
            _pendingSegments.Clear();
            if (_observerFrame is not null) ApplyViewPayload(_observerFrame);
        }
    }
    public Task<bool> StartMatchAsync(CancellationToken ct = default)
        => StartHostMatch?.Invoke(ct) ?? Task.FromResult(false);
    public string? SeatToken => _seatToken;
    public async Task<bool> ResumeSeatAsync(string? seatToken = null, CancellationToken ct = default)
    {
        var token = seatToken ?? _seatToken;
        if (string.IsNullOrEmpty(token)) return false;
        var result = await _client.AttachAsync(new SessionAttachParams { Roles = ["controller", "viewer"], Seat = Seat, Token = token }, ct).ConfigureAwait(false);
        AdmissionState = result.State;
        if (result.Code != "ok") return false;
        _seatToken = token;
        return true;
    }

    /// <summary>
    /// 结束换牌相位（不换＝直接确认）。参数面只有 <c>owner</c>——服务端的换牌应答
    /// 不接受 keep 列表，要保留哪几张由协议外的 <c>mulligan.*</c> 其余工具决定。
    /// </summary>
    public async Task<bool> MulliganDoneAsync(int owner, CancellationToken ct = default)
    {
        var result = await _client.CallAsync("mulligan.done", new JsonObject { ["owner"] = owner }, ct)
            .ConfigureAwait(false);
        if (!result.Ok) ErrorRaised?.Invoke($"mulligan.done: {result.Code} {result.Message}");
        return result.Ok;
    }

    /// <summary>拉一次全量视图（重连后用它补齐，不必等推送）。</summary>
    public async Task RefreshViewAsync(CancellationToken ct = default)
    {
        var result = await _client.CallAsync("observe.view", new JsonObject(), ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            ErrorRaised?.Invoke(result.Message ?? result.Code);
            return;
        }
        ApplyViewPayload(result.Details);
    }

    /// <summary>联机没有"帧"——数据由推送驱动，这里什么也不做。</summary>
    public void Advance(double delta) { }

    public async Task SubmitAsync(UiCommand command, CancellationToken ct = default)
    {
        if (_disposed) return;
        if (IsObserver || AdmissionState != "admitted") { CommandRefused?.Invoke(command); HintRequested?.Invoke("观战模式无法操作对局。"); return; }
        if (command is ChooseMulligan choice)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            IsSubmitting = true;
            try
            {
                if (!_mulliganPending || _view is null) throw new InvalidOperationException("换牌阶段已结束。");
                _mulliganReplacements = _view.SelfHand.Where(c => !choice.KeepUids.Contains(c.Uid)).Select(c => c.Uid).ToHashSet(StringComparer.Ordinal);
                var toolName = _mulliganReplacements.Count == 0 ? "mulligan.done" : "mulligan.begin";
                var result = await _client.CallAsync(toolName, new JsonObject { ["owner"] = Seat }, ct).ConfigureAwait(false);
                if (!result.Ok && result.Code != "targeting") throw new InvalidOperationException(result.Message ?? result.Code);
                if (toolName == "mulligan.done") _mulliganReplacements = null;
                await RefreshViewAsync(ct).ConfigureAwait(false);
            }
            catch (Exception error) { _mulliganReplacements = null; CommandRefused?.Invoke(command); HintRequested?.Invoke(error.Message); MulliganRequested?.Invoke(); }
            finally { IsSubmitting = false; _gate.Release(); }
            return;
        }
        RememberDesiredTarget(command);
        // 记下发起者：这套动作可能走到第二段（选靶），那一段失败时必须知道该判谁被拒。
        _targetCommand = command;
        var (tool, arguments) = Map(command);
        if (tool is null)
        {
            CommandRefused?.Invoke(command);
            HintRequested?.Invoke("联机模式下暂不支持该操作");
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IsSubmitting = true;
        try
        {
            var result = await _client.CallAsync(tool, arguments, ct);
            if (!result.Ok)
            {
                // targeting ≠ 失败：指挥动作是两段式，第一段结束时服务端一定回 targeting。
                // 若在这里发 CommandRefused，UI 会把刚发起的动作当成被拒、就地回滚。
                if (result.Code is not "targeting")
                {
                    CommandRefused?.Invoke(command);
                    if (ReferenceEquals(command, _targetCommand)) _targetCommand = null;
                }
                if (result.Code is not ("targeting" or "cancelled"))
                    HintRequested?.Invoke(result.Message ?? result.Code);
            }
        }
        catch (Exception error)
        {
            CommandRefused?.Invoke(command);
            ErrorRaised?.Invoke($"{command.GetType().Name}: {error.Message}");
        }
        finally
        {
            IsSubmitting = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// 契约命令 → 协议工具。**返回 null 表示联机暂不支持**，调用方必须拒绝而不是静默丢弃。
    ///
    /// <para>
    /// 参数面来自运行时 <c>tools/list</c>（不是猜的）：
    /// <c>play.unit{ref,owner,slot}</c>、<c>match.end_turn{}</c>、<c>match.concede{}</c>。
    /// 其中 <c>ref</c> 正是手牌的 <c>$ref</c>（= <c>UiCardView.Uid</c>）。
    /// </para>
    ///
    /// <para>
    /// **还没映射的**：<c>CommandUnit</c> / <c>MoveUnit</c> / <c>AttackUnit</c> 走的是
    /// <c>command.begin{ref}</c> → 等 <c>notify/target.request</c> → <c>target.submit{sessionId,picks}</c>
    /// 的挂起-应答链，需要先有远端 responder；<c>ChooseMulligan</c> 的 keep 列表在 <c>mulligan.done</c>
    /// 的单参 schema 里没有落点，等确认后再接。
    /// </para>
    /// </summary>
    private (string? Tool, JsonNode? Arguments) Map(UiCommand command) => command switch
    {
        PlayCard play => RoutePlay(play),
        // 指挥动作全部是"发起 + 选靶"两段式：工具只吃 {ref}，位置/目标靠 pending 应答。
        // 能直接指定目标的（攻击、指向性指挥）在 ApplyTargetRequest 里自动命中，玩家感觉不到第二段。
        CommandUnit unit => ("command.begin", new JsonObject { ["ref"] = unit.Uid }),
        MoveUnit move => ("command.move", new JsonObject { ["ref"] = move.Uid }),
        AttackUnit attack => ("command.attack", new JsonObject { ["ref"] = attack.AttackerUid }),
        RetreatUnit retreat => ("command.leave", new JsonObject { ["ref"] = retreat.Uid }),
        EndTurn => ("match.end_turn", new JsonObject()),
        Concede => ("match.concede", new JsonObject()),
        _ => (null, null),
    };

    /// 记下"这条命令希望选中的目标"（引用键），下一条选靶请求优先自动命中它。
    ///
    /// <para>
    /// 服务端只提供"发起指挥"这一个入口，目标要靠 <c>target.submit</c> 回填；
    /// 但玩家点"攻击这个目标"时不该被迫再点一次。所以带明确目标的命令先记下来，
    /// 等 pending 到达再按 <c>candidates[].ref</c> 对上号自动提交。
    /// </para>
    /// </summary>
    private void RememberDesiredTarget(UiCommand command)
    {
        _desiredGap = command is PlayCard { GapId: not null };
        _desiredTargetRef = command switch
        {
            PlayCard play => play.GapId ?? play.TargetUid,
            AttackUnit attack => attack.DefenderUid,
            CommandUnit unit => unit.TargetUid ?? _actions.Moves.FirstOrDefault(m => m.Uid == unit.Uid
                && m.ToZone == "frontline" && m.SlotIndex == unit.SlotIndex)?.GapId,
            MoveUnit move => _actions.Moves.FirstOrDefault(m => m.Uid == move.Uid && m.ToZone == move.ToZone
                && (move.SlotIndex is null || m.SlotIndex == move.SlotIndex))?.GapId,
            // MoveUnit 的目标是槽位（ToZone/SlotIndex），不是实体引用键，
            // 而候选里的槽位 ref 是引擎懒分配的、无法从 SlotIndex 反推——
            // 所以移动仍然交给选槽面板，不在这里硬猜。
            _ => null,
        };
    }

    /// <summary>
    /// 出牌按卡型分流：<c>play.unit{ref,owner,slot}</c> / <c>play.command{ref}</c> / <c>play.counter{ref}</c>。
    ///
    /// <para>
    /// **别全塞 play.unit**：指令卡走 play.unit 会被服务端静默忽略——手牌不减、费用不扣、
    /// 连错误码都不给（实测 `hand 6→6 kredits 3→3`），排查时很容易误判成"选靶链路没通"。
    /// </para>
    /// </summary>
    private (string? Tool, JsonNode? Arguments) RoutePlay(PlayCard play)
    {
        var type = _view?.SelfHand.FirstOrDefault(card => card.Uid == play.Uid)?.Definition.CardType;
        return type switch
        {
            "order" => ("play.command", new JsonObject { ["ref"] = play.Uid }),
            "counter" => ("play.counter", new JsonObject { ["ref"] = play.Uid }),
            // 指定了槽位＝玩家拖到了具体位置，直接落位。
            _ when play.SupportIndex is { } slot => ("play.unit", new JsonObject
            {
                ["ref"] = play.Uid,
                ["owner"] = Seat,
                ["slot"] = slot,
            }),
            // 没指定槽位＝不该由前端硬猜：走"预打出"进部署选位交互，由选靶链路决定落点。
            // 硬猜槽号是本轮最大的坑——UiCardView.SlotIndex 把前线与支援线混在一起，
            // 算出来的"空槽"经常已被占，实测 40+ 次 TargetSlotOccupied，出牌永远失败。
            _ => ("play.unit_pre", new JsonObject { ["ref"] = play.Uid }),
        };
    }

    private void OnNotification(RpcNotification notification)
    {
        if (HandleCosmeticNotification(notification)) return;
        switch (notification.Method)
        {
            case "notify/admission":
                var admitted = notification.Params?["decision"]?.GetValue<string>() ?? "revoked";
                AdmissionState = admitted;
                _seatToken = notification.Params?["seatToken"]?.GetValue<string>();
                if (admitted != "admitted") { _actions = new(); _mulliganPending = false; _targetPending = false; _view = null; _observerFrame = null; TargetClosed?.Invoke(); }
                AdmissionChanged?.Invoke(admitted);
                if (admitted == "admitted") _ = InitializeCosmeticsAsync();
                break;
            case "notify/room.rotated":
                AdmissionState = "revoked"; _seatToken = null; _actions = new(); _mulliganPending = false; _targetPending = false; _view = null; _observerFrame = null; TargetClosed?.Invoke();
                AdmissionChanged?.Invoke("revoked");
                break;
            case "notify/view":
                ApplyViewPayload(notification.Params);
                break;
            case "notify/match.started":
                lock (_projectionGate) { _pendingSegments.Clear(); _lastSegmentSequence = 0; }
                MatchId = notification.Params?["matchId"]?.GetValue<string>() ?? MatchId;
                // 入场信号必须来自"对局开始了"，不能等第一帧投影——否则慢半拍的客户端
                // 第一帧可能已经是 play 相位，换牌阶段被整段跳过（2026-10-24 实机复现）。
                MatchStarted?.Invoke();
                break;
            case "notify/segment":
                ApplySegments(notification.Params);
                break;
            case "notify/target.request":
                ApplyTargetRequest(notification.Params);
                break;
            case "notify/target.expired":
                // 过期＝这次动作不会发生了，必须连同"待定"一起清，否则该单位被永久锁住。
                var expired = _targetCommand;
                _targetCommand = null;
                _targetPending = false;
                TargetClosed?.Invoke();
                if (expired is not null) CommandRefused?.Invoke(expired);
                break;
        }
    }

    /// <summary>
    /// 演出段推送。`notify/segment` 的载荷是引擎事件条目，要用 <see cref="RemotePresentationTranslator"/>
    /// 按 `$ref` 在投影里查卡才能翻译成演出步骤。
    ///
    /// <para>
    /// 段先缓存，随后到达的服务端投影作为同批动作的 after；保留上一投影作为 before。
    /// 同批嵌套段一起翻译，避免对同一个动作重复演出或对尚未更新的盘面查卡。
    /// </para>
    /// </summary>
    private void ApplySegments(JsonNode? payload)
    {
        lock (_projectionGate) BufferSegments(payload);
    }

    private void BufferSegments(JsonNode? payload)
    {
        if (payload?["segments"] is not JsonArray segments) return;
        foreach (var segment in segments)
        {
            var sequence = segment?["sequence"]?.GetValue<long>() ?? 0;
            if (segment is not null && sequence > _lastSegmentSequence)
                _pendingSegments[sequence] = segment;
        }
    }

    private UiMatchView ResolveArt(UiMatchView view)
    {
        UiCardView? Art(UiCardView? card) => card is null ? null : card with
        { Definition = card.Definition with { ArtPath = ArtPathResolver?.Invoke(card.Definition.CardId) ?? card.Definition.ArtPath } };
        return view with { SelfHand = view.SelfHand.Select(c => Art(c)!).ToArray(), SelfLine = view.SelfLine.Select(c => Art(c)!).ToArray(),
            EnemyLine = view.EnemyLine.Select(c => Art(c)!).ToArray(), SelfHq = Art(view.SelfHq), EnemyHq = Art(view.EnemyHq),
            PendingChoices = view.PendingChoices.Select(c => Art(c)!).ToArray(), SelectedTarget = Art(view.SelectedTarget) };
    }

    private void ApplyViewPayload(JsonNode? payload)
    {
        lock (_projectionGate) ApplyViewCore(payload);
    }

    private void ApplyViewCore(JsonNode? payload)
    {
        if (payload is null)
        {
            ErrorRaised?.Invoke("视图载荷为空（服务端应答没有 details）。");
            return;
        }

        if (IsObserver && payload["views"] is JsonArray perspectives)
        {
            if (!ReferenceEquals(_observerFrame, payload))
                foreach (var perspective in perspectives)
                    if (perspective?["view"] is { } projected && projected.Deserialize<UiMatchView>(ViewJson) is { } observed)
                        ObserverProjectionReady?.Invoke(perspective["seat"]!.GetValue<int>(), ResolveArt(observed));
            _observerFrame = payload;
            payload = perspectives.FirstOrDefault(p => p?["seat"]?.GetValue<int>() == Seat);
            if (payload is null) return;
        }
        var viewNode = payload["view"];
        var actionsNode = payload["actions"];
        if (viewNode is null)
        {
            // 静默吞掉等于"重连后画面空白但没有任何报错"——必须响（M3 排障教训）。
            var snippet = payload.ToJsonString();
            ErrorRaised?.Invoke("视图载荷缺 view 字段：" + snippet[..Math.Min(240, snippet.Length)]);
            return;
        }

        UiMatchView view;
        UiBattleActions actions;
        try
        {
            view = viewNode.Deserialize<UiMatchView>(ViewJson)
                ?? throw new JsonException("view 为空");
            actions = actionsNode is null
                ? new UiBattleActions()
                : actionsNode.Deserialize<UiBattleActions>(ViewJson) ?? new UiBattleActions();
        }
        catch (JsonException error)
        {
            ErrorRaised?.Invoke("视图载荷解析失败：" + error.Message);
            return;
        }

        view = ResolveArt(view);
        var before = _view;
        _view = view;
        _actions = IsObserver ? new() : actions;
        actions = _actions;
        MatchId = view.MatchId;
        var wasPending = _mulliganPending;
        _mulliganPending = !IsObserver && view.Phase == "mulligan" && actions.MulliganOpen;
        ProjectionReady?.Invoke(view, actions);

        // The server sends a batch of causal segments followed by its authoritative view.
        // Translate once with both snapshots; removed/deployed cards cannot be resolved
        // correctly against the previous board alone. Silent changes also settle via this queue.
        var entries = _pendingSegments.Values.SelectMany(s => s["entries"]?.AsArray()
            ?? new JsonArray()).ToArray();
        if (_pendingSegments.Count > 0) _lastSegmentSequence = _pendingSegments.Keys.Last();
        _pendingSegments.Clear();
        if (before is not null && before.MatchId == view.MatchId)
        {
            var steps = RemotePresentationTranslator.Translate(entries, before, view, Seat, ArtPathResolver);
            PresentationReady?.Invoke(new UiPresentationResolution(MatchId, steps, view)
                { Before = before, Sequence = _lastSegmentSequence }, actions);
        }

        // 上升沿触发一次：换牌面板只在"刚进入 mulligan"时弹，不能每帧重弹。
        // 必须在 ProjectionReady 之后——面板要用 CurrentView 渲染。
        if (_mulliganPending && !wasPending) MulliganRequested?.Invoke();
    }

    /// <summary>
    /// 选靶请求（<c>notify/target.request</c>）。
    ///
    /// <para>
    /// 载荷是服务端 <c>McpService.PendingJson</c> 的形状：单槽（<c>selector/min/max/candidates</c>），
    /// 每个候选带**1 起的序号** <c>index</c>——<c>target.submit</c> 要的正是这组序号。
    /// 所以 <see cref="UiChoice.Id"/> 直接用序号字符串，提交时原样回传，
    /// 不需要维护 uid ↔ 引擎 ref 的反查表。
    /// </para>
    ///
    /// <para>
    /// <b>先认领再上屏</b>：应答是"先到先服务"，不 <c>target.claim</c> 就可能被同机的 agent 抢先提交，
    /// 玩家点下去只会收到"已被应答"。
    /// </para>
    /// </summary>
    private void ApplyTargetRequest(JsonNode? payload)
    {
        if (payload is not JsonObject body) return;

        var sessionId = body["sessionId"]?.GetValue<string>() ?? "";
        if (sessionId.Length == 0) return;

        var allowed = new List<UiChoice>();
        _picksByChoiceId.Clear();
        _refsByChoiceId.Clear();
        if (body["candidates"] is JsonArray candidates)
            foreach (var node in candidates)
            {
                if (node?["alive"]?.GetValue<bool>() is false) continue;
                var index = node?["index"]?.GetValue<int>() ?? 0;
                if (index <= 0) continue;

                var kind = node?["kind"]?.GetValue<string>() ?? "entity";
                var reference = node?["ref"]?.GetValue<string>() ?? "";
                var name = node?["name"]?.GetValue<string>();
                var id = index.ToString(CultureInfo.InvariantCulture);

                _picksByChoiceId[id] = index;
                if (reference.Length > 0) _refsByChoiceId[id] = reference;
                allowed.Add(new UiChoice(
                    id,
                    string.IsNullOrWhiteSpace(name) ? (reference.Length > 0 ? reference : id) : name,
                    kind is "card" or "unit" ? ChoiceKind.CardDefinition : ChoiceKind.Reference));
            }

        var selector = body["selector"]?.GetValue<string>() ?? "";
        var isDrag = string.Equals(body["mode"]?.GetValue<string>(), "Drag", StringComparison.OrdinalIgnoreCase);
        var slotKind = allowed.Count > 0 && allowed.All(choice => choice.Kind == ChoiceKind.CardDefinition)
            ? ChoiceKind.CardDefinition
            : ChoiceKind.Reference;

        var slot = new UiChoiceSlot(
            selector, slotKind,
            body["min"]?.GetValue<int>() ?? 0,
            body["max"]?.GetValue<int>() ?? 1,
            allowed)
        {
            // 拖拽，或候选本身就是棋盘实体／总部 → 走拖箭头那条交互。
            BoardTargets = isDrag || allowed.Any(choice => choice.Kind == ChoiceKind.Reference),
        };

        _targetSessionId = sessionId;
        _targetPending = true;

        if (selector == "mulligan" && _mulliganReplacements is { } replacements)
        {
            _mulliganReplacements = null;
            var picks = _refsByChoiceId.Where(p => replacements.Contains(p.Value)).Select(p => _picksByChoiceId[p.Key]).ToArray();
            _targetPending = false;
            if (picks.Length != replacements.Count) { _ = CancelTargetAsync(sessionId); HintRequested?.Invoke("换牌候选已变化，请重新选择。"); MulliganRequested?.Invoke(); }
            else _ = SubmitTargetAsync(sessionId, picks);
            return;
        }

        // 玩家已经点明了"打谁"：候选里能找到就直接提交，不再让人点第二次。
        if (_desiredTargetRef is { Length: > 0 } wanted && (!_desiredGap || selector == "slot"))
        {
            _desiredTargetRef = null; _desiredGap = false;
            var hit = allowed.FirstOrDefault(choice =>
                _refsByChoiceId.TryGetValue(choice.Id, out var reference) && reference == wanted);
            if (hit is not null && _picksByChoiceId.TryGetValue(hit.Id, out var index))
            {
                _targetPending = false;
                Console.WriteLine($"[net] 自动命中选靶目标 ref={wanted} pick={index}");
                _ = SubmitTargetAsync(sessionId, [index]);
                return;
            }

            // 指定了目标却不在候选里（典型：敌方前线还有单位时打 HQ）。
            // 必须**主动取消**——把请求挂在那儿等超时，后面每条命令都会吃
            // "当前有未应答的选靶请求"（实测连出牌都被堵死）。
            _targetPending = false;
            Console.WriteLine($"[net] 选靶目标不可用 ref={wanted}，取消本次动作（候选 {allowed.Count} 个）");
            _ = CancelTargetAsync(sessionId);
            return;
        }

        var request = new UiTargetRequest(sessionId, [slot])
        {
            SelectorName = selector,
            IsDrag = isDrag,
        };

        // 无人值守：自己答完，别把请求挂在那儿。
        if (AutoResolveTargets)
        {
            var auto = allowed.FirstOrDefault();
            _targetPending = false;
            if (auto is not null && _picksByChoiceId.TryGetValue(auto.Id, out var autoIndex))
            {
                Console.WriteLine($"[net] 无人值守应答选靶 selector={selector} pick={autoIndex}");
                _ = SubmitTargetAsync(sessionId, [autoIndex]);
            }
            else
            {
                _ = CancelTargetAsync(sessionId);
            }
            return;
        }

        // 🔴 不要 target.claim：服务端 TargetTools.SubmitAsync 把 claimant 写死成
        // TargeterBridgeAdapter.ClaimantService，认领成 "host" 之后提交会被判
        // "会话与请求不符（先到先服务）"。想做 host 优先，得引擎先给 target.submit 加 claimant 参数。
        TargetRequested?.Invoke(request, new RemoteTargetResponder(this, sessionId));
    }

    private async Task SubmitTargetAsync(string sessionId, IReadOnlyList<int> picks)
    {
        var command = _targetCommand;
        try
        {
            var result = await _client.CallAsync("target.submit", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["picks"] = new JsonArray([.. picks.Select(pick => (JsonNode)JsonValue.Create(pick)!)]),
            }).ConfigureAwait(false);
            if (result.Ok)
            {
                // 动作已被接受：后续演出会把该单位的"待定"清掉（Presented），这里只 relinquish 记账。
                if (ReferenceEquals(command, _targetCommand)) _targetCommand = null;
                return;
            }
            ErrorRaised?.Invoke($"target.submit: {result.Code} {result.Message}");
            RefuseTargeting(command);
        }
        catch (Exception error)
        {
            ErrorRaised?.Invoke("target.submit: " + error.Message);
            RefuseTargeting(command);
        }
    }

    /// <summary>
    /// 第二段（选靶）没成，必须把「发起它的那条命令」判为被拒。
    /// <para>
    /// 否则 UI 的 <c>BattleInteractionStateMachine</c> 会一直把该单位当"待定"，
    /// 之后这名单位的所有命令都在 <c>Submit</c> 开头被静默丢弃——表现为"点到非法目标后这个单位再也打不了，
    /// 直到它自己的状态更新（投影刷新）才恢复"。2026-10-24 实机反馈。
    /// </para>
    /// </summary>
    private void RefuseTargeting(UiCommand? command)
    {
        if (command is null) return;
        if (ReferenceEquals(command, _targetCommand)) _targetCommand = null;
        // 留一行可检索的日志：实机"这个单位再也打不了"时，先看有没有这条。
        Console.WriteLine($"[net] targeting refused -> unlock {command.GetType().Name} actor={BattleInteractionActorName(command)}");
        CommandRefused?.Invoke(command);
    }

    private static string BattleInteractionActorName(UiCommand command) => command switch
    {
        CommandUnit unit => unit.Uid,
        AttackUnit attack => attack.AttackerUid,
        MoveUnit move => move.Uid,
        RetreatUnit retreat => retreat.Uid,
        UseKeywordSkill skill => skill.Uid,
        PlayCard play => play.Uid,
        _ => "-",
    };

    private async Task CancelTargetAsync(string sessionId)
    {
        var command = _targetCommand;
        try
        {
            var result = await _client.CallAsync("target.cancel",
                new JsonObject { ["sessionId"] = sessionId }).ConfigureAwait(false);
            if (!result.Ok) ErrorRaised?.Invoke($"target.cancel: {result.Code} {result.Message}");
        }
        catch (Exception error)
        {
            ErrorRaised?.Invoke("target.cancel: " + error.Message);
        }
        // 取消＝这次动作没有发生，同样要判被拒（无论 cancel 本身成功与否）。
        RefuseTargeting(command);
    }

    /// <summary>
    /// 无人值守：所有选靶请求自动应答（选第一个候选），**不**触发 <see cref="TargetRequested"/>。
    ///
    /// <para>
    /// 给自动对战/探针用。正常对局必须为 false——否则玩家看不到选靶面板。
    /// 存在的理由：部署类效果也会走选靶（<c>play.unit</c> 返回 <c>targeting</c>），
    /// 无人值守时如果不自动应答，请求会挂在服务端等超时，后续每条命令都吃"有待决请求"。
    /// </para>
    /// </summary>
    public bool AutoResolveTargets { get; set; }

    /// <summary>把 UI 的选择（序号字符串）翻译成引擎要的 picks。</summary>
    private sealed class RemoteTargetResponder : IUiTargetResponder
    {
        private readonly RemoteMatchFeed _feed;
        private readonly string _sessionId;

        public RemoteTargetResponder(RemoteMatchFeed feed, string sessionId)
        {
            _feed = feed;
            _sessionId = sessionId;
        }

        public bool Complete(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> selectionsBySlot)
        {
            var id = requestId.Length > 0 ? requestId : _sessionId;
            var picks = new List<int>();
            foreach (var selection in selectionsBySlot.Values)
                foreach (var choiceId in selection)
                    if (_feed._picksByChoiceId.TryGetValue(choiceId, out var index))
                        picks.Add(index);

            // 空选择＝取消：拖拽松手落在没有目标的地方就是这条路径。
            if (picks.Count == 0) return Cancel(id);

            _feed._targetPending = false;
            _ = _feed.SubmitTargetAsync(id, picks);
            return true;
        }

        public bool Cancel(string requestId)
        {
            var id = requestId.Length > 0 ? requestId : _sessionId;
            _feed._targetPending = false;
            _ = _feed.CancelTargetAsync(id);
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _client.NotificationReceived -= OnNotification;
        _gate.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
