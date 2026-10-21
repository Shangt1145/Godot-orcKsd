using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

[Collection("Workshop")]
public sealed class IdleTurnDeliveryTests
{
    private static async Task<OrcMatchSession> Ready(int first = 0)
    {
        OrcEngineHost.WarmUp(@"H:\Working Folder\OrC-KSD.Godot\proto\data\kards");
        var session = await OrcMatchSession.CreateDefaultProbeAsync("idle-infantry", 30, 20261021,
            "idle-" + Guid.NewGuid().ToString("N"), firstPlayerIndex: first);
        await session.SettleMulliganAsync(false);
        session.SettleProjection();
        return session;
    }

    [Fact]
    public async Task PollDeliversTurnDrawWithoutAnotherCommandAndDoesNotReplayWhenTheScopeCloses()
    {
        using var session = await Ready();
        session.DriveOpponent = false;
        var resolutions = new List<UiPresentationResolution>();
        session.Host.PresentationReady += (resolution, _) => resolutions.Add(resolution);
        var scope = session.Host.Match.Engine.BeginAction();
        try
        {
            await session.Host.Match.EndTurn();
            session.Pump();
            Assert.Empty(resolutions.SelectMany(r => r.Steps).OfType<UiDrawPresentation>());
            session.PollEvents(); session.Pump();
            Assert.Single(resolutions.SelectMany(r => r.Steps).OfType<UiDrawPresentation>());
            Assert.Single(resolutions.SelectMany(r => r.Steps).OfType<UiTurnPresentation>());
        }
        finally { scope.Dispose(); }
        for (var i = 0; i < 5; i++) { session.PollEvents(); session.Pump(); }
        Assert.Single(resolutions.SelectMany(r => r.Steps).OfType<UiDrawPresentation>());
        Assert.Single(resolutions.SelectMany(r => r.Steps).OfType<UiTurnPresentation>());
        Assert.Equal(resolutions.Count, resolutions.Select(r => r.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task SecondPlayerOpeningStartsTheAiWithoutAnyViewerCommandAndSerializesDuplicateWakeups()
    {
        using var session = await Ready(first: 1);
        Assert.False(session.IsViewerOnTurn);
        var turn = session.View.Turn;
        var draws = new List<UiDrawPresentation>();
        session.Host.PresentationReady += (resolution, _) => draws.AddRange(resolution.Steps.OfType<UiDrawPresentation>());
        await Task.WhenAll(session.DriveOpponentIfNeededAsync(), session.DriveOpponentIfNeededAsync());
        session.PollEvents(); session.Pump();
        Assert.True(session.IsViewerOnTurn);
        Assert.Contains(draws, draw => draw.Side == "self");
        Assert.Equal(turn + 1, session.View.Turn);
        await session.DriveOpponentIfNeededAsync();
        Assert.Equal(turn + 1, session.View.Turn);
    }

    [Fact]
    public async Task ManualOpponentAndCancelledWakeupsDoNotAdvanceTheGame()
    {
        using var session = await Ready(first: 1);
        session.DriveOpponent = false;
        var turn = session.View.Turn;
        await session.DriveOpponentIfNeededAsync();
        Assert.False(session.IsViewerOnTurn);
        Assert.Equal(turn, session.View.Turn);
        session.DriveOpponent = true;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await session.DriveOpponentIfNeededAsync(cancelled.Token);
        Assert.Equal(turn, session.View.Turn);
    }
}
