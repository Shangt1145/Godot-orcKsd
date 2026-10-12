using System.Text.Json.Serialization;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kards.Ui.Tests;

/// <summary>
/// Card-pack pipeline tests over the engine's own distribution surfaces: manifest → precheck →
/// load → keyword registration → real-match attach. Pack keyword ids use a unique family per run
/// because KeywordRegistry is process-global and duplicate registration is refused.
/// </summary>
public class PackSystemTests : IDisposable
{
    private static int _family;
    private readonly string _root;
    private readonly string _nations = Path.GetFullPath("../../../../proto/data/nations");

    public PackSystemTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OrC-KSD.UI", "pack-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private string Family() => "fam" + unchecked(++_family);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    private string UnitCard(string id, string name = "测试步兵") => $$"""
{
  "schemaVersion": 1,
  "id": "{{id}}",
  "name": "{{name}}",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "Germany", "kredits": 1 },
    { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 3 },
    { "component": "tagData", "rarity": "Standard" }
  ]
}
""";

    private string CommandCard(string id) => $$"""
{
  "schemaVersion": 1,
  "id": "{{id}}",
  "name": "测试指令",
  "components": [
    { "component": "typeCategory", "type": ["order"] },
    { "component": "factionCost", "faction": "Germany", "kredits": 1 },
    { "component": "tagData", "rarity": "Standard" }
  ]
}
""";

    private string RallyLibraryPrefab(string effectId) => $$"""
{
  "schemaVersion": 2,
  "root": {
    "id": "{{effectId}}",
    "version": 1,
    "mainTrigger": {
      "id": "t.main",
      "stableKey": "测试·抽牌",
      "kind": "passive",
      "viewType": "Orc.Cards.CardEventView",
      "hooks": ["unit.deployed"],
      "events": [
        {
          "id": "onUnitDeployed",
          "entry": "HandleAsync",
          "csx": "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>\n{\n    if (view.Unit is not Orc.Cards.Card deployed || !object.ReferenceEquals(deployed, view.Host)) return;\n    var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(deployed);\n    if (runtime is null) return;\n    await runtime.DrawAsync(deployed, 1, ct);\n};",
          "version": 1
        }
      ],
      "version": 1
    },
    "otherTriggers": [],
    "modings": [],
    "injects": []
  }
}
""";

    private string CreatePack(string? id = null, string? kind = "pack", string? dependsOn = null,
        string? extraCard = null, string? libraryPrefab = null, string? keywordsJson = null)
    {
        id ??= Family();
        var root = Path.Combine(_root, id);
        Write(Path.Combine(root, "pack.json"), $$"""
{
  "schemaVersion": 1,
  "id": "{{id}}",
  "name": "测试卡包{{id}}",
  "kind": "{{kind}}",
  "version": 1{{(dependsOn is null ? "" : $",\n  \"dependsOn\": [\"{dependsOn}\"]")}}{{(keywordsJson is null ? "" : ",\n  \"keywords\": " + keywordsJson)}}
}
""");
        Write(Path.Combine(root, "cards", "test.infantry.card.json"), UnitCard("test.infantry"));
        if (extraCard is not null) Write(Path.Combine(root, "cards", "test.extra.card.json"), extraCard);
        if (libraryPrefab is not null)
            Write(Path.Combine(root, "effects", libraryPrefab.Split('.')[^2] + ".prefab.json"), RallyLibraryPrefab(libraryPrefab));
        return root;
    }

    // ---------------------------------------------------------------- manifest / load

    [Fact]
    public void Load_ValidPack_ProducesPrefixedProjections()
    {
        var pack = PackSystem.Load(CreatePack(), []);
        Assert.Single(pack.Definitions);
        var projection = Assert.Single(pack.Projections);
        Assert.Equal("pack/" + pack.Manifest.Id + "/test.infantry", projection.CardId);
        Assert.Equal("unit", projection.CardType);
        Assert.Equal(2, projection.BaseAttack);
        Assert.Equal(3, projection.BaseDefense);
        Assert.True(projection.Set.StartsWith("pack:", StringComparison.Ordinal));
        PackSystem.DisableKeywords(pack.Manifest.Id);
    }

    [Fact]
    public void Load_MissingManifest_IsRejected()
    {
        var root = Path.Combine(_root, Family());
        Directory.CreateDirectory(Path.Combine(root, "cards"));
        Assert.Throws<InvalidDataException>(() => PackSystem.Load(root, []));
    }

    [Fact]
    public void Load_ExpansionWithoutEnabledBase_IsRejected()
    {
        var basePack = CreatePack();
        var pack = PackSystem.Load(basePack, []);
        var expansionRoot = CreatePack(kind: "expansion", dependsOn: pack.Manifest.Id);
        var error = Assert.Throws<InvalidDataException>(() => PackSystem.Load(expansionRoot, []));
        Assert.Contains("未启用", error.Message);
        PackSystem.DisableKeywords(pack.Manifest.Id);
    }

