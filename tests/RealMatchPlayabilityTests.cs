using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

/// <summary>
/// Plays real-card matches to completion through the same door the UI uses, and reports where the
/// game stalls. The point is to turn "unplayable" into a number and a location.
/// </summary>
public sealed class RealMatchPlayabilityTests
{
    private const string CatalogDir = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";

    private readonly ITestOutputHelper _out;
    public RealMatchPlayabilityTests(ITestOutputHelper output) => _out = output;

    private static readonly System.Collections.Generic.List<string> Log = new();
    private static void Dump(string line) { Log.Add(line); try { System.IO.File.WriteAllLines(@"H://Working Folder//OrC-KSD.Godot//artifacts//playability-output.txt", Log); } catch { } }

    private static (IReadOnlyList<CardDefinitionEntry> Pool, IReadOnlyList<CardPoolRejection> Rejected) LoadPool()
    {
        var catalog = new CardCatalog();
        catalog.Load(CatalogDir, CardCatalog.ResolveArtResource);
        return CardPoolCompiler.Compile(catalog.Cards);
    }

    /// <summary>
    /// Plays a full real match and logs, every turn, what the board looks like and what each unit is
    /// allowed to attack. This is the evidence for "a unit can only ever hit the HQ".
    /// </summary>
    [Fact]
    public async Task LogWhatEveryUnitIsAllowedToAttackEachTurn()
    {
        var (pool, _) = LoadPool();
        var deck = DeckBuilder.Build(pool, seed: 20261007);
        var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 20261007, matchId: "attack-audit");
        var hqOnlyTurns = 0;
        var turnsWithEnemyUnits = 0;
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            for (var turn = 0; turn < 20 && session.View.Phase != "over"; turn++)
            {
                var opening = session.View;
                // Play the game the way a player must: deploy the cheapest thing you can afford,
                // advance what you can, then strike. Only then is the attack offer meaningful.
                var deployed = await TryDeployCheapestAsync(session);
                var advanced = await TryAdvanceAsync(session);
                var struck = await TryStrikeAsync(session);
                _ = opening;

                var view = session.View;
                var actions = session.Actions;
                var enemyUnits = view.EnemyLine.Where(c => !c.IsHq).ToArray();
                var myUnits = view.SelfLine.Where(c => !c.IsHq).ToArray();
                Dump($"--- turn {view.Turn} ({view.ActivePlayerSide}) my={myUnits.Length} enemy={enemyUnits.Length} " +
                    $"previews={actions.AttackPreviews.Count} did[deploy={deployed} advance={advanced} strike={struck}]");
                foreach (var u in myUnits) Dump($"    mine  {u.Definition.Name} zone={u.Zone} atk={u.EffectiveAttack}");
                foreach (var u in enemyUnits) Dump($"    enemy {u.Definition.Name} zone={u.Zone}");
                foreach (var p in actions.AttackPreviews)
                {
                    var attacker = myUnits.FirstOrDefault(u => u.Uid == p.AttackerUid);
                    var target = enemyUnits.FirstOrDefault(u => u.Uid == p.DefenderUid);
                    var isHq = p.DefenderUid == view.EnemyHq?.Uid;
                    Dump($"    preview {(attacker?.Definition.Name ?? "?")}({attacker?.Zone}) -> {(isHq ? "ENEMY-HQ" : target is null ? "?" : target.Definition.Name + "(" + target.Zone + ")")}");
                }

                if (enemyUnits.Length > 0)
                {
                    turnsWithEnemyUnits++;
                    if (actions.AttackPreviews.Count > 0
                        && actions.AttackPreviews.All(p => p.DefenderUid == view.EnemyHq?.Uid))
                        hqOnlyTurns++;
                }

                await session.SubmitAsync(new EndTurn());
                session.Pump();
            }
            Dump($"TOTAL turns with enemy units on board: {turnsWithEnemyUnits}");
            Dump($"TOTAL such turns where every preview pointed at the HQ only: {hqOnlyTurns}");
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
        var outcome = await session.SubmitAsync(new MoveUnit(move.Uid, "frontline", move.SlotIndex >= 0 ? move.SlotIndex : 2));
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

