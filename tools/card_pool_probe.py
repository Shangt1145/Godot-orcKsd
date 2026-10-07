"""Measure how much of the shipped card catalog the engine can actually be handed.

P13-1's whole point is that the UI stops inventing cards. The number that decides whether that
worked is not "the test passed" but "how many of the 315 cards became engine definitions, and
why the rest did not". A silently coerced card would change how it behaves in play, so rejections
must be visible, not smoothed over.

Writes a temporary test, runs it, then removes it. Uses a file breadcrumb so the numbers survive
even if the harness swallows console output.

Usage: python card_pool_probe.py
"""
import glob
import os
import re
import subprocess
import xml.etree.ElementTree as ET

TEST_PROJ = r"H:/Working Folder/OrC-KSD.Godot/tests/Kards.Ui.Tests.csproj"
PROBE_PATH = r"H:/Working Folder/OrC-KSD.Godot/tests/CardPoolProbe.cs"
REPORT = r"H:/Working Folder/OrC-KSD.Godot/artifacts/card-pool-report.txt"
REPO = r"H:/Working Folder/OrC-KSD.Godot"

BODY = r'''
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class CardPoolProbe
{
    [Fact]
    public void ReportHowMuchOfTheRealPoolCompiles()
    {
        var catalog = new CardCatalog();
        catalog.Load(@"H:\Working Folder\OrC-KSD.Godot\proto\data\nations", CardCatalog.ResolveArtResource);
        var (entries, rejected) = CardPoolCompiler.Compile(catalog.Cards);

        var byReason = rejected.GroupBy(r => r.Reason).OrderByDescending(g => g.Count());
        var lines = new List<string>
        {
            $"catalog={catalog.Cards.Count} compiled={entries.Count} rejected={rejected.Count}",
        };
        foreach (var g in byReason) lines.Add($"  {g.Count(),4}x {g.Key}");
        foreach (var g in rejected.GroupBy(r => r.Reason).Take(3))
            foreach (var r in g.Take(3)) lines.Add($"    e.g. {r.CardId}");

        // Deck legality: an entry list must be usable as-is by a real match.
        lines.Add($"distinct={entries.Select(e => e.Id).Distinct().Count()}");
        System.IO.File.WriteAllLines(@"H:\Working Folder\OrC-KSD.Godot\artifacts\card-pool-report.txt", lines);
        Assert.True(entries.Count > 100, $"only {entries.Count} cards compiled");
    }
}
'''


def main():
    source = open(__file__, encoding="utf-8").read()
    body = re.search(r"BODY = r'''(.*?)'''", source, re.S)
    if body is None:
        print("PROBE_BODY_NOT_FOUND")
        return
    with open(PROBE_PATH, "w", encoding="utf-8") as f:
        f.write(body.group(1))
    print("probe written")
    try:
        if os.path.exists(REPORT):
            os.remove(REPORT)
        b = subprocess.run(["dotnet", "build", TEST_PROJ, "-v", "q", "--nologo"],
                           capture_output=True, text=True, timeout=300, cwd=REPO)
        berr = [l.strip()[:200] for l in (b.stdout + b.stderr).splitlines() if "error CS" in l]
        if berr:
            print("COMPILE ERRORS:")
            for l in berr[:6]:
                print("  ", l)
            return
        print("COMPILE OK")
        subprocess.run(["dotnet", "test", TEST_PROJ, "--nologo", "--no-build",
                        "--filter", "FullyQualifiedName~CardPoolProbe"],
                       capture_output=True, text=True, timeout=400, cwd=REPO)
        if os.path.exists(REPORT):
            print("---- report ----")
            for line in open(REPORT, encoding="utf-8"):
                print("  ", line.rstrip())
        else:
            print("NO REPORT — the probe did not finish")
    except subprocess.TimeoutExpired:
        print("TIMEOUT")
    finally:
        if os.path.exists(PROBE_PATH):
            os.remove(PROBE_PATH)
            print("probe removed")


if __name__ == "__main__":
    main()