    [Fact]
    public void Load_ExpansionWithCommandCard_IsRejected()
    {
        var basePack = CreatePack();
        var pack = PackSystem.Load(basePack, []);
        var expansionRoot = CreatePack(kind: "expansion", dependsOn: pack.Manifest.Id, extraCard: CommandCard("test.order"));
        var error = Assert.Throws<InvalidDataException>(() => PackSystem.Load(expansionRoot, [pack]));
        Assert.Contains("单位", error.Message);
        PackSystem.DisableKeywords(pack.Manifest.Id);
    }

    [Fact]
    public void Load_ExpansionReferencingUnknownEffect_IsRejected()
    {
        var basePack = CreatePack();
        var pack = PackSystem.Load(basePack, []);
        var expansionRoot = CreatePack(kind: "expansion", dependsOn: pack.Manifest.Id, extraCard: $$"""
{
  "schemaVersion": 1,
  "id": "test.draw",
  "name": "测试征召兵",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "Germany", "kredits": 2 },
    { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 2 },
    { "component": "tagData", "rarity": "Standard" },
    { "component": "effects", "prefabs": ["does.not.exist"] }
  ]
}
""");
        var error = Assert.Throws<InvalidDataException>(() => PackSystem.Load(expansionRoot, [pack]));
        Assert.Contains("does.not.exist", error.Message);
        PackSystem.DisableKeywords(pack.Manifest.Id);
    }

    [Fact]
    public void Load_UnmappableCardAttribute_IsRejectedInsteadOfSilentlyDropped()
    {
        var root = CreatePack(extraCard: UnitCard("test.sneaky", "潜行测试"));
        // Append an attribute the engine cannot map — the data loader parks it silently; the pack
        // pipeline must refuse rather than ship a card whose keyword is quietly missing.
        var cardPath = Path.Combine(root, "cards", "test.extra.card.json");
        var json = File.ReadAllText(cardPath).Replace(
            """{ "component": "tagData", "rarity": "Standard" }""",
            """
            { "component": "tagData", "rarity": "Standard" },
            { "component": "keywords", "attributes": ["definitelyNotAKeyword"] }
            """);
        File.WriteAllText(cardPath, json);
        var error = Assert.Throws<InvalidDataException>(() => PackSystem.Load(root, []));
        Assert.Contains("无法映射", error.Message);
    }

    [Fact]
    public void Load_FailsPrecheck_IsRejected()
    {
        var root = CreatePack();
        File.WriteAllText(Path.Combine(root, "cards", "broken.card.json"), "{ not valid");
        Assert.Throws<InvalidDataException>(() => PackSystem.Load(root, []));
    }

    // ---------------------------------------------------------------- keywords

    [Fact]
    public void Keywords_RegisterThenUnregister_AffectDefinitionAdmission()
    {
        var keywordId = "试炼" + Family();
        var pack = PackSystem.Load(CreatePack(libraryPrefab: "test.pack.blessing",
            keywordsJson: $$"""[{ "id": "{{keywordId}}", "label": "试炼", "battle": false, "bindings": [{ "prefabId": "test.pack.blessing", "effectName": "试炼·测试" }] }]"""), []);
        Assert.Contains(keywordId, pack.RegisteredKeywords);
        // Registered: a definition may declare it. Unregistered: the same construction fail-fasts.
        var definition = () => new CardDefinition("词条探测", 1, 0, 1, 1,
            keywords: [new KeywordDeclaration(keywordId)],
            faction: Faction.Germany, rarity: Rarity.Standard);
        definition();
        PackSystem.DisableKeywords(pack.Manifest.Id);
        Assert.Throws<ArgumentException>(definition);
    }

    [Fact]
    public void Keywords_BuiltInNameClash_IsRejected()
    {
        var root = CreatePack(keywordsJson: """[{ "id": "闪击", "label": "闪击", "battle": true, "bindings": [{ "prefabId": "x", "effectName": "x" }] }]""");
        Assert.Throws<InvalidDataException>(() => PackSystem.Load(root, []));
    }

    [Fact]
    public void Keywords_DuplicateAcrossPacks_IsRejected()
    {
        var keywordId = "共享" + Family();
        var manifestJson = $$"""[{ "id": "{{keywordId}}", "label": "共享", "battle": false, "bindings": [{ "prefabId": "test.pack.blessing", "effectName": "共享·测试" }] }]""";
        var first = PackSystem.Load(CreatePack(libraryPrefab: "test.pack.blessing", keywordsJson: manifestJson), []);
        var secondRoot = CreatePack(libraryPrefab: "test.pack.blessing", keywordsJson: manifestJson);
        var error = Assert.Throws<InvalidDataException>(() => PackSystem.Load(secondRoot, [first]));
        Assert.Contains(keywordId, error.Message);
        PackSystem.DisableKeywords(first.Manifest.Id);
    }

