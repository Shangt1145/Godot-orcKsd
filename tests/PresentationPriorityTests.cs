using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class PresentationPriorityTests
{
    private static UiCardView Card(string uid, string zone = "hand") => new()
    {
        Uid = uid, OwnerSide = "self", Zone = zone, Health = 3,
        Definition = new() { CardId = uid, CardType = "unit" }
    };
    private static UiMatchView View(params UiCardView[] cards) => new()
    {
        MatchId = "priority", Phase = "play", ActivePlayerSide = "self", SelfKredits = 8,
        SelfHand = cards.Where(c => c.Zone == "hand").ToArray(),
        SelfHandCount = cards.Count(c => c.Zone == "hand"),
        SelfLine = cards.Where(c => c.Zone != "hand").ToArray()
    };
    private static UiPresentationResolution Resolution(long sequence, UiMatchView after,
        UiPresentationPriority priority = UiPresentationPriority.Indirect, params UiPresentationStep[] steps) =>
        new(after.MatchId, steps, after) { Sequence = sequence, Priority = priority };

    [Fact]
    public async Task DirectLaneOvertakesWaitingFeedbackAndBothLanesRemainFifo()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new List<long>();
        var view = View();
        using var player = new PresentationPlayer(view.MatchId, async (r, _, _) =>
        {
            played.Add(r.Sequence);
            if (r.Sequence == 1) await gate.Task;
        }, view);
        _ = player.Enqueue(Resolution(1, view), new());
        _ = player.Enqueue(Resolution(2, view), new());
        _ = player.Enqueue(Resolution(3, view, UiPresentationPriority.Direct), new());
        _ = player.Enqueue(Resolution(4, view), new());
        _ = player.Enqueue(Resolution(5, view, UiPresentationPriority.Direct), new());
        Assert.Equal(new long[] { 1 }, played); // An in-flight beat is never cut off.
        gate.SetResult();
        await player.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new long[] { 1, 3, 5, 2, 4 }, played);
    }

    [Fact]
    public async Task DelayedDrawCannotRestorePlayedCardOrOldResources()
    {
        var a = Card("a"); var b = Card("b") with { SlotIndex = 1 };
        var initial = View(a);
        var drawn = View(a, b) with { SelfDeckCount = 9 };
        var deployed = View(a with { Zone = "support" }, b with { SlotIndex = 0 })
            with { SelfKredits = 7, SelfDeckCount = 9 };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new List<UiPresentationResolution>();
        using var player = new PresentationPlayer(initial.MatchId, async (r, _, _) =>
        {
            played.Add(r);
            if (r.Sequence == 1) await gate.Task;
        }, initial);
        _ = player.Enqueue(Resolution(1, initial), new());
        _ = player.Enqueue(Resolution(2, drawn, steps: new UiDrawPresentation("self", 1, b)), new());
        _ = player.Enqueue(Resolution(3, deployed, steps: new UiDeploymentPresentation(deployed.SelfLine[0], deployed)), new());
        gate.SetResult(); await player.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new long[] { 1, 3, 2 }, played.Select(r => r.Sequence));
        Assert.Equal("a", Assert.Single(played[^1].After.SelfLine).Uid);
        Assert.Equal("b", Assert.Single(played[^1].After.SelfHand).Uid);
        Assert.Equal(7, played[^1].After.SelfKredits);
        Assert.Equal(0, Assert.IsType<UiDrawPresentation>(Assert.Single(played[^1].Steps)).SlotIndex);
    }

    [Fact]
    public async Task EffectDrawAndDeathTailYieldToTheNextDeployment()
    {
        var a = Card("a"); var b = Card("b") with { SlotIndex = 1 };
        var c = Card("c") with { SlotIndex = 1 };
        var victim = Card("victim", "support") with { OwnerSide = "enemy" };
        var initial = View(a, b) with { EnemyLine = [victim] };
        var first = View(a with { Zone = "support" }, b, c);
        var second = View(a with { Zone = "support" }, b with { Zone = "support", SlotIndex = 1 }, c with { SlotIndex = 0 });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new List<UiPresentationResolution>();
        using var player = new PresentationPlayer(initial.MatchId, async (r, _, _) =>
        {
            played.Add(r);
            if (r.Sequence == 1) await gate.Task;
        }, initial);
        _ = player.Enqueue(Resolution(1, first, steps:
            [new UiDeploymentPresentation(first.SelfLine[0], first),
             new UiBoardImpactsPresentation([new(victim, null, 3, a)]), new UiDrawPresentation("self", 1, c)]), new());
        _ = player.Enqueue(Resolution(2, second, steps: new UiDeploymentPresentation(second.SelfLine[1], second)), new());
        Assert.Single(played);
        Assert.DoesNotContain(played[0].Steps, s => s is UiDrawPresentation or UiRemovalPresentation);
        Assert.DoesNotContain(played[0].After.SelfHand, card => card.Uid == c.Uid);
        Assert.Equal(0, Assert.Single(played[0].After.EnemyLine).Health);
        gate.SetResult(); await player.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new long[] { 1, 2, 1, 1 }, played.Select(r => r.Sequence));
        Assert.Equal(new[] { typeof(UiRemovalPresentation), typeof(UiDrawPresentation) }, played.Skip(2).SelectMany(r => r.Steps).Select(s => s.GetType()));
        Assert.Empty(played[^1].After.EnemyLine);
        Assert.Equal(2, played[^1].After.SelfLine.Count);
        Assert.Equal("c", Assert.Single(played[^1].After.SelfHand).Uid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovementIsDirectWithNoStepsOrWithOnlyTheEngineResourceChange(bool resourceStep)
    {
        var unit = Card("unit", "support"); var initial = View(unit);
        var moved = View(unit with { Zone = "frontline", SlotIndex = 2 });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new List<long>();
        using var player = new PresentationPlayer(initial.MatchId, async (r, _, _) =>
        { played.Add(r.Sequence); if (r.Sequence == 1) await gate.Task; }, initial);
        _ = player.Enqueue(Resolution(1, initial), new());
        _ = player.Enqueue(Resolution(2, initial), new());
        _ = player.Enqueue(Resolution(3, moved, steps: resourceStep
            ? [new UiResourcePresentation("self", UiResourceCause.Gain, 8, 7)] : []), new());
        gate.SetResult(); await player.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new long[] { 1, 3, 2 }, played);
    }

    [Fact]
    public async Task DirectActionCanOvertakeTheRemainingCardsOfARunningDrawBatch()
    {
        var a = Card("a"); var b = Card("b") with { SlotIndex = 1 };
        var c = Card("c") with { SlotIndex = 2 };
        var initial = View(a); var drawn = View(a, b, c);
        var deployed = View(a with { Zone = "support" }, b with { SlotIndex = 0 }, c with { SlotIndex = 1 });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new List<UiPresentationResolution>();
        using var player = new PresentationPlayer(initial.MatchId, async (r, _, _) =>
        { played.Add(r); if (played.Count == 1) await gate.Task; }, initial);
        _ = player.Enqueue(Resolution(1, drawn, steps:
            [new UiDrawPresentation("self", 1, b), new UiDrawPresentation("self", 2, c)]), new());
        _ = player.Enqueue(Resolution(2, deployed, steps: new UiDeploymentPresentation(deployed.SelfLine[0], deployed)), new());
        Assert.Equal(2, played[0].After.SelfHand.Count);
        gate.SetResult(); await player.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new long[] { 1, 2, 1 }, played.Select(r => r.Sequence));
        Assert.DoesNotContain(played[1].After.SelfHand, card => card.Uid == "c");
        Assert.Equal("b", Assert.IsType<UiDrawPresentation>(Assert.Single(played[0].Steps)).Card!.Uid);
        var last = Assert.IsType<UiDrawPresentation>(Assert.Single(played[^1].Steps));
        Assert.Equal("c", last.Card!.Uid); Assert.True(last.Consecutive);
        Assert.Equal(1, last.SlotIndex);
        Assert.Equal("a", Assert.Single(played[^1].After.SelfLine).Uid);
    }
}
