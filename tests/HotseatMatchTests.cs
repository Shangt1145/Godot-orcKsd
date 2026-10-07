using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Kards.Ui.Contracts;
using Orc.Game.Cards;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

/// <summary>
/// Hotseat: one match, two seats, one screen. The engine already supports two players in a single
/// match — what the UI adds is the ability to look at the game from either side.
///
/// The properties that matter: the two seats see different hands, each is offered only its own
/// actions, and switching seats never starts a second game. Hidden information is the whole
/// reason this is worth testing, so the disjointness assertion is the one that must not weaken.
/// </summary>
public sealed class HotseatMatchTests
{
    private const string CatalogDir = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";

    private readonly ITestOutputHelper _out;
    public HotseatMatchTests(ITestOutputHelper output) => _out = output;

    private static (IReadOnlyList<CardDefinitionEntry> Pool, IReadOnlyList<string> Deck) RealPool()
    {
        var catalog = new CardCatalog();
        catalog.Load(CatalogDir, CardCatalog.ResolveArtResource);
        var (pool, _) = CardPoolCompiler.Compile(catalog.Cards);
        return (pool, DeckBuilder.Build(pool, seed: 20261007));
    }

    [Fact]
    public async Task TwoSeatsOverOneMatchSeeDifferentHands()
    {
        var (pool, deck) = RealPool();
        var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 4242, matchId: "hotseat");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            // The other seat adopts the running match. Initialising it would try to start a second
            // game on the same engine, so it must attach instead.
            var second = new OrcMatchHost(session.Host.Match, "hotseat-b", viewerIndex: 1);
            second.AttachToLiveMatch();
            second.Pump();

            var a = session.View;
            var b = second.View;
            // KARDS: the first player draws 4, the second 5. If these ever match, the seats are
            // reading the same player and hidden information is already leaking.
            Assert.Equal(4, a.SelfHand.Count);
            Assert.Equal(5, b.SelfHand.Count);
            Assert.Equal(5, a.EnemyHandCount);
            Assert.Equal(4, b.EnemyHandCount);
            Assert.Empty(a.SelfHand.Select(c => c.Uid).Intersect(b.SelfHand.Select(c => c.Uid)));

            // Each seat sees the other's line from its own side.
            Assert.Same(session.Host.Match, second.Match);
        }
        finally { session.Dispose(); }
    }

    /// <summary>A seat must only be offered the cards and moves that are actually its own.</summary>
    [Fact]
    public async Task EachSeatIsOfferedOnlyItsOwnActions()
    {
        var (pool, deck) = RealPool();
        var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 99, matchId: "hotseat-actions");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();
            var second = new OrcMatchHost(session.Host.Match, "hotseat-b", viewerIndex: 1);
            second.AttachToLiveMatch();
            second.Pump();

            foreach (var (seat, host) in new[] { (0, session.Host), (1, second) })
            {
                var hand = host.View.SelfHand.Select(c => c.Uid).ToHashSet(StringComparer.Ordinal);
                foreach (var uid in host.Actions.PlayableUids)
                    Assert.Contains(uid, hand);
                _out.WriteLine($"seat{seat}: hand={hand.Count} playable={host.Actions.PlayableUids.Count} " +
                    $"moves={host.Actions.Moves.Count} attacks={host.Actions.AttackPreviews.Count}");
            }
        }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// A play by seat 0 must show up in seat 1's view as an enemy board. This is the whole point of
    /// hotseat: one game, observed from both sides, with no divergence between them.
    /// </summary>
    [Fact]
    public async Task APlayByOneSeatAppearsOnTheOthersBoard()
    {
        var (pool, deck) = RealPool();
        var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 20261007, matchId: "hotseat-play");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();
            var second = new OrcMatchHost(session.Host.Match, "hotseat-b", viewerIndex: 1);
            second.AttachToLiveMatch();

            var view = session.View;
            var legal = view.SelfHand
                .Where(c => session.Actions.PlayableUids.Contains(c.Uid)
                    && c.Definition.Cost <= view.SelfKredits
                    && c.Definition.CardType == "unit")
                .ToArray();
            if (legal.Length == 0) return; // this hand dealt nothing playable; not a hotseat concern

            Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(legal[0].Uid)));
            session.Pump();
            second.Pump();

            Assert.Single(session.View.SelfLine);
            // The other seat must see exactly that unit on its enemy line, from its own side.
            Assert.Single(second.View.EnemyLine);
            Assert.Equal(legal[0].Uid, second.View.EnemyLine[0].Uid);
        }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// Adopting a match that has not started is refused rather than half-initialised. The match
    /// constructor rejects an empty deck, so the fixture uses a legal deck and simply never
    /// initialises it — which is the state a second seat must not adopt.
    /// </summary>
    [Fact]
    public void AttachingToAPreparingMatchIsRefused()
    {
        var (pool, deck) = RealPool();
        var match = new Orc.Game.Match(
            new Orc.Game.Collections.CardList(deck), new Orc.Game.Collections.CardList(deck), pool);
        var host = new OrcMatchHost(match, "preparing");
        Assert.Throws<InvalidOperationException>(() => host.AttachToLiveMatch());
    }
}
