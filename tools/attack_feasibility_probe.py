"""Feasibility probe for P11: can the UI drive an attack at all?

The P9 investigation showed the engine hangs when OrcTargeterBridge hands it real slot/HQ
references. Before planning attack-trajectory work on top of that, find out whether the hang
is avoidable from the UI side.

Two strategies, each in its own test so a hang in one does not hide the other:
  B. a single HQ reference only — does a narrow candidate set avoid the hang?
  C. full live references — the known-hanging case, as a control.

Usage: python attack_feasibility_probe.py
"""
import os
import subprocess
import textwrap

TEST_PROJ = r"H:/Working Folder/OrC-KSD.Godot/tests/Kards.Ui.Tests.csproj"
PROBE_PATH = r"H:/Working Folder/OrC-KSD.Godot/tests/AttackFeasibility.cs"

BODY = r'''
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Xunit;

namespace Kards.Ui.Tests;

public class AttackFeasibility
{
    private const string InfantryId = "feasibility-infantry";

    private static Match NewMatch(int seed, List<object?> holder)
    {
        var definitions = new[]
        {
            new CardDefinitionEntry(InfantryId, new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard))
        };
        var bridge = new OrcTargeterBridge(() => holder, (_, _) => { });
        return new Match(
            new CardList(Enumerable.Repeat(InfantryId, 20)),
            new CardList(Enumerable.Repeat(InfantryId, 20)),
            definitions, seed: seed, firstPlayerIndex: 0, targeterBridge: bridge);
    }

    private static void FillAll(Match m, List<object?> holder)
    {
        holder.Clear();
        foreach (var line in new[] { m.Battlefield.PlayerASupportLine, m.Battlefield.FrontLine, m.Battlefield.PlayerBSupportLine })
            foreach (var slot in line) holder.Add(slot.Ref);
        foreach (var p in m.Players) holder.Add(p.Hq.Ref);
    }

    private static async Task<string> DriveAsync(Match match, OrcMatchHost host, UnitCard unit, string label, bool fullRefs)
    {
        var log = new List<string>();
        var initTask = host.InitializeAsync();
        if (await Task.WhenAny(initTask, Task.Delay(TimeSpan.FromSeconds(10))) != initTask)
            return $"{label}: init TIMEOUT";
        log.Add($"{label}: init ok");

        // Battlefield/Players are only reachable once the match is live — filling refs before
        // Initialize throws "manager群不可用", which is what the first probe run actually hit.
        if (fullRefs) Console.WriteLine($"{label}: refs={host.View.SelfHand.Count} hand");
        var m0 = match.MulliganDone(match.Players[0]);
        if (await Task.WhenAny(m0, Task.Delay(TimeSpan.FromSeconds(10))) != m0) return $"{label}: mulligan p0 TIMEOUT";
        var m1 = match.MulliganDone(match.Players[1]);
        if (await Task.WhenAny(m1, Task.Delay(TimeSpan.FromSeconds(10))) != m1) return $"{label}: mulligan p1 TIMEOUT";
        log.Add($"{label}: mulligan ok");

        var depTask = match.PlayManager.BeginUnitPrePlayAsync(unit);
        if (await Task.WhenAny(depTask, Task.Delay(TimeSpan.FromSeconds(15))) != depTask)
            return string.Join(" | ", log) + $" | {label}: DEPLOY TIMEOUT";

        var res = await depTask;
        log.Add($"{label}: deploy status={res.Status}");
        return string.Join(" | ", log);
    }

    [Fact]
    public async Task StrategyB_SingleHqReferenceOnly()
    {
        var holder = new List<object?>();
        var match = NewMatch(7, holder);
        var host = new OrcMatchHost(match, "feas-b");
        var initTask = host.InitializeAsync();
        await initTask;
        holder.Add(match.Players[1].Hq.Ref);
        var unit = (UnitCard)match.Players[0].Hand.First();
        var drive = DriveAsync(match, host, unit, "B", fullRefs: false);
        var done = await Task.WhenAny(drive, Task.Delay(TimeSpan.FromSeconds(30)));
        Console.WriteLine(done == drive ? "B: " + await drive : "B: HUNG");
        Assert.NotNull(match);
    }

    [Fact]
    public async Task StrategyC_FullLiveReferences()
    {
        var holder = new List<object?>();
        var match = NewMatch(7, holder);
        var host = new OrcMatchHost(match, "feas-c");
        await host.InitializeAsync();
        FillAll(match, holder);
        Console.WriteLine($"C refs={holder.Count}");
        var unit = (UnitCard)match.Players[0].Hand.First();
        var drive = DriveAsync(match, host, unit, "C", fullRefs: true);
        var done = await Task.WhenAny(drive, Task.Delay(TimeSpan.FromSeconds(30)));
        Console.WriteLine(done == drive ? "C: " + await drive : "C: HUNG");
        Assert.NotNull(match);
    }
}
'''


def main():
    with open(PROBE_PATH, "w", encoding="utf-8") as f:
        f.write(BODY)
    print("probe written")
    try:
        for name in ("StrategyB_SingleHqReferenceOnly", "StrategyC_FullLiveReferences"):
            print(f"===== {name} =====")
            try:
                r = subprocess.run(
                    ["dotnet", "test", TEST_PROJ, "-v", "q", "--nologo",
                     "--filter", f"FullyQualifiedName~{name}"],
                    capture_output=True, text=True, timeout=200,
                    cwd=r"H:/Working Folder/OrC-KSD.Godot")
                out = r.stdout + r.stderr
                for line in out.splitlines():
                    if any(k in line for k in ("B:", "C:", "error CS", "已通过", "失败")):
                        print("  ", line.strip()[:190])
            except subprocess.TimeoutExpired:
                print("   HUNG (test host never returned)")
        print("FEASIBILITY_DONE")
    finally:
        if os.path.exists(PROBE_PATH):
            os.remove(PROBE_PATH)
            print("probe removed")


if __name__ == "__main__":
    main()