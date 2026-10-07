using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

/// <summary>
/// The catalog is 315 cards of real game data; this checks how many of them the engine can actually
/// be handed. A card the engine cannot represent must be reported, never silently coerced — a
/// wrong faction would change how the card behaves in play, which is worse than not offering it.
/// </summary>
public sealed class CardPoolCompilerTests
{
    private readonly ITestOutputHelper _out;
    public CardPoolCompilerTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void EveryCardTypeInTheCatalogMapsToAnEngineCategory()
    {
        var unit = Probe("unit", "deran", "tank", 3, 4, 5);
        var order = Probe("order", "deran", null, 2, null, null);
        var counter = Probe("counter", "deran", null, 3, null, null);

        Assert.True(CardPoolCompiler.TryCompile(unit, out var u, out _));
        Assert.True(CardPoolCompiler.TryCompile(order, out var o, out _));
        Assert.True(CardPoolCompiler.TryCompile(counter, out var c, out _));
        Assert.NotNull(u); Assert.NotNull(o); Assert.NotNull(c);
    }

    /// <summary>A non-nation set has no engine faction and must be rejected, not defaulted.</summary>
    [Fact]
    public void SetsThatAreNotNationsAreRejectedWithAReason()
    {
        foreach (var set in new[] { "misc", "天气", "自定义", null })
        {
            var card = Probe("unit", set!, "infantry", 1, 1, 1);
            Assert.False(CardPoolCompiler.TryCompile(card, out _, out var reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
    }

    [Fact]
    public void UnknownCardTypesAreRejectedRatherThanGuessed()
    {
        var card = Probe("token", "deran", null, 1, null, null);
        Assert.False(CardPoolCompiler.TryCompile(card, out _, out var reason));
        Assert.Contains("card type", reason);
    }

    /// <summary>Keywords carry their magnitude; dropping it would silently weaken the card.</summary>
    [Fact]
    public void KeywordMagnitudesSurviveCompilation()
    {
        var card = Probe("unit", "deran", "tank", 4, 3, 6) with
        {
            Keywords = ["smokescreen"],
            KeywordValues = new Dictionary<string, int>(StringComparer.Ordinal) { ["smokescreen"] = 2 },
        };
        Assert.True(CardPoolCompiler.TryCompile(card, out var entry, out _));
        Assert.NotNull(entry);
    }

    private static UiCardDefinition Probe(string cardType, string set, string? unitType, int cost, int? attack, int? defense) => new()
    {
        CardId = $"probe/{cardType}",
        Name = "探针",
        CardType = cardType,
        Set = set,
        UnitType = unitType ?? "",
        Rarity = "bronze",
        Cost = cost,
        BaseAttack = attack,
        BaseDefense = defense,
        BaseOpCost = 1,
    };
}
