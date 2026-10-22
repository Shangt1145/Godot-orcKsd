using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// OrC 导入 UI 链路的落地证据：持久化往返（Merge/Remove/重载）、行集解析入口、
// 以及导入卡在真对局中的部署与效果触发（mount 校验拒掉假装配）。
// ─────────────────────────────────────────────────────────────────────────────

public class OrcImportUiTests(ITestOutputHelper output)
{
    private static string Source
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
            {
                var candidate = Path.Combine(d, "proto", "data", "nations");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("找不到卡源目录");
        }
    }

    [Fact]
    public void ImportLibraryPersistsRowsAcrossReload()
    {
        var path = Path.Combine(Path.GetTempPath(), "orc-import-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var library = new OrcImportLibrary(path);
            library.Merge(
            [
                new OrcImportRow("row-a", "甲", "infantry", 2, 1, 2, 3, "部署：抽 1 张牌。"),
                new OrcImportRow("row-b", "乙", "order", 1, 0, 0, 0, "获得 2 个指挥点"),
            ]);
            // 同 id 重导＝覆盖（修订口径），新 id＝追加。
            library.Merge([new OrcImportRow("row-a", "甲改", "tank", 3, 1, 3, 4, "闪击、重甲1")]);
            library.Remove("row-b");

            var reloaded = new OrcImportLibrary(path);
            Assert.Null(reloaded.LoadError);
            var row = Assert.Single(reloaded.Rows);
            Assert.Equal("row-a", row.Id);
            Assert.Equal("甲改", row.Name);
            Assert.Contains("重甲1", row.Text); // 文本往返保真
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseRowsAcceptsCorpusWrapperAndPlainArrays()
    {
        const string wrapper = """
            {"cards":[{"json":{"id":"w1","name":"甲","type":"order","text":{"zh-Hans":"获得 2 个指挥点"}}},
            {"json":{"id":"w2","name":"乙","type":"infantry","cost":3,"attack":4,"defense":5,"text":{"zh-Hans":"闪击"}}}]}
            """;
        var rows = OrcImportService.ParseRows(wrapper);
        Assert.Equal(2, rows.Count);
        Assert.Equal("order", rows[0].Type);
        Assert.Equal(3, rows[1].Cost); // 语料缺字段按默认补齐
        Assert.Equal(4, rows[1].Attack);

        const string plain = """
            [{"id":"p1","name":"丙","type":"infantry","text":"部署：抽 1 张牌。"}]
            """;
        var single = OrcImportService.ParseRows(plain);
        var row = Assert.Single(single);
        Assert.Equal("部署：抽 1 张牌。", row.Text);

        Assert.Throws<InvalidDataException>(() => OrcImportService.ParseRows("{\"other\":1}"));
    }

    /// <summary>导入卡走真对局：opening hand 直取，部署触发抽 1（手牌数守恒＝效果真的发生了）。</summary>
    [Fact]
    public async Task ImportedUnitDeploysAndItsEffectFiresInARealMatch()
    {
        var outcomes = OrcImportService.ImportBatch(
            [new OrcImportRow("ui-deploy-draw", "导入试打兵", "infantry", 1, 1, 2, 3, "部署：抽 1 张牌。")], []);
        var complete = Assert.Single(outcomes);
        Assert.True(complete.Complete, string.Join("；", complete.Diagnostics));
        Assert.NotNull(complete.Entry);
        Assert.NotNull(complete.Card);

        // 目录对局：完整卡池（审核卡）＋ 导入卡；opening hand 直取导入卡。
        var catalog = new Kards.Ui.Core.CardCatalog();
        catalog.Load(Source);
        using var session = await OrcMatchSession.CreateFromCatalogAsync(
            catalog.Cards, 20261016, "orc-import-trial", "probe",
            openingHand: ["orc/ui-deploy-draw"], sourceDirectory: Source, imported: [complete], firstPlayerIndex: 0);
        await session.SettleMulliganAsync(false);
        session.SettleProjection();
        var actor = session.View.SelfHand.Single(c => c.CardId == "orc/ui-deploy-draw");
        for (var i = 0; i < 8 && session.View.SelfKredits < 1; i++)
        {
            Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new EndTurn()));
            session.SettleProjection();
        }
        var hand = session.View.SelfHand.Count;
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(actor.Uid)));
        session.SettleProjection();
        Assert.DoesNotContain(session.View.SelfHand, c => c.Uid == actor.Uid);
        // 部署触发抽了一张：出手 −1、抽牌 ＋1，手牌规模不变——效果没发生就会少一张。
        Assert.Equal(hand, session.View.SelfHand.Count);
        output.WriteLine($"导入卡部署成功，手牌守恒 {hand}");
    }
}
