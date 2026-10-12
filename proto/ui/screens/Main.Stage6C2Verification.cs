using Godot;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyStage6C2()
    {
        try { Show("battle"); await VerifyStage6C2Async(); GetTree().Quit(); }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
    private async Task VerifyStage6C2Async()
    {
        StopRealMatch(); await _battle.VerifyMultiChoicesAsync();
        await VerifyMulliganPanelAsync();
        if (_battle.HasLocalTwoPlayerEntry) throw new Exception("Local two-player menu survived removal.");
        GD.Print("STAGE6C2_VERIFY_OK multi-choice single-human-mulligan local-two-player-removed");
    }
}
