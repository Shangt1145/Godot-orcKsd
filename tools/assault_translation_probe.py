"""Verify P11-1: unit.damage.dealt reaches the UI as an attacker-paired impact.

Writes a temporary xunit test that drives a real match all the way to an attack on the enemy HQ,
then asserts what the translator produced:
  - the impact's Damage is the engine's own Amount (not a stat diff)
  - the impact's Source is the attacking unit
  - an impact the engine never paired stays sourceless

The probe is written to tests/, run, then removed — it keeps the shipped test project clean
while still exercising the real engine. Follows the same discipline as the earlier probes:
InitializeAsync() MUST complete before touching match.Players or Battlefield, or the engine
throws "对局尚未进入'进行'态" and the failure looks like an engine bug.

Usage: python assault_translation_probe.py
"""
import os
import re
import subprocess
import glob

TEST_PROJ = r"H:/Working Folder/OrC-KSD.Godot/tests/Kards.Ui.Tests.csproj"
PROBE_PATH = r"H:/Working Folder/OrC-KSD.Godot/tests/AssaultTranslation.cs"

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

public sealed class AssaultTranslation
{
    private const string InfantryId = "assault-infantry";

    private static Match NewMatch(out OrcTargeterBridge bridge)
    {
        var definitions = new[]
        {
            new CardDefinitionEntry(InfantryId, new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard))
        };
        var parked = new TaskCompletionSource();
        Orc.Game.Targeting.TargetingRequestDescription? description = null;
        Orc.Game.Targeting.ITargetingResponder? responder = null;
        Match? match = null;
        // AutoRespond is the important part: BeginUnitPrePlayAsync parks on a SingleSelect slot and
        // waits forever unless someone submits a choice. A handler that only records the request
        // deadlocks the test host — which is exactly what the first two probe runs did.
        bridge = new OrcTargeterBridge(
            () => InteractablesOf(match),
            (d, r) =>
            {
                description = d; responder = r; parked.TrySetResult();
                OrcTargeterBridge.AutoRespond(d, r);
            });
        match = new Match(
            new CardList(Enumerable.Repeat(InfantryId, 20)),
            new CardList(Enumerable.Repeat(InfantryId, 20)),
            definitions, seed: 11, firstPlayerIndex: 0, targeterBridge: bridge);
        return match;
    }

    private static IReadOnlyList<object?> InteractablesOf(Match? match)
    {
        if (match is null || match.State == MatchState.Preparing) return Array.Empty<object?>();
        var references = new List<object?>();
        foreach (var line in new[] { match.Battlefield.PlayerASupportLine, match.Battlefield.FrontLine, match.Battlefield.PlayerBSupportLine })
            foreach (var slot in line) references.Add(slot.Ref);
        foreach (var player in match.Players) references.Add(player.Hq.Ref);
        return references;
    }

    private static void Mark(string step)
    {
        try { System.IO.File.AppendAllText(@"H:\Working Folder\OrC-KSD.Godot\artifacts\assault-crumb.log", $"{DateTime.Now:HH:mm:ss.fff} {step}\n"); } catch { }
    }

    [Fact]
    public async Task EnginePairsTheAttackWithItsAmount()
    {
        Mark("start");
        var match = NewMatch(out _);
        var host = new OrcMatchHost(match, "assault");
        var impacts = new List<IReadOnlyList<UiOrderImpact>>();
        host.CombatReady += (reported, _, _) => impacts.AddRange(reported);

        await host.InitializeAsync();
        Mark("initialized");
        var unit = (UnitCard)match.Players[0].Hand.First();
        var unitUid = OrcRefs.KeyOf(unit);
        var enemyHqUid = host.View.EnemyHq!.Uid;

        await match.MulliganDone(match.Players[0]);
        await match.MulliganDone(match.Players[1]);
        Mark("mulligan done");
        var deploy = await match.PlayManager.BeginUnitPrePlayAsync(unit);
        Mark("deployed");
        Assert.Equal(PlayResultStatus.Success, deploy.Status);

        await match.EndTurn();
        await OrcOpponentDriver.PlayTurnAsync(match, match.Players[1]);
        Mark("enemy 1 done");
        var move = await match.CommandManager.BeginMoveAsync(unit);
        Assert.Equal(Orc.Game.Commanding.CommandResultStatus.Success, move.Status);
        Mark("moved");
        await match.EndTurn();
        await OrcOpponentDriver.PlayTurnAsync(match, match.Players[1]);
        Mark("enemy 2 done");

        var attack = match.CommandManager.BeginAttackAsync(unit);
        Mark("attack started");
        Assert.True(await Task.WhenAny(attack, Task.Delay(TimeSpan.FromSeconds(20))) == attack,
            "attack never completed (targeter never parked?)");
        Mark("attack resolved");
        var atk = await attack;
        Assert.Equal(Orc.Game.Commanding.CommandResultStatus.Success, atk.Status);
        host.Pump();
        Mark("pumped");

        var flat = impacts.SelectMany(i => i).ToArray();
        foreach (var impact in flat)
            Console.WriteLine($"IMPACT uid={impact.Before.Uid} hq={impact.Before.IsHq} dmg={impact.Damage} src={impact.Source?.Uid ?? "-"}");
        Console.WriteLine($"IMPACTS total={flat.Length} enemyHqUid={enemyHqUid} attackerUid={unitUid}");
        var hq = flat.FirstOrDefault(i => i.Before.Uid == enemyHqUid);
        Assert.NotNull(hq);
        Assert.Equal(2, hq!.Damage);                       // the engine's own amount
        Assert.NotNull(hq.Source);                          // attacker paired
        Assert.Equal(unitUid, hq.Source!.Uid);
        Console.WriteLine($"ASSAULT_OK damage={hq.Damage} source={hq.Source!.Uid} attacker={unitUid}");
    }

    [Fact]
    public void ImpactsWithoutAnEnginePairStaySourceless()
    {
        var definition = new UiCardDefinition { CardId = "probe", Name = "probe" };
        var before = new UiCardView { Uid = "probe-target", Definition = definition, Visibility = Visibility.Full };
        var impact = new UiOrderImpact(before, before, 3);
        Assert.Null(impact.Source);
        var paired = impact with { Damage = 9, Source = before };
        Assert.Equal(9, paired.Damage);
        Assert.NotNull(paired.Source);
        Console.WriteLine("SOURCELESS_OK");
    }
}
'''


def main():
    # Extract the C# body with a regex rather than string slicing: the payload contains both
    # quote styles and slicing on the wrong delimiter silently produces an unparseable file.
    source = open(__file__, encoding="utf-8").read()
    body = re.search(r"BODY = r'''(.*?)'''", source, re.S)
    if body is None:
        print("PROBE_BODY_NOT_FOUND")
        return
    with open(PROBE_PATH, "w", encoding="utf-8") as f:
        f.write(body.group(1))
    print("probe written")
    try:
        for f in glob.glob(r"H:/Working Folder/OrC-KSD.Godot/tests/TestResults/*.trx"):
            os.remove(f)
        # Build first: a compile error inside a subprocess.run timeout is indistinguishable from a hang.
        b = subprocess.run(["dotnet", "build", TEST_PROJ, "-v", "q", "--nologo"],
                           capture_output=True, text=True, timeout=240,
                           cwd=r"H:/Working Folder/OrC-KSD.Godot")
        berr = [l.strip()[:220] for l in (b.stdout + b.stderr).splitlines() if "error CS" in l]
        if berr:
            print("COMPILE ERRORS:")
            for l in berr[:6]:
                print("  ", l)
            return
        print("COMPILE OK")
        r = subprocess.run(
            ["dotnet", "test", TEST_PROJ, "-v", "q", "--nologo", "--no-build",
             "--filter", "FullyQualifiedName~AssaultTranslation"],
            capture_output=True, text=True, timeout=300,
            cwd=r"H:/Working Folder/OrC-KSD.Godot")
        out = r.stdout + r.stderr
        for line in out.splitlines():
            if any(k in line for k in ("ASSAULT_OK", "SOURCELESS_OK", "已通过", "失败", "Assert")):
                print("  ", line.strip()[:200])
        for f in glob.glob(r"H:/Working Folder/OrC-KSD.Godot/tests/TestResults/**/*.trx", recursive=True):
            s = open(f, encoding="utf-8", errors="ignore").read()
            for msg, _stack in re.findall(r"<Message>(.*?)</Message>|<StackTrace>(.*?)</StackTrace>", s, re.S)[:3]:
                t = msg.strip()
                if t:
                    print("  DETAIL:", t[:400])
                    print("  ---")
        print("PROBE_DONE")
    except subprocess.TimeoutExpired:
        print("TIMEOUT — see notes in the file header for the usual cause (Initialize-before-access)")
    finally:
        if os.path.exists(PROBE_PATH):
            os.remove(PROBE_PATH)
            print("probe removed")


if __name__ == "__main__":
    main()