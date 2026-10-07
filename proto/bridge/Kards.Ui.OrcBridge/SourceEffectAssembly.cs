using System.Text.Json;
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
    private bool _disposed;
    public SourceEffectAssembly(Match match, IReadOnlyDictionary<string, JsonElement> plans, Action<string> targetFailure)
    {
        _match = match;
        _targetFailure = targetFailure;
        foreach (var card in match.Players.SelectMany(p => p.Hand.OfType<CardBase>()
            .Concat(Enumerable.Range(0, p.Deck.Count).Select(i => p.Deck.PeekInstance(i)))))
        {
            if (!match.CardLibrary.TryGetRegisteredId(card.Definition, out var id) || !plans.TryGetValue(id, out var declarations)) continue;
            var plan = declarations.EnumerateArray().Single();
            if (card is CommandCard command)
            {
                command.AddPrePlayHandler("source/choose", (view, ctx, ct) => CaptureAsync(command, plan, view, ct));
                command.AddActiveHandler("source/execute", (view, ctx, ct) =>
                    ExecuteAsync(command, plan.GetProperty("actions"), view.Argument as Selection ?? new(), ct));
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

    private async Task CaptureAsync(CommandCard card, JsonElement plan, CardTriggerView view, CancellationToken ct)
    {
        var selection = new Selection();
        if (plan.TryGetProperty("targets", out var targets))
            foreach (var target in targets.EnumerateArray())
            {
                var name = target.GetProperty("id").GetString()!;
                var side = Text(target, "side", "any");
                var result = await _match.TargeterManager.CreateTargeter(
                    new TargetFilter(fineFilter: r => r.Value is UnitCard unit && unit.GetData<UnitStateData>().Position is not null
                        && !unit.GetData<UnitStateData>().IsDestroyed
                        && (side == "any" || ReferenceEquals(unit.Owner, card.Owner) == (side == "friendly"))),
                    slots: [new SingleSelectSlot(name)]).Targeting();
                if (result.Status != TargetingStatus.Success) { _targetFailure(result.Reason?.ToString() ?? result.Status.ToString()); view.CaptureBox!.CancelPrePlay(); return; }
                selection.Targets.Add(name, (Card)result.Outcome!.GetSelection(name).Single().Value);
            }
        var choose = plan.GetProperty("actions").EnumerateArray().FirstOrDefault(a => Text(a, "op") == "chooseOne");
        if (choose.ValueKind == JsonValueKind.Object)
        {
            var options = choose.GetProperty("options").EnumerateArray()
                .Select((o, i) => new OptionEntry(i.ToString(), o.GetProperty("label").GetString()!));
            var result = await _match.TargeterManager.CreateTargeter(slots: [new OptionSelectSlot(options, "choice")]).Targeting();
            if (result.Status != TargetingStatus.Success) { _targetFailure(result.Reason?.ToString() ?? result.Status.ToString()); view.CaptureBox!.CancelPrePlay(); return; }
            selection.Option = result.Outcome!.GetIdentifiers("choice").Single();
        }
        ct.ThrowIfCancellationRequested();
        view.CaptureBox!.Capture(selection);
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
    public void Dispose() { _disposed = true; _match.Engine.Bus.UnmountOwner(this); }
}
