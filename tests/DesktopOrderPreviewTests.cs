using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

[Collection("Workshop")]
public sealed class DesktopOrderPreviewTests
{
    [Fact]
    public async Task DeclaredOrderPreviewsWithoutPlayingAndResolvesTheDraggedTarget()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Kards.Ui.csproj"))) root = Path.GetDirectoryName(root)!;
        var nations = Path.Combine(root, "proto/data/nations");
        var catalog = new CardCatalog(); catalog.Load(nations);
        var unit = new DiyCardDraft { Name = "指向对象", Cost = 0, Attack = 1, Defense = 3 };
        var order = new DiyCardDraft { Name = "声明式指向", CardType = "order", Cost = 0, OrcEffects = [new() { DesktopJson = """
            {"trigger":"play","targets":[{"id":"chosen","kind":"unit","side":"friendly","zone":"support"}],
             "actions":[{"op":"changeAttribute","target":{"sel":"ref","ref":"chosen"},"property":"attack","mode":"add","value":2}]}
            """ }] };
        using var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, 391, "order-preview", "unused",
            sourceDirectory: nations, customCards: [unit, order], openingHand: [unit.Id, order.Id], firstPlayerIndex: 0);
        session.DriveOpponent = false;
        await session.SettleMulliganAsync(false);
        var requests = 0;
        session.TargetRequested += (request, responder) => { requests++; responder.Cancel(request.RequestId); };
        var host = session.View.SelfHand.Single(c => c.CardId == unit.Id);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(host.Uid)));
        session.SettleProjection();
        var command = session.View.SelfHand.Single(c => c.CardId == order.Id);
        for (var i = 0; i < 5; i++)
        {
            session.SettleProjection();
            var preview = Assert.Single(session.Actions.OrderTargets, p => p.Uid == command.Uid);
            Assert.Equal([host.Uid], preview.TargetUids);
            Assert.Equal(1, session.View.SelfLine.Single(c => c.Uid == host.Uid).EffectiveAttack);
            Assert.Contains(session.View.SelfHand, c => c.Uid == command.Uid);
            Assert.Equal(0, requests);
        }
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(command.Uid, TargetUid: host.Uid)));
        session.SettleProjection();
        Assert.Equal(3, session.View.SelfLine.Single(c => c.Uid == host.Uid).EffectiveAttack);
        Assert.DoesNotContain(session.View.SelfHand, c => c.Uid == command.Uid);
        Assert.Equal(0, requests);
    }
}
