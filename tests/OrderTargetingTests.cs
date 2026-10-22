using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class OrderTargetingTests
{
    private const string Source = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
    private static async Task<OrcMatchSession> Ready(params string[] hand)
    {
        var catalog = new CardCatalog(); catalog.Load(Source);
        var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, 7, Guid.NewGuid().ToString(), "probe",
            openingHand: hand, sourceDirectory: Source, firstPlayerIndex: 0);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        var match = session.Host.Match;
        await match.ResourceManager.GainSlotsAsync(match.Players[0], 11);
        await match.ResourceManager.GainPointsAsync(match.Players[0], 12);
        session.SettleProjection(); return session;
    }

    [Theory]
    [InlineData("USG/commands/_11")]
    [InlineData("deran/command/_12")]
    [InlineData("av76/command/-5")]
    [InlineData("av76/command/-7")]
    public async Task ProjectedArrowTargetIsAcceptedByTheActualEffectFilter(string id)
    {
        using var s = await Ready(id, "USG/units/_4");
        var order = s.View.SelfHand.First(c => c.CardId == id);
        var unit = s.View.SelfHand.First(c => c.CardId == "USG/units/_4");
        Assert.Empty(s.Actions.OrderTargets.Single(t => t.Uid == order.Uid).TargetUids);
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(unit.Uid, 0)));
        var targets = s.Actions.OrderTargets.Single(t => t.Uid == order.Uid).TargetUids;
        Assert.Equal(new[] { unit.Uid }, targets);
        Assert.DoesNotContain(s.View.SelfHq!.Uid, targets);
        Assert.DoesNotContain(s.View.EnemyHq!.Uid, targets);
        var panels = 0; s.TargetRequested += (_, _) => panels++;
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(order.Uid, TargetUid: targets.Single())));
        Assert.Equal(0, panels);
        Assert.DoesNotContain(s.Actions.OrderTargets, t => t.Uid == order.Uid);
    }

    [Fact]
    public async Task NonTargetOrdersAndOptionsDoNotPullTargetArrowsAndEnemyOwnershipExposesNone()
    {
        using var s = await Ready("USG/commands/_7", "deran/command/_13", "USG/commands/_11");
        var hand = s.View.SelfHand;
        Assert.DoesNotContain(s.Actions.OrderTargets, t => hand.Any(c => c.Uid == t.Uid
            && c.CardId is "USG/commands/_7" or "deran/command/_13"));
        Assert.Contains(s.Actions.OrderTargets, t => hand.Any(c => c.Uid == t.Uid && c.CardId == "USG/commands/_11"));
        await s.SubmitAsync(new EndTurn());
        Assert.Empty(s.Actions.OrderTargets);
    }

    [Fact]
    public async Task DestroyedUnitLeavesLiveTargetProjectionWhileEarlierSnapshotStaysFrozen()
    {
        using var s = await Ready("USG/commands/_11", "deran/command/_12", "USG/units/_4");
        var unit = s.View.SelfHand.First(c => c.CardId == "USG/units/_4");
        var order = s.View.SelfHand.First(c => c.CardId == "USG/commands/_11");
        var later = s.View.SelfHand.First(c => c.CardId == "deran/command/_12");
        await s.SubmitAsync(new PlayCard(unit.Uid, 0));
        var frozen = UiSnapshots.Freeze(s.Actions);
        await s.SubmitAsync(new PlayCard(order.Uid, TargetUid: unit.Uid));
        Assert.Empty(s.Actions.OrderTargets.Single(t => t.Uid == later.Uid).TargetUids);
        Assert.Contains(unit.Uid, frozen.OrderTargets.Single(t => t.Uid == later.Uid).TargetUids);
    }

    [Fact]
    public void TargetMembershipIsFrozenAlongWithActions()
    {
        var targets = new List<string> { "unit-a" };
        var orders = new List<UiOrderTargeting> { new("order", targets) };
        var frozen = UiSnapshots.Freeze(new UiBattleActions { OrderTargets = orders });
        targets.Clear(); orders.Clear();
        Assert.Equal(new[] { "unit-a" }, frozen.OrderTargets.Single().TargetUids);
    }

    [Fact]
    public void ChoiceCardsAndSourceRemainStableUntilThePlayerAnswers()
    {
        var keywords = new List<string> { "choice" };
        var source = new UiCardView { Uid = "choice-source", Definition = new() { CardId = "order", Keywords = keywords } };
        var choices = new List<UiChoice> { new("card-a", "卡牌 A", ChoiceKind.CardDefinition, source with { Uid = "card-a" }) };
        var slots = new List<UiChoiceSlot> { new("pick", ChoiceKind.CardDefinition, 1, 1, choices) };
        var frozen = UiSnapshots.Freeze(new UiTargetRequest("req", slots) { SourceCard = source });
        slots.Clear(); choices.Clear(); keywords.Clear();
        Assert.Equal("choice-source", frozen.SourceCard!.Uid);
        Assert.Equal(new[] { "choice" }, frozen.SourceCard.Definition.Keywords);
        Assert.Equal("card-a", frozen.Slots.Single().Allowed.Single().Card!.Uid);
        Assert.Equal(new[] { "choice" }, frozen.Slots.Single().Allowed.Single().Card!.Definition.Keywords);
    }
}
