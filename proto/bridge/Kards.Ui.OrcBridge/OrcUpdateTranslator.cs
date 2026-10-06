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
        foreach (var (update, payload) in updates)
        {
            switch (update)
            {
                case GameUpdates.CardHandAdd:
                case GameUpdates.CardDrawn:
                    AddDraw(steps, payload, viewer, after);
                    break;
                case GameUpdates.UnitDeployed:
                    AddDeployment(steps, payload, after);
                    break;
                case GameUpdates.CardStatChanged:
                    AddImpact(impacts, payload, previous, after);
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
                    AddTurn(steps, payload, viewer, after);
                    break;
            }
        }
        // Deaths already play inside the impact presentation; never play them twice.
        steps.RemoveAll(step => step is UiRemovalPresentation removal && died.Contains(removal.Card.Uid));
        return new OrcTranslation(steps, impacts);
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
    /// damage read as the difference between the displayed before and after. No attacker trajectory exists
    /// in the engine's update stream, so the hit lands on the target without a shot.
    /// </summary>
    private void AddImpact(List<UiOrderImpact> impacts, IReadOnlyDictionary<string, object?> payload,
        UiMatchView previous, UiMatchView after)
    {
        var card = PayloadCard(payload);
        if (card is null) return;
        var uid = OrcRefs.KeyOf(card);
        var before = previous.SelfLine.Concat(previous.EnemyLine).FirstOrDefault(c => c.Uid == uid);
        if (before is null && previous.SelfHq?.Uid == uid) before = previous.SelfHq;
        if (before is null && previous.EnemyHq?.Uid == uid) before = previous.EnemyHq;
        if (before?.Visibility != Visibility.Full) return;

        var fields = payload.TryGetValue(GameUpdates.PayloadChangedFields, out var value) ? value as IReadOnlyList<string> : null;
        var healthHit = fields is null || fields.Contains(CardStatFields.Defense) || fields.Contains(CardStatFields.HqHealth);
        if (!healthHit) return;

        var afterView = BoardView(card, after);
        var damage = (before.Health ?? 0) - (afterView?.Health ?? 0);
        if (damage <= 0 && afterView is not null) return;
        if (impacts.Any(i => i.Before.Uid == uid)) return;
        impacts.Add(new UiOrderImpact(before, afterView, Math.Max(0, damage)));
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
