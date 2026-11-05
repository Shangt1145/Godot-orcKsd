using Godot;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyBattleWorld3D()
    {
        var speed = _clock.Current; var quiet = _clock.ReducedMotion; var tilted = _clock.TiltedBattleView;
        try
        {
            var settings = Settings();
            try
            {
                var toggle = settings.FindChild("TiltedBattleView", true, false) as CheckButton
                    ?? throw new InvalidOperationException("Battle view setting missing");
                foreach (var enabled in new[] { true, false })
                {
                    toggle.ButtonPressed = enabled;
                    var reloaded = new AnimClock();
                    try
                    {
                        reloaded._Ready();
                        if (_clock.TiltedBattleView != enabled || reloaded.TiltedBattleView != enabled)
                            throw new InvalidOperationException("Battle view toggle did not persist");
                    }
                    finally { reloaded.Free(); }
                }
            }
            finally { settings.Free(); }
            Show("battle");
            var path = ProjectSettings.GlobalizePath("res://artifacts/battle-3d");
            await _battle.VerifyWorld3DAsync(path);
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("BATTLE_3D_VERIFY_FAIL " + error); GetTree().Quit(1); }
        finally { _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); _clock.SetTiltedBattleView(tilted); }
    }
}
