using System.Text.Json;
using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyCardPool()
    {
        try { await VerifyCardPoolStageAsync(); GetTree().Quit(); }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    private async Task VerifyCardPoolStageAsync()
    {
        Show("battle");
        await StartRealMatchAsync(interactiveMulligan: false, hotseat: true, deckSeed: 7, demoOpeningHand: true);
        var runner = _runner!; var report = runner.CardPoolReport ?? throw new Exception("Catalog admission did not report its support set.");
        if (report.Total != 315 || report.Verified != 21 || report.StructurallyRepresentable <= report.Verified)
            throw new Exception("Reviewed and structural card coverage were confused.");
        var approved = report.Cards.Where(c => c.Support == UiCardSupport.Verified).Select(c => c.CardId).ToHashSet();
        if (runner.CurrentView!.SelfHand.Any(c => !approved.Contains(c.CardId)) || runner.CurrentView.SelfDeckCount != 26)
            throw new Exception("A validation deck included unreviewed cards or the wrong count.");
        // Advance both human seats through the production command path; no AI substitutes the player.
        await runner.SubmitAsync(new EndTurn()); runner.SwitchSeat();
        await runner.SubmitAsync(new EndTurn()); runner.SwitchSeat();
        var card = runner.CurrentView!.SelfHand.First(c => c.CardId == "USG/units/_4");
        await _battle.DragAsync(card.Uid, _battle.DropAtRowEdge(false, false)); runner.Pump();
        await _presentations!.Completion;
        if (!runner.CurrentView!.SelfLine.Any(c => c.Uid == card.Uid) || !_battle.MatchesProjection(runner.CurrentView))
            throw new Exception("Reviewed catalog deployment did not settle through mouse input.");
        var directory = ProjectSettings.GlobalizePath("res://artifacts"); System.IO.Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(directory + "/stage6b2-card-pool.json", JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        GD.Print($"CARD_POOL_STAGE6B2_VERIFY_OK total={report.Total} structural={report.StructurallyRepresentable} verified={report.Verified} deck=30 reviewed-mouse-deployment");
        StopRealMatch(); _battle.ResetDemo();
    }
}
