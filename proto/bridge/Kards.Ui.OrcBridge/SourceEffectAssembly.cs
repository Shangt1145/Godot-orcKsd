using System.Text.Json;
using Kards.Ui.Contracts;
using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Orc.Game.Triggers;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// Reviewed source DSL adapters. All state changes use the engine's rule services.
/// <para>
/// **v4 装配形态（S13 迁移）**：旧实现把主动效果挂成 <c>OrderCard.AddEffect(ActiveEffect&lt;TView&gt;)</c>、
/// 把反制的死亡触发挂成 <c>Trigger&lt;CardEventView&gt;</c> + <c>Engine.Bus.Mount</c>。
/// v4 删除了 <c>ActiveEffect</c>／<c>Trigger</c>／<c>Bus</c> 三者，主动效果的承载点改成了**引擎成员上的边**：
/// </para>
/// <list type="bullet">
/// <item>指令主动效果 → <c>engine#card.played</c>（<see cref="GameUpdates.CardPlayed"/>）。
///   该信号由 <c>PlayManager</c> 在 <c>engine#order.cast</c> 的宣告点（priority 100）发出，
///   位次在**反制检查之后**（被反制的指令不发本信号 ⇒ 效果不触发）、在**扣费/离手之前**——
///   与旧 <c>ICastAction</c> 在 <c>OnResolve</c> 里执行的位次等价。</item>
/// <item>反制的「友方单位死亡」被动 → <c>engine#card.died</c>（<see cref="GameUpdates.CardDied"/>），
///   仍是全局一条边 + 卡内 <c>IsActive</c> 闸门，与旧 <c>Trigger.Passive + Bus.Mount</c> 同构。</item>
/// </list>
/// <para>
/// **为什么不用 <c>engine#order.cast</c> 直接挂结算边**：那要依赖该成员内部的优先级布局（0＝反制、
/// 100＝宣告点、自有边最后），属于引擎流程的私有排序；<c>card.played</c> 是引擎文档里写明的主动效果承载点
/// （见 <c>IOrderAction</c>／<c>OrderCard</c> 注释），排序由引擎保证。代价是拿不到流程载荷里的
/// <c>Target</c>（<c>card.played</c> 载荷只有 Card／Player／Source），目标一律取自预打出捕获——
/// 见 <see cref="TakeCastSelection"/> 的说明。
/// </para>
/// <para>
/// **异常口径**：v4 成员边不做隔离，异常直接上抛、打出段不发生（<c>Signals.cs</c>："隔离＋继续"随 b5 退役）。
/// 因此这里凡是「拿不到本该有的选靶/选项」都**明确抛错**，不静默跳过——静默会让牌打出去却什么都没发生。
/// </para>
/// </summary>
internal sealed class SourceEffectAssembly : IDisposable
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "USG/commands/_7", "USG/commands/_11", "USG/commands/27", "USG/commands/_13",
        "deran/command/_12", "deran/command/_13", "deran/command/_14", "deran/command/_18", "deran/command/25",
        "av76/command/-5", "av76/command/-7", "av76/command/-22"
    };
    public static bool Supports(string cardId) => Supported.Contains(cardId);
    private readonly Match _match;
    private readonly Action<string> _targetFailure;
    private readonly IReadOnlyDictionary<string, JsonElement> _plans;
    /// <summary>本装配挂出去的全部成员边（句柄即权限；<see cref="Dispose"/> 逐条撤销——取代旧的 <c>Bus.UnmountOwner(this)</c>）。</summary>
    private readonly List<IDisposable> _edges = [];
    private bool _disposed;
    /// <summary>
    /// 预打出阶段捕获的完整选择。指向只有三种：单选择（玩家选一个）、全部指向（所有单位）、随机指向（随机一个）；
    /// 后两种由效果自己遍历/抽取，不需要传目标。所以只有"单选择"的目标与 chooseOne 的选项需要我们留住，
    /// 施放时取走并清空。
    /// </summary>
    private Selection? _pendingCapture;
    public SourceEffectAssembly(Match match, IReadOnlyDictionary<string, JsonElement> plans, Action<string> targetFailure)
    {
        _match = match;
        _targetFailure = targetFailure;
        _plans = plans;
        foreach (var card in match.Players.SelectMany(p => p.Hand.OfType<CardBase>()
            .Concat(Enumerable.Range(0, p.Deck.Count).Select(i => p.Deck.PeekInstance(i)))))
        {
            if (!match.CardLibrary.TryGetRegisteredId(card.Definition, out var id) || !plans.TryGetValue(id, out var declarations)) continue;
            var plan = declarations.EnumerateArray().Single();
            if (card is OrderCard command)
            {
                // 预打出捕获（S2）：v4 保留了 AddPrePlayHandler（接为 host#pre-play 的自有边），签名不变。
                command.AddPrePlayHandler("source/choose", (view, ctx, ct) => CaptureAsync(command, plan, view, ct));
                // 主动效果：engine#card.played 上的一条外来边（见类注释的位次论证）。
                // 闸门＝引用相等：card.played 对单位部署也会发，只有本卡自己的宣告才执行本效果。
                _edges.Add(match.Engine.AttachMemberEdge(
                    GameUpdates.CardPlayed,
                    "source/execute:" + id,
                    this,
                    MemberEdges.For<CardEventView>((view, _, ct) => CastAsync(command, plan, view, ct))));
            }
            else if (card is CounterCard counter)
            {
                // Activation/cancellation and reservation/refund stay in UseCounterAsync. The source
                // condition listens to an actual death, not the engine's activation notification.
                _edges.Add(match.Engine.AttachMemberEdge(
                    GameUpdates.CardDied,
                    "source/friendly-death:" + id,
                    this,
                    MemberEdges.For<CardEventView>((view, _, ct) => OnFriendlyDeathAsync(counter, plan, view, ct))));
            }
        }
    }

    private sealed class Selection
    {
        public Dictionary<string, Card> Targets { get; } = new(StringComparer.Ordinal);
        public string? Option { get; set; }
    }

    /// <summary>
    /// 指令主动效果的执行入口（<c>engine#card.played</c> 的订阅体）。
    /// 只处理本卡自己的宣告：<c>card.played</c> 是全局信号，单位的部署宣告也走它。
    /// </summary>
    private async Task CastAsync(OrderCard card, JsonElement plan, CardEventView view, CancellationToken ct)
    {
        if (_disposed || !ReferenceEquals(view.Card, card)) return;
        await ExecuteAsync(card, plan.GetProperty("actions"), TakeCastSelection(plan), ct);
    }

    /// <summary>
    /// 反制的「友方单位死亡」被动（<c>engine#card.died</c> 的订阅体）。
    /// 消耗与更新语义逐字沿用旧实现：置未激活 → 离手（不退费）→ 发 <c>counter.triggered</c> → 执行 actions。
    /// </summary>
    private async Task OnFriendlyDeathAsync(CounterCard counter, JsonElement plan, CardEventView view, CancellationToken ct)
    {
        if (_disposed || !counter.GetData<CounterActivationData>().IsActive || counter.Owner is not { } owner
            || view.Card is not UnitCard dead || !ReferenceEquals(dead.Owner, owner)) return;
        counter.GetData<CounterActivationData>().IsActive = false;
        owner.Hand.Remove(counter); // Consume the reserved card once; spending is not refunded.
        await _match.Engine.Emit(GameUpdates.CounterTriggered, new Dictionary<string, object?>
        {
            [GameUpdates.PayloadCard] = counter, [GameUpdates.PayloadPlayer] = owner,
            ["UiCounterStage"] = "triggered"
        }, ct);
        await ExecuteAsync(counter, plan.GetProperty("actions"), new Selection(), ct);
    }

    public IReadOnlyList<UiOrderTargeting> ReadOrderTargets(Player viewer)
    {
        var units = new[] { _match.Battlefield.PlayerASupportLine, _match.Battlefield.FrontLine, _match.Battlefield.PlayerBSupportLine }
            .SelectMany(line => line.Members).OfType<UnitCard>().ToArray();
        var options = new List<UiOrderTargeting>();
        foreach (var card in viewer.Hand.OfType<OrderCard>())
        {
            if (!_match.CardLibrary.TryGetRegisteredId(card.Definition, out var id) || !_plans.TryGetValue(id, out var declarations)) continue;
            var plan = declarations.EnumerateArray().Single();
            if (!plan.TryGetProperty("targets", out var targets) || targets.GetArrayLength() == 0) continue;
            var side = Text(targets[0], "side", "any");
            options.Add(new(OrcRefs.KeyOf(card), units.Where(unit => IsOrderTarget(card, unit, side))
                .Select(OrcRefs.KeyOf).ToArray()));
        }
        return options;
    }

    private static bool IsOrderTarget(OrderCard card, UnitCard unit, string side) =>
        unit.GetData<UnitStateData>().Line is not null && !unit.GetData<UnitStateData>().IsDestroyed
        && (side == "any" || ReferenceEquals(unit.Owner, card.Owner) == (side == "friendly"));

    private async Task CaptureAsync(OrderCard card, JsonElement plan, CardTriggerView view, CancellationToken ct)
    {
        var selection = new Selection();
        var result = await _match.TargeterManager.RunAsync(async flow =>
        {
            if (plan.TryGetProperty("targets", out var targets))
                foreach (var target in targets.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = target.GetProperty("id").GetString()!;
                    var side = Text(target, "side", "any");
                    var candidates = new[] { _match.Battlefield.PlayerASupportLine, _match.Battlefield.FrontLine, _match.Battlefield.PlayerBSupportLine }.SelectMany(line => line.Members).OfType<UnitCard>().Where(u => IsOrderTarget(card, u, side)).Select(u => u.Ref).ToArray();
                    if (candidates.Length == 0) return TargeterResult.Failed(TargeterFailureReason.NoAvailableCandidates);
                    var step = await flow.Step(SelectorTemplates.FieldUnit,
                        new ReferenceSetParameter(candidates, 1, 1,
                            domainValidator: r => r.Value is UnitCard u && IsOrderTarget(card, u, side),
                            tag: new OrderSelectorTag(name)));
                    while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
                        step = await flow.Retry<Ref<Entity>>();
                    if (step.IsCancelled) return TargeterResult.Cancelled();
                    if (!step.IsOk) return TargeterResult.FromSelectorFailure(step.Failure);
                    selection.Targets.Add(name, (Card)step.Value!.Value);
                }
            var choose = plan.GetProperty("actions").EnumerateArray().FirstOrDefault(a => Text(a, "op") == "chooseOne");
            if (choose.ValueKind == JsonValueKind.Object)
            {
                var labels = choose.GetProperty("options").EnumerateArray().Select((o, i) =>
                    new KeyValuePair<string, string>(i.ToString(), o.GetProperty("label").GetString()!)).ToDictionary(p => p.Key, p => p.Value);
                var step = await flow.Step(SelectorTemplates.Option,
                    new IdentifierSetParameter(labels.Keys, 1, 1, new OrderSelectorTag("choice", labels)));
                while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
                    step = await flow.Retry<string>();
                if (step.IsCancelled) return TargeterResult.Cancelled();
                if (!step.IsOk) return TargeterResult.FromSelectorFailure(step.Failure);
                selection.Option = step.Value;
            }
            return TargeterResult.Ok();
        });
        if (!result.IsOk)
        {
            // 取消/失败：预打出作废，残留的选择不能留到下一张卡（否则下一张会拿到这次的选项）。
            _pendingCapture = null;
            _targetFailure(result.Reason?.ToString() ?? result.Status.ToString());
            view.CaptureBox!.CancelPrePlay(); return;
        }
        ct.ThrowIfCancellationRequested();
        // 引擎新通道（PlayManager.ResolveOrderTarget）只认捕获箱里第一个可解析的 Card；
        // 复合的 Selection 对象传不过去，会在施放时解析成 null（目标全丢）。
        // 这里只交出一张卡，完整选择另存到 _pendingCapture 供施放侧取回选项。
        _pendingCapture = selection;
        view.CaptureBox!.Capture(selection.Targets.Values.FirstOrDefault());
    }

    private async Task ExecuteAsync(CardBase source, JsonElement actions, Selection selection, CancellationToken ct)
    {
        var runtime = EffectRuntime.ResolveFor(source) ?? throw new InvalidOperationException("Source effect runtime is unavailable.");
        foreach (var action in actions.EnumerateArray())
        {
            if (_disposed || _match.State != MatchState.InProgress) return;
            ct.ThrowIfCancellationRequested();
            var op = Text(action, "op");
            switch (op)
            {
                case "draw":
                    // Empty-deck fatigue is not provided by this engine version. Drawing what remains
                    // prevents its empty-deck exception without fabricating a fatigue rule.
                    await runtime.DrawAsync(source, Math.Min(Number(action, "count"), source.Owner!.Deck.Count), ct); break;
                case "gainKreditSlot": await runtime.GainPointSlotsAsync(source, Number(action, "amount"), ct); break;
                case "gainKredits": await runtime.GainPointsAsync(source, Number(action, "amount"), ct); break;
                case "hqMaxUp":
                    await source.Owner!.Hq.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.HqHealth, Number(action, "amount"), this), ct); break;
                case "damageHQ":
                    await runtime.DamageAsync(_match.Players.Single(p => p != source.Owner).Hq, Number(action, "amount"), source, ct); break;
                case "chooseOne":
                    if (selection.Option is not { } choice)
                        throw new InvalidOperationException(
                            $"指令 '{source.Name}' 的选项结果缺失（预打出阶段没有捕获到 chooseOne 选项）：拒绝执行以避免选项错位。");
                    await ExecuteAsync(source, action.GetProperty("options")[int.Parse(choice)].GetProperty("actions"), selection, ct); break;
                case "conditional":
                    var condition = action.GetProperty("condition");
                    var types = condition.GetProperty("unitType").EnumerateArray().Select(t => t.GetString()!).ToArray();
                    if ((await runtime.SelectAsync(source, new("all", "friendly"), ct)).OfType<UnitCard>()
                        .Any(u => u.Definition.UnitTypes.Any(t => types.Contains(t.ToString(), StringComparer.OrdinalIgnoreCase))))
                        await ExecuteAsync(source, action.GetProperty("then"), selection, ct);
                    break;
                default:
                    var destinations = await TargetsAsync(source, action.GetProperty("target"), selection, runtime, ct);
                    foreach (var target in destinations)
                    {
                        switch (op)
                        {
                            case "damage": await runtime.DamageAsync(target, Number(action, "amount"), source, ct); break;
                            case "loseDefense":
                                await runtime.BuffAsync(target, 0, -((CardBase)target).Modifiers.GetEffectiveValue(CardStatFields.DeployCost), ct: ct); break;
                            case "buff":
                                var duration = Text(action, "duration") == "turn" ? EffectDuration.TurnEnd : EffectDuration.Permanent;
                                await runtime.BuffAsync(target, Number(action, "attack"), Number(action, "defense"), duration, ct);
                                await runtime.CostModAsync(target, Number(action, "opCostMod"), duration, ct); break;
                            case "grant": await runtime.GrantAsync(target, KeywordIds.Blitz, ct); break;
                            case "setOpCost":
                                await ((CardBase)target).Modifiers.AddModifierAsync(new SetModifier(CardStatFields.OperateCost, Number(action, "value"), this), ct); break;
                            default: throw new InvalidDataException($"Unassembled source action: {op}");
                        }
                    }
                    break;
            }
        }
    }

    private static async Task<IReadOnlyList<Card>> TargetsAsync(Card source, JsonElement target, Selection selection,
        EffectRuntime runtime, CancellationToken ct)
    {
        if (target.ValueKind == JsonValueKind.String) return [RequireTarget(source, selection, target.GetString()!)];
        if (target.TryGetProperty("id", out var id)) return [RequireTarget(source, selection, id.GetString()!)];
        return await runtime.SelectAsync(source, new(Text(target, "sel"), Text(target, "side")), ct);
    }

    /// <summary>
    /// 取预打出阶段捕获的目标。缺失＝明确失败（不静默跳过）：v4 的 <c>card.played</c> 载荷不带流程的
    /// <c>Target</c>，目标唯一来源就是预打出捕获；拿不到却继续执行会变成"效果打空"这种查不出来的错。
    /// </summary>
    private static Card RequireTarget(Card source, Selection selection, string key) =>
        selection.Targets.TryGetValue(key, out var card)
            ? card
            : throw new InvalidOperationException(
                $"指令 '{source.Name}' 的选靶结果里没有 '{key}'（预打出阶段未捕获到该目标）：拒绝执行以避免效果打空。");

    private static string Text(JsonElement e, string key, string fallback = "") =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.GetString()! : fallback;
    private static int Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.GetInt32() : 0;

    /// <summary>撤销本装配挂出的全部成员边（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pendingCapture = null;
        foreach (var edge in _edges) edge.Dispose();
        _edges.Clear();
    }

    /// <summary>
    /// 施放侧取走预打出留下的完整选择。
    /// <para>
    /// 与旧 <c>TakeCastSelection(plan, view)</c> 的差别：旧实现会拿 <c>OrderCastView.Target</c>（流程载荷里的
    /// 施放目标）补一遍 <c>selection.Targets</c>；v4 的 <c>card.played</c> 载荷没有 <c>Target</c> 键，
    /// 这条补位路径没有了。目标改由预打出捕获**单独**承载——两者本来就是同一张卡
    /// （捕获箱里第一个可解析的 Card 就是 <c>PlayManager.ResolveOrderTarget</c> 交给流程的那一个），
    /// 所以语义等价；差异只在于捕获缺失时旧实现会退化成"无目标"，这里改成明确抛错。
    /// </para>
    /// </summary>
    private Selection TakeCastSelection(JsonElement plan)
    {
        var captured = _pendingCapture;
        _pendingCapture = null;
        if (captured is not null) return captured;

        if (plan.TryGetProperty("targets", out var targets) && targets.GetArrayLength() > 0)
            throw new InvalidOperationException(
                "指令的选靶结果缺失（预打出捕获未发生）：本卡的主动效果无法确定目标，拒绝执行。");
        if (plan.GetProperty("actions").EnumerateArray().Any(a => Text(a, "op") == "chooseOne"))
            throw new InvalidOperationException(
                "指令的选项结果缺失（预打出捕获未发生）：本卡的 chooseOne 无法确定分支，拒绝执行。");
        return new Selection();
    }
}
