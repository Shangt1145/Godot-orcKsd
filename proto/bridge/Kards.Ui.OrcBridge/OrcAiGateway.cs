using System.Diagnostics.CodeAnalysis;
using Orc.Ai;
using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Ai;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Orc.Game.Targeting;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// <see cref="IAiMatchGateway"/> 的游戏内实现：把 AiPilot 的中性操作面接到本局的引擎入口与 targeter 桥。
/// AI 动作链期间，发生的选择器由 <see cref="OrcTargeterBridge"/> 的 AI 模式挂起，决策权交给 AiPilot
/// （HeuristicPolicy：击杀优先 → 敌方总部 → 最强目标），不再走玩家的选择面板，也不再用贪心 AutoRespond。
/// 状态机：RunChainAsync 启动引擎调用 → AdvanceAsync 在「调用结束」与「选择器挂起」之间往返 →
/// SubmitAsync 把 AiPilot 的选择提交回引擎并继续推进。
/// </summary>
public sealed class OrcAiGateway : IAiMatchGateway
{
    private readonly Match _match;
    private readonly OrcTargeterBridge? _bridge;
    private readonly int _seat;
    private Task? _activeAction;
    private TaskCompletionSource<AiActionOutcome>? _actionResult;
    private ISelectorInstance? _heldSelector;
    private string _sessionId = Guid.NewGuid().ToString("N");

    public OrcAiGateway(Match match, OrcTargeterBridge? bridge, int seat = 1)
    {
        _match = match ?? throw new ArgumentNullException(nameof(match));
        // bridge 为 null＝无 AI 模式（测试夹具的裸 Match）：选择器按桥原有策略应答，
        // AI 只负责动作决策与选靶，行为等同旧驱动。
        _bridge = bridge;
        if (seat is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(seat));
        _seat = seat;
    }

    public bool TryGetActiveMatch([NotNullWhen(true)] out Match? match)
    {
        match = _match;
        return _match.State == MatchState.InProgress;
    }

    public void ExpectClaimant(string claimant) { /* 单 AI 单玩家：选择器归属无竞争，无需路由。 */ }

    private Match Match => _match;

    // ---------- 动作链状态机 ----------

    private async Task<AiActionOutcome> RunChainAsync(Func<Task> start, CancellationToken ct)
    {
        _bridge?.BeginAiChain();
        _sessionId = Guid.NewGuid().ToString("N");
        var result = new TaskCompletionSource<AiActionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeAction = Task.Run(async () =>
        {
            try { await start().ConfigureAwait(false); result.TrySetResult(AiActionOutcome.Success()); }
            catch (OperationCanceledException) { result.TrySetResult(AiActionOutcome.Fail("动作被取消。")); }
            catch (Exception ex) { result.TrySetResult(AiActionOutcome.Fail($"{ex.GetType().Name}: {ex.Message}")); }
        }, ct);
        _heldSelector = null;
        return await AdvanceAsync(ct).ConfigureAwait(false);
    }

    /// <summary>推进当前动作：等「引擎调用结束」或「选择器挂起」；后者转成待决请求交回 AiPilot 决策。</summary>
    private async Task<AiActionOutcome> AdvanceAsync(CancellationToken ct)
    {
        var engine = _activeAction!;
        while (!engine.IsCompleted)
        {
            var signal = _bridge?.AiSignal;
            if (signal is null) break; // 无桥（或链已结束）：只等引擎调用跑完
            var settled = await Task.WhenAny(engine, signal).ConfigureAwait(false);
            if (!ReferenceEquals(settled, signal)) break; // 引擎调用先结束
            var selector = _bridge!.TakeHeldAiSelector();
            if (selector is null) continue;
            _heldSelector = selector;
            return AiActionOutcome.Targeting(Translate(selector, _sessionId));
        }
        _bridge?.EndAiChain();
        var outcome = await _actionResult!.Task.ConfigureAwait(false);
        _activeAction = null;
        _actionResult = null;
        return outcome;
    }

