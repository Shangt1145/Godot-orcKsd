using Godot;

namespace Kards.Ui;

public partial class BattleScreen
{
    public async Task VerifyOpeningAnimationAsync(string folder)
    {
        void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
        async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        async Task Capture(string name)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, name + ".png"));
        }
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        try
        {
            _clock.SetReducedMotion(false); _clock.SetSpeed(AnimClock.Speed.Normal); ResetDemo();
            var opening = _state with { MatchId = "opening-test", Phase = "mulligan", Turn = 0 };
            ApplyProjection(opening, new()); ShowMulliganPanel(opening);
            Check(OpeningAnimationActive && _mulliganConfirm.Disabled, "开局未接入输入门禁");
            Check(_openingCards.All(c => c.Card.View is null), "起手卡尚未翻面便露出身份");
            await Wait(.4); await Capture("versus");
            await Wait(.85); await Capture("deal");
            await Wait(1.2);
            Check(!OpeningAnimationActive && !_mulliganConfirm.Disabled && _openingCards.Count == 0, "开局演出未正常释放输入");
            Check(_mulliganRow.GetChildren().OfType<Control>().All(c => c.GetChildren().OfType<BattleCard>().All(b => b.View is not null && b.Scale == Vector2.One)), "起手牌最终姿态或牌面错误");
            await Capture("ready");
            MulliganProjection("transition"); ShowMulliganPanel(opening);
            Check(!OpeningAnimationActive, "同局重连重复播放入场");
            var next = opening with { MatchId = "opening-skip" };
            ApplyProjection(next, new()); ShowMulliganPanel(next); await Wait(.15); FinishOpening();
            Check(!OpeningAnimationActive && _mulliganOpen && !_mulliganConfirm.Disabled, "跳过入场关闭了换牌或没有解锁");
            next = opening with { MatchId = "opening-reset" };
            ApplyProjection(next, new()); ShowMulliganPanel(next); ResetDemo(); await Wait(2.2);
            Check(!OpeningAnimationActive && !_mulligan.Visible && _openingIdentity is null, "切换对局残留旧演出");
            _clock.SetReducedMotion(true);
            next = opening with { MatchId = "opening-quiet" };
            ApplyProjection(next, new()); ShowMulliganPanel(next);
            Check(!OpeningAnimationActive && _mulliganOpen && !_mulliganConfirm.Disabled, "减弱动效没有直接进入换牌");
            ResetDemo(); SetReadOnlyMode("观战中"); ShowMulliganPanel(next);
            Check(!_mulliganOpen && !OpeningAnimationActive, "只读观战出现可操作换牌"); SetReadOnlyMode(null);
            GD.Print("OPENING_VISUAL_VERIFY_OK versus deal reveal ready skip reconnect reset quiet spectator");
        }
        finally { SetReadOnlyMode(null); ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); }
    }
}
