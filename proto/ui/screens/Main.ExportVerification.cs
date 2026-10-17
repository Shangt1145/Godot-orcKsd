using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyExport()
    {
        try
        {
            var expected = System.IO.Directory.GetFiles(RuntimeGameData.Directory("kards"), "*.card.json", System.IO.SearchOption.AllDirectories).Length;
            if (expected <= 99 || _deckService.PlayableCards.Count != expected + _deckService.Report.Verified || _deckService.PackCards.Count != expected)
                throw new Exception($"Export has an incomplete admitted card pool: files={expected} playable={_deckService.PlayableCards.Count} verified={_deckService.Report.Verified} packs={_deckService.PackCards.Count}.");
            foreach (var card in _deckService.PackCards)
                if (_textures.Get(card.ArtPath) is null) throw new Exception("Export has missing artwork: " + card.CardId);
            var text = OrcInspector.CompileText("export.probe", "部署：抽1张牌。", "unit", _catalog.Cards);
            if (!text.ParseComplete || !text.AllCompiled) throw new Exception("Export parser or native compiler failed: " + string.Join("；", text.Problems));
            var count = await PackPlayabilityVerification.VerifyAsync(_catalog.Cards, RuntimeGameData.Directory("nations"));
            if (count != expected) throw new Exception("Export did not mount all original package cards.");
            await VerifyDecksAsync();
            StopRealMatch();
            GD.Print($"EXPORT_VERIFY_OK playable={_deckService.PlayableCards.Count} pack-cards={count} all-pack-effects-mounted artwork={count} embedded-parser native-compiler parent-packs-only expansions-included save-reload exact-deck mulligan");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); StopRealMatch(); GetTree().Quit(1); }
    }
}
