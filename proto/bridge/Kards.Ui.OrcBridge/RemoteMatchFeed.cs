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

/// <summary>
/// 远端对局数据源：引擎跑在 <c>Orc.Server</c> 里，本类只做协议翻译。
///
/// <para>
/// **第一阶段范围（当前）**：连接、`initialize`、`room.*`、`notify/view`（全量投影）、
/// `match.end_turn` / `match.concede` 两条命令。目标是"能连上、能看到盘、能结束回合"。
/// </para>
///
/// <para>
/// **尚未接的两块**（有意留白，不是遗漏）：
/// ① `notify/segment` → <c>UiPresentationResolution</c>：本地翻译器 <c>OrcUpdateTranslator</c>
///    的入参是引擎 <c>Card</c> 对象，远端只有投影。需要一条"用 uid 在投影里查卡"的翻译路径，
///    在此之前远端是**无演出**的（状态仍然正确，只是没有逐段动画）。
/// ② `notify/target.request` 的选靶应答：需要先实现 <c>IUiTargetResponder</c> 的远端版本。
/// </para>
/// </summary>
public sealed class RemoteMatchFeed : IMatchFeed, IAsyncDisposable
{
    private readonly OrcClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _seatToken;
    private string? _targetSessionId;
    /// <summary>选靶候选：UI 侧的 choice id（序号字符串）→ 引擎要的 picks 序号。</summary>
    private readonly Dictionary<string, int> _picksByChoiceId = new(StringComparer.Ordinal);

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
    private bool _disposed;

    private RemoteMatchFeed(OrcClient client, int seat)
    {
        _client = client;
        Seat = seat;
        _client.NotificationReceived += OnNotification;
    }

    /// <summary>本端占用的座位（0 或 1）。</summary>
    public int Seat { get; }

    public string MatchId { get; private set; } = "";
    public UiMatchView? CurrentView => _view;
    public UiBattleActions CurrentActions => _targetPending ? new() : _actions;
    public bool IsSubmitting { get; private set; }
    public bool MulliganPending => _mulliganPending;
    public bool TargetPending => _targetPending;
    public string? MulliganRequestId => _mulliganRequestId;
    public Func<UiActionInteractionState>? InteractionStateReader { get; set; }

