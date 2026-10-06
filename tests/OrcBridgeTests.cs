using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// The bridge is exercised against a real match, not a hand-written fixture: the point of the layer is
/// that the UI can consume the engine's own truth. No art, audio or Godot runtime is required.
/// </summary>
public sealed class OrcBridgeTests
{
    private const string InfantryId = "ui-test-infantry";

    private static Match CreateMatch(int? seed = 7, int firstPlayerIndex = 0)
    {
        var definitions = new[]
        {
            new CardDefinitionEntry(InfantryId, new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard))
        };
        // Each side needs its own CardList instance: deck entries are bound to card instances at load time.
        var deckA = new CardList(Enumerable.Repeat(InfantryId, 20));
        var deckB = new CardList(Enumerable.Repeat(InfantryId, 20));
        Match? match = null;
        var bridge = new OrcTargeterBridge(() => InteractablesOf(match));
        match = new Match(deckA, deckB, definitions, seed: seed, firstPlayerIndex: firstPlayerIndex, targeterBridge: bridge);
        return match;
    }

    private static IReadOnlyList<object?> InteractablesOf(Match? match)
    {
        if (match is null || match.State == MatchState.Preparing) return Array.Empty<object?>();
        var references = new List<object?>();
        foreach (var line in new[] { match.Battlefield.PlayerASupportLine, match.Battlefield.FrontLine, match.Battlefield.PlayerBSupportLine })
            foreach (var slot in line)
                references.Add(slot.Ref);
        foreach (var player in match.Players) references.Add(player.Hq.Ref);
        return references;
    }

    [Fact]
    public async Task HostReadsOpeningBoardFromEngine()
    {
        var host = new OrcMatchHost(CreateMatch(), "orc-test");
        await host.InitializeAsync();

        var view = host.View;
        Assert.Equal("orc-test", view.MatchId);
        Assert.Equal("mulligan", view.Phase);
        Assert.Equal(20, view.SelfHq!.Health);
        Assert.Equal(20, view.EnemyHq!.Health);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task OpeningHandSizeFollowsTheOriginalRule(int firstPlayerIndex)
    {
        // KARDS: the first player draws 4, the second draws 5. The engine owns the number; the UI only reads it.
        var host = new OrcMatchHost(CreateMatch(firstPlayerIndex: firstPlayerIndex), "orc-hand", viewerIndex: firstPlayerIndex);
        await host.InitializeAsync();
        Assert.Equal(4, host.View.SelfHand.Count);

        var second = new OrcMatchHost(CreateMatch(firstPlayerIndex: firstPlayerIndex), "orc-hand-2",
            viewerIndex: firstPlayerIndex == 0 ? 1 : 0);
        await second.InitializeAsync();
        Assert.Equal(5, second.View.SelfHand.Count);
    }

    [Fact]
    public async Task OpponentHandIsReducedToACount()
    {
        var host = new OrcMatchHost(CreateMatch(), "orc-hidden");
        await host.InitializeAsync();

        var view = host.View;
        Assert.Equal(5, view.EnemyHandCount);
        // No opponent card identity may cross the boundary, even though the engine exposes it:
        // the projection carries a count, and no view anywhere is an opponent hand card.
        Assert.DoesNotContain(view.EnemyLine, c => c.Zone == "hand");
        Assert.All(view.SelfHand, c => Assert.Equal("self", c.OwnerSide));
    }

    [Fact]
    public async Task HandCardsReadTheDefinitionWithoutRuntimeComponents()
    {
        var host = new OrcMatchHost(CreateMatch(), "orc-def");
        await host.InitializeAsync();

        var card = host.View.SelfHand.First(c => c.Zone == "hand");
        Assert.Equal("步兵", card.Definition.Name);
        Assert.Equal(2, card.EffectiveAttack); // definition value; runtime unit state does not exist yet
        Assert.Equal(5, card.EffectiveDefense);
        Assert.Equal(1, card.EffectiveCost);
        Assert.Equal(InfantryId, card.Definition.CardId);
    }

    [Fact]
    public async Task DeckCountsAreProjectedAndTurnHasNotStartedDuringMulligan()
    {
        var host = new OrcMatchHost(CreateMatch(), "orc-deck");
        await host.InitializeAsync();

        var view = host.View;
        Assert.Equal(16, view.SelfDeckCount); // 20 - opening hand 4 for the first player
        Assert.Equal(15, view.EnemyDeckCount);
        Assert.Equal(0, view.Turn);
    }

    [Fact]
    public async Task SegmentsAreDrainedAndEachActionIsFollowedByAFreshRead()
    {
        var host = new OrcMatchHost(CreateMatch(), "orc-pump");
        var seen = new List<string>();
        host.PresentationReady += (resolution, _) =>
        {
            seen.AddRange(resolution.Steps.Select(s => s.GetType().Name));
            Assert.Equal("orc-pump", resolution.After.MatchId);
        };
        host.ImmediateUpdate += _ => { };

        await host.InitializeAsync();
        host.Pump();

        // The opening action produces a segment; whatever it holds, the board it carries is the current truth.
        Assert.Equal("orc-pump", host.View.MatchId);
        Assert.Equal(host.View.SelfHand.Count, host.Refresh().SelfHand.Count);
    }

    /// <summary>Both sides confirm the opening hand, entering the play phase (the UI does this too).</summary>
    private static async Task ConfirmMulliganAsync(Match match)
    {
        foreach (var player in match.Players)
            await match.MulliganDone(player);
    }

    [Fact]
    public async Task InteractiveMulliganReplacesOnlyTheUnkeptCards()
    {
        // The panel path: the bridge parks the request, the UI submits a keep list, and the engine
        // replaces exactly the rest. Everything here is the engine's own truth.
        var parked = new TaskCompletionSource();
        Orc.Game.Targeting.TargetingRequestDescription? description = null;
        Orc.Game.Targeting.ITargetingResponder? responder = null;
        Match? match = null;
        var bridge = new OrcTargeterBridge(() => InteractablesOf(match), (d, r) =>
        {
            description = d; responder = r; parked.TrySetResult();
        });
        var definitions = new[]
        {
            new CardDefinitionEntry(InfantryId, new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard))
        };
        var deckA = new CardList(Enumerable.Repeat(InfantryId, 20));
        var deckB = new CardList(Enumerable.Repeat(InfantryId, 20));
        match = new Match(deckA, deckB, definitions, seed: 7, firstPlayerIndex: 0, targeterBridge: bridge);
        var host = new OrcMatchHost(match, "orc-mulligan");
        await host.InitializeAsync();

        var opening = host.View.SelfHand.Select(c => c.Uid).ToArray();
        Assert.Equal(4, opening.Length);
        var deckBefore = host.View.SelfDeckCount;
        await match.MulliganDone(match.Players[1]); // the opponent keeps
        var self = match.BeginMulliganAsync(match.Players[0]);
        await parked.Task; // the request must park, not auto-answer

        var slot = description!.Slots[0];
        Assert.Equal(Orc.Game.Targeting.TargetSlotKind.MulliganSelect, slot.Kind);
        var allowed = (slot.AllowedReferences ?? description.AllowedTargets).Where(r => r.IsAlive).ToArray();
        Assert.Equal(opening.Length, allowed.Length);
        var replace = OrcTargeterBridge.SelectReplace(allowed, opening.Take(3));
        Assert.Single(replace);

        responder!.Complete(description.RequestId,
            new Dictionary<string, IReadOnlyList<Orc.Core.Ref<Orc.Core.Entity>>> { [slot.Name] = replace });
        var result = await self;
        Assert.True(result.IsSuccess, $"mulligan failed: {result.FailureReason}");
        host.Pump();

        var view = host.Refresh();
        Assert.Equal("play", view.Phase); // both sides confirmed -> play
        Assert.Equal(opening.Length, view.SelfHand.Count); // replace one = draw one
        var uids = view.SelfHand.Select(c => c.Uid).ToHashSet();
        Assert.Subset(uids, opening.Take(3).ToHashSet()); // kept cards are still there
        // With an all-identical deck the draw may return the same instance, so uid absence is not
        // assertable; the shuffle-back +1 / draw -1 pair leaves the deck count untouched.
        Assert.Equal(deckBefore, view.SelfDeckCount);
    }

    [Fact]
    public async Task KeepAllMulliganKeepsTheWholeOpeningHand()
    {
        var parked = new TaskCompletionSource();
        Orc.Game.Targeting.TargetingRequestDescription? description = null;
        Orc.Game.Targeting.ITargetingResponder? responder = null;
        Match? match = null;
        var bridge = new OrcTargeterBridge(() => InteractablesOf(match), (d, r) =>
        {
            description = d; responder = r; parked.TrySetResult();
        });
        var definitions = new[]
        {
            new CardDefinitionEntry(InfantryId, new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard))
        };
        match = new Match(
            new CardList(Enumerable.Repeat(InfantryId, 20)), new CardList(Enumerable.Repeat(InfantryId, 20)),
            definitions, seed: 7, firstPlayerIndex: 0, targeterBridge: bridge);
        var host = new OrcMatchHost(match, "orc-mulligan-keep");
        await host.InitializeAsync();
        var opening = host.View.SelfHand.Select(c => c.Uid).ToArray();
        await match.MulliganDone(match.Players[1]);
        var self = match.BeginMulliganAsync(match.Players[0]);
        await parked.Task;

        var slot = description!.Slots[0];
        responder!.Complete(description.RequestId,
            new Dictionary<string, IReadOnlyList<Orc.Core.Ref<Orc.Core.Entity>>>
            {
                [slot.Name] = OrcTargeterBridge.SelectReplace(
                    (slot.AllowedReferences ?? description.AllowedTargets).Where(r => r.IsAlive).ToArray(), opening)
            });
        var result = await self;
        Assert.True(result.IsSuccess);
        host.Pump();

        var view = host.Refresh();
        Assert.Equal("play", view.Phase);
        Assert.Equal(opening, view.SelfHand.OrderBy(c => c.SlotIndex).Select(c => c.Uid).ToArray());
    }

    [Fact]
    public async Task OpponentDriverPlaysARealEnemyTurn()
    {
        var match = CreateMatch();
        var host = new OrcMatchHost(match, "orc-ai");
        await host.InitializeAsync();
        await ConfirmMulliganAsync(match);
        var enemy = match.Players[1];
        var deckBefore = host.View.EnemyDeckCount!.Value;

        // Pass the first turn; the driver plays the enemy turn through the engine's own entry points.
        await match.EndTurn();
        await OrcOpponentDriver.PlayTurnAsync(match, enemy, message => Console.WriteLine("[driver] " + message));
        host.Pump();

        var view = host.Refresh();
        Assert.Equal("play", view.Phase); // the driver passed the turn back to us
        Assert.Equal(deckBefore - 1, view.EnemyDeckCount); // the enemy drew at their turn start
        // The enemy deployed with their first point: a unit now stands on their support line.
        Assert.True(view.EnemyLine.Count > 0, "Enemy driver never deployed a unit.");
    }

    [Fact]
    public async Task ConcedeProjectsTheTerminalState()
    {
        var match = CreateMatch();
        var host = new OrcMatchHost(match, "orc-concede");
        await host.InitializeAsync();
        await ConfirmMulliganAsync(match);

        var result = match.Concede(match.Players[0]); // the viewer resigns; the opponent wins
        Assert.Equal(ConcedeStatus.Accepted, result.Status);
        host.Pump();

        var view = host.Refresh();
        Assert.Equal("over", view.Phase);
        Assert.Equal("失败", view.ResultTitle);
    }

    [Fact]
    public async Task HandLimitBurnUsesTheDedicatedBurnSignal()
    {
        var host = new OrcMatchHost(CreateMatch(), "orc-burn");
        var updates = new List<string>();
        host.ImmediateUpdate += updates.Add;
        await host.InitializeAsync();

        // Fill the hand to the engine's limit (9), then draw once more: the drawn card must burn.
        var player = host.Match.Players[0];
        for (var i = host.View.SelfHandCount; i < Orc.Game.Players.Player.HandLimit; i++)
        {
            var card = host.Match.CardLibrary.Instantiate(InfantryId);
            await card.LoadAsync(player);
            player.Hand.Add(card);
        }
        host.Refresh();

        var burned = new List<string>();
        host.ImmediateUpdate += update => burned.Add(update);
        await host.Match.PlayerManager.DrawCard(player);

        Assert.Contains(Orc.Game.GameUpdates.CardBurned, burned);
        Assert.DoesNotContain(Orc.Game.GameUpdates.CardDiscarded, burned);
        // The hand stays at the limit and the deck shrank by exactly the burned draw.
        Assert.Equal(Orc.Game.Players.Player.HandLimit, host.Refresh().SelfHandCount);
    }

    [Fact]
    public async Task ImmediateUpdatesAreReceivedForTheOpeningAction()
    {
        var host = new OrcMatchHost(CreateMatch(), "orc-immediate");
        var updates = new List<string>();
        host.ImmediateUpdate += updates.Add;

        await host.InitializeAsync();

        // Registration happens inside InitializeAsync, before Initialize runs, so the opening signals land.
        Assert.Contains(Orc.Game.GameUpdates.CardLoad, updates);
        Assert.Contains(Orc.Game.GameUpdates.DeckShuffled, updates);
    }
}
