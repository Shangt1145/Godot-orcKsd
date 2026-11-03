using System.Threading.Channels;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Core;
using Orc.Game.Targeting;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class SelectorMigrationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task NativeSessionDeliversConsecutiveStepsInOrder()
    {
        var seen = new List<string>();
        var manager = new TargeterManager(new OrcTargeterBridge(s =>
        {
            seen.Add(s.SelectorName);
            OrcTargeterBridge.AutoRespond(s);
        }));
        var result = await manager.RunAsync(async flow =>
        {
            var first = await flow.Step(SelectorTemplates.Option, new IdentifierSetParameter(["a", "b"], 1, 1));
            Assert.Equal("a", first.Value);
            var second = await flow.Step(SelectorTemplates.CardPickerList, new IdentifierSetParameter(["card-1"], 1, 1));
            Assert.Equal("card-1", Assert.Single(second.Value!));
            return TargeterResult.Ok();
        }).WaitAsync(Limit);
        Assert.True(result.IsOk);
        Assert.Equal(new[] { SelectorNames.Option, SelectorNames.CardPicker }, seen);
    }

    [Fact]
    public async Task AcceptedInvalidEventReentersTheSameNativeInstance()
    {
        var delivered = Channel.CreateUnbounded<ISelectorInstance>();
        var manager = new TargeterManager(new OrcTargeterBridge(s => delivered.Writer.TryWrite(s)));
        var pending = manager.RunAsync(async flow =>
        {
            var step = await flow.Step(SelectorTemplates.Option, new IdentifierSetParameter(["ok"], 1, 1));
            Assert.Equal(SelectorFailureReason.InvalidSelection, step.Failure);
            step = await flow.Retry<string>();
            Assert.Equal("ok", step.Value);
            return TargeterResult.Ok();
        });
        var first = await delivered.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        Assert.True(first.Submit(new PickEvent(identifiers: ["illegal"])));
        var retry = await delivered.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        Assert.Same(first, retry);
        Assert.Equal(first.Id, retry.Id);
        Assert.True(retry.Submit(new PickEvent(identifiers: ["ok"])));
        Assert.True((await pending.WaitAsync(Limit)).IsOk);
        Assert.False(retry.Submit(new CancelEvent()));
    }

    [Theory]
    [InlineData(false, SelectorStatus.Failed)]
    [InlineData(true, SelectorStatus.Cancelled)]
    public async Task EmptyGestureKeepsTheEnginesClickAndDragSemantics(bool drag, SelectorStatus expected)
    {
        var manager = new TargeterManager(new OrcTargeterBridge(s =>
            s.Submit(drag ? new DropEvent(null) : new PickEvent())));
        var result = await manager.RunAsync(async flow =>
        {
            var definition = new SingleReferenceSelector("probe", drag ? SelectorInteractionMode.Drag : SelectorInteractionMode.Click);
            var step = await flow.Step(definition, new ReferenceSetParameter([], 1, 1));
            Assert.Equal(expected, step.Status);
            return step.IsCancelled ? TargeterResult.Cancelled() : TargeterResult.FromSelectorFailure(step.Failure);
        }).WaitAsync(Limit);
        Assert.Equal(drag ? TargeterStatus.Cancelled : TargeterStatus.Failed, result.Status);
    }

    [Fact]
    public async Task ZeroMinimumMultiSelectAllowsAnEmptyAnswer()
    {
        var manager = new TargeterManager(new OrcTargeterBridge(s => s.Submit(new PickEvent())));
        var result = await manager.RunAsync(async flow =>
        {
            var step = await flow.Step(SelectorTemplates.Hand, new ReferenceSetParameter([], 0, 2));
            Assert.True(step.IsOk); Assert.Empty(step.Value!);
            return TargeterResult.Ok();
        }).WaitAsync(Limit);
        Assert.True(result.IsOk);
    }

    [Fact]
    public async Task PresenterFaultReleasesTheStepAndAllowsTheNextSession()
    {
        var calls = 0;
        var manager = new TargeterManager(new OrcTargeterBridge(s =>
        {
            if (++calls == 1) throw new InvalidOperationException("presenter failed");
            OrcTargeterBridge.AutoRespond(s);
        }));
        async Task<TargeterResult> Flow(ITargeterFlow flow)
        {
            var step = await flow.Step(SelectorTemplates.Option, new IdentifierSetParameter(["ok"], 1, 1));
            return step.IsCancelled ? TargeterResult.Cancelled() : TargeterResult.Ok();
        }
        var first = manager.RunAsync(Flow); var second = manager.RunAsync(Flow);
        Assert.True((await first.WaitAsync(Limit)).IsCancelled);
        Assert.True((await second.WaitAsync(Limit)).IsOk); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NativeMultiSelectUsesAllRequestedReferences()
    {
        using var session = await OrcMatchSession.CreateDefaultProbeAsync("probe", 20, 7, "native-multiple");
        var refs = session.Host.Match.Players[0].Hand.Take(2).Select(c => c.Ref).ToArray();
        var manager = new TargeterManager(new OrcTargeterBridge(s => s.Submit(new PickEvent(refs))));
        var result = await manager.RunAsync(async flow =>
        {
            var step = await flow.Step(SelectorTemplates.Hand, new ReferenceSetParameter(refs, 2, 2));
            Assert.True(step.IsOk); Assert.Equal(refs, step.Value);
            return TargeterResult.Ok();
        }).WaitAsync(Limit);
        Assert.True(result.IsOk);
    }

    private static async Task<OrcMatchSession> ChoiceOrder()
    {
        const string source = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
        var catalog = new CardCatalog(); catalog.Load(source);
        var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, 7, "ui-selector", "probe",
            openingHand: ["deran/command/_13"], sourceDirectory: source, firstPlayerIndex: 0);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        await session.Host.Match.ResourceManager.GainSlotsAsync(session.Host.Viewer, 11);
        await session.Host.Match.ResourceManager.GainPointsAsync(session.Host.Viewer, 12);
        session.SettleProjection(); return session;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UiRetryRejectsOldAnswersAndOnlyCommitsTheChosenBranch(bool cancelRetry)
    {
        using var session = await ChoiceOrder();
        var requests = Channel.CreateUnbounded<(UiTargetRequest Request, IUiTargetResponder Responder)>();
        session.TargetRequested += (request, responder) => requests.Writer.TryWrite((request, responder));
        var order = session.View.SelfHand.Single(); var points = session.View.SelfKredits;
        var hp = session.View.SelfHq!.Health; var deck = session.View.SelfDeckCount;
        var action = session.SubmitAsync(new PlayCard(order.Uid));
        var first = await requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(SelectorNames.Option, first.Request.SelectorName);
        Assert.False(first.Request.IsDrag);
        Assert.True(first.Responder.Complete(first.Request.RequestId,
            new Dictionary<string, IReadOnlyList<string>> { ["choice"] = ["illegal"] }));
        var retry = await requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        Assert.NotEqual(first.Request.RequestId, retry.Request.RequestId);
        Assert.False(first.Responder.Complete(first.Request.RequestId,
            new Dictionary<string, IReadOnlyList<string>> { ["choice"] = ["0"] }));
        Assert.False(first.Responder.Cancel(first.Request.RequestId));
        Assert.True(session.TargetPending); Assert.Equal(points, session.View.SelfKredits);
        if (cancelRetry) Assert.True(retry.Responder.Cancel(retry.Request.RequestId));
        else Assert.True(retry.Responder.Complete(retry.Request.RequestId,
            new Dictionary<string, IReadOnlyList<string>> { ["choice"] = ["0"] }));
        Assert.Equal(cancelRetry ? UiSubmitOutcome.Rejected : UiSubmitOutcome.Applied, await action.WaitAsync(Limit));
        Assert.False(session.TargetPending);
        Assert.Equal(cancelRetry ? points : points - order.Definition.Cost, session.View.SelfKredits);
        Assert.Equal(cancelRetry ? hp : hp + 7, session.View.SelfHq!.Health);
        Assert.Equal(deck, session.View.SelfDeckCount);
        Assert.Equal(cancelRetry, session.View.SelfHand.Any(c => c.Uid == order.Uid));
    }

    // ── 选项请求（引擎 R38 的 choice）：AI 与快照两条路径 ─────────────────────

    [Fact]
    public async Task AiOptionPicksAreOneBasedIdentifiers()
    {
        // 标识集的序号是 **1 起**（与 target.submit 同口径，R38 Q6），不是候选下标。
        // 以前 SubmitAiPick 只认 candidates，选项请求恒判空 ⇒ 取消 ⇒ 「AI 死活不出带 choice 的牌」。
        var gate = new TaskCompletionSource<ISelectorInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new OrcTargeterBridge(s => gate.TrySetResult(s));
        var manager = new TargeterManager(bridge);
        string? picked = null;
        var pending = manager.RunAsync(async flow =>
        {
            var step = await flow.Step(SelectorTemplates.Option, new IdentifierSetParameter(["抽 1 张", "抽 2 张"], 1, 1));
            picked = step.Value;
            return step.IsOk ? TargeterResult.Ok() : TargeterResult.FromSelectorFailure(step.Failure);
        });
        var selector = await gate.Task.WaitAsync(Limit);
        Assert.Empty(selector.Presentation.Candidates);
        Assert.True(bridge.SubmitAiPick(selector, [2], false));
        Assert.True((await pending.WaitAsync(Limit)).IsOk);
        Assert.Equal("抽 2 张", picked);
    }

    [Fact]
    public async Task AiOutOfRangeOptionCancelsInsteadOfPickingTheFirst()
    {
        // 越界不许静默改成第 1 项——那等于替 AI 做决定，而 switch 没有 default，选错就是错分支。
        var gate = new TaskCompletionSource<ISelectorInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new OrcTargeterBridge(s => gate.TrySetResult(s));
        var manager = new TargeterManager(bridge);
        var pending = manager.RunAsync(async flow =>
        {
            var step = await flow.Step(SelectorTemplates.Option, new IdentifierSetParameter(["甲", "乙"], 1, 1));
            return step.IsCancelled ? TargeterResult.Cancelled() : TargeterResult.Ok();
        });
        var selector = await gate.Task.WaitAsync(Limit);
        Assert.False(bridge.SubmitAiPick(selector, [99], false));
        Assert.True((await pending.WaitAsync(Limit)).IsCancelled);
    }

    [Fact]
    public void PromptSurvivesTheSnapshotFreeze()
    {
        // UiSnapshots.Freeze 是 record with：新加的字段必须跟着过去，否则界面拿到的是空串。
        var request = new UiTargetRequest("r", []) { Prompt = "选一项" };
        Assert.Equal("选一项", UiSnapshots.Freeze(request).Prompt);
    }
}