    // ---------------------------------------------------------------- pack library

    [Fact]
    public void PackLibrary_ImportEnableDisable_RoundTrips()
    {
        var root = Path.Combine(_root, "lib-root");
        var library = new PackLibrary(root);
        var source = CreatePack();
        var entry = library.Import(source);
        Assert.False(entry.Enabled);
        Assert.True(Directory.Exists(Path.Combine(root, entry.Id, "cards")));
        Assert.Throws<InvalidDataException>(() => library.Import(source));

        library.SetEnabled(entry.Id, true);
        var projections = library.Reload();
        Assert.True(projections.Count == 1, string.Join("|", library.Entries.Select(e => e.LoadError ?? "ok")));
        Assert.True(library.Entries.Single(e => e.Id == entry.Id).Enabled, library.Entries.Single(e => e.Id == entry.Id).LoadError ?? "");
        Assert.Single(PackSystem.EnabledPacks.Where(p => p.Manifest.Id == entry.Id));

        library.SetEnabled(entry.Id, false);
        Assert.Empty(library.Reload());
        PackSystem.DisableKeywords(entry.Id);
    }

    [Fact]
    public void PackLibrary_ExpansionWithoutBase_DisabledWithReportedError()
    {
        var root = Path.Combine(_root, "lib-root2");
        var library = new PackLibrary(root);
        var baseSource = CreatePack();
        var baseEntry = library.Import(baseSource);
        var expansion = CreatePack(kind: "expansion", dependsOn: PackSystem.ReadManifest(baseSource).Id);
        library.Import(expansion);
        library.SetEnabled(baseEntry.Id, false);
        library.SetEnabled(PackSystem.ReadManifest(expansion).Id, true);
        // SetEnabled already reloaded once; the failure path disabled the expansion and recorded why.
        var error = library.Entries.Single(e => e.Id == PackSystem.ReadManifest(expansion).Id).LoadError;
        Assert.NotNull(error);
        Assert.Contains("未启用", error);
    }

    // ---------------------------------------------------------------- real match

    private string UnitCardWithInlineEffect(string id, string effectId) => $$"""
{
  "schemaVersion": 1,
  "id": "{{id}}",
  "name": "测试军需官",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "Germany", "kredits": 2 },
    { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 2 },
    { "component": "tagData", "rarity": "Standard" },
    { "component": "effects", "inline": [ {{RallyLibraryPrefab(effectId)}} ] }
  ]
}
""";

    private string UnitCardWithLibraryEffect(string id, string effectId) => $$"""
{
  "schemaVersion": 1,
  "id": "{{id}}",
  "name": "测试征召兵",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "Germany", "kredits": 2 },
    { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 2 },
    { "component": "tagData", "rarity": "Standard" },
    { "component": "effects", "prefabs": ["{{effectId}}"] }
  ]
}
""";

    private async Task AssertPackCardJoinsRealMatch(string packRoot, string extraCardId, string libraryEffectId)
    {
        var catalog = new CardCatalog();
        catalog.Load(_nations);
        var pack = PackSystem.Load(packRoot, []);
        try
        {
            Assert.Contains(pack.Definitions, d => d.Id == extraCardId);
            var deck = new ConstructedDeck
            {
                Name = "卡包测试", Options = new DeckOptions { UnrestrictedNations = true, TotalCards = 7 },
                CardIds =
                [
                    .. Enumerable.Repeat(pack.Prefixed(extraCardId), 2),
                    "USG/units/_4", "USG/units/_4", "av76/units/13", "av76/units/18",
                ],
            };
            // Session creation itself asserts every pack card's declared effect mounted: the
            // CreateNativeAsync admission loop throws on any missing or unmounted effect.
            var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, seed: 7,
                matchId: "pack-test", fallbackCardId: "USG/units/_4",
                sourceDirectory: _nations, constructedDeck: deck, packs: [pack]);
            Assert.NotNull(session);
            session.Dispose();
        }
        finally { PackSystem.DisableKeywords(pack.Manifest.Id); }
    }

    [Fact]
    public async Task PackCard_WithInlineEffect_JoinsRealMatch() =>
        await AssertPackCardJoinsRealMatch(
            CreatePack(extraCard: UnitCardWithInlineEffect("test.rallyman", "test.inline.rally")),
            "test.rallyman", "test.inline.rally");

    [Fact]
    public async Task PackCard_WithLibraryEffect_JoinsRealMatch() =>
        await AssertPackCardJoinsRealMatch(
            CreatePack(libraryPrefab: "test.pack.rally", extraCard: UnitCardWithLibraryEffect("test.rallyman", "test.pack.rally")),
            "test.rallyman", "test.pack.rally");
}