    public event Action<UiMatchView, UiBattleActions>? ProjectionReady;
    public event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;
    public event Action<IReadOnlyList<UiOrderImpact>, UiMatchView, UiBattleActions>? CombatReady;
    public event Action? MulliganRequested;
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
        try
        {
            await client.InitializeAsync(new InitializeParams { ClientName = "godot" }, ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new RemoteMatchFeed(client, seat);
    }

    /// <summary>读房间快照（座位、可连地址、卡包状态）。</summary>
    public async Task<RemoteToolResult> RoomInfoAsync(CancellationToken ct = default)
    {
        var result = await _client.CallAsync("room.info", new JsonObject(), ct).ConfigureAwait(false);
        return new RemoteToolResult(result.Ok, result.Code, result.Message, result.Details);
    }

    /// <summary>
    /// 声明座位。房主用 <c>kind:"local"</c>（服务端只接受回环来源），远端玩家用 <c>kind:"remote"</c>。
    /// 返回的 <c>details.seatToken</c> 必须持久化——它是断线重连的唯一凭据。
    /// </summary>
    public async Task<string?> ClaimSeatAsync(
        int seat, string kind, string? name, IReadOnlyList<string> deck, CancellationToken ct = default)
    {
        var arguments = new JsonObject
        {
            ["seat"] = seat,
            ["kind"] = kind,
            ["name"] = name ?? "",
            ["deck"] = new JsonArray([.. deck.Select(id => (JsonNode)JsonValue.Create(id)!)]),
        };
        var result = await _client.CallAsync("room.claim", arguments, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            ErrorRaised?.Invoke(result.Message ?? result.Code);
            return null;
        }

        // 记住本座令牌：room.start / room.resume 都靠它认人（服务端只允许房主开局）。
        _seatToken = result.Details?["seatToken"]?.GetValue<string>();
        return _seatToken;
    }

    /// <summary>房主给空座添加 AI（服务端进程内驱动，不经过协议）。</summary>
    public async Task<bool> AddAiAsync(int seat, IReadOnlyList<string> deck, string? name = null, CancellationToken ct = default)
    {
        var arguments = new JsonObject
        {
            ["seat"] = seat,
            ["name"] = name ?? "AI",
            ["deck"] = new JsonArray([.. deck.Select(id => (JsonNode)JsonValue.Create(id)!)]),
        };
        var result = await _client.CallAsync("room.add_ai", arguments, ct).ConfigureAwait(false);
        if (!result.Ok) ErrorRaised?.Invoke($"room.add_ai: {result.Code} {result.Message}");
        return result.Ok;
    }

    /// <summary>房主开局。远端玩家不调用。必须带本座令牌——服务端拒绝免令牌开局。</summary>
    public async Task<bool> StartMatchAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_seatToken))
        {
            ErrorRaised?.Invoke("room.start: 尚未占座（没有 seatToken），无法开局。");
            return false;
        }

        var arguments = new JsonObject { ["seatToken"] = _seatToken };
        var result = await _client.CallAsync("room.start", arguments, ct).ConfigureAwait(false);
        if (!result.Ok) ErrorRaised?.Invoke(result.Message ?? result.Code);
        return result.Ok;
    }

    /// <summary>本座令牌（未占座＝null）。断线重连时凭它 resume。</summary>
    public string? SeatToken => _seatToken;

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
            var result = await _client.CallAsync(tool, arguments, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                CommandRefused?.Invoke(command);
                // targeting / cancelled 不是错误：一个表示已挂起等选靶，一个是玩家取消。
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
        EndTurn => ("match.end_turn", new JsonObject()),
        Concede => ("match.concede", new JsonObject()),
        _ => (null, null),
    };

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
            _ => ("play.unit", new JsonObject
            {
                // slot 缺省取 0：指挥总部固定在支援线居中槽，槽 0 通常空闲。
                // 取错只会被服务端拒绝并回业务码，不会写坏状态。
                ["ref"] = play.Uid,
                ["owner"] = Seat,
                ["slot"] = play.SupportIndex ?? 0,
            }),
        };
    }

    private void OnNotification(RpcNotification notification)
    {
        switch (notification.Method)
        {
            case "notify/view":
                ApplyViewPayload(notification.Params);
                break;
            case "notify/match.started":
                MatchId = notification.Params?["matchId"]?.GetValue<string>() ?? MatchId;
                break;
            case "notify/segment":
                ApplySegments(notification.Params);
                break;
            case "notify/target.request":
                ApplyTargetRequest(notification.Params);
                break;
            case "notify/target.expired":
                _targetPending = false;
                TargetClosed?.Invoke();
                break;
        }
    }

    /// <summary>
    /// 演出段推送。`notify/segment` 的载荷是引擎事件条目，要用 <see cref="RemotePresentationTranslator"/>
    /// 按 `$ref` 在投影里查卡才能翻译成演出步骤。
    ///
    /// <para>
    /// **已知局限**：段里用的是"当前最新投影"作为 after 状态。若 view 推送比 segment 滞后，
    /// 个别步骤会因为查不到卡而被跳过（不会乱演，只是少一段）。要彻底解决得让服务端在段里带快照，
    /// 那是协议层的事，不在客户端硬猜。
    /// </para>
    /// </summary>
    private void ApplySegments(JsonNode? payload)
    {
        if (_view is null) return;
        if (payload?["segments"] is not JsonArray segments) return;

        foreach (var segment in segments)
        {
            if (segment?["entries"] is not JsonArray entries) continue;
            var nodes = entries.Select(entry => (JsonNode?)entry).ToArray();
            var steps = RemotePresentationTranslator.Translate(nodes, _view);
            if (steps.Count == 0) continue;

            var sequence = segment["sequence"]?.GetValue<long>() ?? 0;
            var resolution = new UiPresentationResolution(MatchId, steps, _view) { Sequence = sequence };
            PresentationReady?.Invoke(resolution, _actions);
        }
    }

    private void ApplyViewPayload(JsonNode? payload)
    {
        if (payload is null) return;
        var viewNode = payload["view"];
        var actionsNode = payload["actions"];
        if (viewNode is null) return;

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

        _view = view;
        _actions = actions;
        MatchId = view.MatchId;
        var wasPending = _mulliganPending;
        _mulliganPending = view.Phase == "mulligan";
        ProjectionReady?.Invoke(view, actions);

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
        var request = new UiTargetRequest(sessionId, [slot])
        {
            SelectorName = selector,
            IsDrag = isDrag,
        };

        _ = ClaimTargetAsync(sessionId);
        TargetRequested?.Invoke(request, new RemoteTargetResponder(this, sessionId));
    }

    private async Task ClaimTargetAsync(string sessionId)
    {
        try
        {
            var result = await _client.CallAsync("target.claim",
                new JsonObject { ["sessionId"] = sessionId, ["claimant"] = "host" }).ConfigureAwait(false);
            if (!result.Ok) ErrorRaised?.Invoke($"target.claim: {result.Code} {result.Message}");
        }
        catch (Exception error)
        {
            ErrorRaised?.Invoke("target.claim: " + error.Message);
        }
    }

    private async Task SubmitTargetAsync(string sessionId, IReadOnlyList<int> picks)
    {
        try
        {
            var result = await _client.CallAsync("target.submit", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["picks"] = new JsonArray([.. picks.Select(pick => (JsonNode)JsonValue.Create(pick)!)]),
            }).ConfigureAwait(false);
            if (!result.Ok) ErrorRaised?.Invoke($"target.submit: {result.Code} {result.Message}");
        }
        catch (Exception error)
        {
            ErrorRaised?.Invoke("target.submit: " + error.Message);
        }
    }

    private async Task CancelTargetAsync(string sessionId)
    {
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
    }

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
