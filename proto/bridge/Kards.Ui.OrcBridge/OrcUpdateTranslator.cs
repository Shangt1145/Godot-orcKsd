using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>What one segment turns into: steps to choreograph, or board impacts for combat presentation.</summary>
public sealed record OrcTranslation(IReadOnlyList<UiPresentationStep> Steps, IReadOnlyList<UiOrderImpact> Impacts);

/// <summary>
/// Update stream -> UI presentation. The engine reports facts ("this happened"); this translator turns them
/// into what the UI can animate, using the freshly read board for slot indices and values.
/// Defense drops become board impacts (flash, damage number, death) rather than silent value swaps.
/// </summary>
public sealed class OrcUpdateTranslator
{
    private readonly OrcCardReader _cards;

    public OrcUpdateTranslator(OrcCardReader cards) { _cards = cards; }

    public OrcTranslation Translate(
        IReadOnlyList<(string Update, IReadOnlyDictionary<string, object?> Payload)> updates,
        Player viewer,
        UiMatchView previous,
        UiMatchView after)
    {
        var steps = new List<UiPresentationStep>();
        var impacts = new List<UiOrderImpact>();
        var died = new HashSet<string>();
        // The engine reports one arrival through two signals: card.drawn ("this was taken") and
        // card.hand.add ("this landed in hand") — see PlayerManager.DrawCard. Both describe the same
        // card, so only the first may choreograph; handling both plays the draw animation twice.
        var arrived = new HashSet<string>(StringComparer.Ordinal);
        // turn.start and turn.start.after describe one turn change the same way (TurnManager), so the
        // banner must be built once or it plays twice per turn.
        var turnAnnounced = false;
        // unit.damage.dealt carries the attacker and the engine's own amount. Signal order is not
        // guaranteed (stat.changed may arrive first), so collect the pairing first and apply it once
        // every impact of this segment is known.
        var assaults = new Dictionary<string, (string Attacker, int Amount)>();
        // slot.gained / slot.lost are semantic pre-signals; slot.changed is the only signal the
        // value really moved. Tracking which pre-signals arrived is what lets the bar animate a
        // card effect differently from a turn increment, without playing the same change twice.
        var slotEffect = 0;
        var pointDelta = 0;
        foreach (var (update, payload) in updates)
        {
            switch (update)
            {
                case GameUpdates.SlotGained:
                case GameUpdates.SlotLost:
                    slotEffect++;
                    break;
                case GameUpdates.PointGained:
                    pointDelta += 1;
                    break;
                case GameUpdates.PointLost:
                    pointDelta -= 1;
                    break;
                case GameUpdates.SlotChanged:
                    AddResource(steps, payload, viewer, slotEffect > 0 ? UiResourceCause.Gain : UiResourceCause.Turn);
                    break;
                case GameUpdates.PointChanged:
                    // point.changed only ever follows a card effect, so the cause follows whichever
                    // pre-signal arrived; spending a card points down, gaining them up.
                    AddResource(steps, payload, viewer, pointDelta < 0 ? UiResourceCause.Spend : UiResourceCause.Gain);
                    break;
                case GameUpdates.CardHandAdd:
                case GameUpdates.CardDrawn:
                    {
                        // One draw, two signals. Keep whichever arrives first and drop the other, or
                        // a single card animates into the hand twice.
                        var arrival = PayloadCard(payload);
                        if (arrival is null || !arrived.Add(OrcRefs.KeyOf(arrival))) break;
                        AddDraw(steps, payload, viewer, after);
                        break;
                    }
                case GameUpdates.UnitDeployed:
                    AddDeployment(steps, payload, after);
                    break;
                case GameUpdates.CardPlayed:
                    if (PayloadCard(payload) is CommandCard order)
                        steps.Add(new UiOrderPresentation(_cards.Read(order, viewer, "hand", 0), []));
                    break;
                case GameUpdates.CounterTriggered:
                    if (PayloadCard(payload) is CounterCard counter)
                    {
                        var triggered = payload.GetValueOrDefault("UiCounterStage") as string == "triggered";
                        var side = counter.Owner == viewer ? "self" : "enemy";
                        var card = side == "self" || triggered ? _cards.Read(counter, viewer, "hand", 0) : null;
                        steps.Add(new UiCounterPresentation(side, triggered ? UiCounterStage.Triggered : UiCounterStage.Armed, card));
                    }
                    break;
                case GameUpdates.CardStatChanged:
                    AddImpact(impacts, payload, previous, after);
                    AppendNewImpacts(steps, impacts);
                    break;
                case GameUpdates.CardDamaged:
                    // The victim-side damage signal. It carries the engine's own amount, so no stat
                    // diff is needed — and unlike stat.changed it fires for HQ damage too, which is
                    // why combat feedback used to go missing on every attack that hit the HQ.
                    AddDamage(impacts, payload, previous, after);
                    AppendNewImpacts(steps, impacts);
                    break;
                case GameUpdates.UnitDamageDealt:
                    RecordAssault(assaults, payload);
                    break;
                case GameUpdates.CardDied:
                    var lost = PayloadCard(payload);
                    if (lost is not null) died.Add(OrcRefs.KeyOf(lost));
                    break;
                case GameUpdates.CardDiscarded:
                    AddDiscard(steps, payload, viewer, UiDiscardKind.Discard);
                    break;
                case GameUpdates.CardBurned:
                    // Burned is its own signal now (Kb): a hand-limit burn never walks the discard path.
                    AddDiscard(steps, payload, viewer, UiDiscardKind.Burn);
                    break;
                case GameUpdates.TurnStartAfter:
                case GameUpdates.TurnStart:
                    // Both signals describe one turn change; announce it once.
                    if (turnAnnounced) break;
                    turnAnnounced = true;
                    AddTurn(steps, payload, viewer, after);
                    break;
            }
        }
        impacts = ApplyAssaults(impacts, assaults, after);
        for (var i = 0; i < steps.Count; i++)
            if (steps[i] is UiBoardImpactsPresentation hits)
                steps[i] = hits with { Impacts = hits.Impacts.Select(hit =>
                    impacts.First(paired => paired.Before.Uid == hit.Before.Uid)).ToArray() };
        // Deaths already play inside the impact presentation; never play them twice.
        steps.RemoveAll(step => step is UiRemovalPresentation removal && died.Contains(removal.Card.Uid));
        foreach (var armed in previous.SelfHand.Where(c => c.IsCounterArmed))
            if (after.SelfHand.Any(c => c.Uid == armed.Uid && !c.IsCounterArmed))
                steps.Add(new UiCounterPresentation("self", UiCounterStage.Disarmed, armed));
        return new OrcTranslation(steps, impacts);
    }

