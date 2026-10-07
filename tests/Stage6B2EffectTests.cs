using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Managers;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class Stage6B2EffectTests
{
    private const string Source = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
    private static CardCatalog Catalog() { var c = new CardCatalog(); c.Load(Source); return c; }
    private static async Task<OrcMatchSession> Ready(params string[] hand)
    {
        var session = await OrcMatchSession.CreateFromCatalogAsync(Catalog().Cards, 7, Guid.NewGuid().ToString(), "probe",
            openingHand: hand, sourceDirectory: Source);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        var match = session.Host.Match;
        await match.ResourceManager.GainSlotsAsync(match.Players[0], 11);
        await match.ResourceManager.GainPointsAsync(match.Players[0], 12);
        session.SettleProjection(); return session;
    }
    private static UiCardView Card(OrcMatchSession s, string id) => s.View.SelfHand.First(c => c.CardId == id);
    private static async Task<UiCardView> Deploy(OrcMatchSession s)
    {
        var unit = Card(s, "deran/units/_6q");
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(unit.Uid, 0)));
        return s.View.SelfLine.Single(c => c.Uid == unit.Uid);
    }

    [Theory]
    [InlineData("USG/commands/_11", 3)]
    [InlineData("deran/command/_12", 3)]
    [InlineData("av76/command/-22", 6)]
    public async Task DamageOrdersConsumeCostAndReachRealDamageAndDeath(string id, int damage)
    {
        using var s = await Ready(id, "deran/units/_6q"); var unit = await Deploy(s);
        var order = Card(s, id); var credits = s.View.SelfKredits;
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(order.Uid, TargetUid: unit.Uid)));
        Assert.DoesNotContain(s.View.SelfHand, c => c.Uid == order.Uid);
        Assert.Equal(credits - order.Definition.Cost, s.View.SelfKredits);
        if (damage == 6) Assert.DoesNotContain(s.View.SelfLine, c => c.Uid == unit.Uid);
        else Assert.Equal(unit.EffectiveDefense - damage, s.View.SelfLine.Single(c => c.Uid == unit.Uid).EffectiveDefense);
    }

    [Fact]
    public async Task TargetCancellationAndIllegalTargetNeverConsumeTheOrderOrCredits()
    {
        using var s = await Ready("USG/commands/_11", "deran/units/_6q"); await Deploy(s);
        var card = Card(s, "USG/commands/_11"); var credits = s.View.SelfKredits;
        s.TargetRequested += (request, responder) =>
        {
            Assert.False(responder.Complete(request.RequestId, new Dictionary<string, IReadOnlyList<string>> { [request.Slots[0].Name] = ["wrong-target"] }));
            Assert.True(responder.Cancel(request.RequestId));
        };
        Assert.Equal(UiSubmitOutcome.Rejected, await s.SubmitAsync(new PlayCard(card.Uid)));
        Assert.Equal(credits, s.View.SelfKredits); Assert.Contains(s.View.SelfHand, c => c.Uid == card.Uid);
        Assert.Equal(UiSubmitOutcome.Rejected, await s.SubmitAsync(new PlayCard(card.Uid, TargetUid: s.View.SelfHq!.Uid)));
        Assert.Equal(credits, s.View.SelfKredits);
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(card.Uid, TargetUid: s.View.SelfLine[0].Uid)));
    }

    [Fact]
    public async Task TargetRequestCanParkAndBeCancelledBySessionLifetime()
    {
        using var s = await Ready("USG/commands/_11", "deran/units/_6q"); await Deploy(s);
        s.TargetRequested += (_, _) => { };
        var pending = s.SubmitAsync(new PlayCard(Card(s, "USG/commands/_11").Uid));
        Assert.True(s.TargetPending); Assert.False(pending.IsCompleted);
        s.Dispose();
        Assert.Contains(await pending.WaitAsync(TimeSpan.FromSeconds(3)), new[] { UiSubmitOutcome.Interrupted, UiSubmitOutcome.Rejected });
    }

    [Fact]
    public async Task DrawAndPointSlotOrdersExecuteTheirDeclaredRules()
    {
        using var s = await Ready("USG/commands/_7", "USG/commands/27");
        var draw = Card(s, "USG/commands/_7"); var deck = s.View.SelfDeckCount; var hand = s.View.SelfHand.Count;
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(draw.Uid)));
        Assert.Equal(deck - 2, s.View.SelfDeckCount); Assert.Equal(hand + 1, s.View.SelfHand.Count);
        // GainSlots at the engine maximum has no effect; free a slot first to assert the actual gain.
        await s.Host.Match.ResourceManager.LoseSlotsAsync(s.Host.Viewer, 1); s.SettleProjection();
        var slots = s.View.SelfMaxKredits; var card = Card(s, "USG/commands/27");
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(card.Uid)));
        Assert.Equal(slots + 1, s.View.SelfMaxKredits);
    }

    [Theory]
    [InlineData("deran/command/_18")]
    [InlineData("deran/command/_14")]
    [InlineData("av76/command/-5")]
    [InlineData("av76/command/-7")]
    public async Task StatAndKeywordOrdersUseTheEngineModifierChain(string id)
    {
        using var s = await Ready(id, "deran/units/_6q"); var unit = await Deploy(s);
        var order = Card(s, id);
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(order.Uid, TargetUid: unit.Uid)));
        var after = s.View.SelfLine.Single(c => c.Uid == unit.Uid);
        if (id == "av76/command/-5")
        {
            Assert.Equal(0, after.EffectiveOpCost);
            Assert.True(((UnitCard)s.Host.Index[unit.Uid]).Keywords.Has(KeywordIds.Blitz));
        }
        else if (id == "av76/command/-7") Assert.Equal(unit.EffectiveDefense - unit.Definition.Cost, after.EffectiveDefense);
        else
        {
            Assert.Equal(unit.EffectiveAttack + 1, after.EffectiveAttack);
            if (id.EndsWith("_18")) Assert.Equal(unit.EffectiveDefense + 2, after.EffectiveDefense);
            else
            {
                Assert.Equal(Math.Max(0, unit.EffectiveOpCost!.Value - 1), after.EffectiveOpCost);
                await s.SubmitAsync(new EndTurn());
                var expired = s.View.SelfLine.Single(c => c.Uid == unit.Uid);
                Assert.Equal(unit.EffectiveAttack, expired.EffectiveAttack); Assert.Equal(unit.EffectiveOpCost, expired.EffectiveOpCost);
            }
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task ChoiceOrderExecutesOnlyThePlayersChosenBranch(string option)
    {
        using var s = await Ready("deran/command/_13");
        s.TargetRequested += (request, responder) => Assert.True(responder.Complete(request.RequestId,
            new Dictionary<string, IReadOnlyList<string>> { [request.Slots[0].Name] = [option] }));
        var hp = s.View.SelfHq!.Health; var deck = s.View.SelfDeckCount;
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(Card(s, "deran/command/_13").Uid)));
        Assert.Equal(option == "0" ? hp + 7 : hp, s.View.SelfHq!.Health);
        Assert.Equal(option == "1" ? deck - 2 : deck, s.View.SelfDeckCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalOrderOnlyRefundsPointsWhenAirUnitsAreControlled(bool air)
    {
        using var s = await Ready("deran/command/25", air ? "av76/units/13" : "deran/units/_6q");
        var actor = s.View.SelfHand.Single(c => c.Definition.CardType == "unit");
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(actor.Uid, 0)));
        var points = s.View.SelfKredits; var deck = s.View.SelfDeckCount;
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(Card(s, "deran/command/25").Uid)));
        Assert.Equal(points - 3 + (air ? 3 : 0), s.View.SelfKredits); Assert.Equal(deck - 2, s.View.SelfDeckCount);
    }

    [Fact]
    public async Task CounterReservesRefundsAndOnlyConsumesOnFriendlyDeathOnce()
    {
        using var s = await Ready("USG/commands/_13", "deran/units/_6q"); var unit = await Deploy(s);
        var counter = Card(s, "USG/commands/_13"); var points = s.View.SelfKredits; var hp = s.View.EnemyHq!.Health;
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(counter.Uid)));
        Assert.Equal(points - 7, s.View.SelfKredits); Assert.Equal(hp, s.View.EnemyHq!.Health);
        Assert.True(s.View.SelfHand.Single(c => c.Uid == counter.Uid).IsCounterArmed);
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(counter.Uid)));
        Assert.Equal(points, s.View.SelfKredits); Assert.False(s.View.SelfHand.Single(c => c.Uid == counter.Uid).IsCounterArmed);
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(counter.Uid)));
        // Opposing death must not satisfy this counter. Create the enemy through the engine library.
        var enemy = (UnitCard)s.Host.Match.CardLibrary.Instantiate("deran/units/_6q"); await enemy.LoadAsync(s.Host.Match.Players[1]);
        await s.SubmitAsync(new EndTurn()); s.SelectViewer(1);
        s.Host.Viewer.Hand.Add(enemy);
        await s.Host.Match.ResourceManager.GainSlotsAsync(s.Host.Viewer, 11);
        await s.Host.Match.ResourceManager.GainPointsAsync(s.Host.Viewer, 12); s.SettleProjection();
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(s.View.SelfHand.Single(c => c.Uid == OrcRefs.KeyOf(enemy)).Uid, 0)));
        s.SelectViewer(0);
        await EffectRuntime.ResolveFor(enemy)!.KillAsync(enemy);
        s.SettleProjection(); Assert.Equal(hp, s.View.EnemyHq!.Health);
        // A secret counter must also work during the opponent's turn.
        await EffectRuntime.ResolveFor((CardBase)s.Host.Index[unit.Uid])!.KillAsync((CardBase)s.Host.Index[unit.Uid]);
        s.SettleProjection(); Assert.Equal(hp - 8, s.View.EnemyHq!.Health);
        Assert.DoesNotContain(s.View.SelfHand, c => c.Uid == counter.Uid); Assert.Equal(0, s.View.SelfCounterCount);
        await EffectRuntime.ResolveFor(enemy)!.KillAsync(enemy);
        s.SettleProjection(); Assert.Equal(hp - 8, s.View.EnemyHq!.Health);
    }

    [Fact]
    public async Task MissingUnitTargetsAreRejectedWithoutParkingOrSpending()
    {
        using var s = await Ready("USG/commands/_11"); var card = s.View.SelfHand[0]; var points = s.View.SelfKredits;
        Assert.Equal(UiSubmitOutcome.Rejected, await s.SubmitAsync(new PlayCard(card.Uid)).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("NoAvailableCandidates", s.LastRejectionReason); Assert.False(s.TargetPending);
        Assert.Equal(points, s.View.SelfKredits); Assert.Contains(s.View.SelfHand, c => c.Uid == card.Uid);
    }

    [Fact]
    public async Task CancellingAnInteractionTokenUnblocksTheNextCommand()
    {
        using var s = await Ready("USG/commands/_11", "deran/units/_6q"); var unit = await Deploy(s);
        s.TargetRequested += (_, _) => { };
        var card = Card(s, "USG/commands/_11"); using var cancel = new CancellationTokenSource();
        var pending = s.SubmitAsync(new PlayCard(card.Uid), cancel.Token); Assert.True(s.TargetPending);
        cancel.Cancel(); await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(s.TargetPending);
        Assert.Contains(s.View.SelfHand, c => c.Uid == card.Uid);
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(card.Uid, TargetUid: unit.Uid)));
    }

    [Fact]
    public async Task InteractionFaultDoesNotLeaveAStaleChoiceOrBlockTheNextPlay()
    {
        using var s = await Ready("USG/commands/_11", "deran/units/_6q"); var unit = await Deploy(s);
        s.TargetRequested += (_, _) => throw new InvalidOperationException("Simulated UI interaction fault");
        var order = Card(s, "USG/commands/_11");
        Assert.Equal(UiSubmitOutcome.Rejected, await s.SubmitAsync(new PlayCard(order.Uid)).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("InteractionFault", s.LastRejectionReason); Assert.False(s.TargetPending);
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(order.Uid, TargetUid: unit.Uid)));
    }

    [Fact]
    public void IncompleteInterruptCountersAreNeverOfferedAsFunctionalCards()
    {
        var pool = VerifiedCardPool.Compile(Catalog().Cards, Source);
        foreach (var id in new[] { "av76/command/-14", "deran/command/_5", "USG/commands/_15" })
        {
            Assert.DoesNotContain(pool.Entries, e => e.Id == id);
            Assert.Equal(UiCardSupport.Unsupported, pool.Report.Cards.Single(c => c.CardId == id).Support);
        }
    }

    [Fact]
    public async Task OrdersCannotSpendUnavailableCredits()
    {
        using var s = await Ready("USG/commands/_7");
        await s.Host.Match.ResourceManager.LosePointsAsync(s.Host.Viewer, 12); s.SettleProjection();
        var order = s.View.SelfHand[0]; var deck = s.View.SelfDeckCount;
        Assert.Equal(UiSubmitOutcome.Rejected, await s.SubmitAsync(new PlayCard(order.Uid)));
        Assert.Equal(deck, s.View.SelfDeckCount); Assert.Contains(s.View.SelfHand, c => c.Uid == order.Uid);
    }

    [Fact]
    public async Task DealKeepsSeedReproducibilityAndHasVarietyWithoutPinnedHands()
    {
        var catalog = Catalog(); var hands = new HashSet<string>();
        for (var seed = 0; seed < 8; seed++)
        {
            using var first = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, seed, "a", "probe", sourceDirectory: Source);
            using var second = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, seed, "b", "probe", sourceDirectory: Source);
            Assert.Equal(first.View.SelfHand.Select(c => c.CardId), second.View.SelfHand.Select(c => c.CardId));
            Assert.Equal(26, first.View.SelfDeckCount); hands.Add(string.Join(",", first.View.SelfHand.Select(c => c.CardId)));
        }
        Assert.True(hands.Count >= 6);
    }
}
