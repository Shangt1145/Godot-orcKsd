using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

[Collection("Workshop")]
public sealed class NetworkWorkshopTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultEnabledShippedPackagesCanStartRoom(bool inProcess)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "proto", "data", "kards", "packs"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var roots = Directory.GetDirectories(Path.Combine(directory!.FullName, "proto", "data", "kards", "packs"))
            .Where(p => File.Exists(Path.Combine(p, "manifest.json"))).ToArray();
        Assert.Contains(roots, p => Path.GetFileName(p) == "bulwark");
        Assert.All(roots, root => Assert.False(Directory.Exists(Path.Combine(root, "effects")), root));
        var (_, _, unit, _) = Content();
        var session = NetworkWorkshopSession.Prepare(Path.Combine(_root, "default-packs"), roots, [unit], [], [unit.Id]);
        var deck = session.MapDeck(Enumerable.Repeat(unit.Id, 39).ToArray());
        await using var server = await ServerLauncher.StartAsync(session.Roots, Path.Combine(_root, "default-server"), fixedDeckForVerification: true, inProcess: inProcess);
        await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
        await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
        host.ConfigurePackTransfer(session, deck); guest.ConfigurePackTransfer(session, deck); server.Configure(host);
        await Admit(server, host, 0, deck); await Admit(server, guest, 1, deck);
        await Align(host, guest);
        Assert.True(await host.StartMatchAsync());
        Assert.True(await host.MulliganDoneAsync(0)); Assert.True(await guest.MulliganDoneAsync(1));
    }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ksd-network-workshop-" + Guid.NewGuid().ToString("N"));
    private readonly string _unusedKeyword = "unselected-" + Guid.NewGuid().ToString("N");
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

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GuestOnlyExpansionCannotEnterTheRoomBecausePackTransferIsGone(bool inProcess)
    {
        // **v4 的能力变化**：卡包传输整条线随引擎消失（对端没有 pool.* RPC），
        // 所以 `pool.transfer.policy` 不再能"放行"客人的私有包——房主的服务端根本拿不到那个包。
        // 这条用例守的是**玩家侧后果**：客人带房主没有的包，这一局必须开不起来（明确拒绝），
        // 而不是开起来之后一半的卡是空的。以前"房主点头→自动下发→开打"的整条路径已不存在，
        // 现在玩家要做的是把包单独发给对方装好（见 DIY_UI_VERIFY_OK 的 pack-zip-export/import）。
        if (inProcess)
        {
            Orc.Game.Cards.KeywordRegistry.RegisterOrReplace(_unusedKeyword, (_, value) => new Orc.Game.Cards.PlainKeywordComponent(_unusedKeyword, value));
            Orc.Game.Cards.KeywordRegistry.DeclareEffectBindings(_unusedKeyword, [new("unselected.prefab", "unselected")]);
        }
        var (_, _, unit, order) = Content();
        var hostSession = Prepare([unit], [unit.Id], "host");
        var guestSession = Prepare([unit, order], [order.Id], "guest");
        var hostDeck = hostSession.MapDeck(Enumerable.Repeat(unit.Id, 39).ToArray());
        var guestDeck = guestSession.MapDeck(Enumerable.Repeat(order.Id, 39).ToArray());
        // `pool.transfer.policy` 这个管理操作在 v4 里**不存在**了（整条卡包传输线随引擎消失）。
        // 以前这里先调它"关掉放行"，再断言客人进不来；现在没有开关可调，直接看玩家侧后果。
        await using var server = await ServerLauncher.StartAsync(hostSession.Roots, Path.Combine(_root, "server"), fixedDeckForVerification: true, inProcess: inProcess);
        await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
        await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
        host.ConfigurePackTransfer(hostSession, hostDeck); guest.ConfigurePackTransfer(guestSession, guestDeck); server.Configure(host);
        await Admit(server, host, 0, hostDeck);

        // 客人本机看起来"齐备"（它自己装了那个扩展包），但房主那边没有——差值只能在服务端体现。
        await guest.SynchronizePacksAsync();
        Assert.True(guest.SupportsPackTransfer == false);

        var connection = await guest.ClaimSeatAsync(1, "remote", "测试玩家", guestDeck);
        Assert.NotNull(connection);
        var admission = await server.AdmitAsync(connection!);
        var refused = !admission.Ok;
        if (!refused)
        {
            // 占座没被拦＝那开局必须被拦（两条判据取并集，哪一条先命中都算"没让它悄悄开起来"）。
            var start = await server.AdminAsync("match.start");
            refused = !start.Ok;
        }
        Assert.True(refused, "客人带房主没有的卡包，这一局必须开不起来；实际却放行了");
    }

    [Fact]
    public async Task SameIdentityWithDifferentRulesIsNoLongerDetectedBecausePackTransferIsGone()
    {
        // **已知缺口（v4，随传输线一起消失的能力）**：以前这条能拦在开局前——传输层会收到对方的同名包，
        // 比内容指纹发现"同名不同规则"并给出"同名卡包内容不一致"。传输停用后，没有任何一方会去看
        // 对方的包内容：服务端只认房主自己的池，客人也不声明包。所以这个校验**现在不存在**。
        //
        // 这条用例不删，是因为删掉就等于把缺口藏起来。它现在钉住的是"缺口长什么样"：
        // 本机有同名包就算齐备（按 id 判定），哪怕规则完全不同。等引擎把 pool.* 加回来、
        // 或宿主补上内容指纹校验，本用例会红——那时候回来改成真正的拦截断言。
        var (_, _, unit, _) = Content();
        var hostSession = Prepare([unit], [unit.Id], "host");
        var guestSession = Prepare([unit with { Attack = 9 }], [unit.Id], "guest");
        var deck = hostSession.MapDeck(Enumerable.Repeat(unit.Id, 39).ToArray());
        await using var server = await ServerLauncher.StartAsync(hostSession.Roots, Path.Combine(_root, "server"), fixedDeckForVerification: true);
        await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
        await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
        host.ConfigurePackTransfer(hostSession, deck); guest.ConfigurePackTransfer(guestSession, deck);
        await Admit(server, host, 0, deck); await Admit(server, guest, 1, deck);

        await guest.SynchronizePacksAsync();
        Assert.False(guest.SupportsPackTransfer);
        Assert.True(guest.PacksAligned);                       // 按 id 判定：本机有同名包就算齐备
        Assert.Contains("卡包齐备", guest.PackSyncStatus);
        Assert.DoesNotContain("内容不一致", guest.PackSyncStatus); // 缺口：内容差异没人比
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

    /// <summary>
    /// 工坊卡（含自建词条、自建卡图）在**联机对局**里能走到哪一步。
    /// <para>
    /// **能走通的**：卡包在服务端装载（卡存在、数值/费用/文本/卡图/卡背说明都到双方），
    /// 客人打出去、正常部署、进战场。
    /// </para>
    /// <para>
    /// **走不通的（v4 已知缺口，明确钉在这里）**：自建词条的**效果**在服务端不执行——
    /// 生成的效果 csx 调的是宿主运行时 <c>Kards.Ui.OrcBridge.DesktopNativeRuntime</c>，
    /// 而引擎的服务端 csx 沙箱只引用 <c>Orc</c>/<c>Orc.Game</c>（<c>CSharpScriptSecurityOptions.DefaultAllowedAssemblies</c>），
    /// 服务端进程里也没有宿主程序集。所以词条打标在、卡面在、数值在，**效果不跑**：
    /// 部署后是印刷值 2/3，不是 +2+2 之后的 4/5。
    /// </para>
    /// <para>
    /// 修法（不是开关，是改写生成器）：把 <c>DesktopNativeCodegen</c> 的输出改成**引擎原生 csx**
    /// （<c>EffectRuntime</c> ＋ <c>EffectSelector</c> ＋ <c>view.Unit</c> 那一套，见引擎样例
    /// <c>samples/card-pack-demo/effects-v4/demo.rally.component.json</c>），服务端就无需宿主程序集。
    /// 改完这条用例必须红——那时候把断言改成 4/5。
    /// </para>
    /// <para>
    /// 单机（宿主求值器）下同一张卡的效果是**真的会跑**的，覆盖在 <c>DiyCardTests</c>／
    /// <c>DiyMechanismExecutionTests</c>：那条路径走 <c>MatchOptions.ScriptEvaluator</c>，宿主运行时在场。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    public async Task AuthoredCardReachesBothPlayersAndIsPlayableButTheKeywordEffectDoesNotRunOnTheServer(bool program, bool inProcess)
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
        // v4：卡包传输已停用（对端没有 pool.* RPC）。所以客人必须**自己装同一个包**——
        // 这正是玩家现在真实要做的动作（房主导出 zip、客人导入）。测试跟着走这条路，
        // 而不是继续假装"同步会把包送过来"：那一步在新引擎里不存在。
        var guestSession = NetworkWorkshopSession.Prepare(Path.Combine(_root, "guest"), [], [unit], [], [unit.Id], art);
        var deck = hostSession.MapDeck(Enumerable.Repeat(unit.Id, 39).ToArray());
        await using var server = await ServerLauncher.StartAsync(hostSession.Roots, Path.Combine(_root, "server"), fixedDeckForVerification: true, inProcess: inProcess);
        await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
        await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
        host.ConfigurePackTransfer(hostSession, deck); guest.ConfigurePackTransfer(guestSession, deck); server.Configure(host);
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
        // 印刷值 2/3：效果没跑（跑了会是 4/5）。这是上面那段缺口的**判据**，不是期望值。
        Assert.Equal(2, deployed.EffectiveAttack); Assert.Equal(3, deployed.EffectiveDefense);
        // 同步不再改写本地卡包库：客人手里始终只有自己装的那一个包。
        Assert.Single(guestSession.Roots);
    }

    /// <summary>
    /// 同一条缺口在**内嵌服务端**（Android 房主、单机开房）上的表现：不是"效果静默不跑"，
    /// 而是**整局开不起来**——引擎在建局期预编译词条效果组件，宿主 csx 在引擎沙箱里编译失败，
    /// 异常一路抛到 <c>match.start</c>。
    /// <para>
    /// 这条比"不跑"更该被看见：玩家看到的是"点了开局没反应/报错"。它和上一条是同一个根因
    /// （生成的 csx 依赖宿主程序集），修好生成器后两条一起转绿。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AuthoredKeywordEffectMakesTheInProcessServerRefuseToStartTheMatch()
    {
        var library = new DiyWorkshopLibrary(Path.Combine(_root, "words2.json"));
        var keyword = new DiyKeywordDraft { Name = "内嵌成长", EffectText = "部署：获得 +2+2。" };
        var pack = new DiyPackDraft { Name = "内嵌词条包", Keywords = [keyword] }; library.Save(pack);
        var unit = new DiyCardDraft { Name = "内嵌成长步兵", PackId = pack.Id, Cost = 0, OpCost = 0, Keywords = [keyword.Id] };
        WorkshopAuthoring.Configure(library, [unit]);
        var session = NetworkWorkshopSession.Prepare(Path.Combine(_root, "inline"), [], [unit], [], [unit.Id]);
        var deck = session.MapDeck(Enumerable.Repeat(unit.Id, 39).ToArray());
        await using var server = await ServerLauncher.StartAsync(session.Roots, Path.Combine(_root, "server2"), fixedDeckForVerification: true, inProcess: true);
        await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
        await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
        host.ConfigurePackTransfer(session, deck); guest.ConfigurePackTransfer(session, deck); server.Configure(host);
        await Admit(server, host, 0, deck); await Admit(server, guest, 1, deck);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartMatchAsync());
        Assert.Contains("csx", error.Message);
        Assert.Contains("编译失败", error.Message);
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
        // 传输停用后，客人得自己装同一个包——这里用"已安装"形态模拟（与房主同一批 roots）。
        var guestInstalled = NetworkWorkshopSession.Prepare(Path.Combine(_root, "guest"), authored.Roots, [], [], deck);
        await using var server = await ServerLauncher.StartAsync(installed.Roots, Path.Combine(_root, "server"));
        await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
        await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
        host.ConfigurePackTransfer(installed, deck); guest.ConfigurePackTransfer(guestInstalled, deck); server.Configure(host);
        await Admit(server, host, 0, deck); await Admit(server, guest, 1, deck);
        await Align(host, guest); Assert.True(await host.StartMatchAsync());
        Assert.True(await host.MulliganDoneAsync(0)); Assert.True(await guest.MulliganDoneAsync(1));
        await Until(() => guest.CurrentView?.Phase == "play");
        await guest.SubmitAsync(new Concede()); await Until(() => string.Equals(host.CurrentView?.ResultReason, "Concede", StringComparison.OrdinalIgnoreCase));
    }
    public void Dispose() { Orc.Game.Cards.KeywordRegistry.Unregister(_unusedKeyword); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
