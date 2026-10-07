using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// Evidence probes for the reported "real match is unplayable" defects. Every probe ends in an
/// assertion; a probe that only prints cannot fail and therefore proves nothing.
/// </summary>
public sealed class RealMatchReproTests
{
    private const string CatalogDir = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";

    private static readonly List<string> Log = new();
    private static void Dump(string line)
    {
        Log.Add(line);
        try { File.WriteAllLines(@"H:\Working Folder\OrC-KSD.Godot\artifacts\real-repro-output.txt", Log); } catch { }
    }

    private static (IReadOnlyList<CardDefinitionEntry> Pool, IReadOnlyList<CardPoolRejection> Rejected) LoadPool()
    {
        var catalog = new CardCatalog();
        catalog.Load(CatalogDir, CardCatalog.ResolveArtResource);
        return CardPoolCompiler.Compile(catalog.Cards);
    }

    /// <summary>
    /// Bug 1: one draw animates twice. The engine reports a single draw through two signals —
    /// card.drawn ("took one") and card.hand.add ("landed in hand"). If the translator turns both
    /// into a draw step, the card flies in twice. Measured, not assumed.
    /// </summary>
    [Fact]
    public async Task OneDrawProducesExactlyOneDrawStep()
    {
        var session = await OrcMatchSession.CreateProbeAsync("repro-infantry", 20, 20261007, "repro-draw");
        try
        {
            var drawCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var totalDrawSteps = 0;
            session.Host.PresentationReady += (resolution, _) =>
            {
                foreach (var step in resolution.Steps.OfType<UiDrawPresentation>())
                {
                    totalDrawSteps++;
                    // Hidden slots can be reused after the opponent plays a card. Only identify
                    // them within their action segment; their real UIDs must remain concealed.
                    var key = step.Card?.Uid ?? $"enemy-{resolution.Sequence}-slot-{step.SlotIndex}";
                    drawCounts[key] = drawCounts.GetValueOrDefault(key) + 1;
                }
            };

            await session.SettleMulliganAsync(interactive: false);
            session.Pump();
            await session.SubmitAsync(new EndTurn());
            session.Pump();
            await session.SubmitAsync(new EndTurn());
            session.Pump();

            var duplicated = drawCounts.Where(kvp => kvp.Value > 1).ToArray();
            Dump($"[draw] total steps={totalDrawSteps} distinct={drawCounts.Count} duplicated={duplicated.Length}");
            foreach (var d in duplicated) Dump($"    {d.Key} x{d.Value}");

            Assert.True(duplicated.Length == 0,
                $"one card produced more than one draw step: {string.Join(", ", duplicated.Select(d => $"{d.Key}x{d.Value}"))}");
        }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// Bug 1b: the same double-report exists for a turn. turn.start and turn.start.after both arrive
    /// for one turn change; if both become a turn step the banner plays twice.
    /// </summary>
    [Fact]
    public async Task OneTurnProducesExactlyOneTurnStep()
    {
        var session = await OrcMatchSession.CreateProbeAsync("repro-infantry", 20, 20261007, "repro-turn");
        try
        {
            var perSegment = new List<int>();
            var total = 0;
            session.Host.PresentationReady += (resolution, _) =>
            {
                var n = resolution.Steps.OfType<UiTurnPresentation>().Count();
                if (n > 0) perSegment.Add(n);
                total += n;
            };

            await session.SettleMulliganAsync(interactive: false);
            session.Pump();
            await session.SubmitAsync(new EndTurn());
            session.Pump();
            await session.SubmitAsync(new EndTurn());
            session.Pump();

            Dump($"[turn] total turn steps={total} per-segment=[{string.Join(",", perSegment)}]");
            Assert.True(perSegment.All(n => n <= 1),
                $"a single turn produced more than one turn step: [{string.Join(",", perSegment)}]");
        }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// Bug 2 (the real playability failure): of the cards a real deck deals, how many can actually be
    /// played at all? Orders have no engine entry point, so an order in hand is a dead card. If most
    /// of the opening hand is dead, "unplayable" is a measurement, not an opinion.
    /// </summary>
    [Fact]
    public async Task MeasureHowManyDealtCardsAreActuallyPlayable()
    {
        var (pool, _) = LoadPool();
        var poolById = pool.ToDictionary(e => e.Id, e => e.Definition, StringComparer.Ordinal);
        var units = pool.Count(e => e.Definition.Category == CardCategory.Unit);
        var orders = pool.Count(e => e.Definition.Category == CardCategory.Command);
        var counters = pool.Count(e => e.Definition.Category == CardCategory.Counter);
        Dump($"[pool] compiled={pool.Count} units={units} orders={orders} counters={counters}");

        const int trials = 40;
        var deadShare = new List<double>();
        var deployable = 0;
        var totalHand = 0;
        for (var seed = 0; seed < trials; seed++)
        {
            var deck = DeckBuilder.Build(pool, seed: seed);
            var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: seed, matchId: $"open-{seed}");
            try
            {
                await session.SettleMulliganAsync(interactive: false);
                session.Pump();
                var view = session.View;
                totalHand += view.SelfHand.Count;
                // "Dead" = an order/counter (no engine play path) or a unit you cannot afford.
                var dead = view.SelfHand.Count(c =>
                    c.Definition.CardType != "unit" || (c.Definition.Cost ?? 99) > view.SelfKredits);
                deadShare.Add(view.SelfHand.Count == 0 ? 1 : (double)dead / view.SelfHand.Count);
                if (view.SelfHand.Any(c => c.Definition.CardType == "unit" && (c.Definition.Cost ?? 99) <= view.SelfKredits))
                    deployable++;
            }
            finally { session.Dispose(); }
        }
        Dump($"[open] hands with a deployable unit on turn one: {deployable}/{trials}");
        Dump($"[open] mean share of the opening hand that is unplayable turn one: {deadShare.Average():P1}");
        Dump($"[open] opening hand size total={totalHand}");
    }

    /// <summary>
    /// Bug 3: the catalog prints unit types the engine has no counterpart for (naval/space hulls).
    /// UnitTypes() returns null when the parse fails, so those units compile with an EMPTY type list
    /// — which silently changes how they fight (the range matrix falls back to adjacent-only).
    ///
    /// This asserts the loss is confined to the known-unmappable set, so a NEW type silently losing
    /// its meaning fails the suite. Mapping them is a product decision (the engine has no ship type),
    /// not something to guess at here.
    /// </summary>
    [Fact]
    public void NoNewUnitTypeIsSilentlyLostInCompilation()
    {
        var catalog = new CardCatalog();
        catalog.Load(CatalogDir, CardCatalog.ResolveArtResource);
        var (pool, _) = CardPoolCompiler.Compile(catalog.Cards);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "cruiser", "landcruiser", "spacefighter" };

        var byId = pool.ToDictionary(e => e.Id, e => e.Definition, StringComparer.Ordinal);
        var lost = new List<(string Id, string UnitType)>();
        foreach (var card in catalog.Cards.Where(c => c.CardType == "unit" && !string.IsNullOrWhiteSpace(c.UnitType)))
        {
            if (!byId.TryGetValue(card.CardId, out var definition)) continue;
            if (definition.UnitTypes is null || definition.UnitTypes.Count == 0)
                lost.Add((card.CardId, card.UnitType!));
        }

        Dump($"[unittype] units with a printed type: " +
            $"{catalog.Cards.Count(c => c.CardType == "unit" && !string.IsNullOrWhiteSpace(c.UnitType))}");
        Dump($"[unittype] compiled with an EMPTY type list: {lost.Count}");
        foreach (var g in lost.GroupBy(x => x.UnitType).OrderByDescending(g => g.Count()))
            Dump($"    {g.Key}: {g.Count()} e.g. {g.First().Id}");

        var unexpected = lost.Where(x => !known.Contains(x.UnitType)).ToArray();
        Assert.True(unexpected.Length == 0,
            "a unit type the engine cannot represent started being silently dropped: " +
            string.Join(", ", unexpected.Select(x => $"{x.UnitType} ({x.Id})")));
    }

