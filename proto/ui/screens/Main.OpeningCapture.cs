using System.Globalization;
using Godot;

namespace Kards.Ui;

public partial class Main
{
    // Records the actual Godot viewport and its wall-clock timestamps; no storyboard recreation.
    private async void CaptureOpeningPolish()
    {
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        try
        {
            _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
            StopRealMatch(); Show("battle"); await StartRealMatchAsync(firstPlayerIndex: 0);
            if (!await AwaitBoardAsync(() => _battle.MulliganPanelVisible, 15)) throw new Exception("录制未进入实际换牌");
            var folder = ProjectSettings.GlobalizePath("res://artifacts/opening-polish-20261023");
            System.IO.Directory.CreateDirectory(folder);
            var frames = new List<(string Path, double Time)>(); var start = Time.GetTicksMsec();
            var marked = false; var confirmed = false;
            while (Time.GetTicksMsec() - start < 6800)
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var elapsed = (Time.GetTicksMsec() - start) / 1000.0;
                if (!marked && elapsed > 2.45 && !_battle.OpeningAnimationActive)
                {
                    marked = true;
                    foreach (var card in _runner!.CurrentView!.SelfHand.Take(2)) _battle.MarkMulliganReplacement(card.Uid);
                }
                if (marked && !confirmed && elapsed > 3.1) { confirmed = true; _battle.ConfirmMulligan(); }
                var name = $"frame-{frames.Count:D5}.jpg";
                GetViewport().GetTexture().GetImage().SaveJpg(System.IO.Path.Combine(folder, name), .92f);
                frames.Add((name, elapsed));
                await ToSignal(GetTree().CreateTimer(.012), SceneTreeTimer.SignalName.Timeout);
            }
            var concat = new List<string> { "ffconcat version 1.0" };
            for (var i = 0; i < frames.Count; i++)
            {
                concat.Add($"file '{frames[i].Path}'");
                concat.Add("duration " + (i + 1 < frames.Count ? frames[i + 1].Time - frames[i].Time : .04).ToString("F6", CultureInfo.InvariantCulture));
            }
            concat.Add($"file '{frames[^1].Path}'");
            System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "capture.ffconcat"), concat);
            GD.Print($"OPENING_CAPTURE_OK frames={frames.Count} actual-engine silent-viewport-capture folder={folder}");
            StopRealMatch(); _battle.ResetDemo(); GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
        finally { _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); }
    }
}
