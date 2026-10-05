using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// Update stream -> UI presentation steps. The engine reports facts ("this happened"); this translator
/// turns them into steps the UI can animate, using the freshly read board for slot indices and values.
/// It never invents an outcome: each step only carries what the update plus the current read provide.
/// </summary>
public sealed class OrcUpdateTranslator
{
    private readonly OrcCardReader _cards;

    public OrcUpdateTranslator(OrcCardReader cards) { _cards = cards; }

    public IReadOnlyList<UiPresentationStep> Translate(
        IReadOnlyList<(string Update, IReadOnlyDictionary<string, object?> Payload)> updates,
        Player viewer,
        UiMatchView previous,
        UiMatchView after)
    {
        var steps = new List<UiPresentationStep>();
        foreach (var (update, payload) in updates)
        {
            switch (update)
            {
                case GameUpdates.CardHandAdd:
                case GameUpdates.CardDrawn:
                    AddDraw(steps, payload, viewer, after);
                    break;
                case GameUpdates.CardDiscarded:
                    AddDiscard(steps, payload, viewer, UiDiscardKind.Discard);
                    break;
                case GameUpdates.CardDied:
                    AddRemoval(steps, payload, previous);
                    break;
                case GameUpdates.TurnStartAfter:
                case GameUpdates.TurnStart:
                    AddTurn(steps, payload, viewer, after);
                    break;
            }
        }
        return steps;
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

    private void AddDiscard(List<UiPresentationStep> steps, IReadOnlyDictionary<string, object?> payload,
        Player viewer, UiDiscardKind kind)
    {
        var card = PayloadCard(payload);
        if (card is null) return;
        var owner = ((CardBase)card).Owner;
        var mine = owner is not null && ReferenceEquals(owner, viewer);
        steps.Add(new UiDiscardPresentation(mine ? "self" : "enemy", mine ? ReadFor(viewer, card) : null, kind));
    }

    private void AddRemoval(List<UiPresentationStep> steps, IReadOnlyDictionary<string, object?> payload, UiMatchView previous)
    {
        var card = PayloadCard(payload);
        if (card is null) return;
        // Values are taken from the last displayed board; the engine does not restate them at death.
        var before = previous.SelfLine.Concat(previous.EnemyLine).FirstOrDefault(c => c.Uid == OrcRefs.KeyOf(card));
        if (before is null) return;
        steps.Add(new UiRemovalPresentation(before));
    }

    private void AddTurn(List<UiPresentationStep> steps, IReadOnlyDictionary<string, object?> payload,
        Player viewer, UiMatchView after)
    {
        var player = payload.TryGetValue(GameUpdates.PayloadPlayer, out var value) ? value as Player : null;
        var side = player is null || ReferenceEquals(player, viewer) ? "self" : "enemy";
        steps.Add(new UiTurnPresentation(side, after.Turn, UiTurnKind.TurnStarted));
    }

    private UiCardView? ReadFor(Player viewer, Orc.Cards.Card card)
        => card is CardBase typed ? _cards.Read(typed, viewer, "hand", 0) : null;

    private static Orc.Cards.Card? PayloadCard(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.TryGetValue(GameUpdates.PayloadCard, out var value)) return OrcRefs.Card(value);
        if (payload.TryGetValue(GameUpdates.PayloadUnit, out var unit)) return OrcRefs.Card(unit);
        return null;
    }
}