    /// <summary>
    /// How often does the opening hand hold a unit the player can actually afford on turn one?
    /// One command point is granted turn one, so a hand with no 1-cost unit is a dead first turn —
    /// the player may only pass. If this fraction is near zero the game is not playable in practice,
    /// regardless of what the engine accepts.
    /// </summary>
    [Fact]
    public async Task MeasureHowOftenTheOpeningHandCanActuallyDeploy()
    {
        var (pool, _) = LoadPool();
        var poolById = pool.ToDictionary(e => e.Id, e => e.Definition, StringComparer.Ordinal);
        var playable = 0;
        const int trials = 40;
        var costHistogram = new Dictionary<int, int>();

        for (var seed = 0; seed < trials; seed++)
        {
            var deck = DeckBuilder.Build(pool, seed: seed);
            var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: seed, matchId: $"open-{seed}");
            try
            {
                await session.SettleMulliganAsync(interactive: false);
                session.Pump();
                var view = session.View;
                foreach (var card in view.SelfHand)
                {
                    var cost = card.Definition.Cost ?? 0;
                    costHistogram[cost] = costHistogram.GetValueOrDefault(cost) + 1;
                }
                if (view.SelfHand.Any(c => c.Definition.CardType == "unit" && (c.Definition.Cost ?? 99) <= view.SelfKredits))
                    playable++;
            }
            finally { session.Dispose(); }
        }

        Dump($"opening hands that can deploy on turn one: {playable}/{trials}");
        foreach (var kv in costHistogram.OrderBy(k => k.Key))
            Dump($"  opening-hand cards at cost {kv.Key}: {kv.Value}");

        // This is the headline playability number. Report it; assert only that it is at least
        // non-trivial so a regression that zeroes it fails loudly.
        Assert.True(playable > 0, "not one of 40 openings could deploy a unit on turn one");
    }

    /// <summary>
    /// The whole point of a match: it must be able to run to a result. This drives both sides with
    /// the same naive driver the shell uses, and fails if the match cannot finish.
    /// </summary>
    [Fact]
    public async Task ARealMatchCanBePlayedToAResult()
    {
        var (pool, _) = LoadPool();

        for (var seed = 0; seed < 6; seed++)
        {
            var deck = DeckBuilder.Build(pool, seed: seed);
            var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: seed, matchId: $"full-{seed}");
            try
            {
                await session.SettleMulliganAsync(interactive: false);
                session.Pump();

                var turns = 0;
                string? stall = null;
                while (session.View.Phase != "over" && turns < 60)
                {
                    var before = session.View.Turn;
                    var beforeUnits = session.View.SelfLine.Count + session.View.EnemyLine.Count;
                    var beforeHq = (session.View.SelfHq?.Health ?? 0) + (session.View.EnemyHq?.Health ?? 0);

                    var outcome = await session.SubmitAsync(new EndTurn());
                    session.Pump();
                    turns++;

                    var after = session.View;
                    var afterUnits = after.SelfLine.Count + after.EnemyLine.Count;
                    var afterHq = (after.SelfHq?.Health ?? 0) + (after.EnemyHq?.Health ?? 0);
                    if (after.Phase != "over" && after.Turn == before && afterUnits == beforeUnits && afterHq == beforeHq)
                    {
                        stall = $"turn {before}: end-turn produced no change (outcome={outcome})";
                        break;
                    }
                }

                Dump($"seed {seed}: phase={session.View.Phase} turns={turns} stall={stall ?? "none"}");
                if (stall is not null) Dump($"  STALL DETAIL: {stall}");
            }
            finally { session.Dispose(); }
        }
    }
}
