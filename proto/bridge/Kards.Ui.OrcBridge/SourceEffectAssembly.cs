using System.Text.Json;
using Kards.Ui.Contracts;
using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Orc.Game.Triggers;

namespace Kards.Ui.OrcBridge;

/// <summary>Reviewed source DSL adapters. All state changes use the engine's rule services.</summary>
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
                command.AddPrePlayHandler("source/choose", (view, ctx, ct) => CaptureAsync(command, plan, view, ct));
                // S13：引擎删除了 OrderCard.AddActiveHandler，主动效果统一经 AddEffect + ActiveEffect 承载
                // （见 OrderCard 注释：施放时由 OrderFlow 按登记序执行 ICastAction）。
                command.AddEffect(new SourceOrderCast(this, command, plan));
            }
            else if (card is CounterCard counter)
            {
                // Activation/cancellation and reservation/refund stay in UseCounterAsync. The source
                // condition listens to an actual death, not the engine's activation notification.
                var trigger = new Trigger<CardEventView>("source/friendly-death", TriggerKind.Passive,
                    events: [new TriggerEvent<CardEventView>("source/counter", async (view, ctx, ct) =>
                    {
                        if (_disposed || !counter.GetData<CounterActivationData>().IsActive || counter.Owner is not { } owner
                            || view.Card is not UnitCard dead || !ReferenceEquals(dead.Owner, owner)) return;
                        counter.GetData<CounterActivationData>().IsActive = false;
                        owner.Hand.Remove(counter); // Consume the reserved card once; spending is not refunded.
                        await match.Engine.Emit(GameUpdates.CounterTriggered, new Dictionary<string, object?>
                        {
                            [GameUpdates.PayloadCard] = counter, [GameUpdates.PayloadPlayer] = owner,
                            ["UiCounterStage"] = "triggered"
                        }, ct);
                        await ExecuteAsync(counter, plan.GetProperty("actions"), new(), ct);
                    })], hooks: [GameUpdates.CardDied], owner: this);
                match.Engine.Bus.Mount(trigger);
            }
        }
    }

    private sealed class Selection
    {
        public Dictionary<string, Card> Targets { get; } = new(StringComparer.Ordinal);
        public string? Option { get; set; }
    }

    public IReadOnlyList<UiOrderTargeting> ReadOrderTargets(Player viewer)
    {
        var units = new[] { _match.Battlefield.PlayerASupportLine, _match.Battlefield.FrontLine, _match.Battlefield.PlayerBSupportLine }
            .SelectMany(line => line).Select(slot => slot.Occupant).OfType<UnitCard>().ToArray();
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
        unit.GetData<UnitStateData>().Position is not null && !unit.GetData<UnitStateData>().IsDestroyed
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
                    var candidates = new[] { _match.Battlefield.PlayerASupportLine, _match.Battlefield.FrontLine, _match.Battlefield.PlayerBSupportLine }.SelectMany(line => line).Select(slot => slot.Occupant).OfType<UnitCard>().Where(u => IsOrderTarget(card, u, side)).Select(u => u.Ref).ToArray();
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
                    await ExecuteAsync(source, action.GetProperty("options")[int.Parse(selection.Option!)].GetProperty("actions"), selection, ct); break;
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
        if (target.ValueKind == JsonValueKind.String) return [selection.Targets[target.GetString()!]];
        if (target.TryGetProperty("id", out var id)) return [selection.Targets[id.GetString()!]];
        return await runtime.SelectAsync(source, new(Text(target, "sel"), Text(target, "side")), ct);
    }
    private static string Text(JsonElement e, string key, string fallback = "") =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.GetString()! : fallback;
    private static int Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.GetInt32() : 0;
    public void Dispose() { _disposed = true; _pendingCapture = null; _match.Engine.Bus.UnmountOwner(this); }

    /// <summary>
    /// S13 迁移：指令的主动效果。引擎把「主动 handler 集」换成了 ActiveEffect（见 OrderCard 注释），
    /// 施放时由 OrderFlow 按登记序调用，语义与原 AddActiveHandler 一致——每次打出执行一次。
    /// 视图用 <see cref="OrderCastView"/>：施放载荷只有 Source/Target/Amount 三个键，
    /// 而引擎的 OrderFlowView 还要求必填的 Orders（载荷里没有），会绑定失败并被静默吞掉。
    /// </summary>
    private sealed class SourceOrderCast : ActiveEffect<OrderCastView>
    {
        public SourceOrderCast(SourceEffectAssembly assembly, CardBase card, JsonElement plan)
            : base("source/execute", castEvents:
            [
                new TriggerEvent<OrderCastView>("source/execute",
                    (view, ctx, ct) => assembly.ExecuteAsync(card, plan.GetProperty("actions"),
                        assembly.TakeCastSelection(plan, view), ct))
            ])
        { }
    }

    /// <summary>
    /// 施放侧重建选择。指向只有三种：**单选择**（玩家选一个）、**全部指向**（所有单位）、**随机指向**（随机一个），
    /// 后两种由效果自己遍历/抽取，不需要玩家传目标。所以这里只需把单选择的那个目标补回来：
    /// 引擎只把捕获箱里第一个可解析的 Card 作为 <c>view.Target</c> 传下来。
    /// chooseOne 的选项从预打出阶段留下的 <see cref="_pendingCapture"/> 取回。
    /// </summary>
    private Selection TakeCastSelection(JsonElement plan, OrderCastView view)
    {
        var selection = new Selection();
        if (view.Target is { } target && plan.TryGetProperty("targets", out var targets))
        {
            foreach (var entry in targets.EnumerateArray())
            {
                if (entry.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } key)
                    selection.Targets.TryAdd(key, target);
            }
        }

        if (_pendingCapture?.Option is { } option)
        {
            selection.Option = option;
            foreach (var pair in _pendingCapture.Targets) selection.Targets.TryAdd(pair.Key, pair.Value);
        }
        _pendingCapture = null;
        return selection;
    }
}
