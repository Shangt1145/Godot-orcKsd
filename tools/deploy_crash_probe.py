"""Isolate the P9 deployment crash against the current engine build.

The breadcrumb trail stops right after MulliganDone, so BeginUnitPrePlayAsync is where the
process dies. The engine's docs for that method point at PrePlayTrigger.InvokeAsync — the
effect trigger chain — and the engine just gained a large effect-system expansion
(a24ef2d..40bb95b, EffectRuntime.cs +332). This script walks the same path step by step
and reports which call kills the host, so the next fix targets the real cause instead of
a guess.
"""
import json
import subprocess
import sys
import textwrap

REPO = r"H:/Working Folder/OrC-KSD-github"
TEST_PROJ = r"H:/Working Folder/OrC-KSD.Godot/tests/Kards.Ui.Tests.csproj"

PROBE = textwrap.dedent(r"""
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

    public class DeployProbe
    {
        private const string InfantryId = "infantry";

        // The real difference from the first probe: the production helper hands the targeter the
        // live slot/HQ references instead of an empty list. The engine has to resolve candidates
        // against real entities here, which is where a crash would actually come from.
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
        public async Task DeployWithNoEffectsDefined()
        {
            var steps = new List<string>();
            void Note(string s) { steps.Add(s); Console.WriteLine("STEP " + s); }

            Orc.Game.Targeting.TargetingRequestDescription? description = null;
            Orc.Game.Targeting.ITargetingResponder? responder = null;
            var parked = new TaskCompletionSource();
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
                definitions, seed: 11, firstPlayerIndex: 0, targeterBridge: bridge);
            var host = new OrcMatchHost(match, "deploy-probe");
            // The P9 test subscribes CombatReady; that is the one remaining difference from the
            // first probe, which passed. If subscribing is what kills the host, this is where.
            var combatImpacts = new List<IReadOnlyList<UiOrderImpact>>();
            host.CombatReady += (impacts, _, _) => { Note($"CombatReady fired ({impacts.Count})"); combatImpacts.AddRange(impacts); };
            Note("match built");
            await host.InitializeAsync();
            Note("initialized");
            var unit = (UnitCard)match.Players[0].Hand.First();
            Note("hand read");
            await match.MulliganDone(match.Players[0]);
            Note("mulligan p0");
            await match.MulliganDone(match.Players[1]);
            Note("mulligan p1");
            var result = await match.PlayManager.BeginUnitPrePlayAsync(unit);
            Note($"deploy returned status={result.Status}");
            if (result.Status == PlayResultStatus.Success)
            {
                host.Pump();
                Note("pumped after deploy");
                await match.EndTurn();
                Note("endturn 1 sent");
                await OrcOpponentDriver.PlayTurnAsync(match, match.Players[1]);
                Note("enemy turn 1 done");
                var move = await match.CommandManager.BeginMoveAsync(unit);
                Note($"move status={move.Status}");
                await match.EndTurn();
                Note("endturn 2 sent");
                await OrcOpponentDriver.PlayTurnAsync(match, match.Players[1]);
                Note("enemy turn 2 done");
                var attack = match.CommandManager.BeginAttackAsync(unit);
                Note("attack started (returns a task; not awaited yet)");
                if (await Task.WhenAny(attack, Task.Delay(TimeSpan.FromSeconds(8))) == attack)
                {
                    var atk = await attack;
                    Note($"attack status={atk.Status}");
                }
                else
                {
                    Note("attack did not complete within 8s — parked on targeter");
                }
                host.Pump();
                Note("pumped after combat");
            }
            if (parked.Task.IsCompleted) Note("targeter parked");
            else Note("no targeter request");
            Assert.NotNull(result);
        }
    }
""")


def main():
    path = r"H:/Working Folder/OrC-KSD.Godot/tests/DeployProbe.cs"
    with open(path, "w", encoding="utf-8") as f:
        f.write(PROBE)
    print("probe written")
    try:
        r = subprocess.run(
            ["dotnet", "test", TEST_PROJ, "-v", "q", "--nologo",
             "--filter", "FullyQualifiedName~DeployProbe"],
            capture_output=True, text=True, timeout=420, cwd=r"H:/Working Folder/OrC-KSD.Godot")
        out = r.stdout + r.stderr
        for line in out.splitlines():
            if any(k in line for k in ("STEP", "error CS", "已通过", "失败", "崩溃", "Exception", "Stack")):
                print(line.strip()[:200])
        if "STEP" not in out:
            print("NO STEP OUTPUT — host died before any output, see above")
        print("PROBE_DONE")
    finally:
        try:
            import os
            os.remove(path)
            print("probe removed")
        except OSError as e:
            print("cleanup failed:", e)


if __name__ == "__main__":
    main()