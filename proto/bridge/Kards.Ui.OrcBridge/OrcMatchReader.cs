using Orc.Cards;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// Match -> the UI's full-board value object. This is the "ask the engine for the current truth" lane:
/// segments tell us what happened, this reader tells us what is. It is also the only place that
/// enforces hidden information: the opponent's hand is reduced to a count, never to card views.
/// </summary>
public sealed class OrcMatchReader
{
    private readonly OrcCardReader _cards;

    public OrcMatchReader(OrcCardReader cards) { _cards = cards; }

    public UiMatchView Read(Match match, Player viewer, string matchId)
    {
        var opponent = OpponentOf(match, viewer);
        var phase = match.Phase switch
        {
            MatchPhase.Mulligan => "mulligan",
            MatchPhase.Play => "play",
            _ => "over"
        };

        var hand = new List<UiCardView>();
        for (var i = 0; i < viewer.Hand.Count; i++)
            if (viewer.Hand[i] is CardBase card)
                hand.Add(_cards.Read(card, viewer, "hand", i));

        var selfLine = Row(match, viewer, viewer, front: false).Concat(Row(match, viewer, viewer, front: true)).ToArray();
        var enemyLine = opponent is null
            ? Array.Empty<UiCardView>()
            : Row(match, opponent, viewer, front: false).Concat(Row(match, opponent, viewer, front: true)).ToArray();

        return new UiMatchView
        {
            MatchId = matchId,
            Turn = TurnOf(match),
            SelfPlayerName = "我方",
            EnemyPlayerName = "对手",
            Phase = phase,
            ResultTitle = ResultTitle(match, viewer),
            ActivePlayerSide = ActiveSideOf(match, viewer, phase),
            SelfKredits = viewer.Points,
            SelfMaxKredits = viewer.PointSlots,
            SelfHandCount = viewer.Hand.Count,
            SelfDeckCount = viewer.Deck.Count,
            EnemyKredits = opponent?.Points,
            EnemyMaxKredits = opponent?.PointSlots,
            EnemyHandCount = opponent?.Hand.Count,
            EnemyDeckCount = opponent?.Deck.Count,
            SelfCounterCount = viewer.Hand.Count(h => h is CardBase c && c.TryGetData<CounterActivationData>(out var a) && a.IsActive),
            SelfHand = hand,
            SelfLine = selfLine,
            SelfHq = HqOf(viewer, viewer),
            EnemyLine = enemyLine,
            EnemyHq = opponent is null ? null : HqOf(opponent, viewer)
        };
    }

    private UiCardView? HqOf(Player owner, Player viewer)
    {
        var slot = owner.Hq.Position;
        return _cards.Read(owner.Hq, viewer, "support", slot?.Index ?? 0);
    }

    private IEnumerable<UiCardView> Row(Match match, Player owner, Player viewer, bool front)
    {
        var battlefield = match.Battlefield;
        var line = front ? battlefield.FrontLine : battlefield.GetSupportLine(owner.Index);
        for (var i = 0; i < line.Count; i++)
            if (line[i].Occupant is UnitCard unit && ReferenceEquals(unit.Owner, owner))
                yield return _cards.Read(unit, viewer, front ? "frontline" : "support", i);
    }

    private static Player? OpponentOf(Match match, Player viewer)
        => match.Players.FirstOrDefault(p => p.Index != viewer.Index);

    private static string ActiveSideOf(Match match, Player viewer, string phase)
    {
        if (phase != "play") return "self";
        try { return ReferenceEquals(match.CurrentPlayer, viewer) ? "self" : "enemy"; }
        catch (InvalidOperationException) { return "self"; }
    }

    private static int TurnOf(Match match)
    {
        try { return match.TurnNumber; }
        catch (InvalidOperationException) { return 0; }
    }

    private static string? ResultTitle(Match match, Player viewer)
    {
        // Only the engine's supplied winner is consumed; the UI never infers a result.
        var winner = match.Winner;
        return winner is null ? null : ReferenceEquals(winner, viewer) ? "胜利" : "失败";
    }
}
