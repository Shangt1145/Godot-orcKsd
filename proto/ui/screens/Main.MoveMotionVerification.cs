using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyMoveMotion()
    {
        try { await VerifyMoveMotionAsync(); GD.Print("MOVE_MOTION_VERIFY_OK"); GetTree().Quit(); }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
        finally { StopRealMatch(); _battle.ResetDemo(); }
    }

    /// <summary>
    /// 换线（支援线 → 前线）在**画面上**有没有发生：牌要离开旧行、抬起来、落进新行。
    /// 这条不查投影（投影由 VerifyBridgeAsync 管），只查 <see cref="BattleScreen.FieldPose"/>。
    /// </summary>
    private async Task VerifyMoveMotionAsync()
    {
        Show("battle");
        await StartRealMatchAsync(interactiveMulligan: false, deckSeed: 2, probe: true);
        if (_runner is null) throw new Exception("真实对局没有起来。");

        async Task Settle()
        {
            _runner!.Pump();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!await AwaitIdleAsync(30)) throw new Exception("演出没有落定。");
        }

        await Settle();
        var actions = _runner!.CurrentActions;
        var hand = _runner!.CurrentView!.SelfHand.FirstOrDefault(c =>
            actions.PlayableUids.Contains(c.Uid) && c.Definition.CardType == "unit"
            && c.Definition.Cost <= _runner!.CurrentView!.SelfKredits);
        if (hand is null) throw new Exception("开局没有可用的单位牌。");
        await _runner!.SubmitAsync(new PlayCard(hand.Uid));
        await Settle();
        await _runner!.SubmitAsync(new EndTurn());
        await Settle();

        var offer = _runner!.CurrentActions.Moves.FirstOrDefault();
        if (offer is null) throw new Exception("引擎没有给出可用的移动。");
        var before = _battle.FieldPose(offer.Uid);
        GD.Print($"[move-motion] offer {offer.Uid} -> {offer.ToZone}[{offer.SlotIndex}] "
            + $"drawn={before.Position} rest={before.RestAt} zone={before.Zone}");

        // 走玩家真正走的那条：拖拽发出的是 CommandUnit（引擎自己判移动还是攻击），
        // 不是 MoveUnit。两条命令在引擎里是不同的入口，演出段也可能不同。
        await _runner!.SubmitAsync(new CommandUnit(offer.Uid, null, offer.SlotIndex));

        var samples = new List<(Vector2 Position, float Elevation, string Zone, bool Tweening)>();
        for (var frame = 0; frame < 40; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var pose = _battle.FieldPose(offer.Uid);
            if (frame % 4 == 0)
                GD.Print($"[move-motion] f{frame} drawn={pose.Position} rest={pose.RestAt} "
                    + $"lift={pose.Elevation:0.000} zone={pose.Zone} tween={pose.Tweening}");
            samples.Add((pose.Position, pose.Elevation, pose.Zone, pose.Tweening));
        }
        await Settle();
        var after = _battle.FieldPose(offer.Uid);
        GD.Print($"[move-motion] settled drawn={after.Position} rest={after.RestAt} zone={after.Zone}");
        var moved = _runner!.CurrentView!.SelfLine.FirstOrDefault(c => c.Uid == offer.Uid);
        GD.Print($"[move-motion] projection zone={moved?.Zone}[{moved?.SlotIndex}]");

        if (moved is null || moved.Zone != offer.ToZone)
            throw new Exception($"投影没落地：{moved?.Zone} != {offer.ToZone}（{_runner.LastRejectionReason}）");
        if (after.Position.DistanceTo(after.RestAt) > .5f)
            throw new Exception($"画面上仍不在该在的位置：drawn={after.Position} rest={after.RestAt}");
        if (!samples.Any(s => s.Elevation > .1f))
            throw new Exception("换线全程没有抬牌——动画根本没播。");
        if (samples.All(s => s.Position.DistanceTo(before.Position) < 1))
            throw new Exception("换线全程位置没动过。");
    }
}
