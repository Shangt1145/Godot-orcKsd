using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// Reproduction probes for two defects reported from real play:
///   1. attacking does nothing / hits the wrong thing
///   2. deployment and movement ignore where the player dropped, always landing in the same slot
///
/// Both are about the same seam: the UI picks a *visual* position (or a uid) and the bridge has to
/// answer the engine's targeter with an *engine reference*. These probes assert on what the engine
/// actually did, never on what the UI offered — the offer can be right while the submission is
/// silently redirected, which is exactly how the earlier round of probes missed this.
/// </summary>
public sealed class TargetHonourTests
{
    private const string Infantry = "probe-infantry";

    private static readonly List<string> Log = new();
    private static void Dump(string line)
    {
        Log.Add(line);
        try { File.WriteAllLines(@"H:\Working Folder\OrC-KSD.Godot\artifacts\target-honour-output.txt", Log); } catch { }
    }

    /// <summary>
    /// The engine offers attack candidates through the bridge's collected references. If the bridge
    /// never hands over the board units, the only survivable candidate is the enemy HQ, and every
    /// chosen unit target is silently replaced by it (or dropped entirely).
    /// </summary>
    [Fact]
    public async Task AttackingTheOfferedUnitDamagesThatUnitNotTheHeadquarters()
    {
        var session = await OrcMatchSession.CreateProbeAsync(Infantry, 20, 20261011, "honour-attack");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            for (var turn = 0; turn < 8 && session.View.Phase != "over"; turn++)
            {
                await DeployCheapestAsync(session);
                await AdvanceAsync(session);

                var view = session.View;
                var offer = session.Actions.AttackPreviews
                    .FirstOrDefault(p => view.EnemyLine.Any(e => !e.IsHq && e.Uid == p.DefenderUid));
                if (offer is null) { await EndTurnAsync(session); continue; }

                var targetBefore = session.View.EnemyLine.First(c => c.Uid == offer.DefenderUid);
                var hqBefore = session.View.EnemyHq?.Health ?? 0;
                Dump($"[attack] turn {session.View.Turn} attacker={offer.AttackerUid[..8]} target={targetBefore.Definition.Name} " +
                    $"targetHp={targetBefore.Health} hqHp={hqBefore}");

                // Exactly what OrcMatchRunner.SubmitAsync does before handing the command over.
                session.NoteSelection(offer.DefenderUid);
                var outcome = await session.SubmitAsync(new AttackUnit(offer.AttackerUid, offer.DefenderUid));
                session.Pump();

                var targetAfter = session.View.EnemyLine.FirstOrDefault(c => c.Uid == offer.DefenderUid);
                var hqAfter = session.View.EnemyHq?.Health ?? 0;
                Dump($"    outcome={outcome} targetHpAfter={targetAfter?.Health?.ToString() ?? "<gone>"} hqHpAfter={hqAfter}");

                if (outcome != UiSubmitOutcome.Applied) continue;

                Assert.True(targetAfter is null || targetAfter.Health < targetBefore.Health,
                    $"the offered target '{targetBefore.Definition.Name}' took no damage " +
                    $"(hp {targetBefore.Health} -> {targetAfter?.Health?.ToString() ?? "<gone>"}), " +
                    $"HQ {hqBefore} -> {hqAfter} — the chosen target was not honoured");
                return;
            }
            Assert.Fail("no turn ever offered an attack against an enemy unit — the audit is inconclusive");
        }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// Deployment. The session places on the slot the player named, through the engine's direct
    /// placement entry: the interactive entry's candidate set is its adjacency rule and only ever offers
    /// one support slot, so it cannot express a choice at all (see DeployPlacementTests). A named slot
    /// that is not free is refused rather than quietly swapped for a different one — the failure this
    /// guards is a drop landing somewhere the player did not put it.
    /// </summary>
    [Fact]
    public async Task DeploymentLandsOnTheRequestedSlotAndRefusesATakenOne()
    {
        for (var requested = 0; requested <= 3; requested++)
        {
            var session = await OrcMatchSession.CreateProbeAsync(Infantry, 20, 20261012, $"honour-deploy-{requested}");
            try
            {
                await session.SettleMulliganAsync(interactive: false);
                session.Pump();
                var line = session.Host.Match.Battlefield.GetSupportLine(session.Host.Viewer.Index);
                var free = line[requested].IsEmpty;
                var uid = session.View.SelfHand.OrderBy(c => c.SlotIndex).First().Uid;

                var outcome = await session.SubmitAsync(new PlayCard(uid, requested));
                session.Pump();
                var placed = session.View.SelfLine.FirstOrDefault(c => c.Uid == uid);
                Dump($"[deploy] requested={requested} free={free} outcome={outcome} " +
                    $"landed={(placed is null ? "<not placed>" : $"{placed.Zone}[{placed.SlotIndex}]")}");

                if (!free)
                {
                    Assert.Equal(UiSubmitOutcome.Rejected, outcome);
                    Assert.Null(placed);
                    continue;
                }
                Assert.Equal(UiSubmitOutcome.Applied, outcome);
                Assert.NotNull(placed);
                Assert.Equal(requested, placed!.SlotIndex);
            }
            finally { session.Dispose(); }
        }
    }

