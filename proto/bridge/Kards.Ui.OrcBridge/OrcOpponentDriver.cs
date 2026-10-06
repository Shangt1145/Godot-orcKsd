using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Commanding;
using Orc.Game.Players;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// Simple opponent driver for the shell engine: the engine has no AI, so without this the enemy turn is
/// a no-op and no full game can be played. The policy is deliberately naive — deploy the cheapest unit,
/// then advance, then attack the first legal target — and every decision goes through the engine's own
/// entry points and availability queries, exactly like the human side. It is a harness, not a rule.
/// </summary>
public static class OrcOpponentDriver
{
    private const int MaxActions = 12;

    public static async Task PlayTurnAsync(Match match, Player opponent, Action<string>? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(opponent);
        var actions = 0;
        while (match.State == MatchState.InProgress && match.Phase == MatchPhase.Play
            && ReferenceEquals(match.CurrentPlayer, opponent) && actions < MaxActions)
        {
            if (!await TryOneActionAsync(match, opponent, log, ct)) break;
            actions++;
            await Task.Delay(80, ct);
        }
        if (match.State == MatchState.InProgress && ReferenceEquals(match.CurrentPlayer, opponent))
            await match.EndTurn(ct);
    }

    private static async Task<bool> TryOneActionAsync(Match match, Player opponent, Action<string>? log, CancellationToken ct)
    {
        // 1) Deploy the cheapest affordable unit in hand.
        foreach (var card in opponent.Hand.OfType<UnitCard>().OrderBy(c => DeployCost(c)))
        {
            if (DeployCost(card) > opponent.Points) continue;
            var play = await match.PlayManager.BeginUnitPrePlayAsync(card, ct);
            log?.Invoke($"deploy {card.Name} -> {play.Status} {play.FailureReason}");
            if (play.Status == PlayResultStatus.Success) return true;
        }
        // 2) Advance units that may move.
        foreach (var unit in BoardUnits(match, opponent))
        {
            var availability = match.CommandManager.GetCommandAvailability(unit);
            if (!availability.Move.CanUse) continue;
            var move = await match.CommandManager.BeginMoveAsync(unit, ct);
            if (move.Status == CommandResultStatus.Success) return true;
        }
        // 3) Attack the first legal target.
        foreach (var unit in BoardUnits(match, opponent))
        {
            var availability = match.CommandManager.GetCommandAvailability(unit);
            if (!availability.Attack.CanUse) continue;
            var strike = await match.CommandManager.BeginAttackAsync(unit, ct);
            if (strike.Status == CommandResultStatus.Success) return true;
        }
        return false;
    }

    private static IEnumerable<UnitCard> BoardUnits(Match match, Player owner)
    {
        var battlefield = match.Battlefield;
        foreach (var line in new[] { battlefield.PlayerASupportLine, battlefield.FrontLine, battlefield.PlayerBSupportLine })
            foreach (var slot in line)
                if (slot.Occupant is UnitCard unit && ReferenceEquals(unit.Owner, owner))
                    yield return unit;
    }

    private static int DeployCost(UnitCard card)
    {
        try { return card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost); }
        catch (InvalidOperationException) { return card.Definition.DeployCost; }
    }
}
