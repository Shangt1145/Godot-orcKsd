using System.Text;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

[Collection("Workshop")]
public sealed class DeskCosmeticNetworkTests
{
    [Fact]
    public async Task LocalModelAndTableclothDoNotBreakExistingRoomImageProtocol()
    {
        var root = Path.Combine(Path.GetTempPath(), "desk-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            OrcEngineHost.WarmUp();
            var workshop = new DiyWorkshopLibrary(Path.Combine(root, "packs.json"));
            var unit = new DiyCardDraft { Name = "外观通道测试", Cost = 0 };
            WorkshopAuthoring.Configure(workshop, [unit]);
            var content = NetworkWorkshopSession.Prepare(Path.Combine(root, "content"), [], [unit], [], [unit.Id]);
            var deck = content.MapDeck(Enumerable.Repeat(unit.Id, 39).ToArray());
            var owner = new CosmeticLibrary(Path.Combine(root, "owner"));
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
            var cloth = owner.Import("tablecloth", "桌布", png);
            var json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"}} ");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(0x46546C67); writer.Write(2); writer.Write(20 + json.Length); writer.Write(json.Length); writer.Write(0x4E4F534A); writer.Write(json);
            var model = owner.ImportModel("本地模型", stream.ToArray(), png);
            owner.Save(new() { Tablecloth = cloth.Image, Decoration = model.Image, Prop = model.Prop! });
            var peer = new CosmeticLibrary(Path.Combine(root, "peer"));
            await using var server = await ServerLauncher.StartAsync(content.Roots, Path.Combine(root, "server"), fixedDeckForVerification: true, inProcess: true);
            await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
            await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
            server.Configure(host); host.ConfigureCosmetics(owner); guest.ConfigureCosmetics(peer);
            foreach (var seat in new[] { (Feed: host, Seat: 0), (Feed: guest, Seat: 1) })
            {
                var connection = await seat.Feed.ClaimSeatAsync(seat.Seat, seat.Seat == 0 ? "local" : "remote", "外观测试", deck);
                Assert.NotNull(connection);
                var admitted = await server.AdmitAsync(connection!); Assert.True(admitted.Ok, admitted.Message);
                Assert.True(await seat.Feed.WaitForAdmissionAsync());
            }
            await host.PublishCosmeticsAsync(); await guest.RefreshCosmeticsAsync();
            Assert.Equal(model.Image, guest.CosmeticProfiles[0].Decoration);
            Assert.True(peer.HasImage(model.Image)); Assert.Empty(guest.CosmeticProfiles[0].Tablecloth);
            Assert.Empty(guest.CosmeticProfiles[0].Prop.Model); Assert.Equal(model.Prop!.Model, host.LocalCosmetics.Prop.Model);
            Assert.Equal(cloth.Image, host.LocalCosmetics.Tablecloth);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
