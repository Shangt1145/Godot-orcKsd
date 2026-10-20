using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class Stage6AReliabilityTests
{
    private static async Task<OrcMatchSession> ReadyAsync(string id)
    {
        // 先手随机后，这些用例都假设"玩家先手能立刻行动"，必须固定座位（产品路径仍是随机）。
        var session = await OrcMatchSession.CreateProbeAsync("probe", 20, 17, id, firstPlayerIndex: 0);
        session.DriveOpponent = false;
        await session.SettleMulliganAsync(interactive: false);
        session.Pump();
        return session;
    }

    [Fact]
    public async Task AnInvalidDeploymentNeverSubstitutesAnotherSlot()
    {
        using var session = await ReadyAsync("invalid-slot");
        var uid = session.View.SelfHand[0].Uid;
        var points = session.View.SelfKredits;
        Assert.Equal(UiSubmitOutcome.Rejected, await session.SubmitAsync(new PlayCard(uid, 999)));
        Assert.Equal("SelectedSlotUnavailable", session.LastRejectionReason);
        Assert.Empty(session.View.SelfLine);
        Assert.Equal(points, session.View.SelfKredits);
        Assert.Contains(session.View.SelfHand, c => c.Uid == uid);
        // Refusing the first request must also clear its intent and unblock the next one.
        var slot = Enumerable.Range(0, session.View.SupportLineSlotCount)
            .First(i => i != session.View.SelfHq!.SlotIndex);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(uid, slot)));
        Assert.Equal(slot, Assert.Single(session.View.SelfLine).SlotIndex);
    }

    [Fact]
    public async Task AnInvalidAttackNeverFallsBackToTheEnemyHq()
    {
        using var session = await ReadyAsync("invalid-target");
        var uid = session.View.SelfHand[0].Uid;
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(uid)));
        await session.SubmitAsync(new EndTurn());
        session.SelectViewer(1);
        await session.SubmitAsync(new EndTurn());
        session.SelectViewer(0);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new MoveUnit(uid, "frontline", 0)));
        await session.SubmitAsync(new EndTurn());
        session.SelectViewer(1);
        await session.SubmitAsync(new EndTurn());
        session.SelectViewer(0);
        Assert.Contains(session.Actions.AttackPreviews, p => p.AttackerUid == uid);
        var health = session.View.EnemyHq!.Health;
        var points = session.View.SelfKredits;
        Assert.Equal(UiSubmitOutcome.Rejected, await session.SubmitAsync(new AttackUnit(uid, "missing-target")));
        Assert.Equal("SelectedTargetUnavailable", session.LastRejectionReason);
        Assert.Equal(health, session.View.EnemyHq!.Health);
        Assert.Equal(points, session.View.SelfKredits);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new AttackUnit(uid, session.View.EnemyHq.Uid)));
        Assert.Equal(health - 2, session.View.EnemyHq!.Health);
    }

    [Fact]
    public async Task ASecondEndTurnCannotPassTheOpponentsTurn()
    {
        using var session = await ReadyAsync("ownership");
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new EndTurn()));
        var turn = session.View.Turn;
        Assert.Equal(UiSubmitOutcome.NotYourTurn, await session.SubmitAsync(new EndTurn()));
        Assert.Equal(turn, session.View.Turn);
    }

    [Fact]
    public async Task ConcurrentCommandsAreRefusedAndExitCancelsTheOpponentDriver()
    {
        using var session = await ReadyAsync("concurrent");
        session.DriveOpponent = true;
        var first = session.SubmitAsync(new EndTurn());
        Assert.False(first.IsCompleted);
        Assert.Equal(UiSubmitOutcome.Busy, await session.SubmitAsync(new EndTurn()));
        session.Dispose();
        Assert.Equal(UiSubmitOutcome.Interrupted, await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(session.IsSubmitting);
    }

    [Fact]
    public async Task FrozenActionsKeepTheirBoardsEvenWhenPumpedLater()
    {
        using var session = await ReadyAsync("history");
        var resolutions = new List<UiPresentationResolution>();
        session.Host.PresentationReady += (r, _) => resolutions.Add(r);
        var uid = session.View.SelfHand[0].Uid;
        await session.SubmitAsync(new PlayCard(uid));
        var playTurn = session.View.Turn;
        await session.SubmitAsync(new EndTurn());
        session.Pump();
        var play = Assert.Single(resolutions, r => r.Steps.OfType<UiDeploymentPresentation>().Any());
        Assert.Equal(playTurn, play.After.Turn);
        Assert.Equal("self", play.After.ActivePlayerSide);
        Assert.Equal("enemy", resolutions.Last().After.ActivePlayerSide);
        Assert.True(resolutions.Zip(resolutions.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
        Assert.True(session.Host.HistoricalStatesVerified);
    }

    [Fact]
    public async Task MixedDamageAndDrawAreDeliveredInOneOrderedPresentation()
    {
        using var session = await ReadyAsync("mixed");
        var resolutions = new List<UiPresentationResolution>();
        session.Host.PresentationReady += (r, _) => resolutions.Add(r);
        var match = session.Host.Match;
        using (match.Engine.BeginAction())
        {
            // R11：Hq.ApplyDamageAsync 收为 internal，唯一调用方是 MatchActionServices（也是 internal）。
            // 界面侧没有公开的伤害门户，这里经反射驱动同一条数值路径，只为造出一次真实伤害。
            await DamageHqAsync(match.Players[0].Hq, 1);
            await match.PlayerManager.DrawCard(match.Players[0]);
        }
        session.Host.CaptureCompletedActions();
        session.Pump();
        var resolution = Assert.Single(resolutions);
        var impact = Assert.Single(resolution.Steps.OfType<UiBoardImpactsPresentation>());
        Assert.Equal(1, Assert.Single(impact.Impacts).Damage);
        Assert.Single(resolution.Steps.OfType<UiDrawPresentation>());
        Assert.True(resolution.Steps.ToList().IndexOf(impact) <
            resolution.Steps.ToList().FindIndex(s => s is UiDrawPresentation));
        Assert.Equal(19, resolution.After.SelfHq!.Health);
    }

    [Fact]
    public async Task DisposingAParkedMulliganCancelsTheMatch()
    {
        var session = await OrcMatchSession.CreateProbeAsync("probe", 20, 17, "cancel");
        await session.SettleMulliganAsync(interactive: true);
        Assert.True(session.MulliganPending);
        session.Dispose();
        session.Dispose();
        Assert.False(session.IsRunning);
        Assert.False(session.MulliganPending);
        Assert.Equal(UiSubmitOutcome.Interrupted, await session.SubmitAsync(new EndTurn()));
        session.Pump();
    }

    [Fact]
    public async Task AnObserverCannotConsumeThePrimaryHostsSegments()
    {
        using var session = await ReadyAsync("observer");
        var observer = new OrcMatchHost(session.Host.Match, "observer", 1);
        observer.AttachToLiveMatch();
        var presentations = 0;
        session.Host.PresentationReady += (_, _) => presentations++;
        using (session.Host.Match.Engine.BeginAction())
            await session.Host.Match.PlayerManager.DrawCard(session.Host.Viewer);
        observer.Pump();
        session.Pump();
        Assert.Equal(1, presentations);
        Assert.Equal("observer", observer.View.MatchId);
        observer.Detach();
    }

    [Fact]
    public async Task BatchedExternalActionsExposeTheMissingHistoricalSnapshots()
    {
        using var session = await ReadyAsync("external-actions");
        using (session.Host.Match.Engine.BeginAction())
            await session.Host.Match.PlayerManager.DrawCard(session.Host.Viewer);
        using (session.Host.Match.Engine.BeginAction())
            await session.Host.Match.PlayerManager.DrawCard(session.Host.Viewer);
        session.Pump();
        Assert.False(session.Host.HistoricalStatesVerified);
    }

    [Fact]
    public void UiSourcesAndContractAssembliesKeepTheEngineBoundary()
    {
        var root = FindRoot();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "proto", "ui"), "*.cs", SearchOption.AllDirectories))
        {
            var code = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path),
                @"(?s)/\*.*?\*/|(?m)//.*$|@?""(?:""""|\\.|[^""])*""", "");
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(code, @"\bOrc\."), path);
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(code,
                @"\b(CardPoolCompiler|DeckBuilder)\b|OrcMatchSession\.Create(Async|ProbeAsync)\("), path);
        }
        // Orc.UI.Contracts 是中立的视图契约程序集（项目既有约定：零引擎依赖、proto/ui 经全局 using 消费），
        // 只有引擎运行时程序集才算越界。
        foreach (var assembly in new[] { typeof(UiMatchView).Assembly, typeof(PresentationPlayer).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a =>
                a.Name is not null && a.Name != "Orc.UI.Contracts"
                && (a.Name.StartsWith("Orc.", StringComparison.Ordinal) || a.Name == "Orc"));
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Project root not found.");
    }

    private static UiPresentationResolution Resolution(long sequence) => new("queue", [],
        new UiMatchView { MatchId = "queue", Turn = (int)sequence }) { Sequence = sequence };

    [Fact]
    public async Task PlaybackWaitsForTheEntirePreviousAction()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<long>();
        using var player = new PresentationPlayer("queue", async (r, _, _) =>
        {
            started.Add(r.Sequence);
            if (r.Sequence == 1) await gate.Task;
        });
        _ = player.Enqueue(Resolution(1), new());
        _ = player.Enqueue(Resolution(2), new());
        _ = player.Enqueue(Resolution(3) with { MatchId = "old-match" }, new());
        Assert.Equal(new long[] { 1 }, started);
        Assert.True(player.IsBusy);
        gate.SetResult();
        await player.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new long[] { 1, 2 }, started);
        Assert.False(player.IsBusy);
    }

    [Fact]
    public async Task DisposingPlaybackStopsQueuedActionsAndUnblocksCompletion()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<long>();
        CancellationToken running = default;
        var player = new PresentationPlayer("queue", (r, _, ct) =>
        {
            started.Add(r.Sequence); running = ct; return never.Task;
        });
        _ = player.Enqueue(Resolution(1), new());
        _ = player.Enqueue(Resolution(2), new());
        player.Dispose();
        await player.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(running.IsCancellationRequested);
        Assert.Equal(new long[] { 1 }, started);
        Assert.False(player.IsBusy);
    }

    [Fact]
    public async Task PlaybackFreezesNestedSourceAndActionListsBeforeWaiting()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tags = new List<string> { "original" };
        var playable = new List<string> { "card" };
        UiPresentationResolution? received = null;
        UiBattleActions? receivedActions = null;
        using var player = new PresentationPlayer("queue", async (r, a, _) =>
        {
            if (r.Sequence == 1) await gate.Task;
            else { received = r; receivedActions = a; }
        });
        _ = player.Enqueue(Resolution(1), new());
        var card = new UiCardView { Uid = "source", Definition = new UiCardDefinition { CardId = "test", Keywords = tags } };
        _ = player.Enqueue(Resolution(2) with { Steps = [new UiBoardImpactsPresentation([new(card, card, 1, card)])] },
            new() { PlayableUids = playable });
        tags.Clear(); playable.Clear(); gate.SetResult();
        await player.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("original", Assert.Single(Assert.Single(received!.Steps.OfType<UiBoardImpactsPresentation>()).Impacts).Source!.Definition.Keywords.Single());
        Assert.Equal("card", Assert.Single(receivedActions!.PlayableUids));
    }

    /// <summary>
    /// R11 把 <c>Hq.ApplyDamageAsync</c> 收成 internal（唯一调用方 MatchActionServices 也是 internal），
    /// 界面侧已没有公开的伤害门户。这里经反射驱动同一条数值路径，只为在测试里造出一次真实伤害。
    /// </summary>
    private static async Task DamageHqAsync(Orc.Game.Players.Hq hq, int amount)
    {
        var method = typeof(Orc.Game.Players.Hq).GetMethod("ApplyDamageAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Hq.ApplyDamageAsync 不在预期位置（引擎收口方式又变了）");
        await (Task)method.Invoke(hq, [amount, CancellationToken.None])!;
    }
}
