using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private void RefreshDiy()
    {
        WorkshopAuthoring.Configure(_workshopLibrary,_diy.Cards);WorkshopVisuals.Reset();
        _diyEditor?.RefreshWorkshopOptions();
        _deckService.UpdateCustom(_diy.Cards);
        _catalog.SetCustomCards(_deckService.CustomCards);
        if (_pool is not null) _pool.Text = $"{_deckService.PlayableCards.Count} 张可用卡牌";
        _artById = _catalog.Cards.Where(c => c.ArtPath.Length > 0).ToDictionary(c => c.CardId, c => c.ArtPath);
        _collection.RefreshCatalog();
        _deckEditor.RefreshCatalog(); _deckLibrary.Refresh();
        _home.Refresh();
        _workshop?.RefreshData();
        if (_deckService.CustomErrors.Count > 0)
            Godot.GD.PushWarning(string.Join("；", _deckService.CustomErrors.Select(p => $"{p.Key}：{p.Value}")));
    }

    /// <summary>
    /// OrC 批量导入的重编译与全链刷新：行 → 解析/编译 → 完整卡进图鉴与构筑池。
    /// 编译逐行隔离（单行失败＝该行待核对，不拖垮整批）。
    /// </summary>
    private void RefreshImported()
    {
        try
        {
            var outcomes = OrcImportService.ImportBatch(_orcImports.Rows, _catalog.Cards
                .Where(c => !c.CardId.StartsWith("orc/", StringComparison.Ordinal)).ToArray());
            _importedOutcomes = outcomes;
            var complete = outcomes.Where(o => o.Complete && o.Card is not null).Select(o => o.Card!).ToArray();
            _deckService.UpdateImported(complete);
            _catalog.SetImportedCards(complete);
            var pending = outcomes.Count - complete.Length;
            if (pending > 0)
                Godot.GD.PushWarning($"OrC 导入：{complete.Length} 张入池，{pending} 张待核对。");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Godot.GD.PushError($"OrC 导入重编译失败：{e.Message}");
            _importedOutcomes = [];
            _deckService.UpdateImported([]);
            _catalog.SetImportedCards([]);
        }
        if (_pool is not null) _pool.Text = $"{_deckService.PlayableCards.Count} 张可用卡牌";
        _artById = _catalog.Cards.Where(c => c.ArtPath.Length > 0).ToDictionary(c => c.CardId, c => c.ArtPath);
        _collection?.RefreshCatalog();
        _deckEditor?.RefreshCatalog(); _deckLibrary?.Refresh();
        _home?.Refresh();
    }

    /// <summary>
    /// 迁移 S8：卡包不再由前端自造模型装载——改为初始化**引擎宿主**（发现 `<base>/packs` 下的卡包，
    /// 并注入内置插件与 `.ui` 投影插件），再从快照取卡包卡的**静态投影**喂给图鉴/构筑。
    /// 单包装载失败＝隔离该包并记警告，绝不抛到启动链上。
    /// </summary>
    private static string OfficialCardArt(string id)
    {
        // Pack ids are namespaced; image names use the official card id only.
        // 点号在卡 id 里合法（如 bulwark.entrench-infantry），这里必须放行，否则新卡包整包没卡图。
        var parts = id.Split('/');
        if (parts.Length != 3 || parts[0] != "pack"
            || parts[2].Length == 0 || parts[2].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.')) return "";
        var path = "res://proto/art/kards/" + parts[2] + ".png";
        return Godot.ResourceLoader.Exists(path) ? path : "";
    }
    private void ReloadPacks()
    {
        try
        {
            var baseDirectory = RuntimeGameData.Directory("kards");
            var (cards, failures, _) = OrcEngineHost.LoadPackCards(baseDirectory, OfficialCardArt);
            foreach (var failure in failures)
                Godot.GD.PushWarning($"卡包/插件装载隔离：{failure}");
            _catalog.SetPackCards(cards);
            _deckService.UpdatePacks(cards);
            _deckService.UpdateCustom(_diy.Cards);
            _catalog.SetCustomCards(_deckService.CustomCards);
        }
        catch (Exception e)
        {
            Godot.GD.PushError($"卡包装载失败：{e.Message}");
        }
        if (_pool is not null) _pool.Text = $"{_deckService.PlayableCards.Count} 张可用卡牌";
        _artById = _catalog.Cards.Where(c => c.ArtPath.Length > 0).ToDictionary(c => c.CardId, c => c.ArtPath);
        _collection?.RefreshCatalog();
        _deckEditor?.RefreshCatalog(); _deckLibrary?.Refresh();
        _home?.Refresh();
    }
}
