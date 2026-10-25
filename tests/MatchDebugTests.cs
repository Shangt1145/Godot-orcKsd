using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

[Collection("engine-host")]
public sealed class MatchDebugTests
{
    [Fact]
    public async Task DebuggingIsOptInAndNativeMutationsAffectOnlyTheSelectedPlayerAndCard()
    {
        using var session=await OrcMatchSession.CreateDefaultProbeAsync("debug-probe",20,714,"debug-test",firstPlayerIndex:0);
        await session.SettleMulliganAsync(false);session.SettleProjection();session.DriveOpponent=true;
        var hand=session.View.SelfHand.Count;var enemy=session.View.EnemyHandCount;
        Assert.False((await session.DebugAsync(new(MatchDebugAction.Draw))).Success);
        Assert.Equal(hand,session.View.SelfHand.Count);
        Assert.True((await session.DebugAsync(new(MatchDebugAction.Enable))).Success);
        Assert.False(session.DriveOpponent);
        Assert.True((await session.DebugAsync(new(MatchDebugAction.Draw,2,"enemy"))).Success);
        Assert.Equal(hand,session.View.SelfHand.Count);Assert.Equal(enemy+2,session.View.EnemyHandCount);
        Assert.False((await session.DebugAsync(new(MatchDebugAction.Draw,11))).Success);
        var enemyHq=session.View.EnemyHq!.Uid;
        Assert.True((await session.DebugAsync(new(MatchDebugAction.Damage,3,TargetUid:enemyHq))).Success);
        Assert.Equal(17,session.View.EnemyHq.Health);Assert.Equal(20,session.View.SelfHq!.Health);
        Assert.True((await session.DebugAsync(new(MatchDebugAction.Heal,2,TargetUid:enemyHq))).Success);
        Assert.Equal(19,session.View.EnemyHq.Health);
        var id=Assert.Single(session.ReadDebugSnapshot().Definitions).Id;
        Assert.True((await session.DebugAsync(new(MatchDebugAction.Summon,CardId:id))).Success);
        var unit=Assert.Single(session.View.SelfLine);
        Assert.True((await session.DebugAsync(new(MatchDebugAction.SetAttack,7,TargetUid:unit.Uid))).Success);
        Assert.Equal(7,Assert.Single(session.View.SelfLine).EffectiveAttack);
        Assert.False((await session.DebugAsync(new(MatchDebugAction.Destroy,TargetUid:enemyHq))).Success);
        Assert.True((await session.DebugAsync(new(MatchDebugAction.Destroy,TargetUid:unit.Uid))).Success);
        Assert.Empty(session.View.SelfLine);
        Assert.False((await session.DebugAsync(new(MatchDebugAction.Damage,1,TargetUid:unit.Uid))).Success);
        Assert.True((await session.DebugAsync(new(MatchDebugAction.Disable))).Success);
        Assert.True(session.DriveOpponent);Assert.False(session.ReadDebugSnapshot().Enabled);
    }

    [Fact]
    public async Task DebugCommandsRespectCommandLockAndCanCancelAParkedDeploymentWithoutSpendingResources()
    {
        var root=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(root,"Kards.Ui.csproj")))root=Path.GetDirectoryName(root)!;
        var catalog=new Kards.Ui.Core.CardCatalog();catalog.Load(Path.Combine(root,"proto/data/nations"));
        var ally=new DiyCardDraft{Name="调试目标",Cost=0};
        var deploy=new DiyCardDraft{Name="调试部署",Cost=1,EffectText="部署：使1个友方单位获得+1+1。"};
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,715,"debug-lock","probe",sourceDirectory:Path.Combine(root,"proto/data/nations"),customCards:[ally,deploy],openingHand:[ally.Id,deploy.Id],firstPlayerIndex:0);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);session.SettleProjection();
        await session.DebugAsync(new(MatchDebugAction.Enable));
        var target=session.View.SelfHand.Single(c=>c.CardId==ally.Id);
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(target.Uid)));session.SettleProjection();
        var actor=session.View.SelfHand.Single(c=>c.CardId==deploy.Id);var credits=session.View.SelfKredits;
        var attack=Assert.Single(session.View.SelfLine).EffectiveAttack;
        var events=session.Match.Engine.RootStream.Entries.Count;
        for(var i=0;i<5;i++)Assert.Contains(session.Actions.OrderTargets,t=>t.Uid==actor.Uid);
        Assert.Equal(events,session.Match.Engine.RootStream.Entries.Count);
        var requested=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TargetRequested+=(_,_)=>requested.TrySetResult();
        var playing=session.SubmitAsync(new PlayCard(actor.Uid,GapId:session.Actions.DeploymentGaps.Last().GapId));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.TargetPending);Assert.False((await session.DebugAsync(new(MatchDebugAction.Draw))).Success);
        Assert.True((await session.DebugAsync(new(MatchDebugAction.CancelChoice))).Success);
        Assert.NotEqual(UiSubmitOutcome.Applied,await playing);session.SettleProjection();
        Assert.Equal(credits,session.View.SelfKredits);Assert.Contains(session.View.SelfHand,c=>c.Uid==actor.Uid);
        Assert.Equal(attack,Assert.Single(session.View.SelfLine).EffectiveAttack);
    }
}
