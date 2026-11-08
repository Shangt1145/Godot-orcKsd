using System.IO.Compression;
using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

[Collection("Workshop")]
public sealed class CardSkinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orcksd-workshop-skins", Guid.NewGuid().ToString("N"));
    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
    [Fact] public void CompatibilityCardOwnsItsCompleteCardAndNumericLayoutAfterRestart()
    {
        var path = Path.Combine(_root, "cards.json"); Directory.CreateDirectory(_root);
        var library = new DiyCardLibrary(path);
        var skin = new CardSkin { Art = library.SaveAlternateArt(Png()), CostBox = new(.7f, .8f, .2f, .1f) };
        var card = new DiyCardDraft { Name = "完整异画", AlternateArt = skin }; library.Save(card);
        var loaded = new DiyCardLibrary(path); Assert.Null(loaded.LoadError);
        Assert.Equal(skin, Assert.Single(loaded.Cards).AlternateArt);
        Assert.Equal(Png(), File.ReadAllBytes(Path.Combine(loaded.ArtDirectory, skin.Art)));
        Assert.Equal(card.RuntimeId, Assert.Single(loaded.Cards).RuntimeId);
    }
    [Fact] public void InvalidNumericLayoutAndEscapingImagesAreRejected()
    {
        var valid = new CardSkin { Art = "complete.png" }; valid.Checked();
        foreach (var skin in new[] { valid with { Art = "../escape.png" }, valid with { Art = "C:/card.png" }, valid with { Ink = "bad" },
            valid with { CostBox = new(.9f, 0, .2f, .1f) }, valid with { DefenseBox = new(float.NaN, 0, .1f, .1f) } })
            Assert.Throws<InvalidDataException>(() => skin.Checked());
        Assert.Throws<InvalidDataException>(() => DiyCardLibrary.CheckShape(new() { AlternateArt = valid with { Art = "../escape.png" } }));
    }
    [Fact] public void NativeDraftAndExportKeepCompleteCardAndLayoutWithoutChangingRules()
    {
        OrcEngineHost.WarmUp(); Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "base.zip"); ZipFile.CreateFromDirectory(@"H:\Working Folder\OrC-KSD-github\samples\card-pack-demo", zip);
        var store = new NativeWorkshop(Path.Combine(_root, "owner"), Path.Combine(_root, "shipped"));
        var pack = store.Import(zip).Pack; store.Begin(pack.Id); var asset = store.Assets().First(a => a.Kind == "Card");
        var before = store.Read(asset.Path);
        var skin = new CardSkin { Art = "pending.png", AttackBox = new(.08f, .85f, .15f, .09f) };
        store.SaveSkin(asset.Id, skin, Png());
        var saved = store.PreviewSkin(asset.Id)!;
        Assert.StartsWith("art/alt-", saved.Art); Assert.Equal(skin.AttackBox, saved.AttackBox);
        Assert.Equal(before, store.Read(asset.Path));
        var output = Path.Combine(_root, "export.zip"); store.Export(pack.Id, output, true);
        var peer = new NativeWorkshop(Path.Combine(_root, "peer"), Path.Combine(_root, "other-shipped"));
        peer.Import(output); peer.Begin(pack.Id);
        Assert.Equal(saved, peer.PreviewSkin(asset.Id));
        Assert.NotNull(peer.ResolvePreviewSkin(asset.Id));
        peer.SaveSkin(asset.Id, null); Assert.Null(peer.PreviewSkin(asset.Id));
        Assert.Equal(before, peer.Read(asset.Path));
    }
    [Fact] public void CompatibilitySessionPackageIncludesImageAndNumericLayout()
    {
        OrcEngineHost.WarmUp(); Directory.CreateDirectory(_root);
        var library = new DiyCardLibrary(Path.Combine(_root, "cards.json"));
        var draft = new DiyCardDraft { Name = "异画导出", Cost = 0, AlternateArt = new() { Art = library.SaveAlternateArt(Png()), CostBox = new(.7f, .8f, .2f, .1f) } };
        WorkshopAuthoring.Configure(new DiyWorkshopLibrary(Path.Combine(_root, "packs.json")), [draft]);
        var session = NetworkWorkshopSession.Prepare(Path.Combine(_root, "session"), [], [draft], [], [draft.Id], library.ArtDirectory);
        var root = session.Roots.Single();
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "workshop.presentation.json")));
        var raw = metadata.RootElement.GetProperty("cards").EnumerateObject().Single().Value.GetProperty("alternateArt");
        var skin = raw.Deserialize<CardSkin>()!;
        Assert.Equal(draft.AlternateArt.CostBox, skin.CostBox);
        Assert.Equal(Png(), File.ReadAllBytes(Path.Combine(root, skin.Art)));
    }
    [Fact] public void LegacyCardFilesNeedNoAlternateArtSetting()
    {
        Directory.CreateDirectory(_root); var file = Path.Combine(_root, "cards.json");
        var library = new DiyCardLibrary(file); library.Save(new() { Name = "旧卡" });
        Assert.Null(Assert.Single(new DiyCardLibrary(file).Cards).AlternateArt);
    }
    private static string Hash(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
    [Fact] public void ArbitraryFormatAndSizeCompleteCardIsStoredVerbatim()
    {
        var path = Path.Combine(_root, "cards.json"); Directory.CreateDirectory(_root);
        var library = new DiyCardLibrary(path);
        // 旧口径会被 2 MiB / PNG 校验拦下；现在只要求字节非空，扩展名与字节原样保留。
        var bytes = new byte[3 * 1024 * 1024 + 7]; new Random(17).NextBytes(bytes);
        var name = library.SaveAlternateArt(bytes, ".WEBP");
        Assert.StartsWith("alt-", name); Assert.EndsWith(".webp", name);
        Assert.Equal(Hash(bytes), Hash(File.ReadAllBytes(Path.Combine(library.ArtDirectory, name))));
        var skin = new CardSkin { Art = name, CostBox = new(.7f, .8f, .2f, .1f) };
        skin.Checked();
        library.Save(new DiyCardDraft { Name = "任意大小异画", AlternateArt = skin });
        Assert.Equal(skin, Assert.Single(new DiyCardLibrary(path).Cards).AlternateArt);
        new CardSkin { Art = "art/alt-ABC.tga" }.Checked();
        Assert.Equal(".png", CardSkinFile.ExtensionOf("card-without-extension"));
    }
    [Fact] public void NativePackKeepsArbitraryCompleteCardBytesAndExtension()
    {
        OrcEngineHost.WarmUp(); Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "base.zip"); ZipFile.CreateFromDirectory(@"H:\Working Folder\OrC-KSD-github\samples\card-pack-demo", zip);
        var store = new NativeWorkshop(Path.Combine(_root, "owner"), Path.Combine(_root, "shipped"));
        var pack = store.Import(zip).Pack; store.Begin(pack.Id);
        var asset = store.Assets().First(a => a.Kind == "Card");
        var before = store.Read(asset.Path);
        var bytes = new byte[2 * 1024 * 1024 + 11]; new Random(9).NextBytes(bytes);
        store.SaveSkin(asset.Id, new CardSkin { Art = "pending.tga" }, bytes);
        var saved = store.PreviewSkin(asset.Id)!;
        Assert.StartsWith("art/alt-", saved.Art); Assert.EndsWith(".tga", saved.Art);
        var resolved = store.ResolvePreviewSkin(asset.Id)!;
        Assert.Equal(Hash(bytes), Hash(File.ReadAllBytes(resolved.Art["packfull://".Length..])));
        Assert.Equal(before, store.Read(asset.Path));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
