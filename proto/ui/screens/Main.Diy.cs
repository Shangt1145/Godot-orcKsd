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
}
