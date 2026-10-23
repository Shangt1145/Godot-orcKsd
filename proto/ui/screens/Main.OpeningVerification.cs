using Godot;

namespace Kards.Ui;

public partial class Main
{
    private async Task VerifyRealMulliganMotionAsync(string folder)
    {
        foreach (var first in new[] { 0, 1 })
        {
            Show("battle"); await StartRealMatchAsync(firstPlayerIndex: first);
            if (!await AwaitBoardAsync(() => _battle.MulliganPanelVisible, 15)) throw new Exception("真实换牌未出现");
            var phases = new List<string>();
            void Phase(string phase) => phases.Add(phase);
            _battle.PresentationPhase += Phase;
            try
            {
                var before = _runner!.CurrentView!;
                _battle.MarkMulliganReplacement(before.SelfHand[0].Uid); _battle.ConfirmMulligan();
                if (!await AwaitBoardAsync(() => phases.Contains("mulligan-replacements-settled"), 20))
                    throw new Exception("真实静默换牌未播放补入：" + string.Join(",", phases));
                if (_runner!.CurrentView!.SelfHand.Count < before.SelfHand.Count) throw new Exception("换牌丢失手牌");
                GD.Print($"REAL_MULLIGAN_MOTION_VERIFY_OK first={first} replacements=1 arrival-complete");
            }
            finally { _battle.PresentationPhase -= Phase; StopRealMatch(); _battle.ResetDemo(); }
        }
    }

    private async void VerifyOpening()
    {
        try
        {
            StopRealMatch(); Show("battle");
            var folder = ProjectSettings.GlobalizePath("res://artifacts/opening-20261023"); System.IO.Directory.CreateDirectory(folder);
            await _battle.VerifyOpeningAnimationAsync(folder);
            await _battle.VerifyMulliganMotionAsync(folder);
            // Real engine requests: confirm during an introduction must still submit the exact keep list.
            await VerifyMulliganPanelAsync();
            await VerifyRealMulliganMotionAsync(folder);
            Show("battle"); await StartRealMatchAsync(firstPlayerIndex: 1);
            if (!await AwaitBoardAsync(() => _battle.MulliganPanelVisible, 15)) throw new Exception("后手换牌未出现");
            await ToSignal(GetTree().CreateTimer(.4), SceneTreeTimer.SignalName.Timeout);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, "real-versus.png"));
            if (!await AwaitBoardAsync(() => !_battle.OpeningAnimationActive, 8)) throw new Exception("后手开局演出未结束");
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, "real-ready.png"));
            _battle.ConfirmMulligan();
            if (!await AwaitBoardAsync(() => _runner?.CurrentView?.Phase == "play", 20)) throw new Exception("后手开局未进入真实回合");
            StopRealMatch(); _battle.ResetDemo();
            GD.Print("OPENING_VERIFY_OK real-mulligan first-and-second early-confirm natural-finish reach-play"); GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
    }
}
