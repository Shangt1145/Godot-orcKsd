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
            var rejected = OrcEngineHost.RejectedPackCards;
            var admitted = expected - rejected.Count;
            if (expected <= 99 || admitted <= 99 || rejected.Distinct(StringComparer.Ordinal).Count() != rejected.Count
                || _deckService.PlayableCards.Count != admitted + _deckService.Report.Verified || _deckService.PackCards.Count != admitted)
                throw new Exception($"Export has an incomplete admitted card pool: files={expected} rejected={rejected.Count} playable={_deckService.PlayableCards.Count} verified={_deckService.Report.Verified} packs={_deckService.PackCards.Count}.");
            if (rejected.Count > 0) GD.Print($"EXPORT_PACK_REJECTED cards={rejected.Count} {string.Join(",", rejected)}");
            foreach (var card in _deckService.PackCards)
                if (_textures.Get(card.ArtPath) is null) throw new Exception("Export has missing artwork: " + card.CardId);
            var text = OrcInspector.CompileText("export.probe", "部署：抽1张牌。", "unit", _catalog.Cards);
            if (!text.ParseComplete || !text.AllCompiled) throw new Exception("Export parser or native compiler failed: " + string.Join("；", text.Problems));
            var backendText = BackendTextAuthoring.Parse("export.backend.probe", "部署：抽1张牌。", "unit");
            if (!backendText.Success || backendText.Effects.Length == 0)
                throw new Exception("Export backend text compiler failed: " + string.Join("；", backendText.Problems));
            var count = await PackPlayabilityVerification.VerifyAsync(_catalog.Cards, RuntimeGameData.Directory("nations"));
            if (count != admitted) throw new Exception("Export did not mount all admitted package cards.");
            await VerifyDecksAsync();
            StopRealMatch();
            GD.Print($"EXPORT_VERIFY_OK playable={_deckService.PlayableCards.Count} pack-cards={count} rejected={rejected.Count} all-admitted-pack-effects-mounted artwork={count} embedded-parser backend-text-compiler parent-packs-only expansions-included save-reload exact-deck mulligan");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); StopRealMatch(); GetTree().Quit(1); }
    }
}
