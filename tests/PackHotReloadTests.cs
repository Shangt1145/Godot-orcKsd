using System.IO.Compression;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 卡包**热加载**判据：改完卡包之后，引擎的卡包目录要立刻跟着变，不需要重启进程。
/// <para>
/// 为什么单开一个文件：现有 <see cref="NativeWorkshopTests"/> 构造 <see cref="NativeWorkshop"/> 时
/// **不传 activate 回调**（第三个参数缺省 null），所以那里的 <c>Activate()</c> 是个空操作，
/// 「保存/启停之后引擎真的重装了吗」从来没被验证过。这里补上——把 <c>OrcEngineHost.SetWorkshopRoots</c>
/// 当成回调传进去，和 <c>Main.cs</c> 里的真实接线一致。
/// </para>
/// <para>
/// 判据是**引擎目录**（<c>OrcEngineHost.Snapshot().CardPacks</c>），不是卡包管理器自己的列表——
/// 后者只是 UI 的书签，前者才是建局时真正读的东西。
/// </para>
/// </summary>
[Collection("Workshop")]
public sealed class PackHotReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ksd-reload-" + Guid.NewGuid().ToString("N"));
    private static string Sample => @"H:\Working Folder\OrC-KSD-github\samples\card-pack-demo";

    public PackHotReloadTests() { Directory.CreateDirectory(_root); }

    public void Dispose()
    {
        OrcEngineHost.Reset();
        try { Directory.Delete(_root, true); } catch { /* 临时目录，删不掉不算失败 */ }
    }

    private string Archive()
    {
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(Sample, zip, CompressionLevel.Fastest, false);
        return zip;
    }

    /// <summary>和 <c>Main.cs:163</c> 完全同一条接线：activate 回调＝<c>OrcEngineHost.SetWorkshopRoots</c>。</summary>
    private NativeWorkshop Store()
        => new(Path.Combine(_root, "user"), Path.Combine(_root, "shipped"), OrcEngineHost.SetWorkshopRoots);

    private static bool InEngineCatalog(string packId)
        => OrcEngineHost.Snapshot().CardPacks.Any(p => p.Manifest.Id == packId);

    [Fact]
    public void 导入卡包后引擎目录立刻出现该包()
    {
        // 从「没有任何磁盘包」的干净状态起步：宿主先按空目录初始化。
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        OrcEngineHost.Reset();
        OrcEngineHost.WarmUp(empty);
        Assert.False(InEngineCatalog("demo"), "前置：干净起点不该有 demo 包");

        var store = Store();
        var imported = store.Import(Archive()).Pack;

        Assert.Equal("demo", imported.Id);
        Assert.True(InEngineCatalog("demo"),
            "导入之后引擎目录里必须有这个包——否则玩家得重启游戏才能用新卡");
    }

    [Fact]
    public void 停用再启用卡包引擎目录跟着变()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        OrcEngineHost.Reset();
        OrcEngineHost.WarmUp(empty);

        var store = Store();
        store.Import(Archive());
        Assert.True(InEngineCatalog("demo"));

        store.SetEnabled("demo", false);
        Assert.False(InEngineCatalog("demo"), "停用之后引擎目录里不该还留着它");

        store.SetEnabled("demo", true);
        Assert.True(InEngineCatalog("demo"), "重新启用之后要回来，不需要重启");
    }

    /// <summary>
    /// 把「对局中禁止改卡包」这道闸门的存在性钉住：闸门靠 <c>EngineHost.CardPacksLocked</c>，
    /// 而**宿主必须自己调用 <c>LockCardPacks()</c> 才有意义**（引擎不会自动加锁）。
    /// <para>
    /// 这条用例断言的是「闸门现在是开着的」——也就是**当前的真实状态**。它绿不代表没问题：
    /// 恰恰说明 <c>NativeWorkshop.Idle()</c> 与 <c>SetWorkshopRoots</c> 里那两处
    /// 「请先结束当前对局」的检查永远走不到。等宿主在开局时补上 Lock/Unlock，
    /// 这条用例会红，提醒把它改成「对局中为 true」。
    /// </para>
    /// </summary>
    [Fact]
    public void 宿主当前从不给卡包加锁所以对局中的闸门是开着的()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        OrcEngineHost.Reset();
        OrcEngineHost.WarmUp(empty);

        var host = OrcEngineHost.Current!;
        Assert.False(host.CardPacksLocked,
            "如果这里变成 true，说明宿主补上了 LockCardPacks——请把本用例改成断言对局中为 true，"
            + "并检查 NativeWorkshop.Idle() 的拦截是否终于能触发了");
    }
}