    private static void AppendNewImpacts(List<UiPresentationStep> steps, List<UiOrderImpact> impacts)
    {
        var reported = steps.OfType<UiBoardImpactsPresentation>().SelectMany(s => s.Impacts)
            .Select(i => i.Before.Uid).ToHashSet(StringComparer.Ordinal);
        var fresh = impacts.Where(i => !reported.Contains(i.Before.Uid)).ToArray();
        if (fresh.Length == 0) return;
        if (steps.LastOrDefault() is UiBoardImpactsPresentation previous)
            steps[^1] = previous with { Impacts = previous.Impacts.Concat(fresh).ToArray() };
        else steps.Add(new UiBoardImpactsPresentation(fresh));
    }

    /// <summary>
    /// Notes who hit whom and by how much. Keyed by the victim's uid; one attacker per victim per
    /// segment, because the engine resolves a single damage roll per target.
    /// </summary>
    private static void RecordAssault(Dictionary<string, (string Attacker, int Amount)> assaults,
        IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.GetValueOrDefault(GameUpdates.PayloadUnit) is not Card attacker) return;
        if (payload.GetValueOrDefault(GameUpdates.PayloadCard) is not Card victim) return;
        var amount = payload.GetValueOrDefault(GameUpdates.PayloadAmount) as int? ?? 0;
        if (amount <= 0) return;
        assaults[OrcRefs.KeyOf(victim)] = (OrcRefs.KeyOf(attacker), amount);
    }

    /// <summary>
    /// Pairs each impact with its attacker and replaces the derived damage with the engine's amount.
    /// An impact the engine never paired stays sourceless: it plays as a bare hit, no invented shot.
    /// </summary>
    private static List<UiOrderImpact> ApplyAssaults(List<UiOrderImpact> impacts,
        Dictionary<string, (string Attacker, int Amount)> assaults, UiMatchView after)
    {
        if (assaults.Count == 0) return impacts;
        var paired = new List<UiOrderImpact>(impacts.Count);
        foreach (var impact in impacts)
        {
            if (!assaults.TryGetValue(impact.Before.Uid, out var hit)) { paired.Add(impact); continue; }
            var source = after.SelfLine.Concat(after.EnemyLine).FirstOrDefault(c => c.Uid == hit.Attacker);
            // The attacker's own view may already be gone (it died in the same segment); the impact
            // then keeps the engine's amount but plays without a trajectory.
            paired.Add(impact with { Damage = hit.Amount, Source = source });
        }
        return paired;
    }

    /// <summary>
    /// Draw steps carry the slot the card occupies in the freshly read hand. A draw that never reached
    /// the hand (hand-limit burn) has no slot and is reported as a burn instead.
    /// </summary>
    private void AddDraw(List<UiPresentationStep> steps, IReadOnlyDictionary<string, object?> payload,
        Player viewer, UiMatchView after)
    {
        var card = PayloadCard(payload);
        if (card is null) return;
        var owner = ((CardBase)card).Owner;
        var mine = owner is not null && ReferenceEquals(owner, viewer);
        if (mine)
        {
            var slot = after.SelfHand.FirstOrDefault(c => c.Uid == OrcRefs.KeyOf(card))?.SlotIndex;
            if (slot is null)
            {
                steps.Add(new UiDiscardPresentation("self", ReadFor(viewer, card), UiDiscardKind.Burn));
                return;
            }
            steps.Add(new UiDrawPresentation("self", slot.Value, ReadFor(viewer, card)));
        }
        else
        {
            // Opponent draws are anonymous: count only, never a card view.
            var index = Math.Max(0, (after.EnemyHandCount ?? 0) - 1);
            steps.Add(new UiDrawPresentation("enemy", index));
        }
    }

    /// <summary>
    /// A deployment plays the paper fly-in. `Deployed` must be the board with the unit already on its
    /// slot (the demo fixture's semantics): the presentation renders it, finds the landed card there and
    /// flies a paper copy onto it. Staging a board *without* the unit makes that lookup fail and the
    /// whole choreography is skipped.
    /// </summary>
    private void AddDeployment(List<UiPresentationStep> steps, IReadOnlyDictionary<string, object?> payload, UiMatchView after)
    {
        var card = PayloadCard(payload);
        if (card is null) return;
        var view = BoardView(card, after);
        if (view is null) return;
        steps.Add(new UiDeploymentPresentation(view, after));
    }

    /// <summary>
    /// A defense or HQ-health drop becomes a board impact: flash, damage number and death smoke, with the
    /// damage read as the difference between the displayed before and after. That derived number is only a
    /// fallback — when unit.damage.dealt is present, <see cref="ApplyAssaults"/> replaces it with the
    /// engine's own amount and attaches the attacker.
    /// </summary>
    private void AddImpact(List<UiOrderImpact> impacts, IReadOnlyDictionary<string, object?> payload,
        UiMatchView previous, UiMatchView after)
    {
        var card = PayloadCard(payload);
        if (card is null) return;
        var fields = payload.TryGetValue(GameUpdates.PayloadChangedFields, out var value) ? value as IReadOnlyList<string> : null;
        var healthHit = fields is null || fields.Contains(CardStatFields.Defense) || fields.Contains(CardStatFields.HqHealth);
        if (!healthHit) return;

        var (before, afterView) = Resolve(impacts, card, previous, after);
        if (before is null) return;
        var damage = (before.Health ?? 0) - (afterView?.Health ?? 0);
        if (damage <= 0 && afterView is not null) return;
        impacts.Add(new UiOrderImpact(before, afterView, Math.Max(0, damage)));
    }

    /// <summary>
    /// The victim-side damage signal. The engine states the amount it actually applied, so the
    /// displayed before/after pair is only used for the card snapshots, never for the number.
    /// </summary>
    private void AddDamage(List<UiOrderImpact> impacts, IReadOnlyDictionary<string, object?> payload,
        UiMatchView previous, UiMatchView after)
    {
        var card = PayloadCard(payload);
        if (card is null) return;
        var amount = payload.GetValueOrDefault(GameUpdates.PayloadAmount) as int? ?? 0;
        if (amount <= 0) return;
        var (before, afterView) = Resolve(impacts, card, previous, after);
        if (before is null) return;
        impacts.Add(new UiOrderImpact(before, afterView, amount));
    }

    /// <summary>
    /// Locates the hit card across the previous board and the fresh one, and refuses to report the
    /// same victim twice in a segment (stat.changed and card.damaged can both describe one hit).
    /// </summary>
    private static (UiCardView? Before, UiCardView? After) Resolve(List<UiOrderImpact> impacts, Card card,
        UiMatchView previous, UiMatchView after)
    {
        var uid = OrcRefs.KeyOf(card);
        if (impacts.Any(i => i.Before.Uid == uid)) return (null, null);
        var before = previous.SelfLine.Concat(previous.EnemyLine).FirstOrDefault(c => c.Uid == uid);
        if (before is null && previous.SelfHq?.Uid == uid) before = previous.SelfHq;
        if (before is null && previous.EnemyHq?.Uid == uid) before = previous.EnemyHq;
        if (before?.Visibility != Visibility.Full) return (null, null);
        return (before, BoardView(card, after));
    }

    /// <summary>
    /// A command-point change. The engine reports old and new, so the bar animates the actual delta
    /// instead of guessing. Only the viewer-side change becomes a step; the opponent's bar follows
    /// the board render, and inventing an animation for hidden information would leak it.
    /// </summary>
    private static void AddResource(List<UiPresentationStep> steps, IReadOnlyDictionary<string, object?> payload,
        Player viewer, UiResourceCause cause)
    {
        if (payload.GetValueOrDefault(GameUpdates.PayloadPlayer) is not Player player) return;
        var mine = ReferenceEquals(player, viewer);
        var oldPoints = payload.GetValueOrDefault(GameUpdates.PayloadOldPoints) as int?;
        var newPoints = payload.GetValueOrDefault(GameUpdates.PayloadNewPoints) as int?;
        var oldSlots = payload.GetValueOrDefault(GameUpdates.PayloadOldSlots) as int?;
        var newSlots = payload.GetValueOrDefault(GameUpdates.PayloadNewSlots) as int?;
        if (mine) steps.Add(new UiResourcePresentation("self", cause, oldPoints ?? 0, newPoints ?? 0, oldSlots, newSlots));
        else if (newSlots is not null) steps.Add(new UiResourcePresentation("enemy", cause, 0, 0, oldSlots, newSlots));
    }

    private void AddDiscard(List<UiPresentationStep> steps, IReadOnlyDictionary<string, object?> payload,
        Player viewer, UiDiscardKind kind)
    {
        var card = PayloadCard(payload);
        if (card is null) return;
        var owner = ((CardBase)card).Owner;
        var mine = owner is not null && ReferenceEquals(owner, viewer);
        steps.Add(new UiDiscardPresentation(mine ? "self" : "enemy", mine ? ReadFor(viewer, card) : null, kind));
    }

    private void AddTurn(List<UiPresentationStep> steps, IReadOnlyDictionary<string, object?> payload,
        Player viewer, UiMatchView after)
    {
        var player = payload.TryGetValue(GameUpdates.PayloadPlayer, out var value) ? value as Player : null;
        var side = player is null || ReferenceEquals(player, viewer) ? "self" : "enemy";
        steps.Add(new UiTurnPresentation(side, after.Turn, UiTurnKind.TurnStarted));
    }

    private UiCardView? ReadFor(Player viewer, Card card)
        => card is CardBase typed ? _cards.Read(typed, viewer, "hand", 0) : null;

    private static UiCardView? BoardView(Card card, UiMatchView after)
    {
        var uid = OrcRefs.KeyOf(card);
        var hit = after.SelfLine.FirstOrDefault(c => c.Uid == uid) ?? after.EnemyLine.FirstOrDefault(c => c.Uid == uid);
        if (hit is not null) return hit;
        if (after.SelfHq?.Uid == uid) return after.SelfHq;
        return after.EnemyHq?.Uid == uid ? after.EnemyHq : null;
    }

    private static Card? PayloadCard(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.TryGetValue(GameUpdates.PayloadCard, out var value)) return OrcRefs.Card(value);
        if (payload.TryGetValue(GameUpdates.PayloadUnit, out var unit)) return OrcRefs.Card(unit);
        return null;
    }
}
