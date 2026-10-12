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

    /// <summary>Loads the built-in packs (太空战争's companions: 内置卡包 + official expansions) and
    /// republishes the merged catalog. Base packs load before their expansions; any failure is an
    /// error log, never a startup exception.</summary>
    private void ReloadPacks()
    {
        try
        {
            var root = Godot.ProjectSettings.GlobalizePath("res://proto/data/kards");
            var loaded = new List<PackContent>();
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(dir, "pack.json"))) continue;
                var manifest = PackSystem.ReadManifest(dir);
                // expansions must load after their base pack; two passes keep that true regardless of name order.
                if (manifest.Kind != "pack") continue;
                loaded.Add(PackSystem.Load(dir, loaded));
            }
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(dir, "pack.json"))) continue;
                var manifest = PackSystem.ReadManifest(dir);
                if (manifest.Kind == "pack") continue;
                if (loaded.Any(p => p.Manifest.Id == manifest.Id)) continue;
                loaded.Add(PackSystem.Load(dir, loaded));
            }
            var projections = loaded.SelectMany(p => p.Projections).ToArray();
            _catalog.SetPackCards(projections);
            _deckService.UpdatePacks(projections);
        }
        catch (Exception e)
        {
            Godot.GD.PushError($"内置卡包装载失败：{e.Message}");
        }
        if (_pool is not null) _pool.Text = $"{_catalog.Cards.Count} 张卡牌";
        _artById = _catalog.Cards.Where(c => c.ArtPath.Length > 0).ToDictionary(c => c.CardId, c => c.ArtPath);
        _collection?.RefreshCatalog();
        _deckEditor?.RefreshCatalog(); _deckLibrary?.Refresh();
        _home?.Refresh();
    }
}
