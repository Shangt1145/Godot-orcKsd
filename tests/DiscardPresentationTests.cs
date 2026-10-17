using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class DiscardPresentationTests
{
    [Fact]
    public async Task BurnIsPresentedOncePerPlayerWithoutExposingOpponentIdentity()
    {
        var fixture = await OrcDiscardVerification.RunAsync(burn: true);
        var burns = fixture.Resolutions.SelectMany(r => r.Steps).OfType<UiDiscardPresentation>().ToArray();
        Assert.Equal(2, burns.Length);
        Assert.All(burns, b => Assert.Equal(UiDiscardKind.Burn, b.Kind));
        Assert.NotNull(Assert.Single(burns, b => b.Side == "self").Card);
        Assert.Null(Assert.Single(burns, b => b.Side == "enemy").Card);
        Assert.Equal(9, fixture.Final.SelfHand.Count);
    }
    [Fact]
    public async Task NativeDiscardsKeepOwnPaperSnapshotAndKeepOpponentPaperAnonymous()
    {
        var fixture = await OrcDiscardVerification.RunAsync();
        var discards = fixture.Resolutions.SelectMany(r => r.Steps).OfType<UiDiscardPresentation>().ToArray();
        Assert.True(discards.Length == 2, $"resolutions={fixture.Resolutions.Count} steps={string.Join(',',fixture.Resolutions.SelectMany(r=>r.Steps).Select(s=>s.GetType().Name))} hand={fixture.Initial.SelfHand.Count}->{fixture.Final.SelfHand.Count}");
        var own = Assert.Single(discards, d => d.Side == "self");
        Assert.NotNull(own.Card);
        Assert.Equal(fixture.Initial.SelfHand[0].Uid, own.Card.Uid);
        Assert.Equal(fixture.Initial.SelfHand[0].Definition.Name, own.Card.Definition.Name);
        Assert.Null(Assert.Single(discards, d => d.Side == "enemy").Card);
        Assert.Equal(fixture.Initial.SelfHand.Count - 1, fixture.Final.SelfHand.Count);
        Assert.Equal(fixture.Initial.EnemyHandCount - 1, fixture.Final.EnemyHandCount);
    }
}
