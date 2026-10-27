using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// P3：DIY 卡包整包导出/导入。
///
/// <para>
/// 判据是"对方装上就能用"，不是"ZIP 生成了"：所以这里走完整条链路——草稿库 → 导出 ZIP →
/// 另一台机器的工坊目录导入 → 卡真的进了卡池，且国家/词条/卡图/风味都还在。
/// </para>
/// </summary>
[Collection("Workshop")]
public sealed class DiyPackExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ksd-pack-export-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");

    public DiyPackExportTests() { Directory.CreateDirectory(_root); OrcEngineHost.WarmUp(); }

    private sealed record Fixture(DiyWorkshopLibrary Packs, DiyCardLibrary Cards, DiyPackDraft Pack,
        DiyCardDraft Unit, DiyCardDraft Order);

    /// <summary>一个"齐全"的包：自定义国家 + 自定义词条 + 一张带图单位 + 一张指令。</summary>
    private Fixture Build()
    {
        var packs = new DiyWorkshopLibrary(Path.Combine(_root, "packs.json"));
        var cards = new DiyCardLibrary(Path.Combine(_root, "diy", "cards.json"));
        var nation = new DiyNationDraft { Name = "测试国" };
        var keyword = new DiyKeywordDraft { Name = "成长", EffectText = "部署：获得 +1+1。" };
        var pack = new DiyPackDraft { Name = "导出测试包", Nations = [nation], Keywords = [keyword] };
        packs.Save(pack);
        var unit = new DiyCardDraft
        {
            Name = "导出步兵", PackId = pack.Id, Nation = nation.Id, Cost = 0, OpCost = 0,
            Attack = 2, Defense = 3, Keywords = [keyword.Id], Flavor = "导出的风味文本",
        };
        cards.Save(unit);
        unit = unit with { ArtFile = cards.SaveArt(unit.Id, Png) };
        cards.Save(unit);
        var order = new DiyCardDraft
        {
            Name = "导出指令", PackId = pack.Id, Nation = nation.Id, CardType = "order", Cost = 1,
            EffectText = "抽1张牌。",
        };
        cards.Save(order);
        WorkshopAuthoring.Configure(packs, cards.Cards);
        return new(packs, cards, pack, unit, order);
    }

    [Fact]
    public void DIY卡包导出ZIP后对方导入即可用()
    {
        var fixture = Build();
        var zip = Path.Combine(_root, "pack.zip");
        var result = DiyPackExport.Export(fixture.Pack, fixture.Cards.Cards.Where(c => c.PackId == fixture.Pack.Id).ToArray(),
            [], fixture.Cards.ArtDirectory, zip);

        Assert.Empty(result.Problems);
        Assert.Equal(zip, result.Archive);
        Assert.Equal(2, result.Cards);
        Assert.True(File.Exists(zip));

        // 对方机器：全新工坊目录，用界面上的同一个入口（NativeWorkshop.Import）安装。
        var other = new NativeWorkshop(Path.Combine(_root, "other"), Path.Combine(_root, "no-shipped"));
        var imported = other.Import(zip);
        Assert.Equal(fixture.Pack.Id[5..], imported.Pack.Id);
        Assert.Equal(2, imported.Pack.Cards);
        Assert.Empty(imported.Pack.Diagnostics);
        Assert.True(imported.Pack.Enabled);

        // 国家、词条、卡图、风味、卡面文本都要在包里，不能只是"卡定义进去了"。
        var root = imported.Pack.Root;
        var presentation = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "workshop.presentation.json")))!;
        Assert.Contains(presentation["nations"]!.AsArray(), n => n!["id"]!.ToString() == fixture.Unit.Nation);

        // 词条：规则进 network-vocabulary.json，名字进展示层。两边的 id 必须**同一个**——
        // 对不上就等于收包人卡面上那串 "◇ kw-76e1c7658f9a…" 永远换不回名字。
        Assert.True(WorkshopAuthoring.TryKeyword(fixture.Pack.Keywords[0].Id, out var engineKeyword));
        var vocabulary = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "network-vocabulary.json")))!.AsArray();
        Assert.Contains(vocabulary, row => row!["id"]!.ToString() == engineKeyword);
        var keywordName = Assert.Single(NativeWorkshop.ReadKeywords(root));
        Assert.Equal(engineKeyword, keywordName.Id);
        Assert.Equal("成长", keywordName.Label);

        // 卡面数据挂在包内局部 id（card.<hash>）上，不是草稿 id：按风味文本反查那一格。
        var face = Assert.Single(presentation["cards"]!.AsObject(),
            c => c.Value!["flavor"]?.ToString() == "导出的风味文本");
        var art = Path.Combine(root, face.Value!["art"]!.ToString());
        Assert.Equal(Png, File.ReadAllBytes(art));

        // 卡名在卡定义里，卡面规则文本在 display-texts.json 里——同一张卡两处都要有。
        var texts = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "display-texts.json")))!.AsObject();
        Assert.True(texts.ContainsKey(face.Key), "卡面文本里没有这张卡：" + face.Key);
        var definition = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "cards", face.Key + ".card.json")))!;
        Assert.Equal("导出步兵", definition["name"]!.ToString());

        // 最后一格：收包人的卡面。他的机器上没有我的草稿库，所以名字只能从包里来——
        // 这才是"词条齐全"在玩家眼里的样子，而不是"JSON 里有这个 id"。
        var host = OrcEngineHost.Current!; var baseline = host.CreateSnapshot();
        try
        {
            WorkshopAuthoring.Configure(new DiyWorkshopLibrary(Path.Combine(_root, "recipient.json")), []);
            new NativeWorkshop(Path.Combine(_root, "live"), Path.Combine(_root, "no-shipped"), OrcEngineHost.SetWorkshopRoots).Import(zip);
            Assert.Equal("成长", WorkshopAuthoring.KeywordLabel(engineKeyword));
            Assert.Equal("测试国", WorkshopAuthoring.NationLabel(fixture.Unit.Nation));
        }
        finally
        {
            host.RestoreFrom(baseline);
            WorkshopAuthoring.Configure(fixture.Packs, fixture.Cards.Cards);
        }
    }

    [Fact]
    public void 分享前对账把坏卡挡在导出之前()
    {
        var fixture = Build();
        // 卡图被手工删掉（玩家清理磁盘时最容易发生的事）。
        File.Delete(Path.Combine(fixture.Cards.ArtDirectory, fixture.Unit.ArtFile));
        var zip = Path.Combine(_root, "blocked.zip");
        var result = DiyPackExport.Export(fixture.Pack, fixture.Cards.Cards.Where(c => c.PackId == fixture.Pack.Id).ToArray(),
            [], fixture.Cards.ArtDirectory, zip);

        Assert.Null(result.Archive);
        Assert.False(File.Exists(zip));
        Assert.Contains(result.Problems, p => p.Contains("卡图", StringComparison.Ordinal) && p.Contains(fixture.Unit.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void 空包与原生卡本地修改都不许静默导出()
    {
        var fixture = Build();
        var empty = new DiyPackDraft { Name = "空包" };
        fixture.Packs.Save(empty);
        var problems = DiyPackExport.Check(empty, [], [], fixture.Cards.ArtDirectory);
        Assert.Contains(problems, p => p.Contains("还没有卡牌", StringComparison.Ordinal));

        var edited = fixture.Unit with { Id = "diy/edited-source", SourceCardId = "pack/builtin/card.95th_rifle_regiment", PackId = fixture.Pack.Id };
        problems = DiyPackExport.Check(fixture.Pack, [edited], [], fixture.Cards.ArtDirectory);
        Assert.Contains(problems, p => p.Contains("本地修改", StringComparison.Ordinal));
    }

    [Fact]
    public void 扩展包缺基础包只提示不拦截()
    {
        var fixture = Build();
        var expansion = new DiyPackDraft { Name = "扩展测试包", BasePackId = fixture.Pack.Id };
        fixture.Packs.Save(expansion);
        var card = new DiyCardDraft
        {
            Name = "扩展步兵", PackId = expansion.Id, Nation = fixture.Pack.Nations[0].Id,
            Cost = 0, OpCost = 0, Attack = 1, Defense = 1,
        };
        fixture.Cards.Save(card);
        WorkshopAuthoring.Configure(fixture.Packs, fixture.Cards.Cards);

        var zip = Path.Combine(_root, "expansion.zip");
        var result = DiyPackExport.Export(expansion, [card], [], fixture.Cards.ArtDirectory, zip);

        // 扩展包本身是完整的：ZIP 必须真的写出来，"要先装基础包"只能出现在提示里。
        Assert.Equal(zip, result.Archive);
        Assert.True(File.Exists(zip));
        Assert.Contains(result.Problems, p => p.Contains("基础包", StringComparison.Ordinal));
    }

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { } }
}