    /// <summary>Movement: the front line accepts any empty slot, so a chosen slot must be honoured.</summary>
    [Fact]
    public async Task MovementLandsWhereThePlayerAsked()
    {
        for (var requested = 0; requested <= 3; requested++)
        {
            var session = await OrcMatchSession.CreateProbeAsync(Infantry, 20, 20261013, $"honour-move-{requested}");
            session.DriveOpponent = false;
            try
            {
                await session.SettleMulliganAsync(interactive: false);
                session.Pump();
                var uid = await DeployCheapestAsync(session);
                Assert.NotNull(uid);

                await EndTurnAsync(session);   // opponent
                session.SelectViewer(1);
                await EndTurnAsync(session);   // back to me, unit refreshed
                session.SelectViewer(0);

                var deployed = session.View.SelfLine.FirstOrDefault(c => c.Uid == uid);
                Assert.NotNull(deployed);

                session.NoteSlot(requested);
                var outcome = await session.SubmitAsync(new MoveUnit(uid, "frontline", requested));
                session.Pump();
                var moved = session.View.SelfLine.FirstOrDefault(c => c.Uid == uid);
                Dump($"[move] requested={requested} outcome={outcome} " +
                    $"landed={(moved is null ? "<gone>" : $"{moved.Zone}[{moved.SlotIndex}]")}");
                Assert.Equal(UiSubmitOutcome.Applied, outcome);
                Assert.NotNull(moved); Assert.Equal("frontline", moved.Zone); Assert.Equal(requested, moved.SlotIndex);
            }
            finally { session.Dispose(); }
        }

    }

    /// <summary>
    /// The companion case: the very same path must still damage the headquarters when that is what
    /// was clicked. If honouring a unit target broke the HQ target, this is where it shows.
    /// </summary>
    [Fact]
    public async Task AttackingTheEnemyHeadquartersDamagesIt()
    {
        var session = await OrcMatchSession.CreateProbeAsync(Infantry, 20, 20261014, "honour-hq");
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            for (var turn = 0; turn < 8 && session.View.Phase != "over"; turn++)
            {
                await DeployCheapestAsync(session);
                await AdvanceAsync(session);

                var offer = session.Actions.AttackPreviews
                    .FirstOrDefault(p => p.DefenderUid == session.View.EnemyHq?.Uid);
                if (offer is null) { await EndTurnAsync(session); continue; }

                var hqBefore = session.View.EnemyHq!.Health ?? 0;
                session.NoteSelection(offer.DefenderUid);
                var outcome = await session.SubmitAsync(new AttackUnit(offer.AttackerUid, offer.DefenderUid));
                session.Pump();
                var hqAfter = session.View.EnemyHq?.Health ?? 0;
                Dump($"[hq] turn {session.View.Turn} outcome={outcome} hq {hqBefore} -> {hqAfter}");
                if (outcome != UiSubmitOutcome.Applied) continue;

                Assert.True(hqAfter < hqBefore, $"the headquarters took no damage ({hqBefore} -> {hqAfter})");
                return;
            }
            Assert.Fail("no turn ever offered an attack against the enemy headquarters");
        }
        finally { session.Dispose(); }
    }

    private static async Task<string?> DeployCheapestAsync(OrcMatchSession session)
    {
        var view = session.View;
        if (view.ActivePlayerSide != "self") return null;
        var playable = session.Actions.PlayableUids;
        var cheapest = view.SelfHand
            .Where(c => playable.Contains(c.Uid) && c.Definition.CardType == "unit"
                && (c.Definition.Cost ?? 99) <= view.SelfKredits)
            .OrderBy(c => c.Definition.Cost ?? 99)
            .FirstOrDefault();
        if (cheapest is null) return null;
        var outcome = await session.SubmitAsync(new PlayCard(cheapest.Uid));
        session.Pump();
        return outcome == UiSubmitOutcome.Applied ? cheapest.Uid : null;
    }

    private static async Task AdvanceAsync(OrcMatchSession session)
    {
        var move = session.Actions.Moves.FirstOrDefault(m => m.ToZone == "frontline");
        if (move is null) return;
        session.NoteSlot(2);
        await session.SubmitAsync(new MoveUnit(move.Uid, "frontline", 2));
        session.Pump();
    }

    private static async Task EndTurnAsync(OrcMatchSession session)
    {
        await session.SubmitAsync(new EndTurn());
        session.Pump();
    }
}
