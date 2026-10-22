using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Judicators;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class ActionPortalTests
{
    private static async Task<OrcMatchSession> Match()
    {
        var s = await OrcMatchSession.CreateProbeAsync("gap-probe", 20, 37, "gap-tests", firstPlayerIndex: 0);
        s.DriveOpponent = false;
        await s.SettleMulliganAsync(false); s.SettleProjection();
        await s.Host.Match.ResourceManager.GainPointsAsync(s.Host.Match.Players[0], 30);
        s.Host.Refresh();
        return s;
    }

    public static IEnumerable<object[]> Positions() => Enumerable.Range(0, 4)
        .SelectMany(count => Enumerable.Range(0, count + 2).Select(rank => new object[] { count, rank }));

    [Theory, MemberData(nameof(Positions))]
    public async Task NativeFormationOffersEveryBoundaryIncludingHeadquartersAndHonoursInsertion(int count, int rank)
    {
        using var s = await Match(); var p = s.Host.Match.Players[0]; var line = s.Host.Match.Battlefield.GetSupportLine(p);
        var cards = p.Hand.OfType<UnitCard>().Take(count + 1).ToArray();
        for (var i = 0; i < count; i++)
            Assert.True((await s.Host.Match.PlayManager.PlayUnitAsync(cards[i], line.GetInsertionGaps().Last())).IsSuccess);
        s.SettleProjection();
        var gaps = s.Actions.DeploymentGaps;
        Assert.Equal(count + 2, gaps.Count);
        Assert.Equal(Enumerable.Range(0, count + 2), gaps.Select(g => g.Ordinal));
        Assert.Equal(gaps.Select(g => g.GapId), s.Actions.DeploymentGaps.Select(g => g.GapId));
        var expected = line.Members.Cast<Entity>().ToList();
        expected.Insert(rank, cards[count]);
        Assert.Equal(UiSubmitOutcome.Applied, await s.SubmitAsync(new PlayCard(OrcRefs.KeyOf(cards[count]), GapId: gaps[rank].GapId)));
        s.SettleProjection();
        Assert.Equal(expected, line.Members.Cast<Entity>());
        foreach (var unit in cards) Assert.Same(line, unit.GetData<UnitStateData>().Line);
        Assert.Contains(p.Hq, line.Members);
        var projected = s.View.SelfLine.Append(s.View.SelfHq!).OrderBy(c => c.SlotIndex).Select(c => c.Uid);
        Assert.Equal(expected.Select(OrcRefs.KeyOf), projected);
        Assert.Equal(count == 3 ? 0 : count + 3, s.Actions.DeploymentGaps.Count);
    }

    [Fact]
    public async Task FourUnitsCanStandOnTheSameSideOfHeadquarters()
    {
        using var s = await Match(); var p = s.Host.Match.Players[0]; var line = s.Host.Match.Battlefield.GetSupportLine(p);
        var units = p.Hand.OfType<UnitCard>().ToArray();
        foreach (var unit in units) Assert.True((await s.Host.Match.PlayManager.PlayUnitAsync(unit, line.GetInsertionGaps()[0])).IsSuccess);
        Assert.Equal(4, line.IndexOf(p.Hq));
        Assert.Empty(line.GetInsertionGaps());
        Assert.Equal(units.Reverse(), line.Members.Take(4).Cast<UnitCard>());
    }

    [Fact]
    public async Task StaleAndForeignGapRefusalsLeaveBoardHandAndCreditsUntouched()
    {
        using var s = await Match(); var p = s.Host.Match.Players[0]; var line = s.Host.Match.Battlefield.GetSupportLine(p);
        var units = p.Hand.OfType<UnitCard>().ToArray(); var old = line.GetInsertionGaps()[0];
        Assert.True((await s.Host.Match.PlayManager.PlayUnitAsync(units[0], line.GetInsertionGaps()[1])).IsSuccess);
        var positions = line.Members.ToArray(); var points = p.Points; var hand = p.Hand.ToArray();
        Assert.Equal(PlayFailureReason.FormationGapUnavailable,
            (await s.Host.Match.PlayManager.PlayUnitAsync(units[1], old)).FailureReason);
        Assert.Equal(PlayFailureReason.FormationGapUnavailable,
            (await s.Host.Match.PlayManager.PlayUnitAsync(units[1], s.Host.Match.Battlefield.PlayerBSupportLine.GetInsertionGaps()[0])).FailureReason);
        Assert.Equal(positions, line.Members); Assert.Equal(points, p.Points); Assert.Equal(hand, p.Hand);
        Assert.Equal(UiSubmitOutcome.Rejected, await s.SubmitAsync(new PlayCard(OrcRefs.KeyOf(units[1]), GapId: old.Id.ToString("N"))));
        Assert.Equal("FormationGapUnavailable", s.LastRejectionReason);
    }

    [Fact]
    public async Task NativeCostRevalidationRejectsBeforeFormationReflow()
    {
        using var s = await Match(); var p = s.Host.Match.Players[0]; var line = s.Host.Match.Battlefield.GetSupportLine(p);
        var unit = p.Hand.OfType<UnitCard>().First(); var gap = line.GetInsertionGaps()[0];
        var positions = line.Members.ToArray();
        await s.Host.Match.ResourceManager.ChangePointsAsync(p, 0, Orc.Game.Managers.PointChangeKind.Set);
        Assert.Equal(PlayFailureReason.PlayVerificationRejected, (await s.Host.Match.PlayManager.PlayUnitAsync(unit, gap)).FailureReason);
        Assert.Equal(positions, line.Members); Assert.Same(p.Hq, line[0]);
        Assert.Contains(unit, p.Hand); Assert.False(unit.TryGetData<UnitStateData>(out _));
    }

    [Fact]
    public async Task NativeJudicatorPullsFreshUiStateAtQueryAndCommitAndLeavesUntouchedCardsAvailable()
    {
        using var s = await Match(); var units = s.Host.Match.Players[0].Hand.OfType<UnitCard>().ToArray();
        var uid = OrcRefs.KeyOf(units[0]); var pending = new[] { uid }; var phase = UiInteractionPhase.Ready; var reads = 0;
        s.InteractionStateReader = () => { reads++; return new(phase, pending); };
        Assert.DoesNotContain(uid, s.Actions.PlayableUids);
        Assert.Contains(OrcRefs.KeyOf(units[1]), s.Actions.PlayableUids);
        Assert.True(s.Host.Match.CanAcceptInput(s.Host.Match.Players[0], units[1], units[0]));
        var line = s.Host.Match.Battlefield.PlayerASupportLine; var gap = line.GetInsertionGaps()[0];
        Assert.Equal(PlayFailureReason.InteractionBlocked, (await s.Host.Match.PlayManager.PlayUnitAsync(units[0], gap)).FailureReason);
        Assert.Equal(PlayFailureReason.InteractionBlocked, (await s.Host.Match.PlayManager.PlayUnitAsync(units[0], line.GetInsertionGaps()[0])).FailureReason);
        Assert.Equal(UiSubmitOutcome.Rejected, await s.SubmitAsync(new PlayCard(uid, GapId: gap.Id.ToString("N"))));
        phase = UiInteractionPhase.Choosing; pending = [];
        Assert.False(s.Host.Match.PlayManager.CanPlay(units[1]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Host.Match.EndTurn());
        phase = UiInteractionPhase.Ready;
        Assert.True((await s.Host.Match.PlayManager.PlayUnitAsync(units[0], gap)).IsSuccess);
        // 可动/可攻在新引擎里是配额（1＝可，0＝否），属性本身只读。
        units[0].GetData<CommandData>().SetMoveQuota(1);
        pending = [uid];
        Assert.Equal(CommandFailureReason.InteractionBlocked, (await s.Host.Match.CommandManager.BeginCommandAsync(units[0])).FailureReason);
        Assert.False(s.Host.Match.CommandManager.GetCommandAvailability(units[0]).AnyActionAvailable);
        pending = [];
        Assert.True(s.Host.Match.CommandManager.GetCommandAvailability(units[0]).Move.CanUse);
        Assert.True(reads > 5);
        Assert.Contains(JudicatorNames.ActionInput, Orc.Game.GameHooks.BuiltInJudicatorNames);
    }

    [Fact]
    public async Task GapPortalAlsoEnforcesNativeTurnAndHandMembership()
    {
        using var s = await Match(); var enemy = s.Host.Match.Players[1]; var card = enemy.Hand.OfType<UnitCard>().First();
        Assert.Equal(PlayFailureReason.NonOwnerTurn,
            (await s.Host.Match.PlayManager.PlayUnitAsync(card, s.Host.Match.Battlefield.PlayerBSupportLine.GetInsertionGaps()[0])).FailureReason);
        var p = s.Host.Match.Players[0]; var own = p.Hand.OfType<UnitCard>().First(); var line = s.Host.Match.Battlefield.PlayerASupportLine;
        p.Hand.Remove(own);
        Assert.Equal(PlayFailureReason.CardNotInHand, (await s.Host.Match.PlayManager.PlayUnitAsync(own, line.GetInsertionGaps()[0])).FailureReason);
        // Backend spawning is independent of user gesture restrictions.
        s.InteractionStateReader = () => new(UiInteractionPhase.Suspended, []);
        Assert.True((await s.Host.Match.PlayManager.JoinUnitAsync(own, line.GetInsertionGaps()[0])).IsSuccess);
    }

    [Fact]
    public async Task DeploymentEffectSelectionDoesNotConsumeTheRecordedFormationGap()
    {
        var catalog = new CardCatalog();
        const string source = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
        catalog.Load(source);
        var draft = new DiyCardDraft { Id = "diy/gap-effect", Name = "选位后部署", Cost = 1,
            EffectText = "部署：对一个单位造成 1 点伤害。" };
        using var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, 7, "gap-effect", "probe",
            sourceDirectory: source, customCards: [draft], openingHand: ["deran/units/_6q", draft.Id], firstPlayerIndex: 0);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        await session.Host.Match.ResourceManager.GainPointsAsync(session.Host.Viewer, 20); session.SettleProjection();
        var plain = session.View.SelfHand.First(c => c.CardId == "deran/units/_6q");
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(plain.Uid, 0)));
        var targetRequests = 0;
        session.TargetRequested += (request, responder) => {
            targetRequests++;
            Assert.True(responder.Complete(request.RequestId,
                new Dictionary<string, IReadOnlyList<string>> { [request.Slots[0].Name] = [plain.Uid] }));
        };
        var gap = session.Actions.DeploymentGaps.Last();
        var card = session.View.SelfHand.First(c => c.CardId == draft.Id);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(card.Uid, GapId: gap.GapId)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(targetRequests > 0);
        Assert.Equal(gap.Ordinal, session.View.SelfLine.Single(c => c.Uid == card.Uid).SlotIndex);
    }

    [Fact]
    public void InteractionStateReservesOperatedSourcesAndNeverDrawsOrDamageTargets()
    {
        var definition = new UiCardDefinition { CardId = "fixture" };
        var state = new BattleInteractionStateMachine(); var source = new UiCardView { Uid = "source", Definition = definition };
        var victim = new UiCardView { Uid = "victim", Definition = definition }; var drawn = new UiCardView { Uid = "drawn", Definition = definition };
        Assert.True(state.Reserve(new PlayCard(source.Uid))); Assert.False(state.Reserve(new PlayCard(source.Uid)));
        var after = new UiMatchView { MatchId = "ui-input", SelfHand = [drawn] };
        state.Presented(new(after.MatchId, [new UiDrawPresentation("self", 0, drawn), new UiStatusPresentation(victim, victim, UiStatusKind.Buff)], after));
        Assert.True(state.IsPending(source.Uid)); Assert.False(state.IsPending(victim.Uid)); Assert.False(state.IsPending(drawn.Uid));
        Assert.True(state.Reserve(new PlayCard(drawn.Uid)));
        state.Presented(new(after.MatchId, [new UiOrderPresentation(source, [])], after));
        Assert.False(state.IsPending(source.Uid)); Assert.True(state.IsPending(drawn.Uid));
        state.Refuse(new PlayCard(drawn.Uid)); Assert.Empty(state.Read().PendingActorUids);
    }
}
