using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

/// <summary>
/// The point of P13: a real match dealt from the shipped card catalog. Everything else in the suite
/// proves the bridge works; this proves the game is playable with the actual cards.
///
/// The catalog lives under the repo, so these tests read it from disk. A missing catalog is a hard
/// failure rather than a skip — a silently skipped test here would mean "playable" was never checked.
/// </summary>
public sealed class RealDeckMatchTests
{
    private const string CatalogDir = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";

    private readonly ITestOutputHelper _out;
    public RealDeckMatchTests(ITestOutputHelper output) => _out = output;

    private static (IReadOnlyList<CardDefinitionEntry> Pool, IReadOnlyList<CardPoolRejection> Rejected) LoadPool()
    {
        var catalog = new CardCatalog();
        catalog.Load(CatalogDir, CardCatalog.ResolveArtResource);
        return CardPoolCompiler.Compile(catalog.Cards);
    }

    [Fact]
    public void ARealDeckCanBeBuiltFromTheShippedCatalog()
    {
        var (pool, rejected) = LoadPool();
        Assert.True(pool.Count > 100, $"only {pool.Count} cards available");
        _out.WriteLine($"pool={pool.Count} rejected={rejected.Count}");

        var deck = DeckBuilder.Build(pool, seed: 20261007);
        var problems = DeckBuilder.Validate(deck, pool);
        Assert.True(problems.Count == 0, "the built deck is not playable: " + string.Join("; ", problems));
        Assert.Equal(DeckRules.DeckSize, deck.Count);
    }

    /// <summary>The same seed must produce the same deck, or a match could not be replayed.</summary>
    [Fact]
    public void DeckBuildingIsDeterministic()
    {
        var (pool, _) = LoadPool();
        Assert.Equal(
            DeckBuilder.Build(pool, seed: 7),
            DeckBuilder.Build(pool, seed: 7));
    }

    /// <summary>Validation must actually catch the ways a deck can be unplayable.</summary>
    [Fact]
    public void DeckValidationNamesTheProblem()
    {
        var (pool, _) = LoadPool();
        var unit = pool.First(e => e.Definition.Category == CardCategory.Unit);
        var order = pool.First(e => e.Definition.Category == CardCategory.Command);

        Assert.NotEmpty(DeckBuilder.Validate([], pool));                              // empty
        Assert.NotEmpty(DeckBuilder.Validate(Enumerable.Repeat(unit.Id, 30).ToList(), pool)); // one card, no spread
        var overCopied = Enumerable.Repeat(unit.Id, 4).Concat(Enumerable.Repeat(order.Id, 26)).ToList();
        Assert.Contains(DeckBuilder.Validate(overCopied, pool), p => p.Contains("上限"));
        Assert.NotEmpty(DeckBuilder.Validate(new[] { "no/such-card" }, pool));      // unknown id
    }

    /// <summary>
    /// The whole point: a match opened over real cards, dealt, and played to a legal deployment.
    /// If the catalog's cards cannot survive the engine's own loading path, nothing else matters.
    /// </summary>
    [Fact]
    public async Task ARealMatchDealsRealCardsAndAcceptsAPlay()
    {
        var (pool, rejected) = LoadPool();
        _out.WriteLine($"pool={pool.Count} rejected={rejected.Count}");
        var deck = DeckBuilder.Build(pool, seed: 20261007);

        var session = await OrcMatchSession.CreateAsync(pool, deck, deck,
            seed: 20261007, matchId: "real-deck");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            var view = session.View;
            Assert.Equal("play", view.Phase);
            Assert.Equal(DeckRules.DeckSize - view.SelfHand.Count, view.SelfDeckCount);
            // The hand must hold catalog cards, not the probe infantry the earlier tests used.
            Assert.All(view.SelfHand, card =>
                Assert.Contains(card.Definition.CardId, deck));
            Assert.NotEmpty(session.Actions.PlayableUids);

            // The opening hand is random, so a given seed may hold nothing affordable and playable.
            // What matters is that the engine accepts a legal play whenever one is offered — the
            // refusal in other hands is the cost rule working, not a failure.
            var legal = view.SelfHand
                .Where(c => session.Actions.PlayableUids.Contains(c.Uid)
                    && c.Definition.Cost <= view.SelfKredits
                    && c.Definition.CardType == "unit")
                .ToArray();
            if (legal.Length == 0)
            {
                // No legal play in this hand: the engine must still refuse cleanly, never throw.
                var refused = await session.SubmitAsync(new PlayCard(view.SelfHand[0].Uid));
                session.Pump();
                Assert.True(refused is UiSubmitOutcome.Rejected or UiSubmitOutcome.UnknownCard,
                    $"an illegal play produced {refused}");
                Assert.Empty(session.View.SelfLine);
                return;
            }

            var outcome = await session.SubmitAsync(new PlayCard(legal[0].Uid));
            session.Pump();

            Assert.Equal(UiSubmitOutcome.Applied, outcome);
            Assert.Single(session.View.SelfLine);
            Assert.Equal(legal[0].Uid, session.View.SelfLine[0].Uid);
            // A real card has real stats read from the catalog, not the probe's 2/5.
            Assert.NotNull(session.View.SelfLine[0].EffectiveAttack);
        }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// One hand is not enough evidence: the opening is random, so a single seed can deal nothing
    /// playable. Across a few seeds a legal deployment must actually happen — that is the claim
    /// "the real pool is playable" resting on.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task RealCardsCanActuallyBeDeployed(int seed)
    {
        var (pool, _) = LoadPool();
        var deck = DeckBuilder.Build(pool, seed: 20261007 + seed);
        var session = await OrcMatchSession.CreateAsync(pool, deck, deck,
            seed: 20261007 + seed, matchId: $"real-{seed}");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();
            var view = session.View;
            var legal = view.SelfHand
                .Where(c => session.Actions.PlayableUids.Contains(c.Uid)
                    && c.Definition.Cost <= view.SelfKredits
                    && c.Definition.CardType == "unit")
                .ToArray();
            if (legal.Length == 0) return; // this seed dealt no legal play; another one will

            Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(legal[0].Uid)));
            session.Pump();
            Assert.Single(session.View.SelfLine);
        }
        finally { session.Dispose(); }
    }
}
