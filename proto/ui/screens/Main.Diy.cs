using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private void RefreshDiy()
    {
        _deckService.UpdateCustom(_diy.Cards);
        _catalog.SetCustomCards(_deckService.CustomCards);
        if (_pool is not null) _pool.Text = $"{_catalog.Cards.Count} 张卡牌";
        _artById = _catalog.Cards.Where(c => c.ArtPath.Length > 0).ToDictionary(c => c.CardId, c => c.ArtPath);
        _collection.RefreshCatalog();
        _deckEditor.RefreshCatalog(); _deckLibrary.Refresh();
        _home.Refresh();
        if (_deckService.CustomErrors.Count > 0)
            Godot.GD.PushWarning(string.Join("；", _deckService.CustomErrors.Select(p => $"{p.Key}：{p.Value}")));
    }

    /// <summary>
    /// 迁移 S8：卡包不再由前端自造模型装载——改为初始化**引擎宿主**（发现 `<base>/packs` 下的卡包，
    /// 并注入内置插件与 `.ui` 投影插件），再从快照取卡包卡的**静态投影**喂给图鉴/构筑。
    /// 单包装载失败＝隔离该包并记警告，绝不抛到启动链上。
    /// </summary>
    private void ReloadPacks()
    {
        try
        {
            var baseDirectory = Godot.ProjectSettings.GlobalizePath("res://proto/data/kards");
            var (cards, failures, _) = OrcEngineHost.LoadPackCards(baseDirectory);
            foreach (var failure in failures)
                Godot.GD.PushWarning($"卡包/插件装载隔离：{failure}");
            _catalog.SetPackCards(cards);
            _deckService.UpdatePacks(cards);
        }
        catch (Exception e)
        {
            Godot.GD.PushError($"卡包装载失败：{e.Message}");
        }
        if (_pool is not null) _pool.Text = $"{_catalog.Cards.Count} 张卡牌";
        _artById = _catalog.Cards.Where(c => c.ArtPath.Length > 0).ToDictionary(c => c.CardId, c => c.ArtPath);
        _collection?.RefreshCatalog();
        _deckEditor?.RefreshCatalog(); _deckLibrary?.Refresh();
        _home?.Refresh();
    }
}
