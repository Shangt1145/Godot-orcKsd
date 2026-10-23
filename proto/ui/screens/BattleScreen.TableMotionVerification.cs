using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    public async Task VerifyTableMotionAsync(string folder)
    {
        void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
        async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        ResetDemo();
        var hand = _cards.Values.First(c => c.Mode == BattleCardMode.Hand);
        float DragAtRate(int fps)
        {
            _pressed = hand; _dragging = true; hand.Rotation = 0; _grabOffset = hand.PivotOffset;
            _dragPointer = new(600, 400); _dragTiltTarget = .09f;
            for (var i = 0; i < fps / 2; i++) _Process(1.0 / fps);
            Check((hand.Position + hand.PivotOffset).DistanceTo(_dragPointer) < .001f, "拖牌锚点随倾角漂移");
            var rotation = hand.Rotation; _pressed = null; _dragging = false; return rotation;
        }
        Check(Math.Abs(DragAtRate(30) - DragAtRate(144)) < .003, "拖牌角度依赖鼠标／渲染帧率");
        ResetDemo();
        var mover = _state.SelfLine.First(c => c.Zone == "support");
        var from = _cards[mover.Uid].Position;
        ApplyProjection(_state with { SelfLine = _state.SelfLine.Select(c => c.Uid == mover.Uid ? c with { Zone = "frontline", SlotIndex = 1 } : c).ToArray() }, new());
        await Wait(.13); var moving = _cards[mover.Uid];
        Check(moving.DeploymentElevation > .1f && moving.Position != from && moving.Position != moving.RestPosition, "换线仍然是瞬移或没有抬牌");
        await Wait(.45);
        Check(moving.Position.DistanceTo(moving.RestPosition) < .01 && Math.Abs(moving.DeploymentElevation) < .001, "换线落定后姿态未归零");
        var restingZ = moving.ZIndex;
        MoveFieldPaper(moving, moving.RestPosition + new Vector2(0, 90), 0, true);
        await Wait(.05); FinishMotion();
        Check(moving.ZIndex == restingZ && _fieldMotionZ.Count == 0 && moving.DeploymentElevation == 0, "打断换线后层级或阴影残留");

        StartScenario("fighter");
        var attacker = _cards.Values.First(c => c.View is { OwnerSide: "self", Zone: "frontline", IsHq: false });
        var defender = _cards.Values.First(c => c.View is { OwnerSide: "enemy", IsHq: true });
        var phases = new List<string>(); void Phase(string p) => phases.Add(p);
        _combat.PhaseChanged += Phase;
        try { await _combat.PresentAssaultAsync(attacker, [(new UiOrderImpact(defender.View!, defender.View!, 1, attacker.View), defender)]); }
        finally { _combat.PhaseChanged -= Phase; }
        Check(phases.Contains("aircraft-land") && attacker.Position.DistanceTo(attacker.RestPosition) < .01f
            && attacker.FlightOffset.Length() < .01f && attacker.Scale == Vector2.One, "引擎归因攻击没有飞机降落阶段或留在空中");
        var second = _cards.Values.First(c => c.View is { OwnerSide: "enemy", IsHq: false });
        var firstAfter = defender.View! with { Health = Math.Max(1, (defender.View!.Health ?? 20) - 1) };
        var secondBefore = second.View!;
        var secondAfter = secondBefore with { Health = Math.Max(1, (secondBefore.Health ?? 5) - 1) };
        var orderCorrect = false; var impacts = 0; phases.Clear();
        void MultiplePhase(string p)
        {
            phases.Add(p);
            if (p == "assault-target-impact" && ++impacts == 1)
                orderCorrect = defender.View?.Health == firstAfter.Health && second.View?.Health == secondBefore.Health;
        }
        _combat.PhaseChanged += MultiplePhase;
        try
        {
            await _combat.PresentAssaultAsync(attacker, [(new UiOrderImpact(defender.View!, firstAfter, 1, attacker.View), defender),
                (new UiOrderImpact(secondBefore, secondAfter, 1, attacker.View), second)]);
        }
        finally { _combat.PhaseChanged -= MultiplePhase; }
        Check(orderCorrect && impacts == 2 && phases.Count(p => p == "aircraft-takeoff") == 1
            && phases.Count(p => p == "aircraft-land") == 1, "多目标命中延后、重复起飞或落地缺失");
        await WaitForCombatResidueAsync();
        Check(_combat.LiveEffectCount == 0, "接触火花没有到期回收");
        ResetDemo();
        GD.Print("TABLE_MOTION_VERIFY_OK drag-30-144fps stable-anchor field-lift-settle attributed-aircraft-landing multi-target-order one-takeoff effects-cleanup");
    }

    public async Task PlayTableMotionShowcaseAsync()
    {
        async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        ResetDemo();
        var hand = _cards.Values.First(c => c.Mode == BattleCardMode.Hand);
        CardHovered(hand, true); await Wait(.45); CardHovered(hand, false); await Wait(.28);
        var mover = _state.SelfLine.First(c => c.Zone == "support");
        ApplyProjection(_state with { SelfLine = _state.SelfLine.Select(c => c.Uid == mover.Uid ? c with { Zone = "frontline", SlotIndex = 1 } : c).ToArray() }, new());
        await Wait(.75);
        foreach (var type in new[] { "tank", "infantry", "fighter" })
        {
            StartScenario(type); CancelSelection();
            var attacker = _cards.Values.First(c => c.View is { OwnerSide: "self", Zone: "frontline", IsHq: false });
            var defender = _cards.Values.First(c => c.View is { OwnerSide: "enemy", IsHq: true });
            await Wait(.18);
            await _combat.PresentAssaultAsync(attacker, [(new UiOrderImpact(defender.View!, defender.View! with { Health = Math.Max(0, (defender.View!.Health ?? 20) - 1) }, 1, attacker.View), defender)]);
            await Wait(.45);
        }
        await Wait(.6);
    }
}
