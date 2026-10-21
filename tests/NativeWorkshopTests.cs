using System.IO.Compression;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Xunit;

namespace Kards.Ui.Tests;

[Collection("Workshop")]
public sealed class NativeWorkshopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ksd-workshop-" + Guid.NewGuid().ToString("N"));
    private string Sample => @"H:\Working Folder\OrC-KSD-github\samples\card-pack-demo";
    private NativeWorkshop Store => new(Path.Combine(_root, "user"), Path.Combine(_root, "shipped"));
    public NativeWorkshopTests() { Directory.CreateDirectory(_root); OrcEngineHost.WarmUp(); }
    private string Archive(bool wrapper = false)
    {
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(Sample, zip, CompressionLevel.Fastest, wrapper);
        return zip;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ImportRestartExportAndDuplicateRetainExactFiles(bool wrapper)
    {
        var store = Store; var archive = Archive(wrapper); var result = store.Import(archive);
        Assert.Equal("demo", result.Pack.Id); Assert.True(result.Pack.Enabled);
        Assert.NotEmpty(result.Pack.Diagnostics.Concat(new[] { result.Pack.Name }));
        Assert.True(store.Import(archive).Unchanged);
        Assert.Single(Store.Packages());
        var exported = Path.Combine(_root, "export.zip"); Store.Export("demo", exported);
        using var actual = ZipFile.OpenRead(exported);
        using var expected = ZipFile.OpenRead(Archive());
        Assert.Equal(expected.Entries.OrderBy(e => e.FullName).Select(e => (e.FullName, e.Length)), actual.Entries.OrderBy(e => e.FullName).Select(e => (e.FullName, e.Length)));
        store.SetEnabled("demo", false); Assert.False(Assert.Single(Store.Packages()).Enabled);
    }

    [Fact]
    public void NativeCommitIsDraftUntilPromotedAndKeepsUnknownFields()
    {
        var store = Store; var pack = store.Import(Archive()).Pack; store.Begin(pack.Id);
        var asset = store.Assets().First(a => a.Kind == "Card");
        var formal = File.ReadAllText(Path.Combine(pack.Root, asset.Path));
        var json = JsonNode.Parse(store.Read(asset.Path))!.AsObject(); json["name"] = "草稿测试"; json["futureField"] = new JsonObject { ["retain"] = true };
        store.Commit(asset.Path, json.ToJsonString());
        Assert.Equal(formal, File.ReadAllText(Path.Combine(pack.Root, asset.Path)));
        Assert.Contains("futureField", store.Read(asset.Path));
        Assert.Contains(store.PreviewCards(), c => c.Name == "草稿测试");
        store.Promote();
        Assert.False(store.HasSession); Assert.False(Directory.Exists(pack.Root + ".tmp"));
        Assert.Contains("futureField", File.ReadAllText(Path.Combine(pack.Root, asset.Path)));
        store.Begin(pack.Id); Assert.Equal("草稿测试", JsonNode.Parse(store.Read(asset.Path))!["name"]!.GetValue<string>()); store.Discard();
    }

    [Theory]
    [InlineData("../escape.json")] [InlineData("/absolute.json")] [InlineData("cards/A.json:evil")] [InlineData("cards/CON.json")]
    public void RejectsUnsafeArchivePathsWithoutInstalling(string path)
    {
        var file = Path.Combine(_root, "bad.zip");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry(path).Open()); writer.Write("{}"); }
        Assert.Throws<InvalidDataException>(() => Store.Import(file)); Assert.Empty(Store.Packages());
    }

    [Fact]
    public void RejectsCaseCollisionsAndMultiplePackages()
    {
        var file = Path.Combine(_root, "collision.zip");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            zip.CreateEntry("Cards/a.json"); zip.CreateEntry("cards/A.json");
        }
        Assert.Throws<InvalidDataException>(() => Store.Import(file));
    }

    [Fact]
    public void FailedActivationRollsBackInstallation()
    {
        var store = new NativeWorkshop(Path.Combine(_root, "user"), Path.Combine(_root, "shipped"), roots =>
        { if (roots.Count > 0) throw new InvalidDataException("activation failed"); });
        Assert.Throws<InvalidDataException>(() => store.Import(Archive())); Assert.Empty(store.Packages());
    }

    [Fact]
    public void InterruptedDraftIsRetainedAndCanBeBackedUpExplicitly()
    {
        var store = Store; var pack = store.Import(Archive()).Pack; store.Begin(pack.Id);
        var asset = store.Assets().First(a => a.Kind == "Card");
        var content = store.Read(asset.Path); var restarted = Store;
        Assert.True(Assert.Single(restarted.Packages()).HasDraft);
        restarted.Begin(pack.Id); Assert.True(restarted.RecoveredDraft); Assert.Equal(content, restarted.Read(asset.Path));
        Assert.True(Directory.Exists(pack.Root + ".tmp")); restarted.Discard();
        Assert.NotEmpty(Directory.GetDirectories(Path.Combine(_root, "user", "backups")));
    }

    [Fact]
    public void RecoveryPersistsAcrossAnotherRestartAndPromotesWithoutChangingFormalEarly()
    {
        var first = Store; var pack = first.Import(Archive()).Pack; first.Begin(pack.Id);
        var added = first.CreateCard("interrupted.card"); var source = new NativeCardDocument(first.Read(added.Path)); source.Rename("重启前"); first.Commit(added.Path, source.Json);
        var second = Store; second.Begin(pack.Id); Assert.True(second.RecoveredDraft);
        Assert.Contains(second.Assets(), a => a.Id == added.Id); source = new(second.Read(added.Path)); source.Rename("再次保存"); second.SaveCardDocument(added, source.Json, "显示说明", "背景");
        Assert.False(File.Exists(Path.Combine(pack.Root, added.Path)));
        var third = Store; third.Begin(pack.Id); Assert.Equal("再次保存", new NativeCardDocument(third.Read(added.Path)).Name); Assert.Equal("背景", third.CardPresentation(added.Id).Flavor);
        third.Promote(); Assert.False(Directory.Exists(pack.Root + ".tmp")); Assert.Equal("再次保存", new NativeCardDocument(File.ReadAllText(Path.Combine(pack.Root, added.Path))).Name);
        var fourth = Store; fourth.Begin(pack.Id); Assert.False(fourth.RecoveredDraft); fourth.Discard();
    }

    [Fact]
    public void SwitchingPackagesKeepsEachDraftAndRecoversNewUnpublishedPackage()
    {
        var store = Store; store.Import(Archive()); store.Create("unpublished", "未发布", "demo"); var added = store.CreateCard("new.card");
        store.Begin("demo"); Assert.Equal("demo", store.SessionPackage); store.Begin("unpublished"); Assert.True(store.RecoveredDraft); Assert.Contains(store.Assets(), a => a.Id == added.Id);
        store.Promote(); Assert.Contains(Store.Packages(), p => p.Id == "unpublished");
    }

    [Fact]
    public void BrokenInterruptedManifestCanBeArchivedWithoutManualFileOperations()
    {
        var store = Store; var pack = store.Import(Archive()).Pack; store.Begin(pack.Id);
        File.WriteAllText(Path.Combine(store.TemporaryRoot!, "manifest.json"), "{");
        var restarted = Store; var error = Assert.Throws<InvalidDataException>(() => restarted.Begin(pack.Id)); Assert.DoesNotContain(pack.Root, error.Message);
        restarted.DiscardStoredDraft(pack.Id); Assert.False(Directory.Exists(pack.Root + ".tmp"));
        restarted.Begin(pack.Id); Assert.False(restarted.RecoveredDraft); restarted.Discard();
        Assert.Contains(Directory.GetFiles(Path.Combine(_root, "user", "backups"), "manifest.json", SearchOption.AllDirectories), p => File.ReadAllText(p) == "{");
    }

    [Fact]
    public void NativePackageCreationAndNewCardUseBackendIdentities()
    {
        var store = Store; store.Import(Archive());
        store.Create("created.pack", "新卡包", "demo");
        var asset = store.CreateCard("created.unit");
        var original = JsonNode.Parse(store.Read(asset.Path))!.AsObject(); original["name"] = "新建单位";
        store.Commit(asset.Path, original.ToJsonString()); store.Promote();
        var installed = Store.Packages().Single(p => p.Id == "created.pack"); Assert.Equal(1, installed.Cards);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(installed.Root, "manifest.json")))!;
        Assert.True(Guid.TryParse(manifest["guid"]!.GetValue<string>(), out _));
        store.Begin("created.pack"); Assert.Single(store.Assets()); store.Discard();
    }

    [Fact]
    public async Task PromotedPackageReachesNewNativeMatchAndFrozenSnapshotKeepsItsBaseline()
    {
        // Match the application's shipped+installed catalog. Built-in keyword bindings are
        // process-wide, so importing a test pack must retain their shipped effect library.
        var host = OrcEngineHost.Current!; var baseline = host.CreateSnapshot();
        try
        {
            var store = new NativeWorkshop(Path.Combine(_root, "user"), @"H:\Working Folder\OrC-KSD.Godot\proto\data\kards", OrcEngineHost.SetWorkshopRoots);
            store.Import(Archive()); var frozen = PackSet.FromSnapshot(OrcEngineHost.Snapshot());
            store.Begin("demo"); var asset = store.Assets().Single(a => a.Id == "demo.rifleman" && a.Kind == "Card");
            var json = JsonNode.Parse(store.Read(asset.Path))!; json["components"]!.AsArray().OfType<JsonObject>().Single(c => c["component"]!.GetValue<string>() == "battleStats")["attack"] = 8;
            store.Commit(asset.Path, json.ToJsonString()); store.Promote();
            var packs = PackSet.FromSnapshot(OrcEngineHost.Snapshot());
            Assert.Equal(1, frozen.Definitions.Single(c => c.Id == asset.AppId).Definition.Attack);
            Assert.Equal(8, packs.Definitions.Single(c => c.Id == asset.AppId).Definition.Attack);
            using var match = await OrcMatchSession.CreateAsync(packs.Definitions, Enumerable.Repeat(asset.AppId, 12).ToArray(), Enumerable.Repeat(asset.AppId, 12).ToArray(), 31, "imported-pack", openingHand: [asset.AppId], firstPlayerIndex: 0);
            match.DriveOpponent = false; await match.SettleMulliganAsync(false);
            var actor = match.View.SelfHand.Single(c => c.CardId == asset.AppId);
            Assert.Equal(UiSubmitOutcome.Applied, await match.SubmitAsync(new PlayCard(actor.Uid)));
            match.SettleProjection(); Assert.Equal(8, match.View.SelfLine.Single(c => c.Uid == actor.Uid).EffectiveAttack);
        }
        finally { host.RestoreFrom(baseline); }
    }

    [Fact]
    public void SpaceWarNativeEffectsAreImportedWithoutLegacyConversion()
    {
        var zip = Path.Combine(_root, "spacewar.zip");
        ZipFile.CreateFromDirectory(@"H:\Working Folder\OrC-KSD-github\samples\card-pack-space-war", zip);
        var store = Store; var result = store.Import(zip);
        Assert.Equal(4, result.Pack.Cards); Assert.Empty(result.Pack.Diagnostics);
        store.Begin(result.Pack.Id); Assert.Equal(4, store.PreviewCards().Count);
        var asset = store.Assets().First(a => a.Kind == "Effect");
        Assert.Equal(File.ReadAllText(Path.Combine(result.Pack.Root, asset.Path)), store.Read(asset.Path));
        store.Discard();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void NativeDocumentSaveRoundTripsPresentationAndDeleteCleansReferences()
    {
        var store = Store; store.Import(Archive()); store.Begin("demo");
        var asset = store.CreateCard("roundtrip");
        store.SaveCardDocument(asset, store.Read(asset.Path), "显示说明", "背景描述");
        Assert.Equal("背景描述", store.PreviewCards().Single(c => c.CardId == asset.AppId).Flavor);
        Assert.Equal("显示说明", store.CardPresentation(asset.Id).Text);
        Assert.Contains(store.DeleteImpact(asset), a => a.Id == asset.Id);
        store.DeleteAsset(asset); Assert.DoesNotContain(store.PreviewCards(), c => c.CardId == asset.AppId); store.Promote();
    }

    [Fact]
    public void CompiledRulesReplaceOnlyEditorOwnedEffectsAndSurviveReopen()
    {
        var store = Store; store.Import(Archive()); store.Begin("demo"); var asset = store.CreateCard("rules");
        OrcEffectDraft[] rules = [new() { Template = "deploy_basic", Guard = "SelfUnit", Hook = "unit.deployed", Ops = [new() { Op = "draw", Count = 1 }] }];
        var json = store.Read(asset.Path); store.SaveCardDocument(asset, json, "部署：抽1张牌。", "", rules);
        var first = JsonNode.Parse(store.Read(asset.Path))!;
        var effects = first["components"]!.AsArray().OfType<JsonObject>().Single(c => c["component"]!.ToString() == "effects");
        Assert.Single(effects["inline"]!.AsArray());
        store.SaveCardDocument(asset, store.Read(asset.Path), "部署：抽1张牌。", "", rules);
        effects = JsonNode.Parse(store.Read(asset.Path))!["components"]!.AsArray().OfType<JsonObject>().Single(c => c["component"]!.ToString() == "effects"); Assert.Single(effects["inline"]!.AsArray());
        store.Promote(); store.Begin("demo"); Assert.Single(store.CardPresentation(asset.Id).EditableRules); store.Discard();
    }

    [Fact]
    public void FailedDocumentSaveRestoresSourceAndPresentation()
    {
        var store = Store; store.Import(Archive()); store.Begin("demo"); var asset = store.CreateCard("atomic");
        var source = store.Read(asset.Path);
        Assert.ThrowsAny<Exception>(() => store.SaveCardDocument(asset, "{", "不应保存", "不应保存"));
        Assert.Equal(source, store.Read(asset.Path)); Assert.Equal("", store.CardPresentation(asset.Id).Text); Assert.Equal("", store.CardPresentation(asset.Id).Flavor);
        store.Discard();
    }

    [Fact]
    public void CustomNationAndArtworkKeepAuthoringMetadataAcrossExport()
    {
        var store = Store; store.Import(Archive()); store.Begin("demo"); var asset = store.CreateCard("customnation");
        var nation = store.CreateNation("中文国家"); var document = new NativeCardDocument(store.Read(asset.Path)); document.Set("factionCost", "faction", JsonValue.Create(nation.Id));
        store.SaveCardDocument(asset, document.Json, "说明", "背景", []);
        store.SetArtwork(asset.Id, [1, 2, 3], false); Assert.Equal("背景", store.CardPresentation(asset.Id).Flavor);
        Assert.Contains("authoringEffects", File.ReadAllText(Path.Combine(store.TemporaryRoot!, "workshop.presentation.json")));
        Assert.Contains(store.PreviewCards(), c => c.CardId == asset.AppId); store.Promote();
        var zip = Path.Combine(_root, "custom.zip"); store.Export("demo", zip);
        var other = new NativeWorkshop(Path.Combine(_root, "other"), Path.Combine(_root, "none")); other.Import(zip); other.Begin("demo");
        Assert.Equal(nation.Id, Assert.Single(other.DraftNations).Id); Assert.Equal("背景", other.CardPresentation(asset.Id).Flavor); other.Discard();
    }

    [Fact]
    public void ShippedReferencesUseUserCopyAndInterruptedNewDraftCanBeExported()
    {
        var shipped = Path.Combine(_root, "shipped", "packs", "demo");
        foreach (var file in Directory.GetFiles(Sample, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(shipped, Path.GetRelativePath(Sample, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
        var before = Directory.GetFiles(shipped, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var store = Store; store.Create("reference.probe", "引用检查", "demo"); store.CreateCard("probe");
        Assert.DoesNotContain(store.SessionWarnings, w => w.Contains("demo") && w.Contains("未找到"));
        Assert.NotEmpty(Directory.GetDirectories(Path.Combine(_root, "user", "authoring-references")));
        Assert.DoesNotContain(store.EnabledRoots(), p => p.Contains("authoring-references"));
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        var restarted = Store; var draft = Assert.Single(restarted.InterruptedDrafts()); Assert.True(draft.IsNew);
        var zip = Path.Combine(_root, "interrupted.zip"); restarted.ExportInterruptedDraft(draft.Root, zip); Assert.True(File.Exists(zip)); store.Discard();
    }

    [Fact]
    public void CorruptCatalogKeepsLocalPackagesDisabled()
    {
        var store = Store; store.Import(Archive()); File.WriteAllText(Path.Combine(_root, "user", "catalog.json"), "{");
        var restarted = Store; Assert.False(Assert.Single(restarted.Packages()).Enabled); Assert.NotEmpty(restarted.StartupDiagnostics);
        Assert.Throws<InvalidDataException>(() => restarted.Begin("demo"));
    }

    [Fact]
    public void EditingEffectDescriptionKeepsOpaqueSourceAndIdentity()
    {
        var store = Store; store.Import(Archive()); store.Begin("demo"); var effect = store.Effects().First();
        var before = store.Read(effect.Asset.Path); store.SaveEffectInfo(effect.Asset.Id, "补给", "给玩家的说明");
        var after = store.Effects().Single(e => e.Asset.Id == effect.Asset.Id);
        Assert.Equal("补给", after.Name); Assert.Equal("给玩家的说明", after.Description); Assert.Null(after.Rules); Assert.Equal(before, store.Read(effect.Asset.Path)); store.Discard();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GraphAuthoredEffectExecutesAfterNativePromotion(bool shared)
    {
        // Keep shipped keyword effects available, including bindings registered by earlier tests.
        var host = OrcEngineHost.Current!; var baseline = host.CreateSnapshot();
        try
        {
            var store = new NativeWorkshop(Path.Combine(_root, "user"), @"H:\Working Folder\OrC-KSD.Godot\proto\data\kards", OrcEngineHost.SetWorkshopRoots);
            store.Import(Archive()); store.Begin("demo"); var asset = store.CreateCard("graph.draw");
            const string graph = """{"trigger":"deploy","targets":[],"actions":[{"op":"drawOne","side":"self"}]}""";
            if (shared)
            {
                var effect = store.CreateEffect("补给训练"); var original = store.Read(effect.Asset.Path);
                var backup = store.SaveEffect(effect, "补给训练", "部署时抽一张牌", [new() { DesktopJson = graph }]);
                Assert.Equal(original, File.ReadAllText(Path.Combine(backup, effect.Asset.Path)));
                Assert.Equal("补给训练", store.Effects().Single(e => e.Asset.Id == effect.Asset.Id).Name);
                var doc = new NativeCardDocument(store.Read(asset.Path)); doc.Set("effects", "prefabs", new JsonArray(effect.Asset.Id));
                store.SaveCardDocument(asset, doc.Json, "部署：抽1张牌。", "共享图形规则");
            }
            else store.SaveCardDocument(asset, store.Read(asset.Path), "部署：抽1张牌。", "图形规则", [new() { DesktopJson = graph }]);
            store.Promote();
            var catalog = OrcEngineHost.LoadPackCards();
            var source = new CardCatalog(); source.Load(@"H:\Working Folder\OrC-KSD.Godot\proto\data\nations");
            using var match = await OrcMatchSession.CreateFromCatalogAsync(source.Cards.Concat(catalog.Cards).ToArray(), 31, "native-graph", "unused", sourceDirectory: @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations", openingHand: [asset.AppId], firstPlayerIndex: 0);
            match.DriveOpponent = false; await match.SettleMulliganAsync(false); var actor = match.View.SelfHand.Single(c => c.CardId == asset.AppId); var count = match.View.SelfHand.Count;
            Assert.Equal(UiSubmitOutcome.Applied, await match.SubmitAsync(new PlayCard(actor.Uid))); match.SettleProjection();
            Assert.Equal(count, match.View.SelfHand.Count); Assert.Contains(match.View.SelfLine, c => c.Uid == actor.Uid);
        }
        finally { host.RestoreFrom(baseline); }
    }
}
