using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    private async Task WaitForCombatResidueAsync()
    {
        var until = Time.GetTicksMsec() + 2500;
        while (_combat.LiveEffectCount > 0 && Time.GetTicksMsec() < until)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_combat.LiveEffectCount > 0) throw new InvalidOperationException("战斗余效未在期限内回收");
    }
    public async Task VerifyPaperMotionAsync(string folder)
    {
        void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
        async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        async Task Capture(string name)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, name + ".png"));
        }
        _clock.SetReducedMotion(false); _clock.SetSpeed(AnimClock.Speed.Normal); ResetDemo();
        var hand = _cards.Values.First(c => c.Mode == BattleCardMode.Hand);
        var origin = hand.Position; CardHovered(hand, true);
        Check(hand.Position == origin, "悬停仍在瞬移");
        await Wait(.08); Check(hand.Position.Y < origin.Y && hand.Position.Y > origin.Y - 90, "悬停没有中间姿态");
        await Capture("hover-in");
        var halfway = hand.Position; CardHovered(hand, false);
        Check(hand.Position == halfway, "反向打断先归零"); await Wait(.3);
        Check(hand.Position.DistanceTo(hand.RestPosition) < .1f, "回手没有落在实际手牌位置");
        for (var i = 0; i < 12; i++) { CardHovered(hand, true); await Wait(.02); CardHovered(hand, false); }
        await Wait(.3); Check(hand.Position.DistanceTo(hand.RestPosition) < .1f && _handMotion.Count == 0, "快速浏览残留旧动画");
        _clock.SetReducedMotion(true); CardHovered(hand, true); CardHovered(hand, false);
        Check(hand.Position == hand.RestPosition, "减弱动效未立即归位"); _clock.SetReducedMotion(false);
        var initial = _state;
        var returning = initial.SelfLine.First(c => !c.IsHq);
        var handAfterReturn = initial.SelfHand.Append(returning with { Zone = "hand", SlotIndex = initial.SelfHand.Count }).ToArray();
        ApplyProjection(initial with { SelfHand = handAfterReturn, SelfHandCount = handAfterReturn.Length, SelfLine = initial.SelfLine.Where(c => c.Uid != returning.Uid).ToArray() }, _actions);
        Check(_ghosts.Count > 0 && !_cards[returning.Uid].Visible, "场上卡回手没有交接代理");
        await Wait(.6); Check(_cards[returning.Uid].Visible && _cards[returning.Uid].Mode == BattleCardMode.Hand && _ghosts.Count == 0, "回手残留重复卡牌");
        var beforeTransform = _state; var transformed = beforeTransform.SelfHand[0] with { Definition = beforeTransform.SelfHand[0].Definition with { CardId = "visual-transformed", Name = "变形验证" } };
        ApplyProjection(beforeTransform with { SelfHand = beforeTransform.SelfHand.Select(c => c.Uid == transformed.Uid ? transformed : c).ToArray() }, _actions);
        Check(_ghosts.Count > 0, "变形没有翻面交接"); await Wait(.6);
        Check(_ghosts.Count == 0 && _cards[transformed.Uid].View!.CardId == "visual-transformed", "变形没有交回正确卡牌");
        ResetDemo();
        StartScenario("artillery");
        var attacker = _cards.Values.First(c => c.View is { IsHq: false, Visibility: Visibility.Full, OwnerSide: "self", Zone: "frontline" });
        var defender = _cards.Values.First(c => c.View is { IsHq: true, OwnerSide: "enemy" });
        _aim.Hide();
        foreach (var style in new[] { "rifle", "machinegun", "autocannon", "cannon", "artillery", "rocket", "bomb", "flame" })
        {
            BattleWeaponProfile.Override(attacker.View!.CardId, style);
            var first = _combat.PresentAssaultAsync(attacker, [(new UiOrderImpact(defender.View!, defender.View!, 1, attacker.View), defender)]);
            await Wait(.15); await Capture("weapon-" + style); await first;
        }
        BattleWeaponProfile.Override(attacker.View!.CardId, null);
        await _combat.PresentImpactsAsync([(new UiOrderImpact(defender.View!, defender.View!, 1), defender)]);
        Check(_combat.GetChildren().OfType<CombatDust>().Any(), "动作结束清掉了残烟");
        var dust = _combat.GetChildren().OfType<CombatDust>().First();
        var next = _combat.PresentImpactsAsync([(new UiOrderImpact(defender.View!, defender.View!, 1), defender)]);
        Check(dust.GetParent() == _combat, "下一次行动清掉上一击残烟");
        await next; await Capture("residual-overlap"); await Wait(1.5);
        Check(_combat.LiveEffectCount == 0, "余效没有自行回收");
        _combat.Interrupt(); ResetDemo();
        GD.Print("PAPER_MOTION_VERIFY_OK hover-continuity interrupt-return rapid-sweep quiet field-to-hand transform eight-weapons residual-overlap cleanup");
    }
}
