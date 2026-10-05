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
        return new Match(deckA, deckB, definitions, seed: seed, firstPlayerIndex: firstPlayerIndex);
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
