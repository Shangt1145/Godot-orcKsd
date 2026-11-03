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

/// <summary>
/// The attack trajectory, end to end against a real match: deploy, advance, strike the enemy HQ.
/// Two things are asserted that the UI could not know before the engine grew unit.damage.dealt —
/// the damage number is the engine's own amount, and the impact carries the attacker.
/// </summary>
public sealed class AssaultPresentationTests
{
    /// <summary>
    /// 动作实现集是**进程级**注册（引擎 R17）：直接 new Match 的测试必须先登记，
    /// 否则 <c>MatchActionCatalog</c> 装配期就 fail-fast（「未注册动作实现」）。
    /// <para>
    /// 以前这条能过，是因为别的测试类先跑过 <c>FoundationActions.Register()</c>——
    /// 靠测试执行顺序的运气，加几条测试就会翻脸（2026-11-03 实测）。
    /// <c>SetDefault</c> 可重复调用，这里显式登记一次，不再赌顺序。
    /// </para>
    /// </summary>
    static AssaultPresentationTests() => Orc.Foundation.Actions.FoundationActions.Register();

    private const string InfantryId = "assault-infantry";

    private static Match NewMatch(Action<Orc.Game.Targeting.ISelectorInstance>? present = null)
    {
        var definitions = new[]
        {
            new CardDefinitionEntry(InfantryId, new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard))
        };
        Match? match = null;
        // AutoRespond is required, not optional: BeginUnitPrePlayAsync parks on a native selector
        // and waits indefinitely unless a choice is submitted. A handler that only records the
        // request deadlocks the test host.
        var bridge = new OrcTargeterBridge(present ?? OrcTargeterBridge.AutoRespond);
        match = new Match(
            new CardList(Enumerable.Repeat(InfantryId, 20)),
            new CardList(Enumerable.Repeat(InfantryId, 20)),
            definitions, seed: 11, firstPlayerIndex: 0, targeterBridge: bridge, packageActivator: new Orc.Packages.SnapshotPackageActivator(OrcEngineHost.Snapshot()));
        return match;
    }



    [Fact]
    public async Task AttackOnTheEnemyHqCarriesTheEngineAmountAndTheAttacker()
    {
        var aimAtHq = false;
        var match = NewMatch(selector =>
        {
            if (!aimAtHq) { OrcTargeterBridge.AutoRespond(selector); return; }
            // Backend candidates now include enemy units too; this scenario explicitly aims at the HQ.
            var hq = selector.Presentation.Candidates.Single(r => r.Value is Orc.Game.Players.Hq);
            selector.Submit(new Orc.Game.Targeting.DropEvent(hq));
        });
        var host = new OrcMatchHost(match, "assault");
        var impacts = new List<IReadOnlyList<UiOrderImpact>>();
        host.CombatReady += (reported, _, _) => impacts.AddRange(reported);

        await host.InitializeAsync();
        var unit = (UnitCard)match.Players[0].Hand.First();
        var unitUid = OrcRefs.KeyOf(unit);
        var enemyHqUid = host.View.EnemyHq!.Uid;

        await match.MulliganDone(match.Players[0]);
        await match.MulliganDone(match.Players[1]);
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.BeginUnitPrePlayAsync(unit)).Status);

        await match.EndTurn();
        await OrcOpponentDriver.PlayTurnAsync(match, match.Players[1]);
        Assert.Equal(Orc.Game.Commanding.CommandResultStatus.Success,
            (await match.CommandManager.BeginMoveAsync(unit)).Status);
        await match.EndTurn();
        await OrcOpponentDriver.PlayTurnAsync(match, match.Players[1]);

        aimAtHq = true;
        var attack = match.CommandManager.BeginAttackAsync(unit);
        Assert.True(await Task.WhenAny(attack, Task.Delay(TimeSpan.FromSeconds(20))) == attack,
            "the attack never completed");
        Assert.Equal(Orc.Game.Commanding.CommandResultStatus.Success, (await attack).Status);
        host.Pump();

        var hq = impacts.SelectMany(i => i).FirstOrDefault(i => i.Before.Uid == enemyHqUid);
        Assert.NotNull(hq);
        Assert.Equal(2, hq!.Damage);              // the engine's own amount, not a stat diff
        Assert.NotNull(hq.Source);                // unit.damage.dealt paired the attacker
        Assert.Equal(unitUid, hq.Source!.Uid);
    }

    [Fact]
    public void AnImpactWithoutAnEnginePairStaysSourceless()
    {
        var definition = new UiCardDefinition { CardId = "probe", Name = "probe" };
        var before = new UiCardView { Uid = "probe-target", Definition = definition, Visibility = Visibility.Full };
        Assert.Null(new UiOrderImpact(before, before, 3).Source);
        var paired = new UiOrderImpact(before, before, 3) with { Damage = 9, Source = before };
        Assert.Equal(9, paired.Damage);
        Assert.NotNull(paired.Source);
    }
}
