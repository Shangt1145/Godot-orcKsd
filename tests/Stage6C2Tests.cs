using System.Threading.Channels;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Targeting;
using Orc.Game.Triggers;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class Stage6C2Tests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task SingleHumanMulliganRejectsUnknownDuplicateAndStaleAnswers()
    {
        // 起手张数随先手变化（先手 4／后手 5）；这条用例断的是 4，必须固定先手。
        using var session = await OrcMatchSession.CreateDefaultProbeAsync("probe", 20, 17, "single-mulligan", firstPlayerIndex: 0);
        await session.SettleMulliganAsync(true);
        var opening = session.View; var token = session.MulliganRequestId;
        Assert.True(session.MulliganPending); Assert.Equal(4, opening.SelfHand.Count);
        Assert.Equal(UiSubmitOutcome.Rejected, await session.SubmitAsync(new ChooseMulligan([], "old-request")));
        Assert.Equal(UiSubmitOutcome.Rejected, await session.SubmitAsync(new ChooseMulligan(["unknown"], token)));
        var uid = opening.SelfHand[0].Uid;
        Assert.Equal(UiSubmitOutcome.Rejected, await session.SubmitAsync(new ChooseMulligan([uid, uid], token)));
        Assert.True(session.MulliganPending);
        var keep = opening.SelfHand.Skip(1).Select(c => c.Uid).ToArray();
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new ChooseMulligan(keep, token)).WaitAsync(Limit));
        session.SettleProjection();
        Assert.Equal("play", session.View.Phase); Assert.Equal(4, session.View.SelfHand.Count);
        Assert.Equal(opening.SelfDeckCount, session.View.SelfDeckCount);
        Assert.All(keep, id => Assert.Contains(session.View.SelfHand, c => c.Uid == id));
        Assert.Equal(UiSubmitOutcome.Rejected, await session.SubmitAsync(new ChooseMulligan([], token)));
    }

    [Fact]
    public async Task DisposingAParkedHumanMulliganReleasesTheNativeFlow()
    {
        var session = await OrcMatchSession.CreateDefaultProbeAsync("probe", 20, 3, "exit-mulligan");
        await session.SettleMulliganAsync(true);
        session.Dispose();
        Assert.False(session.MulliganPending);
        Assert.Equal(UiSubmitOutcome.Interrupted, await session.SubmitAsync(new ChooseMulligan([])));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NativeCommandForwardsTheWholeSelectionAndDefinitionFaces(bool empty, bool cancel)
    {
        CardDefinition Definition(string name, string category) => new(name, 0, 0, 1, 2, category: category,
            faction: Faction.Germany, rarity: Rarity.Standard);
        var pool = new[] { new CardDefinitionEntry("order", Definition("选择指令", CardCategory.Command)),
            new CardDefinitionEntry("candidate", Definition("引擎候选单位", CardCategory.Unit)) };
        var deck = Enumerable.Repeat("order", 20).ToArray();
        using var session = await OrcMatchSession.CreateAsync(pool, deck, deck, 4, "multi-command", firstPlayerIndex: 0);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        var requests = Channel.CreateUnbounded<(UiTargetRequest Request, IUiTargetResponder Responder)>();
        session.TargetRequested += (request, responder) => requests.Writer.TryWrite((request, responder));
        var order = (OrderCard)session.Host.Viewer.Hand[0];
        var refs = session.Host.Viewer.Hand.Skip(1).Take(2).Select(c => c.Ref).ToArray();
        var effectRan = false;
        order.AddPrePlayHandler("test/native-multi", async (view, ctx, ct) =>
        {
            var result = await session.Host.Match.TargeterManager.RunAsync(async flow =>
            {
                var hand = await flow.Step(SelectorTemplates.Hand, new ReferenceSetParameter(refs, 0, 2));
                if (hand.IsCancelled) return TargeterResult.Cancelled();
                Assert.True(hand.IsOk); Assert.Equal(empty ? 0 : 2, hand.Value!.Count);
                if (!empty) Assert.Equal(refs, hand.Value);
                var cards = await flow.Step(SelectorTemplates.CardPickerList,
                    new IdentifierSetParameter(["candidate"], 1, 1));
                if (cards.IsCancelled) return TargeterResult.Cancelled();
                Assert.Equal("candidate", Assert.Single(cards.Value!));
                return TargeterResult.Ok();
            });
            if (result.IsOk) view.CaptureBox!.Capture("chosen"); else view.CaptureBox!.CancelPrePlay();
        });
        // S13：引擎删除 AddActiveHandler，主动效果改由 ActiveEffect 承载（与 SourceEffectAssembly 同一迁移）。
        // 施放载荷只有 Source/Target/Amount（OrderFlowView），捕获的字符串选择值不再回传。
        order.AddEffect(new TestCast(view =>
        {
            Assert.Same(order, view.Source); effectRan = true; return Task.CompletedTask;
        }));
        Assert.Single(order.Effects.OfType<ICastAction>()); // 二分：是没装上，还是装上没执行
        var uid = session.View.SelfHand[0].Uid;
        var action = session.SubmitAsync(new PlayCard(uid));
        var first = await requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(SelectorNames.Hand, first.Request.SelectorName);
        Assert.All(first.Request.Slots[0].Allowed, c => Assert.NotNull(c.Card));
        session.SelectViewer(1); Assert.Same(order.Owner, session.Host.Viewer);
        if (cancel) Assert.True(first.Responder.Cancel(first.Request.RequestId));
        else
        {
            var ids = empty ? [] : first.Request.Slots[0].Allowed.Select(c => c.Id).ToArray();
            Assert.True(first.Responder.Complete(first.Request.RequestId,
                new Dictionary<string, IReadOnlyList<string>> { ["default"] = ids }));
            var second = await requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
            var candidate = Assert.Single(second.Request.Slots[0].Allowed);
            Assert.Equal(ChoiceKind.CardDefinition, candidate.Kind);
            Assert.Equal("candidate", candidate.Card!.CardId); Assert.Equal("引擎候选单位", candidate.Label);
            Assert.Equal(Visibility.Full, candidate.Card.Visibility);
            Assert.False(first.Responder.Cancel(first.Request.RequestId));
            Assert.True(second.Responder.Complete(second.Request.RequestId,
                new Dictionary<string, IReadOnlyList<string>> { ["default"] = ["candidate"] }));
        }
        Assert.Equal(cancel ? UiSubmitOutcome.Rejected : UiSubmitOutcome.Applied, await action.WaitAsync(Limit));
        Assert.Equal(!cancel, effectRan); Assert.False(session.TargetPending);
        Assert.Equal(cancel, session.View.SelfHand.Any(c => c.Uid == uid));
        Assert.DoesNotContain(session.Host.Viewer.Hand.OfType<CardBase>(), c => c.Definition.Name == "引擎候选单位");
    }

    /// <summary>测试用主动效果：引擎的 OrderCard 已不接受 AddActiveHandler，主动效果必须装成 ActiveEffect。</summary>
    private sealed class TestCast : ActiveEffect<OrderCastView>
    {
        public TestCast(Func<OrderCastView, Task> body)
            : base("test/native-effect", castEvents:
            [
                new TriggerEvent<OrderCastView>("test/native-effect", (view, ctx, ct) => body(view))
            ])
        { }
    }
}
