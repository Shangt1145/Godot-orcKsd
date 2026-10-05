using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// Engine availability -> the UI's action projection. Everything here comes from an engine query surface.
/// The engine has no "can this card be played" query, so hand cards are listed as attemptable and legality is
/// decided by the engine when the play is submitted; its refusal reason is shown to the player as-is.
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
            .Where(entry => hand.Contains(entry.Value))
            .Select(entry => entry.Key)
            .ToArray();

        var moves = new List<UiMoveOption>();
        var previews = new List<UiAttackPreview>();
        foreach (var unit in OwnUnits(match, viewer))
        {
            var availability = match.CommandManager.GetCommandAvailability(unit);
            var uid = OrcRefs.KeyOf(unit);
            if (availability.Move.CanUse) moves.Add(new UiMoveOption(uid, "frontline"));
            foreach (var candidate in availability.Attack.Candidates)
            {
                if (!candidate.IsAlive) continue;
                previews.Add(new UiAttackPreview(uid, OrcRefs.KeyOf(candidate.Value), null, null, false, false));
            }
        }

        return new UiBattleActions
        {
            CanEndTurn = true,
            AttacksEnabled = true,
            PlayableUids = playable,
            Moves = moves,
            AttackPreviews = previews
        };
    }

    private static IEnumerable<UnitCard> OwnUnits(Match match, Player viewer)
    {
        var battlefield = match.Battlefield;
        foreach (var line in new[] { battlefield.PlayerASupportLine, battlefield.FrontLine, battlefield.PlayerBSupportLine })
            foreach (var slot in line)
                if (slot.Occupant is UnitCard unit && ReferenceEquals(unit.Owner, viewer))
                    yield return unit;
    }
}
