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

    /// <summary>
    /// uid -> engine card, rebuilt by every <see cref="Read"/>. Commands arrive as uids, so this is how a
    /// click is mapped back to an engine entity. It is a snapshot: a card destroyed since the last read is
    /// simply absent here.
    /// </summary>
    public IReadOnlyDictionary<string, Card> LastIndex { get; private set; } = new Dictionary<string, Card>();

    public UiMatchView Read(Match match, Player viewer, string matchId)
    {
        var opponent = OpponentOf(match, viewer);
        var phase = match.Phase switch
        {
            MatchPhase.Mulligan => "mulligan",
            MatchPhase.Play => "play",
            _ => "over"
        };

        var index = new Dictionary<string, Card>();
        var hand = new List<UiCardView>();
        for (var i = 0; i < viewer.Hand.Count; i++)
            if (viewer.Hand[i] is CardBase card)
            {
                hand.Add(_cards.Read(card, viewer, "hand", i));
                index[OrcRefs.KeyOf(card)] = card;
            }

        var selfLine = Row(match, viewer, viewer, false, index).Concat(Row(match, viewer, viewer, true, index)).ToArray();
        var enemyLine = opponent is null
            ? Array.Empty<UiCardView>()
            : Row(match, opponent, viewer, false, index).Concat(Row(match, opponent, viewer, true, index)).ToArray();
        if (opponent is not null) HqOf(opponent, viewer, index);
        var selfHq = HqOf(viewer, viewer, index);
        LastIndex = index;

        return new UiMatchView
        {
            MatchId = matchId,
            Turn = TurnOf(match),
            SelfPlayerName = "我方",
            EnemyPlayerName = "对手",
            Phase = phase,
            ResultTitle = ResultTitle(match, viewer),
            ResultReason = ResultReasonOf(match),
            FinalTurn = match.Winner is null ? null : TurnOf(match),
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
            // The board needs the real line widths: a drop position only means something relative to
            // the slots that exist, and those are drawn only when occupied.
            FrontLineSlotCount = match.Battlefield.FrontLine.Count,
            SupportLineSlotCount = match.Battlefield.GetSupportLine(viewer.Index).Count,
            SelfHand = hand,
            SelfLine = selfLine,
            SelfHq = selfHq,
            EnemyLine = enemyLine,
            EnemyHq = opponent is null ? null : HqOf(opponent, viewer)
        };
    }

    private UiCardView HqOf(Player owner, Player viewer) => HqOf(owner, viewer, new Dictionary<string, Card>());

    private UiCardView HqOf(Player owner, Player viewer, Dictionary<string, Card> index)
    {
        var slot = owner.Hq.Position;
        index[OrcRefs.KeyOf(owner.Hq)] = owner.Hq;
        return _cards.Read(owner.Hq, viewer, "support", slot?.Index ?? 0);
    }

    private IEnumerable<UiCardView> Row(Match match, Player owner, Player viewer, bool front, Dictionary<string, Card> index)
    {
        var battlefield = match.Battlefield;
        var line = front ? battlefield.FrontLine : battlefield.GetSupportLine(owner.Index);
        for (var i = 0; i < line.Count; i++)
            if (line[i].Occupant is UnitCard unit && ReferenceEquals(unit.Owner, owner))
            {
                index[OrcRefs.KeyOf(unit)] = unit;
                yield return _cards.Read(unit, viewer, front ? "frontline" : "support", i);
            }
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

    /// <summary>
    /// The engine records why a match ended alongside the winner. Passing the enum name through
    /// keeps the reason engine-owned; the UI maps it to wording, never to a judgement.
    /// </summary>
    private static string? ResultReasonOf(Match match)
    {
        try { return match.EndReason?.ToString(); }
        catch (InvalidOperationException) { return null; }
    }
}
