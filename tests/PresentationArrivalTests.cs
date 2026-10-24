using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Core;
using Orc.Game;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class PresentationArrivalTests
{
    [Fact]
    public async Task FirstSnapshotArrivalOwnsTheDrawAndLaterSignalCannotReplayIt()
    {
        using var session = await OrcMatchSession.CreateDefaultProbeAsync("arrival-probe", 20, 31, "arrivals", firstPlayerIndex: 0);
        var viewer = session.Host.Viewer;
        var native = viewer.Hand.First(); var uid = OrcRefs.KeyOf(native);
        var incoming = session.View.SelfHand.Single(c => c.Uid == uid);
        var before = session.View with { SelfHand = session.View.SelfHand.Where(c => c.Uid != uid).ToArray() };
        var translator = new OrcUpdateTranslator((_, _, _, _) => incoming);
        var first = translator.Translate([], viewer, before, session.View);
        Assert.Equal(uid, Assert.Single(first.Steps.OfType<UiDrawPresentation>()).Card!.Uid);
        var payload = new Dictionary<string, object?> { [GameUpdates.PayloadCard] = native, [GameUpdates.PayloadPlayer] = viewer };
        var later = translator.Translate([(GameUpdates.CardDrawn, payload), (GameUpdates.CardHandAdd, payload)], viewer, session.View, session.View);
        Assert.Empty(later.Steps.OfType<UiDrawPresentation>());
    }

    [Fact]
    public async Task TurnBannerDoesNotExposeTheArrivingCardBeforeItsDrawBeat()
    {
        var card = new UiCardView { Uid = "new", Zone = "hand", OwnerSide = "self", Definition = new() { CardId = "new" } };
        var before = new UiMatchView { MatchId = "arrivals", Phase = "play", ActivePlayerSide = "self" };
        var after = before with { SelfHand = [card], SelfHandCount = 1 };
        var played = new List<UiPresentationResolution>();
        using var player = new PresentationPlayer("arrivals", (resolution, _, _) => { played.Add(resolution); return Task.CompletedTask; }, before);
        await player.Enqueue(new("arrivals", [new UiTurnPresentation("self", 2, UiTurnKind.TurnStarted), new UiDrawPresentation("self", 0, card)], after)
            { Before = before }, new());
        Assert.Empty(played[0].After.SelfHand);
        Assert.Equal("new", Assert.Single(played[^1].After.SelfHand).Uid);
    }

    [Fact]
    public async Task IndependentStatusUpdatesAndRemovalsAreBatchedButRepeatedActorChangesStayOrdered()
    {
        UiCardView Card(string uid) => new() { Uid = uid, Zone = "support", OwnerSide = "self", Definition = new() { CardId = uid } };
        var a = Card("a"); var b = Card("b");
        var view = new UiMatchView { MatchId = "batch", SelfLine = [a, b] };
        var played = new List<UiPresentationResolution>();
        using var player = new PresentationPlayer("batch", (resolution, _, _) => { played.Add(resolution); return Task.CompletedTask; }, view);
        await player.Enqueue(new("batch", [new UiStatusPresentation(a, a, UiStatusKind.Suppressed), new UiStatusPresentation(b, b, UiStatusKind.Suppressed),
            new UiStatusPresentation(a, a, UiStatusKind.Cleared), new UiRemovalPresentation(a), new UiRemovalPresentation(b)], view) { Before = view }, new());
        Assert.Equal(new[] { 2, 1, 2 }, played.Select(r => r.Steps.Count));
    }
}