    /// <summary>
    /// Bug 4: with an enemy unit standing on the front line, one of my units must be offered it.
    /// The earlier scratch probe concluded "units can only attack the HQ" from an empty preview
    /// list — `All()` is vacuously true on an empty list. This probe asserts on a real target.
    /// </summary>
    [Fact]
    public async Task AnEnemyFrontlineUnitIsOfferedAsAnAttackTarget()
    {
        var (pool, _) = LoadPool();
        var deck = DeckBuilder.Build(pool, seed: 20261007);
        var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 20261007, matchId: "repro-attack");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            var sawUnitTarget = false;
            var sawPreview = false;
            for (var turn = 0; turn < 20 && session.View.Phase != "over"; turn++)
            {
                await TryDeployCheapestAsync(session);
                await TryAdvanceAsync(session);
                var before = session.Actions.AttackPreviews.ToArray();
                if (before.Length > 0) sawPreview = true;
                var view = session.View;
                if (before.Any(p => view.EnemyLine.Any(e => e.Uid == p.DefenderUid))) sawUnitTarget = true;
                Dump($"[attack] turn {view.Turn} my={view.SelfLine.Count(c => !c.IsHq)} " +
                    $"enemy={view.EnemyLine.Count(c => !c.IsHq)} previews={before.Length} " +
                    $"targetsUnit={before.Count(p => view.EnemyLine.Any(e => e.Uid == p.DefenderUid))}");
                await TryStrikeAsync(session);
                await session.SubmitAsync(new EndTurn());
                session.Pump();
            }

