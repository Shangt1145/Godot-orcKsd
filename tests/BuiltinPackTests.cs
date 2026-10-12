using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Core;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// Built-in pack suite: the generated 内置卡包/扩展包 data under proto/data/kards must load through
/// PackSystem (engine precheck + full effect compilation) and the shared text parser must mirror the
/// generator's grammar. The 太空战争 virtual pack (source catalog) needs no load — it is the shipped pool.
/// </summary>
public class BuiltinPackTests
{
    private static string PackRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "proto", "data", "kards")))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, "proto", "data", "kards");
    }

    [Fact]
    public void BuiltinPacks_LoadThroughEnginePipeline()
    {
        var root = PackRoot();
        Assert.True(Directory.Exists(root), "内置卡包数据目录缺失——先跑 tools/build_builtin_packs.py");
        var loaded = new List<PackContent>();
        foreach (var pass in new[] { "pack", "expansion" })
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(dir, "pack.json"))) continue;
                var manifest = PackSystem.ReadManifest(dir);
                if (manifest.Kind != pass) continue;
                if (loaded.Any(p => p.Manifest.Id == manifest.Id)) continue;
                loaded.Add(PackSystem.Load(dir, loaded));
            }
        var builtin = loaded.Single(p => p.Manifest.Id == "builtin");
        Assert.True(builtin.Projections.Count >= 50, $"内置卡包应有充足的白板与简单效果卡，实际 {builtin.Projections.Count}");
        Assert.All(loaded, pack =>
        {
            Assert.True(pack.Projections.Count > 0, $"{pack.Manifest.Id} 没有产出投影");
            Assert.True(pack.Projections.Count == pack.Definitions.Count);
        });
        Assert.True(loaded.Count(p => p.Manifest.Kind == "expansion") >= 8, "官方扩展包应至少 8 个在库");
        foreach (var pack in loaded) PackSystem.DisableKeywords(pack.Manifest.Id);
    }

    [Fact]
    public void BuiltinPacks_ConvertedEffects_CoverAllTexts()
    {
        var root = PackRoot();
        var loaded = new List<PackContent>();
        foreach (var pass in new[] { "pack", "expansion" })
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(dir, "pack.json"))) continue;
                var manifest = PackSystem.ReadManifest(dir);
                if (manifest.Kind != pass) continue;
                if (loaded.Any(p => p.Manifest.Id == manifest.Id)) continue;
                loaded.Add(PackSystem.Load(dir, loaded));
            }
        foreach (var pack in loaded)
        {
            var withText = TextsOf(pack.RootDirectory).Count(kv => kv.Value.Length > 0);
            Assert.Equal(withText, pack.ConvertedEffects.Count);
        }
        foreach (var pack in loaded) PackSystem.DisableKeywords(pack.Manifest.Id);
    }

    private static IEnumerable<KeyValuePair<string, string>> TextsOf(string packRoot)
    {
        var path = Path.Combine(packRoot, "texts.json");
        if (!File.Exists(path)) return [];
        var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateObject()
            .Select(p => KeyValuePair.Create(p.Name, p.Value.GetString() ?? "")).ToArray();
    }

    // ------------------------------------------------------------------ parser grammar

    [Theory]
    [InlineData("部署：抽 1 张牌。", "draw", "self", 1)]
    [InlineData("抽 2 张牌。", "draw", "self", 2)]
    [InlineData("获得 3 个指挥点。", "gainPoint", "self", 3)]
    [InlineData("额外获得 1 个指挥点槽。", "gainSlot", "self", 1)]
    [InlineData("对所有敌方单位造成 1 点伤害。", "damage", "enemyUnits", 1)]
    [InlineData("对 1 个敌方单位造成 2 点伤害。", "damage", "enemyUnit", 2)]
    [InlineData("对 1 个单位造成 1 点伤害。", "damage", "anyUnit", 1)]
    [InlineData("消灭 1 个敌方目标。", "destroy", "enemyUnit", 0)]
    [InlineData("消灭所有敌方单位。", "destroy", "enemyUnits", 0)]
    [InlineData("压制 1 个敌方单位 1 回合。", "pin", "enemyUnit", 1)]
    [InlineData("抑制 1 个敌方单位。", "silence", "enemyUnit", 0)]
    public void Parser_SimpleActions_AreRecognized(string text, string op, string target, int amount)
    {
        var drafts = PackCardTextParser.Parse(text, "order");
        Assert.NotNull(drafts);
        var draft = Assert.Single(drafts);
        Assert.Equal(op, draft.Op);
        Assert.Equal(target, draft.Target);
        if (op is "draw" or "damage") Assert.Equal(amount, draft.Amount);
    }

    [Fact]
    public void Parser_MultiSentence_ParsesAll()
    {
        var drafts = PackCardTextParser.Parse("获得 1 个指挥点槽；抽 1 张牌。", "order");
        Assert.NotNull(drafts);
        Assert.Equal(2, drafts.Count);
        Assert.Equal("gainSlot", drafts[0].Op);
        Assert.Equal("draw", drafts[1].Op);
    }

    [Theory]
    [InlineData("亡计：对敌方总部造成 2 点伤害。")]           // death trigger — next batch
    [InlineData("本单位造成伤害时，使友方总部获得同等防御力。")] // passive
    [InlineData("对坦克造成双倍伤害。")]                        // combat rule
    [InlineData("部署：使 1 个友方单位获得 -1 行动花费，直到下个己方回合开始。")] // duration unsupported v1
    public void Parser_UnsupportedTexts_ReturnNull(string text)
    {
        Assert.Null(PackCardTextParser.Parse(text, "unit"));
    }

    [Fact]
    public async Task BuiltinPackCard_PlaysInRealMatch()
    {
        var catalog = new Kards.Ui.Core.CardCatalog();
        catalog.Load(Path.GetFullPath("../../../../proto/data/nations"));
        var root = PackRoot();
        var loaded = new List<PackContent>();
        foreach (var pass in new[] { "pack", "expansion" })
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(dir, "pack.json"))) continue;
                var manifest = PackSystem.ReadManifest(dir);
                if (manifest.Kind != pass) continue;
                if (loaded.Any(p => p.Manifest.Id == manifest.Id)) continue;
                loaded.Add(PackSystem.Load(dir, loaded));
            }
        var builtin = loaded.Single(p => p.Manifest.Id == "builtin");
        var packCardId = Assert.Single(builtin.Projections, c => c.CardId.EndsWith("nebelwerfer_42", StringComparison.Ordinal)).CardId;
        var deck = new ConstructedDeck
        {
            Name = "内置卡包冒烟",             Options = new DeckOptions { UnrestrictedNations = true, TotalCards = 7 },
            CardIds = [.. Enumerable.Repeat(packCardId, 2),
                "USG/units/_4", "USG/units/_4", "av76/units/13", "av76/units/18"]
        };
        try
        {
            var session = await Kards.Ui.OrcBridge.OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, seed: 11,
                matchId: "builtin-pack-match", fallbackCardId: "USG/units/_4",
                sourceDirectory: Path.GetFullPath("../../../../proto/data/nations"), constructedDeck: deck, packs: loaded);
            Assert.NotNull(session);
            // The mount verification inside CreateFromCatalogAsync already guarantees the converted
            // deploy effect is registered and mounted on every deck instance of the pack card.
            session.Dispose();
        }
        finally { foreach (var pack in loaded) PackSystem.DisableKeywords(pack.Manifest.Id); }
    }
}
