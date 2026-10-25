using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

[Collection("Workshop")]
public sealed class NetworkWorkshopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ksd-network-workshop-" + Guid.NewGuid().ToString("N"));
    public NetworkWorkshopTests() { Directory.CreateDirectory(_root); OrcEngineHost.WarmUp(); }
    private (DiyPackDraft Base, DiyPackDraft Extension, DiyCardDraft Unit, DiyCardDraft Order) Content()
    {
        var library = new DiyWorkshopLibrary(Path.Combine(_root, "packs.json"));
        var pack = new DiyPackDraft { Name = "联机基础包" }; library.Save(pack);
        var expansion = new DiyPackDraft { Name = "联机扩展包", BasePackId = pack.Id }; library.Save(expansion);
        var unit = new DiyCardDraft { PackId = pack.Id, Name = "同步步兵", Cost = 0, OpCost = 0 };
        var order = new DiyCardDraft { PackId = expansion.Id, Name = "同步炮击", Cost = 0, CardType = "order", EffectText = "造成3点总部伤害，获得1张同步步兵。", OrcEffects = [new() { Template = "played_basic", Guard = "SelfCard", Ops = [new() { Op = "damage", Sel = "all", SelZone = "hq", Side = "enemy", Amount = 3 }, new() { Op = "addToHand", Name = unit.Name, Count = 1 }] }] };
        WorkshopAuthoring.Configure(library, [unit, order]);
        return (pack, expansion, unit, order);
    }
    private NetworkWorkshopSession Prepare(IReadOnlyList<DiyCardDraft> cards, IReadOnlyList<string> deck, string name)
        => NetworkWorkshopSession.Prepare(Path.Combine(_root, name), [], cards, [], deck);
    private static JsonObject Policy(bool allow) => new() { ["allowGuestPacks"] = allow, ["requireAlignment"] = true };
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(30, timeout.Token);
    }
    private static async Task Admit(ServerLauncher server, RemoteMatchFeed feed, int seat, IReadOnlyList<string> deck)
    {
        var connection = await feed.ClaimSeatAsync(seat, "remote", "测试玩家", deck);
        Assert.NotNull(connection);
        var admission = await server.AdmitAsync(connection!); Assert.True(admission.Ok, admission.Message);
        await Until(() => feed.AdmissionState == "admitted");
    }
    private static async Task Align(RemoteMatchFeed host, RemoteMatchFeed guest)
    {
        for (int i = 0; i < 4; i++) { await guest.SynchronizePacksAsync(); await host.SynchronizePacksAsync(); }
        Assert.True(host.PacksAligned, host.PackSyncStatus); Assert.True(guest.PacksAligned, guest.PackSyncStatus);
    }

    [Fact]
    public void DiyAndExpansionFreezeWithStableIdsAndDoNotChangeAuthoringFiles()
    {
        var (_, _, unit, order) = Content();
        var before = File.ReadAllText(Path.Combine(_root, "packs.json"));
        var first = Prepare([unit, order], [order.Id], "one");
        var second = Prepare([unit, order], [order.Id], "two");
        Assert.Equal(first.MapDeck([unit.Id, order.Id]), second.MapDeck([unit.Id, order.Id]));
        Assert.Equal(2, first.Roots.Count);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "packs.json")));
        Assert.All(first.Roots, root => Assert.True(File.Exists(Path.Combine(root, "manifest.json"))));
        foreach (var root in first.Roots)
        {
            var manifest = Orc.Packages.PackageManifest.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
            var registration = Orc.Packages.PackageRegistrar.Register(root, manifest, new Orc.Packages.AssetRegistry());
            Assert.True(registration.Content.Definitions.Count == 1, string.Join(";", registration.Diagnostics.Select(d => d.Message)));
        }
    }

    [Fact]
    public async Task GuestOnlyExpansionRequiresHostOptInThenSyncsAndExecutesInRealServer()
    {
        var (_, _, unit, order) = Content();
        var hostSession = Prepare([unit], [unit.Id], "host");
        var guestSession = Prepare([unit, order], [order.Id], "guest");
        var hostDeck = hostSession.MapDeck(Enumerable.Repeat(unit.Id, 39).ToArray());
        var guestDeck = guestSession.MapDeck(Enumerable.Repeat(order.Id, 39).ToArray());
        await using var server = await ServerLauncher.StartAsync(hostSession.Roots, Path.Combine(_root, "server"), fixedDeckForVerification: true);
        Assert.True((await server.AdminAsync("pool.transfer.policy", Policy(false))).Ok);
        await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
        await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
        host.ConfigurePackTransfer(hostSession, hostDeck); guest.ConfigurePackTransfer(guestSession, guestDeck); server.Configure(host);
        await Admit(server, host, 0, hostDeck); await Admit(server, guest, 1, guestDeck);
        await guest.SynchronizePacksAsync();
        Assert.False(guest.PacksAligned); Assert.Contains("接收好友卡包", guest.PackSyncStatus);
        var rejected = await server.AdminAsync("match.start"); Assert.False(rejected.Ok); Assert.Equal("pool-mismatch", rejected.Code);
        Assert.True((await server.AdminAsync("pool.transfer.policy", Policy(true))).Ok);
        await Align(host, guest);
        Assert.Single(hostSession.Roots); // Receiving never changes the local library.
        Assert.True(await host.StartMatchAsync());
        await host.RefreshViewAsync(); await guest.RefreshViewAsync();
        Assert.NotNull(host.CurrentView); Assert.NotNull(guest.CurrentView);
        Assert.True(await host.MulliganDoneAsync(0)); Assert.True(await guest.MulliganDoneAsync(1));
        await Until(() => guest.CurrentView?.Phase == "play");
        await host.RefreshViewAsync(); await guest.RefreshViewAsync();
        if (guest.CurrentView!.ActivePlayerSide != "self")
        { await host.SubmitAsync(new EndTurn()); await guest.RefreshViewAsync(); }
        var hq = guest.CurrentView!.EnemyHq!.Health;
        var card = guest.CurrentView.SelfHand.First();
        var errors = new List<string>(); guest.HintRequested += errors.Add; guest.ErrorRaised += errors.Add;
        await guest.SubmitAsync(new PlayCard(card.Uid)); await guest.RefreshViewAsync();
        Assert.True(guest.CurrentView.SelfHand.All(c => c.Uid != card.Uid), string.Join(";", errors) + "; phase=" + guest.CurrentView.Phase + "; turn=" + guest.CurrentView.ActivePlayerSide + "; type=" + card.Definition.CardType);
        Assert.Equal(hq - 3, guest.CurrentView!.EnemyHq!.Health);
        Assert.Contains(guest.CurrentView.SelfHand, c => c.CardId == guestSession.CardIds[unit.Id]);
        Assert.Equal(order.EffectText, card.Definition.Text);
        var token = guest.SeatToken!;
        await guest.DisposeAsync();
        await using var reconnected = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
        reconnected.ConfigurePackTransfer(guestSession, guestDeck);
        Assert.True(await reconnected.ResumeSeatAsync(token));
        await reconnected.SynchronizePacksAsync(); await reconnected.RefreshViewAsync();
        Assert.Equal(hq - 3, reconnected.CurrentView!.EnemyHq!.Health);
        await reconnected.SubmitAsync(new Concede());
        await Until(() => string.Equals(host.CurrentView?.ResultReason, "Concede", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SameIdentityWithDifferentRulesBlocksBeforeStart()
    {
        var (_, _, unit, _) = Content();
        var hostSession = Prepare([unit], [unit.Id], "host");
        var guestSession = Prepare([unit with { Attack = 9 }], [unit.Id], "guest");
        var deck = hostSession.MapDeck(Enumerable.Repeat(unit.Id, 39).ToArray());
        await using var server = await ServerLauncher.StartAsync(hostSession.Roots, Path.Combine(_root, "server"), fixedDeckForVerification: true);
        await server.AdminAsync("pool.transfer.policy", Policy(true));
        await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
        await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
        host.ConfigurePackTransfer(hostSession, deck); guest.ConfigurePackTransfer(guestSession, deck);
        await Admit(server, host, 0, deck); await Admit(server, guest, 1, deck);
        await guest.SynchronizePacksAsync(); Assert.False(guest.PacksAligned);
        Assert.Contains("同名卡包内容不一致", guest.PackSyncStatus);
        var start = await server.AdminAsync("match.start"); Assert.False(start.Ok); Assert.Equal("pool-mismatch", start.Code);
    }

    [Fact]
    public void SamePackageDoesNotDependOnOtherSelectedPackagesOrUnrelatedVocabulary()
    {
        var (pack, extension, unit, order) = Content();
        var first = Prepare([unit, order], [unit.Id], "first");
        var library = new DiyWorkshopLibrary(Path.Combine(_root, "mixed.json"));
        library.Save(pack); library.Save(extension);
        var unrelated = new DiyPackDraft { Nations = [new() { Name = "无关国家" }], Keywords = [new() { Name = "无关词条", EffectText = "部署：抽1张牌。" }] }; library.Save(unrelated);
        var other = new DiyCardDraft { PackId = unrelated.Id, Name = "另一套牌" };
        WorkshopAuthoring.Configure(library, [unit, order, other]);
        var mixed = Prepare([unit, order, other], [unit.Id, other.Id], "mixed");
        string Root(NetworkWorkshopSession session) => session.Roots.Single(root => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")))!["id"]!.ToString() == pack.Id[5..]);
        Assert.Equal(WorkshopPackageArchive.Fingerprint(Root(first)), WorkshopPackageArchive.Fingerprint(Root(mixed)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AuthoredKeywordAndArtworkReachBothPlayersAndRunOnServer(bool program)
    {
        var library = new DiyWorkshopLibrary(Path.Combine(_root, "words.json"));
        var keyword = new DiyKeywordDraft { Name = "联机成长", EffectText = "部署：获得 +2+2。",
            ProgramJson = program ? """{"handlers":[{"event":"unit.deployed","subject":"self","actions":[{"op":"buff","value":"2","second":"2"}]}]}""" : null };
        var pack = new DiyPackDraft { Name = "词条卡包", Keywords = [keyword] }; library.Save(pack);
        var unit = new DiyCardDraft { Name = "带图成长步兵", PackId = pack.Id, Cost = 0, OpCost = 0, Keywords = [keyword.Id], ArtFile = "network.png", Flavor = "同步后的卡牌背景" };
        WorkshopAuthoring.Configure(library, [unit]);
        var art = Path.Combine(_root, "art"); Directory.CreateDirectory(art);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
        var picture = new byte[310000]; new Random(71).NextBytes(picture); png.CopyTo(picture, 0); File.WriteAllBytes(Path.Combine(art, unit.ArtFile), picture);
        var hostSession = NetworkWorkshopSession.Prepare(Path.Combine(_root, "host"), [], [unit], [], [unit.Id], art);
        var guestSession = Prepare([], [], "guest");
        var deck = hostSession.MapDeck(Enumerable.Repeat(unit.Id, 39).ToArray());
        await using var server = await ServerLauncher.StartAsync(hostSession.Roots, Path.Combine(_root, "server"), fixedDeckForVerification: true);
        await server.AdminAsync("pool.transfer.policy", Policy(false));
        await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
        await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
        host.ConfigurePackTransfer(hostSession, deck); guest.ConfigurePackTransfer(guestSession, []); server.Configure(host);
        await Admit(server, host, 0, deck); await Admit(server, guest, 1, deck);
        await Align(host, guest); Assert.True(await host.StartMatchAsync());
        Assert.True(await host.MulliganDoneAsync(0)); Assert.True(await guest.MulliganDoneAsync(1));
        await Until(() => guest.CurrentView?.Phase == "play"); await guest.RefreshViewAsync(); await host.RefreshViewAsync();
        if (guest.CurrentView!.ActivePlayerSide != "self") { await host.SubmitAsync(new EndTurn()); await guest.RefreshViewAsync(); }
        var card = guest.CurrentView!.SelfHand.First();
        Assert.StartsWith("packart://", card.Definition.ArtPath);
        Assert.Equal(picture, File.ReadAllBytes(card.Definition.ArtPath[10..]));
        Assert.Equal(unit.Flavor, card.Definition.Flavor);
        await guest.SubmitAsync(new PlayCard(card.Uid, 0)); await guest.RefreshViewAsync();
        var deployed = Assert.Single(guest.CurrentView!.SelfLine);
        Assert.Equal(4, deployed.EffectiveAttack); Assert.Equal(5, deployed.EffectiveDefense);
        Assert.Empty(guestSession.Roots);
    }

    [Fact]
    public async Task InstalledNativePackagePlaysThroughNormalThirtyNineCardAdmission()
    {
        var library = new DiyWorkshopLibrary(Path.Combine(_root, "strict-packs.json"));
        var pack = new DiyPackDraft { Name = "合法构筑卡包" }; library.Save(pack);
        var cards = Enumerable.Range(0, 10).Select(n => new DiyCardDraft { Name = "联机单位" + n, PackId = pack.Id, Cost = 0, OpCost = 0 }).ToArray();
        WorkshopAuthoring.Configure(library, cards);
        var originalDeck = cards.SelectMany(c => Enumerable.Repeat(c.Id, 4)).Take(39).ToArray();
        var authored = Prepare(cards, originalDeck, "authored");
        var deck = authored.MapDeck(originalDeck);
        var installed = NetworkWorkshopSession.Prepare(Path.Combine(_root, "installed"), authored.Roots, [], [], deck);
        var empty = Prepare([], [], "guest");
        await using var server = await ServerLauncher.StartAsync(installed.Roots, Path.Combine(_root, "server"));
        await server.AdminAsync("pool.transfer.policy", Policy(false));
        await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
        await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
        host.ConfigurePackTransfer(installed, deck); guest.ConfigurePackTransfer(empty, []); server.Configure(host);
        await Admit(server, host, 0, deck); await Admit(server, guest, 1, deck);
        await Align(host, guest); Assert.True(await host.StartMatchAsync());
        Assert.True(await host.MulliganDoneAsync(0)); Assert.True(await guest.MulliganDoneAsync(1));
        await Until(() => guest.CurrentView?.Phase == "play");
        await guest.SubmitAsync(new Concede()); await Until(() => string.Equals(host.CurrentView?.ResultReason, "Concede", StringComparison.OrdinalIgnoreCase));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
