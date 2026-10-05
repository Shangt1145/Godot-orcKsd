using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class BattleDemoTests
{
    [Fact]
    public void SuppliedActionCollectionsAreFrozenBeforeDeferredInteraction()
    {
        var plays = new List<string> { "unit-a" };
        var moves = new List<UiMoveOption> { new("unit-b", "frontline") };
        var previews = new List<UiAttackPreview> { new("unit-b", "enemy-a", 2, 1, false, false) };
        var frozen = UiSnapshots.Freeze(new UiBattleActions { PlayableUids = plays, Moves = moves, AttackPreviews = previews });
        plays.Clear(); moves.Clear(); previews.Clear();
        Assert.Equal("unit-a", Assert.Single(frozen.PlayableUids));
        Assert.Equal("unit-b", Assert.Single(frozen.Moves).Uid);
        Assert.Equal("enemy-a", Assert.Single(frozen.AttackPreviews).DefenderUid);
    }
    private static BattleDemoAdapter Demo() => new(Enumerable.Range(0, 12).Select(i => new UiCardDefinition
    {
        CardId = "fixture/" + i, Name = "单位 " + i, CardType = "unit", UnitType = "infantry",
        Cost = 1, BaseAttack = 2, BaseDefense = i % 3 + 1, BaseOpCost = 1, ArtPath = "fixture.png"
    }).ToArray());
    [Fact]
    public void DeploymentPreservesIdentityAndMovesBetweenZonesWithoutChangingOldSnapshot()
    {
        var demo = Demo(); var before = demo.State; var uid = demo.Actions.PlayableUids[0];
        Assert.True(demo.TrySubmit(new PlayCard(uid), out _));
        Assert.DoesNotContain(demo.State.SelfHand, c => c.Uid == uid);
        Assert.Single(demo.State.SelfLine, c => c.Uid == uid && c.Zone == "support");
        Assert.Equal(before.SelfKredits - 1, demo.State.SelfKredits);
        Assert.Equal(5, before.SelfHand.Count);
        Assert.Equal(4, demo.State.SelfHandCount);
        Assert.False(demo.TrySubmit(new PlayCard(uid), out _));
    }
    [Fact]
    public void InvalidCommandsDoNotSpendResourcesOrMoveCards()
    {
        var demo = Demo(); var before = demo.State;
        Assert.False(demo.TrySubmit(new PlayCard("enemy-0"), out _));
        Assert.False(demo.TrySubmit(new MoveUnit("self-0", "enemy-support"), out _));
        Assert.False(demo.TrySubmit(new AttackUnit("self-0", "enemy-0"), out _));
        Assert.Same(before, demo.State);
    }
    [Fact]
    public void FullSupportLineRemovesDeploymentOptions()
    {
        var demo = Demo();
        Assert.True(demo.TrySubmit(new PlayCard(demo.Actions.PlayableUids[0]), out _));
        Assert.True(demo.TrySubmit(new PlayCard(demo.Actions.PlayableUids[0]), out _));
        Assert.Empty(demo.Actions.PlayableUids);
        var uid = demo.State.SelfHand[0].Uid;
        Assert.False(demo.TrySubmit(new PlayCard(uid), out _));
    }
    [Fact]
    public void MoveUpdatesZoneAndCannotBeRepeatedUntilNextTurn()
    {
        var demo = Demo(); var move = demo.Actions.Moves[0];
        Assert.True(demo.TrySubmit(new MoveUnit(move.Uid, move.ToZone), out _));
        Assert.Equal("frontline", demo.State.SelfLine.Single(c => c.Uid == move.Uid).Zone);
        Assert.DoesNotContain(demo.Actions.Moves, m => m.Uid == move.Uid);
        Assert.False(demo.TrySubmit(new MoveUnit(move.Uid, move.ToZone), out _));
    }
    [Fact]
    public void EnemyTurnClearsOptionsAndNextTurnRestoresThem()
    {
        var demo = Demo();
        Assert.Contains(demo.Actions.AttackPreviews, p => p.DefenderDies);
        Assert.Contains(demo.Actions.AttackPreviews, p => p.AttackerDies);
        Assert.True(demo.TrySubmit(new EndTurn(), out _));
        Assert.Empty(demo.Actions.PlayableUids); Assert.Empty(demo.Actions.Moves); Assert.Empty(demo.Actions.AttackPreviews);
        Assert.False(demo.Actions.CanEndTurn);
        demo.BeginNextTurn();
        Assert.Equal(5, demo.State.Turn); Assert.Equal("self", demo.State.ActivePlayerSide);
        Assert.Equal(9, demo.State.SelfKredits); Assert.True(demo.Actions.CanEndTurn);
        Assert.NotEmpty(demo.Actions.AttackPreviews);
    }
    [Fact]
    public void CombatProjectionFreezesActorsAndAfterStateBeforePlayback()
    {
        var keywords = new List<string> { "guard" }; var values = new Dictionary<string, int> { ["guard"] = 2 };
        var actor = new UiCardView { Uid = "a", Definition = new() { CardId = "fixture", Keywords = keywords, KeywordValues = values }, Keywords = keywords, KeywordValues = values, Health = 4 };
        var afterList = new List<UiCardView> { actor with { Health = 3 } };
        var frozen = UiSnapshots.Freeze(new UiCombatResolution("fixture", actor, actor with { Uid = "b" }, 2, 1,
            afterList[0], null, new() { MatchId = "fixture", SelfLine = afterList }));
        keywords.Clear(); values["guard"] = 99; afterList.Clear();
        Assert.Equal(4, frozen.Attacker.Health); Assert.Equal(3, frozen.AttackerAfter!.Health);
        Assert.Equal("guard", Assert.Single(frozen.Attacker.Definition.Keywords));
        Assert.Equal(2, frozen.After.SelfLine[0].KeywordValues["guard"]);
        Assert.Null(frozen.DefenderAfter);
    }
    [Fact]
    public void ProductionActionsDoNotEnableDemoAttacksByDefault()
    {
        Assert.False(new UiBattleActions().AttacksEnabled);
        var projected = UiSnapshots.Freeze(new UiBattleActions { AttacksEnabled = true, AttackPreviews = [new("a", "b", null, null, false, false)] });
        Assert.True(projected.AttacksEnabled);
        Assert.Null(projected.AttackPreviews[0].DamageToDefender);
    }
    [Theory]
    [InlineData("infantry", 1)]
    [InlineData("artillery", 0)]
    [InlineData("bomber", 0)]
    public void DemoFixtureEmitsResolvedProjectionAndCannotReplaySameAction(string type, int incoming)
    {
        var demo = Demo(); demo.ResetCombatScenario(type); var before = demo.State;
        var attack = new AttackUnit("self-front", "enemy-0");
        Assert.True(demo.TryResolveAttack(attack, out var projection, out _));
        Assert.Equal(3, projection!.DamageToDefender); Assert.Equal(incoming, projection.DamageToAttacker);
        Assert.Null(projection.DefenderAfter); Assert.Equal(4 - incoming, projection.AttackerAfter!.Health);
        Assert.Equal(before.SelfKredits - 1, projection.After.SelfKredits);
        Assert.Equal(3, before.EnemyLine[0].Health);
        Assert.DoesNotContain(projection.After.EnemyLine, c => c.Uid == "enemy-0");
        var after = demo.State;
        Assert.False(demo.TryResolveAttack(attack, out _, out _)); Assert.Same(after, demo.State);
    }
}
