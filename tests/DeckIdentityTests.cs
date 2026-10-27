using System.Text;
using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// P5 存档与 ID：卡组引用在"删卡重导"后还能不能对上，以及存档版本读不懂时的行为。
/// 判据都是"文件里到底是什么"，不看调用方有没有提示——提示可以撒谎，字节不会。
/// </summary>
public class DeckIdentityTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orcksd-p5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static DiyCardDraft Card(string name = "测试步兵") => new() { Name = name, Cost = 1, OpCost = 1 };

    // ── 卡组引用 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 删卡后重导同一张卡沿用原编号_卡组引用自动接上()
    {
        var dir = NewDir();
        var library = new DiyCardLibrary(Path.Combine(dir, "cards.json"));
        var original = Card();
        library.Save(original);
        var code = DiyCardLibrary.Export(original);

        library.Delete(original.Id);
        Assert.Empty(library.Cards);

        var (restored, reused) = library.ImportCard(code);
        Assert.True(reused, "编号空闲时导入应当沿用原编号");
        Assert.Equal(original.Id, restored.Id);

        library.Save(restored);
        // 卡组存的是原来的 diy/<guid>，重新导入后必须还能解析到这张卡。
        var deck = new ConstructedDeck { Name = "测试卡组", CardIds = [original.Id] };
        Assert.Empty(ConstructionRules.Missing(deck, library.Cards.Select(c => Definition(c.Id)).ToArray()));
    }

    [Fact]
    public void 编号被占用时导入另发新号_绝不覆盖现有卡牌()
    {
        var dir = NewDir();
        var library = new DiyCardLibrary(Path.Combine(dir, "cards.json"));
        var occupant = Card("本机的卡");
        library.Save(occupant);

        // 同一编号、不同名字的代码（模拟"另一张卡用了同一个号"）。
        var code = DiyCardLibrary.Export(Card("别人的卡") with { Id = occupant.Id });
        var (imported, reused) = library.ImportCard(code);

        Assert.False(reused, "编号已被占用时必须另发新号");
        Assert.NotEqual(occupant.Id, imported.Id);
        library.Save(imported);
        Assert.Equal(2, library.Cards.Count);
        Assert.Equal("本机的卡", library.Cards.Single(c => c.Id == occupant.Id).Name);
    }

    [Fact]
    public void 静态Import始终换号_Parse保留代码里的编号()
    {
        var draft = Card() with { Id = "diy/fixed0001" };
        var code = DiyCardLibrary.Export(draft);
        // Parse 是"读出来是什么就是什么"，Import 是"永远当成一张新卡"——
        // 两个语义分开写，才不用为了落盘改掉导入的旧约定。
        Assert.Equal("diy/fixed0001", DiyCardLibrary.Parse(code).Id);
        Assert.NotEqual("diy/fixed0001", DiyCardLibrary.Import(code).Id);
    }

    [Fact]
    public void 断链能被检出_重新绑定只换编号不动张数()
    {
        var deck = new ConstructedDeck { Name = "断链卡组", CardIds = ["diy/lost", "diy/lost", "diy/kept", "orc/x"] };
        var catalog = new[] { Definition("diy/kept"), Definition("orc/x") };

        var missing = ConstructionRules.Missing(deck, catalog);
        Assert.Equal(["diy/lost"], missing);

        var rebound = deck.Rebind("diy/lost", "diy/kept");
        Assert.Equal(deck.CardIds.Length, rebound.CardIds.Length);
        Assert.DoesNotContain("diy/lost", rebound.CardIds);
        Assert.Equal(3, rebound.CardIds.Count(id => id == "diy/kept"));
        Assert.Empty(ConstructionRules.Missing(rebound, catalog));
        // 换的只是编号，别的字段一个不动。
        Assert.Equal(deck.Name, rebound.Name);
        Assert.Equal(deck.Id, rebound.Id);
    }

    private static UiCardDefinition Definition(string id) => new()
    {
        CardId = id, Name = id, Set = "av76", Rarity = "iron", CardType = "unit", UnitType = "infantry",
    };

    // ── 存档版本 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 版本太新与太旧说的是不同的话_且原文件一个字节都不动()
    {
        var newer = NewDir();
        var newerPath = Path.Combine(newer, "cards.json");
        var newerBytes = Encoding.UTF8.GetBytes("{\"Schema\":99,\"Cards\":[]}");
        File.WriteAllBytes(newerPath, newerBytes);
        var newerLibrary = new DiyCardLibrary(newerPath);
        Assert.Contains("更新版本", newerLibrary.LoadError);
        Assert.Equal(newerBytes, File.ReadAllBytes(newerPath));

        var older = NewDir();
        var olderPath = Path.Combine(older, "cards.json");
        var olderBytes = Encoding.UTF8.GetBytes("{\"Schema\":0,\"Cards\":[]}");
        File.WriteAllBytes(olderPath, olderBytes);
        var olderLibrary = new DiyCardLibrary(olderPath);
        Assert.Contains("太旧", olderLibrary.LoadError);
        Assert.Equal(olderBytes, File.ReadAllBytes(olderPath));

        // 读失败之后不许写：否则"读不懂但还有救"的文件会被一份空库覆盖掉。
        Assert.Throws<IOException>(() => newerLibrary.Save(Card()));
        Assert.Equal(newerBytes, File.ReadAllBytes(newerPath));
    }

    [Fact]
    public void 导入库读失败后拒绝写入_原文件不被覆盖()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "imports.json");
        var original = Encoding.UTF8.GetBytes("{\"Schema\":99,\"Rows\":[]}");
        File.WriteAllBytes(path, original);

        var library = new OrcImportLibrary(path);
        Assert.NotNull(library.LoadError);
        var row = new OrcImportRow("orc/one", "测试", "unit", 1, 1, 1, 1, "");
        // 以前这里没有闸：读失败 → _rows 空 → 导入一行就把原文件整份覆盖掉。
        Assert.Throws<IOException>(() => library.Merge([row]));
        Assert.Throws<IOException>(() => library.Remove("orc/one"));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void 四处库都走同一个版本判定_版本号是唯一真源()
    {
        Assert.Equal(1, SchemaVersioning.Latest);
        Assert.True(SchemaVersioning.Acceptable(1));
        Assert.False(SchemaVersioning.Acceptable(0));
        Assert.False(SchemaVersioning.Acceptable(SchemaVersioning.Latest + 1));
        Assert.Equal("原样返回", SchemaVersioning.Migrate("原样返回", 1, "测试库"));
        var tooNew = Assert.Throws<InvalidDataException>(() => SchemaVersioning.Require(2, "测试库"));
        Assert.Contains("测试库", tooNew.Message);
        Assert.Contains("原文件保留", tooNew.Message);
    }

    [Fact]
    public void 卡组与工坊读不懂时也保留原文件()
    {
        var dir = NewDir();
        var deckPath = Path.Combine(dir, "decks.json");
        var deckBytes = Encoding.UTF8.GetBytes("{\"Schema\":99,\"ActiveId\":null,\"Decks\":[]}");
        File.WriteAllBytes(deckPath, deckBytes);
        var decks = new DeckLibrary(deckPath);
        Assert.Contains("更新版本", decks.LoadError);
        Assert.Throws<IOException>(() => decks.Save(new ConstructedDeck()));
        Assert.Equal(deckBytes, File.ReadAllBytes(deckPath));

        var workshopPath = Path.Combine(dir, "workshop.json");
        var workshopBytes = Encoding.UTF8.GetBytes("{\"Schema\":99,\"Packs\":[]}");
        File.WriteAllBytes(workshopPath, workshopBytes);
        var workshop = new DiyWorkshopLibrary(workshopPath);
        Assert.Contains("更新版本", workshop.LoadError);
        Assert.Equal(workshopBytes, File.ReadAllBytes(workshopPath));
    }
}
