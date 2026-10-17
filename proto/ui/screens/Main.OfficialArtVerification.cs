using Godot;

namespace Kards.Ui;

public partial class Main
{
    private async void CaptureOfficialArt()
    {
        try
        {
            var directory = ProjectSettings.GlobalizePath("res://artifacts/beta1");
            System.IO.Directory.CreateDirectory(directory);
            Show("collection"); _collection.OpenPack("pack/builtin");
            await MenuSettledAsync();
            var cards = _collection.Results;
            if (cards.Count != _deckService.PackCards.Count || cards.Any(c => c.ArtPath.Length == 0 || _textures.Get(c.ArtPath) is null))
                throw new Exception("Original collection artwork failed verification.");
            if (_collection.VisibleCards.Any(c => c.Definition.ArtPath.Length == 0))
                throw new Exception("A visible original card has no artwork.");
            if (!cards.Any(c => c.CardId == _collection.SelectedCardId))
                throw new Exception("Collection detail still shows a card from another pack.");
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(directory + "/official-collection.png");
            Show("editor"); _diyEditor.Load(new());
            await MenuSettledAsync();
            _diyEditor.EditorScroll.EnsureControlVisible(_diyEditor.ImportArtButton);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(directory + "/diy-import-only.png");
            GD.Print($"BETA1_CAPTURE_OK original-cards={cards.Count} artwork={cards.Count} import-only-editor");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
    }
}
