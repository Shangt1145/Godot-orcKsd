using Godot;
namespace Kards.Ui;
public partial class Main
{
    private async void VerifyPaperMotion()
    {
        try
        {
            StopRealMatch(); Show("battle");
            var folder = ProjectSettings.GlobalizePath("res://artifacts/paper-motion-20261023"); System.IO.Directory.CreateDirectory(folder);
            await _battle.VerifyPaperMotionAsync(folder);
            await _battle.VerifyTableMotionAsync(folder); GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
    }
}
