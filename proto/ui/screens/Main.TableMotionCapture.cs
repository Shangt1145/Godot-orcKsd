using System.Globalization;
using Godot;

namespace Kards.Ui;

public partial class Main
{
    private async void CaptureTableMotion()
    {
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        try
        {
            StopRealMatch(); Show("battle"); _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
            var folder = ProjectSettings.GlobalizePath("res://artifacts/table-motion-20261023"); System.IO.Directory.CreateDirectory(folder);
            var frames = new List<(string Path, double Time)>(); var start = Time.GetTicksMsec();
            var showcase = _battle.PlayTableMotionShowcaseAsync();
            while (!showcase.IsCompleted && Time.GetTicksMsec() - start < 16000)
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var elapsed = (Time.GetTicksMsec() - start) / 1000.0;
                var name = $"frame-{frames.Count:D5}.jpg";
                GetViewport().GetTexture().GetImage().SaveJpg(System.IO.Path.Combine(folder, name), .92f);
                frames.Add((name, elapsed));
                await ToSignal(GetTree().CreateTimer(.012), SceneTreeTimer.SignalName.Timeout);
            }
            if (!showcase.IsCompleted) throw new Exception("对战动作演示超时");
            await showcase;
            var concat = new List<string> { "ffconcat version 1.0" };
            for (var i = 0; i < frames.Count; i++)
            {
                concat.Add($"file '{frames[i].Path}'");
                concat.Add("duration " + (i + 1 < frames.Count ? frames[i + 1].Time - frames[i].Time : .04).ToString("F6", CultureInfo.InvariantCulture));
            }
            concat.Add($"file '{frames[^1].Path}'");
            System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "capture.ffconcat"), concat);
            GD.Print($"TABLE_CAPTURE_OK frames={frames.Count} visual-fixture silent-viewport folder={folder}");
            _battle.ResetDemo(); GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
        finally { _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); }
    }
}