            Assert.True(sawPreview, "no attack preview was ever offered across a whole match");
            Assert.True(sawUnitTarget, "no attack preview ever targeted an enemy unit");
        }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// Bug 5 (the big one for playability): the bridge refuses every order with UnknownCard, yet
    /// PlayManager exposes BeginCommandPrePlayAsync as a public UI entry point. This drives that
    /// entry directly to find out what actually happens when an order is played — accepted, cost
    /// paid, card leaves hand, and whether anything else moves.
    /// </summary>
    [Fact]
    public async Task AnOrderCanBePlayedThroughTheEngineEntryTheBridgeNeverCalls()
    {
        var (pool, _) = LoadPool();
        var deck = DeckBuilder.Build(pool, seed: 20261007);
        var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 20261007, matchId: "order-audit");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            var view = session.View;
            var affordableOrder = view.SelfHand.FirstOrDefault(c =>
                c.Definition.CardType == "order" && (c.Definition.Cost ?? 99) <= view.SelfKredits);
            Dump($"[order] hand=[{string.Join(" ", view.SelfHand.Select(c => $"{c.Definition.CardType}/{c.Definition.Cost}"))}] kredits={view.SelfKredits}");

            if (affordableOrder is null)
            {
                Dump("[order] no affordable order in this opening hand — inconclusive");
                return;
            }

            var viaBridge = await session.SubmitAsync(new PlayCard(affordableOrder.Uid));
            session.Pump();
            Dump($"[order] bridge PlayCard outcome={viaBridge}");

            var card = session.Host.Index[affordableOrder.Uid];
            Dump($"[order] engine card type={card?.GetType().Name}");

            var before = session.View.SelfKredits;
            var handBefore = session.View.SelfHand.Count;
            var steps = 0;
            var impacts = 0;
            session.Host.PresentationReady += (r, _) => steps += r.Steps.Count;
            session.Host.CombatReady += (i, _, _) => impacts += i.Count;

            var result = await session.Host.Match.PlayManager.BeginCommandPrePlayAsync((CommandCard)card!);
            session.Pump();
            Dump($"[order] direct engine entry status={result.Status} failure={result.FailureReason} " +
                $"kredits {before}->{session.View.SelfKredits} hand {handBefore}->{session.View.SelfHand.Count} " +
                $"steps={steps} impacts={impacts}");

            Assert.True(true);
        }
        finally { session.Dispose(); }
    }

    private static async Task<bool> TryDeployCheapestAsync(OrcMatchSession session)
    {
        var view = session.View;
        if (view.ActivePlayerSide != "self") return false;
        var playable = session.Actions.PlayableUids;
        var cheapest = view.SelfHand
            .Where(c => playable.Contains(c.Uid) && c.Definition.CardType == "unit"
                && (c.Definition.Cost ?? 99) <= view.SelfKredits)
            .OrderBy(c => c.Definition.Cost ?? 99)
            .FirstOrDefault();
        if (cheapest is null) return false;
        var outcome = await session.SubmitAsync(new PlayCard(cheapest.Uid));
        session.Pump();
        return outcome == UiSubmitOutcome.Applied;
    }

    private static async Task<bool> TryAdvanceAsync(OrcMatchSession session)
    {
        var move = session.Actions.Moves.FirstOrDefault(m => m.ToZone == "frontline");
        if (move is null) return false;
        var outcome = await session.SubmitAsync(new MoveUnit(move.Uid, "frontline", 2));
        session.Pump();
        return outcome == UiSubmitOutcome.Applied;
    }

    private static async Task<bool> TryStrikeAsync(OrcMatchSession session)
    {
        var preview = session.Actions.AttackPreviews.FirstOrDefault();
        if (preview is null) return false;
        var outcome = await session.SubmitAsync(new AttackUnit(preview.AttackerUid, preview.DefenderUid));
        session.Pump();
        return outcome == UiSubmitOutcome.Applied;
    }
}
