using System.Text.Json;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// Shipped card data must say what the official text says. A card whose text describes a
/// self-event ("what this unit attacks, it suppresses") is not a deploy ability — turning it into
/// one invents a targeting prompt the player can never justify.
/// </summary>
public sealed class CardDataFidelityTests
{
    private const string PacksDir = @"H:\Working Folder\OrC-KSD.Godot\proto\data\kards\packs";

    private static string ReadCard(string pack, string id) =>
        File.ReadAllText(Path.Combine(PacksDir, pack, "cards", id + ".card.json"));

    /// <summary>
    /// H39 sWG: text = 「抑制被本单位攻击的单位」 (EN: Suppress units attacked by this unit).
    /// It asks for nothing on deploy; the suppression rides on the damage it deals.
    /// </summary>
    [Fact]
    public void H39_suppresses_what_it_damages_and_never_asks_for_a_deploy_target()
    {
        var json = ReadCard("exp-winterwar", "h39_swg");
        Assert.Contains("unit.damage.dealt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("unit.deployed", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectAsync", json, StringComparison.Ordinal);
    }

    /// <summary>Every shipped effect graph must be readable JSON with a mounted trigger.</summary>
    [Fact]
    public void Every_shipped_effect_graph_declares_at_least_one_hook()
    {
        var checkedGraphs = 0;
        foreach (var pack in Directory.GetDirectories(PacksDir))
        foreach (var file in Directory.GetFiles(Path.Combine(pack, "cards"), "*.card.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var component in doc.RootElement.GetProperty("components").EnumerateArray())
            {
                if (component.GetProperty("component").GetString() != "effects") continue;
                foreach (var graph in component.GetProperty("inline").EnumerateArray())
                {
                    checkedGraphs++;
                    var hooks = graph.GetProperty("root").GetProperty("mainTrigger").GetProperty("hooks");
                    Assert.True(hooks.GetArrayLength() > 0, $"{Path.GetFileName(file)} 的效果没有挂任何钩子");
                }
            }
        }
        Assert.True(checkedGraphs > 0, "没有读到任何内联效果图——路径错了");
    }
}
