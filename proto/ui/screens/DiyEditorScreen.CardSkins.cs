using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyEditorScreen
{
    private void OpenAlternateArt()
    {
        var draft = Read();
        var card = DiyCardCompiler.Preview(draft, _catalog());
        var image = draft.AlternateArt is { } old ? _textures.Get("packfull://" + Path.Combine(_library.ArtDirectory, old.Art)) : null;
        WorkshopSkinDialog.Open(this, card, draft.AlternateArt, _textures, image, (skin, bytes) =>
        {
            var file = bytes is null ? skin.Art : _library.SaveAlternateArt(bytes, CardSkinFile.ExtensionOf(skin.Art));
            var current = Read(); _draft = current with { AlternateArt = skin with { Art = file } };
            Changed(); RefreshPreview();
            _notice.Text = "异画已应用到卡牌草稿；保存卡牌后生效。";
        }, () => { _draft = Read() with { AlternateArt = null }; Changed(); RefreshPreview(); });
    }
}
