using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Board;
using Orc.Game.Players;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// Engine availability -> the UI's action projection. Everything here comes from an engine query surface.
/// Native CanPlay evaluates entry validation, ownership and frontend admission. Effects can still
/// reject their selections during the actual chain; no effect is executed just to illuminate a card.
/// Damage forecasts are not invented: previews carry candidate targets without numbers.
/// </summary>
public sealed class OrcActionReader
{
    public UiBattleActions Read(Match match, Player viewer, IReadOnlyDictionary<string, Card> index)
    {
        if (match.Phase != MatchPhase.Play) return new UiBattleActions();
        try
        {
            if (!ReferenceEquals(match.CurrentPlayer, viewer)) return new UiBattleActions();
        }
        catch (InvalidOperationException)
        {
            return new UiBattleActions();
        }

        var hand = new HashSet<Card>(viewer.Hand);
        var playable = index
            .Where(entry => hand.Contains(entry.Value) && entry.Value is CardBase card && match.PlayManager.CanPlay(card))
            .Select(entry => entry.Key)
            .ToArray();

        var moves = new List<UiMoveOption>();
        var previews = new List<UiAttackPreview>();
        foreach (var unit in OwnUnits(match, viewer))
        {
            var availability = match.CommandManager.GetCommandAvailability(unit);
            var uid = OrcRefs.KeyOf(unit);
            if (availability.Move.CanUse)
                foreach (var candidate in availability.Move.Candidates)
                    if (candidate.IsAlive && candidate.Value is FormationGap gap)
                        moves.Add(new UiMoveOption(uid, ReferenceEquals(match.Battlefield.FrontLine,gap.Line) ? "frontline" : "support", gap.Ordinal, OrcRefs.KeyOf(gap)));
            foreach (var candidate in availability.Attack.Candidates)
            {
                if (!candidate.IsAlive) continue;
                previews.Add(new UiAttackPreview(uid, OrcRefs.KeyOf(candidate.Value), null, null, false, false));
            }
        }

        return new UiBattleActions
        {
            CanEndTurn = match.CanAcceptInput(viewer, viewer.Hq),
            AttacksEnabled = true,
            PlayableUids = playable,
            HandPlayabilityKnown = true,
            DeploymentGaps = match.Battlefield.GetSupportLine(viewer).GetInsertionGaps()
                .Select(gap => new UiDeploymentGap(OrcRefs.KeyOf(gap), gap.Ordinal,
                    gap.Left is null ? null : OrcRefs.KeyOf(gap.Left), gap.Right is null ? null : OrcRefs.KeyOf(gap.Right))).ToArray(),
            Moves = moves,
            AttackPreviews = previews
        };
    }

    private static IEnumerable<UnitCard> OwnUnits(Match match, Player viewer)
    {
        var battlefield = match.Battlefield;
        foreach (var line in new[] { battlefield.PlayerASupportLine, battlefield.FrontLine, battlefield.PlayerBSupportLine })
            foreach (var card in line.Members)
                if (card is UnitCard unit && ReferenceEquals(unit.Owner, viewer))
                    yield return unit;
    }
}