    public async Task<AiActionOutcome> SubmitAsync(string sessionId, IReadOnlyList<int> picks, bool cancel,
        CancellationToken ct, string claimant)
    {
        var selector = _heldSelector;
        if (selector is null || !string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
            return AiActionOutcome.Fail("没有待决的选择请求（会话不符或已结束）。");
        _heldSelector = null;
        if (_bridge is null || !_bridge.SubmitAiPick(selector, picks, cancel))
            return AiActionOutcome.Fail("选择提交被引擎拒绝（候选不符或已失效）。");
        return await AdvanceAsync(ct).ConfigureAwait(false);
    }

    private static AiPending Translate(ISelectorInstance selector, string sessionId)
    {
        var presentation = selector.Presentation;
        var candidates = presentation.Candidates
            .Select((reference, index) => new AiCandidate(index, KindOf(reference), OrcRefs.KeyOf(reference.Value)))
            .ToArray();
        return new AiPending(sessionId, selector.SelectorName, presentation.Min, presentation.Max,
            candidates, presentation.Identifiers ?? []);
    }

    private static string KindOf(Ref<Entity> reference) => reference.Value switch
    {
        UnitCard => "unit",
        Hq => "hq",
        _ => "card"
    };

    // ---------- 入口转发（与人类路径同一批引擎门禁入口） ----------

    public async Task<AiActionOutcome> PlayUnitAsync(string cardRef, int seat, int slot, CancellationToken ct)
        => await RunChainAsync(async () =>
        {
            if (Resolve(cardRef) is not UnitCard unit) throw new KeyNotFoundException(cardRef);
            // 部署槽由 targeter 会话给出（AiPilot 对 slot 选择器取最靠前候选），slot 参数不入引擎。
            await Match.PlayManager.BeginUnitPrePlayAsync(unit, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    public async Task<AiActionOutcome> PlayCommandPreAsync(string cardRef, CancellationToken ct)
        => await RunChainAsync(async () =>
        {
            if (Resolve(cardRef) is not OrderCard order) throw new KeyNotFoundException(cardRef);
            await Match.PlayManager.BeginOrderPrePlayAsync(order, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    public async Task<AiActionOutcome> UseCounterAsync(string cardRef, CancellationToken ct)
        => await RunChainAsync(async () =>
        {
            if (Resolve(cardRef) is not CounterCard counter) throw new KeyNotFoundException(cardRef);
            await Match.PlayManager.UseCounterAsync(counter, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    public async Task<AiActionOutcome> BeginAttackAsync(string unitRef, CancellationToken ct)
        => await RunChainAsync(async () =>
        {
            if (Resolve(unitRef) is not UnitCard unit) throw new KeyNotFoundException(unitRef);
            await Match.CommandManager.BeginAttackAsync(unit, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    public async Task<AiActionOutcome> BeginMoveAsync(string unitRef, CancellationToken ct)
        => await RunChainAsync(async () =>
        {
            if (Resolve(unitRef) is not UnitCard unit) throw new KeyNotFoundException(unitRef);
            await Match.CommandManager.BeginMoveAsync(unit, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    public async Task<AiActionOutcome> EndTurnAsync(CancellationToken ct)
        => await RunChainAsync(() => Match.EndTurn(ct), ct).ConfigureAwait(false);

    public async Task<AiActionOutcome> MulliganDoneAsync(int seat, CancellationToken ct)
    {
        var player = Match.Players.ElementAtOrDefault(seat);
        if (player is null) return AiActionOutcome.Fail($"座位不存在：{seat}");
        await Match.MulliganDone(player, ct).ConfigureAwait(false);
        return AiActionOutcome.Success();
    }

    public async Task<AiCommandAvailability> GetCommandAvailabilityAsync(string unitRef, CancellationToken ct)
    {
        if (Resolve(unitRef) is not UnitCard unit) return new AiCommandAvailability(false, false);
        var availability = Match.CommandManager.GetCommandAvailability(unit);
        return new AiCommandAvailability(availability.Attack.CanUse, availability.Move.CanUse);
    }

    /// <summary>引用键 → 引擎卡（遍历双方总部、手牌、场上；Uid＝entity.Id 的 N 形态）。</summary>
    private Card? Resolve(string uid)
    {
        foreach (var player in Match.Players)
        {
            if (player.Hq is { } hq && OrcRefs.KeyOf(hq) == uid) return hq;
            foreach (var card in player.Hand)
                if (OrcRefs.KeyOf(card) == uid) return card;
        }
        var battlefield = Match.Battlefield;
        foreach (var slot in battlefield.PlayerASupportLine
            .Concat(battlefield.FrontLine)
            .Concat(battlefield.PlayerBSupportLine))
            if (slot.Occupant is Card occupant && OrcRefs.KeyOf(occupant) == uid) return occupant;
        return null;
    }
}
