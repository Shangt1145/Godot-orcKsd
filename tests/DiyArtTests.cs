using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>自导入卡图：存储（art/ 目录）、校验（受限文件名/缩放域）、Preview scheme、持久化往返。</summary>
public class DiyArtTests
{
    private static DiyCardDraft Unit(string id = "diy/art-test") => new() { Id = id, Name = "卡图测试", Cost = 0, OpCost = 0 };
    private static string TempFolder() { var f = Path.Combine(Path.GetTempPath(), "diyart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(f); return f; }

    [Fact]
    public void SaveArtStoresPngAndDeleteCleansIt()
    {
        var folder = TempFolder();
        try
        {
            var library = new DiyCardLibrary(Path.Combine(folder, "cards.json"));
            var draft = Unit("diy/art-clean");
            var name = library.SaveArt(draft.Id, [1, 2, 3, 4]);
            Assert.Equal("art-clean.png", name);
            var file = Path.Combine(folder, "art", name);
            Assert.True(File.Exists(file));
            Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(file));
            library.Save(draft with { ArtFile = name });
            library.Delete(draft.Id);
            Assert.False(File.Exists(file)); // 删卡顺带清理导入图
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void SaveArtRejectsOversizedPayload()
    {
        var library = new DiyCardLibrary(Path.Combine(TempFolder(), "cards.json"));
        Assert.Throws<InvalidDataException>(() => library.SaveArt("diy/x", new byte[4_000_001]));
        Assert.Throws<InvalidDataException>(() => library.SaveArt("diy/x", []));
        Assert.Throws<InvalidDataException>(() => library.SaveArt("pack/not-diy", [1]));
    }
    [Theory]
    [InlineData("../evil.png")]
    [InlineData("a/b.png")]
    [InlineData("bad name.png")]
    [InlineData("noext")]
    public void CheckShapeRejectsUnsafeArtFileNames(string artFile)
    {
        var folder = TempFolder();
        try
        {
            var library = new DiyCardLibrary(Path.Combine(folder, "cards.json"));
            Assert.Throws<InvalidDataException>(() => library.Save(Unit() with { ArtFile = artFile }));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData(40)]
    [InlineData(400)]
    public void CheckShapeRejectsOutOfRangeArtScale(int scale)
    {
        var folder = TempFolder();
        try
        {
            var library = new DiyCardLibrary(Path.Combine(folder, "cards.json"));
            Assert.Throws<InvalidDataException>(() => library.Save(Unit() with { ArtFile = "ok.png", ArtScale = scale }));
            library.Save(Unit() with { ArtFile = "ok.png", ArtScale = 150 }); // 合法域 50–300
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void PreviewPrefersImportedArtOverCatalogReference()
    {
        var draft = Unit() with { ArtFile = "abc.png", ArtScale = 180, ArtCardId = "whatever" };
        var preview = DiyCardCompiler.Preview(draft, []);
        Assert.Equal("diyart://abc.png", preview.ArtPath);
        var fallback = DiyCardCompiler.Preview(Unit() with { ArtFile = "" }, []);
        Assert.Equal("", fallback.ArtPath);
    }
    [Fact]
    public void StorageAndExportImportRoundTripArtFields()
    {
        var folder = TempFolder();
        try
        {
            var path = Path.Combine(folder, "cards.json");
            var library = new DiyCardLibrary(path);
            var draft = Unit("diy/art-roundtrip") with { ArtFile = "art-roundtrip.png", ArtScale = 220 };
            library.Save(draft);
            var reloaded = Assert.Single(new DiyCardLibrary(path).Cards);
            Assert.Equal("art-roundtrip.png", reloaded.ArtFile); Assert.Equal(220, reloaded.ArtScale);
            var imported = DiyCardLibrary.Import(DiyCardLibrary.Export(reloaded));
            Assert.Equal(reloaded.ArtFile, imported.ArtFile); Assert.Equal(reloaded.ArtScale, imported.ArtScale);
        }
        finally { Directory.Delete(folder, true); }
    }
    // Zoom 注册表（DiyArtStore）位于 Godot UI 装配，不入 xunit 面；由 verify-ui 实机探针覆盖。
}
