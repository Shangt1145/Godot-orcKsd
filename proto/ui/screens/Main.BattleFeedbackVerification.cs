using Godot;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyBattleFeedback()
    {
        try
        {
            StopRealMatch(); Show("battle");
            var folder = ProjectSettings.GlobalizePath("res://artifacts/battle-feedback-20261024");
            System.IO.Directory.CreateDirectory(folder);
            async Task Capture(string name)
            {
                if (DisplayServer.GetName() == "headless") return;
                await ShotAsync(folder, name);
            }
            await _battle.VerifyFeedbackRegressionsAsync(Capture);
            await _battle.VerifyArrowChoicesAsync();
            await VerifyNativeDeploymentDragAsync();
            await VerifySavedDemoPackAsync();
            GD.Print("BATTLE_FEEDBACK_REGRESSION_OK"); GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
    }
}
