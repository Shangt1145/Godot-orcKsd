using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyDiscardAnimation()
    {
        try
        {
            StopRealMatch(); Show("battle");
            var fixture = await OrcDiscardVerification.RunAsync();
            await _battle.VerifyDiscardAnimationAsync(fixture, DisplayServer.GetName()=="headless" ? null
                : name => ShotAsync(ProjectSettings.GlobalizePath("res://artifacts"),name));
            GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
    }
}
