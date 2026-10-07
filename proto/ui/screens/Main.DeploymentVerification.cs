using Godot;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyDeployment()
    {
        try
        {
            StopRealMatch(); Show("battle");
            var capture = OS.GetCmdlineUserArgs().Contains("--capture-deployment");
            var directory = ProjectSettings.GlobalizePath("res://artifacts/deployment-verify");
            if (capture)
            {
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.WriteAllText(directory + "/.gdignore", "");
            }
            await _battle.VerifyDeploymentVisualsAsync(capture ? name => ShotAsync(directory, name) : null);
            await _battle.VerifyDemoNonBlockingAsync();
            await VerifyNonBlockingInputAsync();
            GD.Print("DEPLOYMENT_VERIFY_OK"); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
