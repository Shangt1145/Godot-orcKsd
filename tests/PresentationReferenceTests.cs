using System.IO.Compression;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using Kards.Ui.Contracts;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 展示层引用完整性（2026-10-26 玩家事故：一条孤儿键让整包卡消失）。
///
/// <para>
/// 不变量：`display-texts.json` / `desktop-effects.json` 里指向"不存在的卡"的条目，
/// 只能丢那一条并留告警，**不得阻断卡定义投影**；编辑器保存也不得再产出新的孤儿键。
/// </para>
/// </summary>
[Collection("engine-host")]
public sealed class PresentationReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ksd-pres-" + Guid.NewGuid().ToString("N"));
    private static string Sample => @"H:\Working Folder\OrC-KSD-github\samples\card-pack-demo";
    public PresentationReferenceTests()
    {
        Directory.CreateDirectory(_root);
        OrcEngineHost.WarmUp();
    }
    public void Dispose()
    {
        OrcEngineHost.Reset();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>把样例包复制成 `&lt;base&gt;/packs/demo`，可选地往展示文件里塞脏数据。</summary>
    private string BaseWithPack(Action<string>? corrupt = null)
    {
        var baseDir = Path.Combine(_root, "base-" + Guid.NewGuid().ToString("N"));
        var pack = Path.Combine(baseDir, "packs", "demo");
        Directory.CreateDirectory(Path.GetDirectoryName(pack)!);
        WorkshopPackageArchive.Copy(Sample, pack);
        corrupt?.Invoke(pack);
        return baseDir;
    }

    private static void WriteKey(string pack, string file, string key, JsonNode value)
    {
        var path = Path.Combine(pack, file);
        var node = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        node[key] = value;
        File.WriteAllText(path, node.ToJsonString());
    }

    [Fact]
    public void 孤儿卡面文本不再拖垮整包()
    {
        OrcEngineHost.Reset();
        var baseDir = BaseWithPack(pack => WriteKey(pack, "display-texts.json", "card.deadbeef", JsonValue.Create("指向已不存在的卡")));

        var (cards, failures, packs) = OrcEngineHost.LoadPackCards(baseDir);

        Assert.NotEmpty(cards);
        Assert.Single(packs);
        Assert.Contains(failures, f => f.Contains("卡面文本引用了不存在的卡", StringComparison.Ordinal));
    }

    [Fact]
    public void 孤儿选靶声明不再拖垮整包()
    {
        OrcEngineHost.Reset();
        var baseDir = BaseWithPack(pack => WriteKey(pack, "desktop-effects.json", "card.deadbeef",
            new JsonArray(JsonValue.Create("""{"trigger":"deploy","actions":[]}"""))));

        var (cards, failures, packs) = OrcEngineHost.LoadPackCards(baseDir);

        Assert.NotEmpty(cards);
        Assert.Single(packs);
        Assert.Contains(failures, f => f.Contains("选靶声明引用了不存在的卡", StringComparison.Ordinal));
    }

    [Fact]
    public void 展示文件整个损坏也只丢展示数据()
    {
        OrcEngineHost.Reset();
        var baseDir = BaseWithPack(pack => File.WriteAllText(Path.Combine(pack, "display-texts.json"), "{ 这不是 JSON"));

        var (cards, failures, _) = OrcEngineHost.LoadPackCards(baseDir);

        Assert.NotEmpty(cards);
        Assert.Contains(failures, f => f.Contains("display-texts.json 无法解析", StringComparison.Ordinal));
    }

    [Fact]
    public void 卡变成非法后保存会摘掉它的展示引用()
    {
        var store = new NativeWorkshop(Path.Combine(_root, "user"), Path.Combine(_root, "shipped"));
        var zip = Path.Combine(_root, "demo.zip");
        ZipFile.CreateFromDirectory(Sample, zip, CompressionLevel.Fastest, false);
        var pack = store.Import(zip).Pack;
        store.Begin(pack.Id);
        var asset = store.Assets().First(a => a.Kind == "Card");
        var localId = asset.Id;

        store.SaveCardPresentation(localId, "这段文本会变成孤儿", "");
        var displayTexts = Path.Combine(store.TemporaryRoot!, "display-texts.json");
        Assert.True(JsonNode.Parse(File.ReadAllText(displayTexts))!.AsObject().ContainsKey(localId));

        // 让卡字段级非法：改成指令卡（order）却不带 effects —— 引擎按 D40 判 Definition=null。
        var json = JsonNode.Parse(store.Read(asset.Path))!.AsObject();
        var components = json["components"]!.AsArray();
        foreach (var component in components.OfType<JsonObject>())
            if (component["component"]?.ToString() == "typeCategory")
                component["type"] = new JsonArray(JsonValue.Create("order"));
        json["components"] = new JsonArray(components
            .Where(c => c?["component"]?.ToString() != "effects")
            .Select(c => c?.DeepClone()).ToArray());
        store.SaveCardDocument(asset, json.ToJsonString(), "这段文本会变成孤儿", "");

        var after = JsonNode.Parse(File.ReadAllText(displayTexts))!.AsObject();
        Assert.False(after.ContainsKey(localId), "非法卡保存后仍在 display-texts.json 留下孤儿键");
        Assert.Contains(store.SessionWarnings, w => w.Contains("不合法", StringComparison.Ordinal));
        Assert.DoesNotContain(store.PreviewCards(), c => c.CardId.EndsWith(localId, StringComparison.Ordinal));
    }

    [Fact]
    public void 删除卡会同时清掉三处展示引用()
    {
        var store = new NativeWorkshop(Path.Combine(_root, "user"), Path.Combine(_root, "shipped"));
        var zip = Path.Combine(_root, "demo2.zip");
        ZipFile.CreateFromDirectory(Sample, zip, CompressionLevel.Fastest, false);
        var pack = store.Import(zip).Pack;
        store.Begin(pack.Id);
        var asset = store.Assets().First(a => a.Kind == "Card");
        var localId = asset.Id;
        store.SaveCardPresentation(localId, "待删除的文本", "风味");

        store.DeleteAsset(asset);

        foreach (var file in new[] { "display-texts.json", "desktop-effects.json" })
        {
            var path = Path.Combine(store.TemporaryRoot!, file);
            if (!File.Exists(path)) continue;
            Assert.False(JsonNode.Parse(File.ReadAllText(path))!.AsObject().ContainsKey(localId), $"{file} 仍残留已删除卡的键");
        }
    }
}
