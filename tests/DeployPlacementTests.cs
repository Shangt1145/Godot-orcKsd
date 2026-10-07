using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game;
using Orc.Game.Cards;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

/// <summary>
/// Which slot a deployment lands on, measured on the real engine.
///
/// The engine offers deployments two entries. <c>BeginUnitPrePlayAsync</c> is the interactive one and
/// its candidate set is men's adjacency rule — for the support line that is a single slot, the one
/// next to the last occupied position. <c>PlayUnitAsync</c> is listed as an available entry point too
/// and takes the slot from the caller, checking only that it is empty. These probes measure what each
/// entry actually does, including whether the cheaper one still enforces the command points, because
/// the doc comment claims a re-check that the guard clauses do not obviously perform.
/// </summary>
public sealed class DeployPlacementTests
{
    private const string CatalogDir = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
    private readonly ITestOutputHelper _out;
    public DeployPlacementTests(ITestOutputHelper output) => _out = output;

    private static (IReadOnlyList<CardDefinitionEntry> Pool, IReadOnlyList<CardPoolRejection> Rejected) LoadPool()
    {
        var catalog = new CardCatalog();
        catalog.Load(CatalogDir, CardCatalog.ResolveArtResource);
        return CardPoolCompiler.Compile(catalog.Cards);
    }

    /// <summary>Where a card ended up, or why it did not, phrased for the log.</summary>
    private static string Where(OrcMatchSession session, string uid)
        => session.View.SelfLine.FirstOrDefault(c => c.Uid == uid) is { } placed
            ? $"{placed.Zone}[{placed.SlotIndex}]"
            : session.View.SelfHand.Any(c => c.Uid == uid) ? "<still in hand>" : "<gone>";

    /// <summary>
    /// The interactive entry: the engine decides the slot, and its candidate set is the adjacency rule.
    /// Whatever the player asked for is not represented here at all.
    /// </summary>
    [Fact]
    public async Task TheInteractiveEntryOnlyEverOffersTheAdjacentSlot()
    {
        var (pool, _) = LoadPool();
        var deck = DeckBuilder.Build(pool, seed: 20261007);
        var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 2, matchId: "deploy-interactive",
            openingHand: DemoOpeningHand.CardIds);
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();
            var match = session.Host.Match;
            var player = match.Players[0];
            var line = match.Battlefield.GetSupportLine(player);
            var empty = Enumerable.Range(0, line.Count).Where(i => line[i].IsEmpty).ToArray();
            var unit = player.Hand.OfType<UnitCard>().OrderBy(u => u.Definition.DeployCost).Skip(1).First();

            _out.WriteLine($"support slots={line.Count} empty=[{string.Join(',', empty)}]");
            _out.WriteLine($"kredits={session.View.SelfKredits} unit={unit.Name} cost={unit.Definition.DeployCost} " +
                $"points={player.Points}");

            var result = await match.PlayManager.BeginUnitPrePlayAsync(unit);
            session.Pump();
            _out.WriteLine($"interactive status={result.Status} reason={result.FailureReason} " +
                $"kredits={session.View.SelfKredits} points={player.Points} where={Where(session, session.View.SelfHand.FirstOrDefault()?.Uid ?? "")}");
        }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// The direct entry, aimed at the far end of an otherwise empty support line — a slot the adjacency
    /// rule would never offer. The cheapest card is used on purpose: the first turn grants one command
    /// point, so only a one-cost unit can prove the placement without the points getting in the way.
    /// </summary>
    [Fact]
    public async Task TheDirectEntryPlacesOnTheSlotItIsGiven()
    {
        var (pool, _) = LoadPool();
        var deck = DeckBuilder.Build(pool, seed: 20261007);
        var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 2, matchId: "deploy-direct",
            openingHand: DemoOpeningHand.CardIds);
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();
            var match = session.Host.Match;
            var player = match.Players[0];
            var line = match.Battlefield.GetSupportLine(player);
            var unit = (UnitCard)player.Hand.OfType<UnitCard>().OrderBy(u => u.Definition.DeployCost).First();
            var uid = OrcRefs.KeyOf(unit);
            var empty = Enumerable.Range(0, line.Count).Where(i => line[i].IsEmpty).ToArray();
            var far = empty.Last();

            var before = session.View.SelfKredits;
            var result = await match.PlayManager.PlayUnitAsync(unit, line[far]);
            session.Pump();
            var landed = session.View.SelfLine.FirstOrDefault(c => c.Uid == uid);

            _out.WriteLine($"[direct] empty=[{string.Join(',', empty)}] ask=slot{far} unit={unit.Name} " +
                $"cost={unit.Definition.DeployCost} kredits={before} -> status={result.Status} " +
                $"kreditsAfter={session.View.SelfKredits} landed={landed?.Zone}[{landed?.SlotIndex}]");

            Assert.Equal(PlayResultStatus.Success, result.Status);
            Assert.NotNull(landed);
            Assert.Equal("support", landed!.Zone);
            Assert.Equal(far, landed.SlotIndex);
            Assert.Equal(before - unit.Definition.DeployCost, session.View.SelfKredits);
        }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// The direct entry skips the interactive pre-play request, so this guards the rule it must still
    /// keep: a card that costs more than the player has is refused, with no card spent and no points
    /// charged. Written because the entry's doc claims an "outer re-check" that its guard clauses do
    /// not obviously contain — the check turns out to live in the play chain's verification.
    /// </summary>
    [Fact]
    public async Task TheDirectEntryStillEnforcesTheCommandPoints()
    {
        var (pool, _) = LoadPool();
        var deck = DeckBuilder.Build(pool, seed: 20261007);
        var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 2, matchId: "deploy-points",
            openingHand: DemoOpeningHand.CardIds);
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();
            var match = session.Host.Match;
            var player = match.Players[0];
            var line = match.Battlefield.GetSupportLine(player);
            // One point on the first turn; the second-cheapest card of the pinned hand costs two.
            var unit = (UnitCard)player.Hand.OfType<UnitCard>().OrderBy(u => u.Definition.DeployCost).Skip(1).First();
            var uid = OrcRefs.KeyOf(unit);
            var far = Enumerable.Range(0, line.Count).Where(i => line[i].IsEmpty).Last();

            var before = session.View.SelfKredits;
            Assert.True(unit.Definition.DeployCost > before, "the probe needs a card the player cannot afford");
            var result = await match.PlayManager.PlayUnitAsync(unit, line[far]);
            session.Pump();

            _out.WriteLine($"[points] unit={unit.Name} cost={unit.Definition.DeployCost} kredits={before} " +
                $"status={result.Status} reason={result.FailureReason} kreditsAfter={session.View.SelfKredits} " +
                $"handAfter={session.View.SelfHand.Count}");

            Assert.NotEqual(PlayResultStatus.Success, result.Status);
            Assert.Equal(before, session.View.SelfKredits);
            Assert.Contains(session.View.SelfHand, c => c.Uid == uid);
            Assert.DoesNotContain(session.View.SelfLine, c => c.Uid == uid);
        }
        finally { session.Dispose(); }
    }
}
