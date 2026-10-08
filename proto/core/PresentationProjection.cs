using System.Text.Json;
using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>
/// Apply only an item's changes to the visible board. An older feedback item may finish after
/// a newer direct action; per-card and per-value revisions prevent resurrection or rollback.
/// All values still come from adapter snapshots, never UI rules.
/// </summary>
internal sealed class PresentationProjection(UiMatchView? initial)
{
    private UiMatchView? _shown = initial;
    private readonly Dictionary<string, long> _revisions = new();
    private readonly Dictionary<string, UiCardView> _waitingCards = new();
    private long _lineOrderRevision;
    private string[] _selfOrder = initial?.SelfLine.Select(c => c.Uid).ToArray() ?? [];
    private string[] _enemyOrder = initial?.EnemyLine.Select(c => c.Uid).ToArray() ?? [];

    private T Value<T>(string key, T before, T after, T current, long order)
    {
        if (EqualityComparer<T>.Default.Equals(before, after) || _revisions.GetValueOrDefault(key) > order) return current;
        _revisions[key] = order;
        return after;
    }

    private static IEnumerable<UiCardView> Cards(UiMatchView view) => view.SelfHand.Concat(view.SelfLine)
        .Concat(view.EnemyLine).Concat(new[] { view.SelfHq, view.EnemyHq }.OfType<UiCardView>());

    private static bool Same(UiCardView? a, UiCardView? b) =>
        ReferenceEquals(a, b) || JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    public UiMatchView Apply(UiMatchView? before, UiMatchView after, long order)
    {
        _shown ??= before ?? after;
        if (before is null) return _shown = after;
        var current = _shown;
        if (order >= _lineOrderRevision)
        {
            _lineOrderRevision = order;
            _selfOrder = after.SelfLine.Select(c => c.Uid).ToArray();
            _enemyOrder = after.EnemyLine.Select(c => c.Uid).ToArray();
        }
        var cards = Cards(current).ToDictionary(c => c.Uid);
        var oldCards = Cards(before).ToDictionary(c => c.Uid);
        var newCards = Cards(after).ToDictionary(c => c.Uid);
        foreach (var uid in oldCards.Keys.Union(newCards.Keys))
        {
            oldCards.TryGetValue(uid, out var oldCard); newCards.TryGetValue(uid, out var newCard);
            var key = "card:" + uid;
            // A newer action can reindex a card whose draw is still waiting. Keep its newest
            // values, but reveal it only when the older arrival beat actually plays.
            if (oldCard is null && newCard?.Zone == "hand" && _waitingCards.Remove(uid, out var waiting))
            { cards[uid] = waiting; continue; }
            if (Same(oldCard, newCard) || _revisions.GetValueOrDefault(key) > order) continue;
            _revisions[key] = order;
            if (newCard is null) { cards.Remove(uid); _waitingCards.Remove(uid); }
            else if (oldCard?.Zone == "hand" && newCard.Zone == "hand" && !cards.ContainsKey(uid))
                _waitingCards[uid] = newCard;
            else { _waitingCards.Remove(uid); cards[uid] = newCard; }
        }
        T V<T>(string key, T previous, T next, T shown) => Value(key, previous, next, shown, order);
        var hand = cards.Values.Where(c => c.OwnerSide == "self" && c.Zone == "hand").OrderBy(c => c.SlotIndex).ToArray();
        UiCardView[] Line(string side, string[] ordering) => cards.Values.Where(c => c.OwnerSide == side && c.Zone != "hand" && !c.IsHq)
            .OrderBy(c => Array.IndexOf(ordering, c.Uid) is var rank && rank >= 0 ? rank : int.MaxValue)
            .ThenBy(c => c.SlotIndex).ToArray();
        _shown = current with
        {
            Turn = V(nameof(after.Turn), before.Turn, after.Turn, current.Turn),
            Phase = V(nameof(after.Phase), before.Phase, after.Phase, current.Phase),
            ActivePlayerSide = V(nameof(after.ActivePlayerSide), before.ActivePlayerSide, after.ActivePlayerSide, current.ActivePlayerSide),
            SelfPlayerName = V(nameof(after.SelfPlayerName), before.SelfPlayerName, after.SelfPlayerName, current.SelfPlayerName),
            EnemyPlayerName = V(nameof(after.EnemyPlayerName), before.EnemyPlayerName, after.EnemyPlayerName, current.EnemyPlayerName),
            SelfKredits = V(nameof(after.SelfKredits), before.SelfKredits, after.SelfKredits, current.SelfKredits),
            SelfMaxKredits = V(nameof(after.SelfMaxKredits), before.SelfMaxKredits, after.SelfMaxKredits, current.SelfMaxKredits),
            EnemyKredits = V(nameof(after.EnemyKredits), before.EnemyKredits, after.EnemyKredits, current.EnemyKredits),
            EnemyMaxKredits = V(nameof(after.EnemyMaxKredits), before.EnemyMaxKredits, after.EnemyMaxKredits, current.EnemyMaxKredits),
            SelfDeckCount = V(nameof(after.SelfDeckCount), before.SelfDeckCount, after.SelfDeckCount, current.SelfDeckCount),
            EnemyDeckCount = V(nameof(after.EnemyDeckCount), before.EnemyDeckCount, after.EnemyDeckCount, current.EnemyDeckCount),
            EnemyHandCount = V(nameof(after.EnemyHandCount), before.EnemyHandCount, after.EnemyHandCount, current.EnemyHandCount),
            SelfCounterCount = V(nameof(after.SelfCounterCount), before.SelfCounterCount, after.SelfCounterCount, current.SelfCounterCount),
            FrontLineSlotCount = V(nameof(after.FrontLineSlotCount), before.FrontLineSlotCount, after.FrontLineSlotCount, current.FrontLineSlotCount),
            SupportLineSlotCount = V(nameof(after.SupportLineSlotCount), before.SupportLineSlotCount, after.SupportLineSlotCount, current.SupportLineSlotCount),
            ResultTitle = V(nameof(after.ResultTitle), before.ResultTitle, after.ResultTitle, current.ResultTitle),
            ResultReason = V(nameof(after.ResultReason), before.ResultReason, after.ResultReason, current.ResultReason),
            FinalTurn = V(nameof(after.FinalTurn), before.FinalTurn, after.FinalTurn, current.FinalTurn),
            SelectedTarget = V(nameof(after.SelectedTarget), before.SelectedTarget, after.SelectedTarget, current.SelectedTarget),
            PendingChoices = V(nameof(after.PendingChoices), before.PendingChoices, after.PendingChoices, current.PendingChoices),
            PendingChoiceCaption = V(nameof(after.PendingChoiceCaption), before.PendingChoiceCaption, after.PendingChoiceCaption, current.PendingChoiceCaption),
            SelfHand = hand, SelfHandCount = hand.Length,
            SelfLine = Line("self", _selfOrder), EnemyLine = Line("enemy", _enemyOrder),
            SelfHq = cards.Values.FirstOrDefault(c => c.OwnerSide == "self" && c.IsHq),
            EnemyHq = cards.Values.FirstOrDefault(c => c.OwnerSide == "enemy" && c.IsHq)
        };
        return _shown;
    }
}
