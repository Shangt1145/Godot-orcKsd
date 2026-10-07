using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// The pinned demonstration opening hand: one real unit at each deployment-slam size tier.
///
/// Two things need guarding. The hand must actually replace what the engine dealt (the pin is only
/// worth anything if it wins), and the four ids must stay real and keep the defenses the ladder
/// assumes — a catalog change that moves a unit's defense would silently flatten the ladder.
/// </summary>
public sealed class OpeningHandTests
{
    private const string CatalogDir = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";

    private static (IReadOnlyList<CardDefinitionEntry> Pool, IReadOnlyList<CardPoolRejection> Rejected) LoadPool()
    {
        var catalog = new CardCatalog();
        catalog.Load(CatalogDir, CardCatalog.ResolveArtResource);
        return CardPoolCompiler.Compile(catalog.Cards);
    }

    /// <summary>
    /// Every pinned id must be a compiled unit with exactly the documented defense, and the four
    /// defenses must actually span the whole slam ladder (tier 0 ≤2, tier 1 3–5, tier 2 >5, top
    /// above 7) — otherwise the hand demonstrates less than it claims.
    /// </summary>
    [Fact]
    public void TheDemoOpeningHandIsRealAndSpansEverySlamTier()
    {
        var (pool, _) = LoadPool();
        var byId = pool.ToDictionary(e => e.Id, e => e.Definition, StringComparer.Ordinal);

        for (var i = 0; i < DemoOpeningHand.CardIds.Count; i++)
        {
            var id = DemoOpeningHand.CardIds[i];
            Assert.True(byId.ContainsKey(id), $"{id} is not in the compiled pool (rejected or renamed)");
            var definition = byId[id];
            Assert.Equal(CardCategory.Unit, definition.Category);
            Assert.Equal(DemoOpeningHand.ExpectedDefense[i], definition.Defense);
        }

        var defenses = DemoOpeningHand.ExpectedDefense;
        Assert.Contains(defenses, d => d <= 2);            // tier 0
        Assert.Contains(defenses, d => d is >= 3 and <= 5); // tier 1
        Assert.Contains(defenses, d => d > 5);              // tier 2
        Assert.Contains(defenses, d => d > 7);              // top of the range
    }

    /// <summary>
    /// The pin must win outright: the hand the player is shown is exactly the requested cards, in
    /// order, and no dealt card survives. If the engine's deal leaked through, the demonstration
    /// would still be random.
    /// </summary>
    [Fact]
    public async Task APinnedOpeningHandReplacesWhateverWasDealt()
    {
        var (pool, _) = LoadPool();
        var deck = DeckBuilder.Build(pool, seed: 20261007);

        var dealt = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 20261007, matchId: "pin-off");
        string[] dealtIds;
        try
        {
            dealtIds = dealt.View.SelfHand.OrderBy(c => c.SlotIndex)
                .Select(c => c.Definition.CardId).ToArray();
        }
        finally { dealt.Dispose(); }

        var pinned = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 20261007, matchId: "pin-on",
            openingHand: DemoOpeningHand.CardIds);
        try
        {
            var hand = pinned.View.SelfHand.OrderBy(c => c.SlotIndex).ToArray();
            Assert.Equal(DemoOpeningHand.CardIds.ToArray(), hand.Select(c => c.Definition.CardId).ToArray());
            Assert.Equal(DemoOpeningHand.ExpectedDefense.ToArray(),
                hand.Select(c => c.Health ?? c.EffectiveDefense ?? -1).ToArray());
            // The pin is not a no-op: this seed's own deal must differ, or the test proves nothing.
            Assert.NotEqual(dealtIds, DemoOpeningHand.CardIds.ToArray());
        }
        finally { pinned.Dispose(); }
    }

    /// <summary>
    /// Pinning must not break the match: the mulligan still settles and play still starts.
    /// </summary>
    [Fact]
    public async Task APinnedHandStillReachesPlay()
    {
        var (pool, _) = LoadPool();
        var deck = DeckBuilder.Build(pool, seed: 20261007);
        var session = await OrcMatchSession.CreateAsync(pool, deck, deck, seed: 20261007, matchId: "pin-play",
            openingHand: DemoOpeningHand.CardIds);
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();
            Assert.Equal("play", session.View.Phase);
            Assert.Equal(DemoOpeningHand.CardIds.Count, session.View.SelfHand.Count);
            Assert.NotEmpty(session.Actions.PlayableUids);
        }
        finally { session.Dispose(); }
    }
}
