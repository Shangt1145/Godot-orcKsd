using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class NativeStateFeedbackTests
{
    [Fact]
    public async Task RealLethalAttackPublishesHqDestructionAndOneNativeResult()
    {
        var fixture = await OrcStateFeedbackVerification.RunHqZeroAsync();
        Assert.Equal(20, fixture.Initial.EnemyHq!.Health); Assert.Equal(0, fixture.Final.EnemyHq!.Health);
        Assert.Equal("胜利", fixture.Final.ResultTitle); Assert.Equal("HqZero", fixture.Final.ResultReason);
        var resolution = Assert.Single(fixture.Resolutions);
        var hit = Assert.Single(resolution.Steps.OfType<UiBoardImpactsPresentation>().SelectMany(s => s.Impacts), i => i.Before.IsHq);
        Assert.Equal(20, hit.Damage); Assert.Null(hit.After); Assert.NotNull(hit.Source);
    }

    [Fact]
    public async Task NativeMechanismsPublishCurrentKeywordsAndUniqueStatusSteps()
    {
        var fixture = await OrcStateFeedbackVerification.RunAsync();
        var unit = Assert.Single(fixture.Initial.SelfLine);
        Assert.True(unit.IsCovert); Assert.False(unit.IsVeteran);
        Assert.Equal(2, unit.KeywordValues["armor"]);
        var statuses = fixture.Resolutions.SelectMany(r => r.Steps).OfType<UiStatusPresentation>().ToArray();
        var reveal = Assert.Single(statuses, s => s.Status == UiStatusKind.Revealed);
        Assert.True(reveal.Before.IsCovert); Assert.False(reveal.After.IsCovert);
        Assert.Equal(Visibility.Full, reveal.Before.Visibility); // Covert does not conceal identity.
        var veteran = Assert.Single(statuses, s => s.Status == UiStatusKind.Veteran);
        Assert.Equal(unit.Uid, veteran.After.Uid); Assert.True(veteran.After.IsVeteran);
        Assert.Equal(unit.CardId, veteran.After.CardId); // Native promotion retains definition identity.
        Assert.Equal(4, veteran.After.EffectiveAttack); Assert.Equal(7, veteran.After.Health);
        Assert.DoesNotContain("armor", veteran.After.Keywords); Assert.Contains("blitz", veteran.After.Keywords);
        Assert.Single(statuses, s => s.Status == UiStatusKind.Suppressed);
        Assert.Contains(statuses, s => s.Status == UiStatusKind.KeywordsChanged && s.After.KeywordValues.GetValueOrDefault("suppressed") == 2);
        Assert.Single(statuses, s => s.Status == UiStatusKind.Cleared);
        Assert.Single(statuses, s => s.Status == UiStatusKind.Silenced);
        Assert.Single(statuses, s => s.Status == UiStatusKind.TypesChanged && s.After.UnitTypes.Contains("bomber"));
        var final = Assert.Single(fixture.Final.SelfLine);
        Assert.True(final.IsSilenced); Assert.False(final.IsVeteran); Assert.Contains("smokescreen", final.Keywords);
        Assert.Single(statuses, s => s.Status == UiStatusKind.Heal && s.After.Health == 23);
        Assert.Equal(new[] { "self", "enemy" }, fixture.Resolutions.SelectMany(r => r.Steps).OfType<UiShufflePresentation>().Select(s => s.Side));
        Assert.True(fixture.Resolutions.Zip(fixture.Resolutions.Skip(1)).All(p => p.First.Sequence < p.Second.Sequence));
        Assert.All(fixture.Resolutions, r => Assert.DoesNotContain(r.After.EnemyLine, c => c.Zone == "hand"));
        Assert.Equal(2, unit.KeywordValues["armor"]); // Earlier snapshots survived full content replacement.
    }

    [Theory]
    [InlineData("deran/command/_18", UiStatusKind.Buff)]
    [InlineData("av76/command/-5", UiStatusKind.CostChanged)]
    [InlineData("deran/command/_13", UiStatusKind.Heal)]
    public async Task ReviewedOrdersDeliverTheirRealEffectsToPlayback(string id, UiStatusKind status)
    {
        var path = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
        var catalog = new CardCatalog(); catalog.Load(path);
        using var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, 7, Guid.NewGuid().ToString(), "probe",
            openingHand: ["deran/units/_6q", id], sourceDirectory: path, firstPlayerIndex: 0);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        var match = session.Host.Match;
        await match.ResourceManager.GainSlotsAsync(match.Players[0], 10);
        await match.ResourceManager.GainPointsAsync(match.Players[0], 12);
        var unit = session.View.SelfHand.First(c => c.CardId == "deran/units/_6q");
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(unit.Uid)));
        session.SettleProjection();
        var effects = new List<UiPresentationStep>(); session.Host.PresentationReady += (r, _) => effects.AddRange(r.Steps);
        session.TargetRequested += (request, responder) => Assert.True(responder.Complete(request.RequestId,
            new Dictionary<string, IReadOnlyList<string>> { [request.Slots[0].Name] = ["0"] }));
        var order = session.View.SelfHand.First(c => c.CardId == id);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(order.Uid,
            TargetUid: id == "deran/command/_13" ? null : unit.Uid)));
        session.Pump();
        Assert.Single(effects.OfType<UiOrderPresentation>());
        Assert.Single(effects.OfType<UiStatusPresentation>(), s => s.Status == status);
        if (id == "av76/command/-5") Assert.Contains("blitz", Assert.Single(session.View.SelfLine).Keywords);
        session.Pump(); Assert.Single(effects.OfType<UiOrderPresentation>());
    }

    [Fact]
    public async Task ConcedeProducesOneAuthoritativeResultAndRejectsAllFurtherCommands()
    {
        using var session = await OrcMatchSession.CreateProbeAsync("probe", 24, 17, "concede", firstPlayerIndex: 0);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false); session.SettleProjection();
        var hand = session.View.SelfHand; var points = session.View.SelfKredits;
        var results = new List<UiPresentationResolution>(); session.Host.PresentationReady += (r, _) => results.Add(r);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new Concede()));
        Assert.Equal("over", session.View.Phase); Assert.Equal("失败", session.View.ResultTitle);
        Assert.Equal("Concede", session.View.ResultReason); Assert.NotNull(session.View.FinalTurn);
        session.Pump(); session.Pump();
        Assert.Single(results); Assert.Empty(results[0].Steps); Assert.Equal("play", results[0].Before!.Phase);
        foreach (var command in new UiCommand[] { new Concede(), new EndTurn(), new PlayCard(hand[0].Uid),
            new MoveUnit(hand[0].Uid, "frontline", 0), new AttackUnit(hand[0].Uid, session.View.EnemyHq!.Uid) })
            Assert.Equal(UiSubmitOutcome.WrongPhase, await session.SubmitAsync(command));
        session.Pump(); Assert.Single(results); Assert.Equal(points, session.View.SelfKredits);
        Assert.Equal(hand.Select(c => c.Uid), session.View.SelfHand.Select(c => c.Uid)); Assert.False(session.Actions.CanEndTurn);
    }

    [Fact]
    public async Task ConcedeCannotChangeAnEnemyTurnOrAParkedMulligan()
    {
        using var session = await OrcMatchSession.CreateProbeAsync("probe", 24, 17, "concede-gates", firstPlayerIndex: 0);
        session.DriveOpponent = false; await session.SettleMulliganAsync(true);
        Assert.Equal(UiSubmitOutcome.WrongPhase, await session.SubmitAsync(new Concede())); Assert.True(session.MulliganPending);
        await session.CompleteMulliganAsync(session.View.SelfHand.Select(c => c.Uid).ToArray());
        await session.SubmitAsync(new EndTurn());
        Assert.Equal(UiSubmitOutcome.NotYourTurn, await session.SubmitAsync(new Concede()));
        Assert.Equal("play", session.View.Phase); Assert.Null(session.View.ResultTitle);
    }

    [Fact]
    public async Task OrderCausedStatFeedbackPrecedesTheNextAttack()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unit = new UiCardView { Uid = "unit", Definition = new UiCardDefinition { CardId = "unit" },
            Zone = "frontline", Health = 5, EffectiveAttack = 2 };
        var buffed = unit with { EffectiveAttack = 3, Health = 7 };
        var order = new UiCardView { Uid = "order", Definition = new UiCardDefinition { CardId = "order", CardType = "order" } };
        var initial = new UiMatchView { MatchId = "buff-chain", Phase = "play", SelfLine = [unit], SelfHand = [order] };
        var after = initial with { SelfLine = [buffed], SelfHand = [] };
        var steps = new List<UiPresentationStep>();
        using var player = new PresentationPlayer(initial.MatchId, async (r, _, _) =>
        { if (r.Sequence == 1) await gate.Task; steps.AddRange(r.Steps); }, initial);
        _ = player.Enqueue(new(initial.MatchId, [new UiShufflePresentation("enemy")], initial) { Sequence = 1, Before = initial }, new());
        _ = player.Enqueue(new(initial.MatchId, [new UiOrderPresentation(order, []), new UiStatusPresentation(unit, buffed, UiStatusKind.Buff)], after)
            { Sequence = 2, Before = initial }, new());
        var target = unit with { Uid = "enemy", OwnerSide = "enemy" };
        _ = player.Enqueue(new(initial.MatchId, [new UiBoardImpactsPresentation([new(target, target with { Health = 2 }, 3, buffed)])], after)
            { Sequence = 3, Before = after }, new());
        gate.SetResult(); await player.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(steps.FindIndex(s => s is UiStatusPresentation) < steps.FindIndex(s => s is UiBoardImpactsPresentation));
    }

    [Fact]
    public async Task TerminalPresentationFollowsPendingFeedbackAndClosesTheQueue()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = new UiMatchView { MatchId = "terminal", Phase = "play" };
        var sequences = new List<long>();
        using var player = new PresentationPlayer(initial.MatchId, async (r, _, _) =>
        { sequences.Add(r.Sequence); if (r.Sequence == 1) await gate.Task; }, initial);
        UiPresentationResolution R(long n, params UiPresentationStep[] steps) => new(initial.MatchId, steps, initial) { Sequence = n, Before = initial };
        _ = player.Enqueue(R(1, new UiShufflePresentation("self")), new());
        _ = player.Enqueue(R(2, new UiShufflePresentation("enemy")), new());
        _ = player.Enqueue(R(3, new UiTurnPresentation("self", 2)) with { Priority = UiPresentationPriority.Direct }, new());
        _ = player.Enqueue(R(4) with { After = initial with { Phase = "over", ResultTitle = "失败" } }, new());
        _ = player.Enqueue(R(5, new UiShufflePresentation("self")), new());
        gate.SetResult(); await player.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new long[] { 1, 3, 2, 4 }, sequences); Assert.False(player.IsBusy);
        await player.Enqueue(R(6), new()); Assert.Equal(4, sequences.Count);
    }
}
