using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    public async Task VerifyWorld3DAsync(string directory)
    {
        async Task Frame()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
        ResetDemo();
        for (var i = 0; i < 5; i++) await Frame();
        var fields = _cards.Values.Where(c => c.Visible && c.Mode is BattleCardMode.Field or BattleCardMode.Hq).ToArray();
        if (fields.Length == 0 || _world3D.ActorCount < fields.Length) throw new InvalidOperationException("3D actors missing");
        foreach (var card in fields)
        {
            var screen = card.WorldCenter ?? throw new InvalidOperationException("3D projection missing");
            var mapped = _world3D.MapPointer(screen);
            if (!new Rect2(card.Position, card.Size).HasPoint(mapped)) throw new InvalidOperationException("3D ray pick drift: " + card.Uid);
        }
        var selectable = fields.First(c => c.Mode == BattleCardMode.Field && c.View?.OwnerSide == "self");
        var click = _canvas.GetGlobalTransformWithCanvas() * selectable.WorldCenter!.Value;
        GetViewport().PushInput(new InputEventMouseMotion { Position = click, GlobalPosition = click }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = click, GlobalPosition = click, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        await Frame();
        if (_pressed != selectable) throw new InvalidOperationException("Projected card did not receive real GUI press");
        GetViewport().PushInput(new InputEventMouseButton { Position = click, GlobalPosition = click, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        CancelSelection();
        var windowSize = GetWindow().Size;
        try
        {
            GetWindow().Size = new(1100, 720);
            for (var i = 0; i < 3; i++) await Frame();
            var compactPoint = _canvas.GetGlobalTransformWithCanvas() * selectable.WorldCenter!.Value;
            GetViewport().PushInput(new InputEventMouseButton { Position = compactPoint, GlobalPosition = compactPoint, ButtonIndex = MouseButton.Left, Pressed = true }, true);
            await Frame();
            if (_pressed != selectable) throw new InvalidOperationException("3D GUI pick drift after resize");
            GetViewport().PushInput(new InputEventMouseButton { Position = compactPoint, GlobalPosition = compactPoint, ButtonIndex = MouseButton.Left, Pressed = false }, true);
            CancelSelection();
        }
        finally { GetWindow().Size = windowSize; }
        for (var i = 0; i < 3; i++) await Frame();
        // Load an existing GLB fixture into the same world; no equipped profile is modified.
        if (!_world3D.SetProp("self", "1854D100059597DC75D41544B5F8FE57ACC8BD9F929A0549B7ACFC357B513355"))
            throw new InvalidOperationException("Shared-world GLB prop fixture unavailable");
        for (var i = 0; i < 3; i++) await Frame();
        Directory.CreateDirectory(directory);
        GetViewport().GetTexture().GetImage().SavePng(directory + "/battle-3d-table.png");
        var flyer = fields.FirstOrDefault(c => c.Mode == BattleCardMode.Field) ?? fields[0];
        foreach (var tilted in new[] { true, false })
        {
            _clock.SetTiltedBattleView(tilted);
            for (var i = 0; i < 3; i++) await Frame();
            foreach (var card in fields)
                if (!new Rect2(card.Position, card.Size).HasPoint(_world3D.MapPointer(card.WorldCenter!.Value)))
                    throw new InvalidOperationException("View switch ray pick drift");
            if (!tilted && _world3D.Camera.Basis.Y.Dot(Vector3.Forward) < .999f)
                throw new InvalidOperationException("Flat view still tilted");
            GetViewport().GetTexture().GetImage().SavePng(directory + (tilted ? "/battle-3d-tilted.png" : "/battle-3d-flat.png"));
            var before = _world3D.AltitudeOf(flyer);
            flyer.DeploymentElevation = 1;
            for (var i = 0; i < 3; i++) await Frame();
            if (_world3D.AltitudeOf(flyer) - before < 1) throw new InvalidOperationException("3D altitude missing");
            GetViewport().GetTexture().GetImage().SavePng(directory + (tilted ? "/battle-3d-lift-tilted.png" : "/battle-3d-lift.png"));
            flyer.DeploymentElevation = 0;
        }
        ResetDemo();
        for (var i = 0; i < 3; i++) await Frame();
        var physical = _cardLayer.GetChildren().OfType<BattleCard>().Concat(_canvas.GetChildren().OfType<BattleCard>())
            .Count(c => c.Visible && !c.IsQueuedForDeletion() && c.Mode != BattleCardMode.Inspect);
        if (_world3D.ActorCount != physical)
            throw new InvalidOperationException("3D actors leaked across reset");
        var hand = _cards.Values.First(c => c.Mode == BattleCardMode.Hand && InteractionActions.PlayableUids.Contains(c.Uid));
        var handCount = _state.SelfHand.Count;
        var grab = _canvas.GetGlobalTransformWithCanvas() * _world3D.ProjectAnchor(hand.Position + hand.Size * new Vector2(.5f, .2f));
        GetViewport().PushInput(new InputEventMouseMotion { Position = grab, GlobalPosition = grab }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = grab, GlobalPosition = grab, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        await Frame();
        if (_pressed != hand) throw new InvalidOperationException("3D hand cannot be grabbed");
        var drop = _canvas.GetGlobalTransformWithCanvas() * new Vector2(965, 510);
        GetViewport().PushInput(new InputEventMouseMotion { Position = drop, GlobalPosition = drop, Relative = drop - grab, ButtonMask = MouseButtonMask.Left }, true);
        await Frame();
        GetViewport().PushInput(new InputEventMouseButton { Position = drop, GlobalPosition = drop, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        if (_demo.State.SelfHand.Count != handCount - 1) throw new InvalidOperationException("3D drag did not deploy");
        ResetDemo();
        for (var i = 0; i < 3; i++) await Frame();
        await VerifyTableMotionAsync(directory);
        await VerifyWorldDeploymentsAsync(directory);
        StartScenario("fighter");
        for (var i = 0; i < 3; i++) await Frame();
        var aircraft = _cards.Values.First(c => c.View is { OwnerSide: "self", Zone: "frontline", IsHq: false });
        var defender = _cards.Values.First(c => c.View is { OwnerSide: "enemy", IsHq: true });
        var assault = _combat.PresentAssaultAsync(aircraft, [(new UiOrderImpact(defender.View!, defender.View!, 1, aircraft.View), defender)]);
        await ToSignal(GetTree().CreateTimer(.30 * _clock.Scale), SceneTreeTimer.SignalName.Timeout);
        await Frame();
        if (aircraft.DeploymentElevation <= .1f) throw new InvalidOperationException("Aircraft did not take off in shared world");
        GetViewport().GetTexture().GetImage().SavePng(directory + "/battle-3d-aircraft.png");
        await assault;
        if (aircraft.DeploymentElevation != 0) throw new InvalidOperationException("Aircraft altitude not cleared");
        GD.Print("BATTLE_3D_VERIFY_OK view-toggle=true persisted=true flat=true tilted=true projection=true picking=true gui-press=true resized-pick=true hand-drag=true prop=true altitude=true reset=true aircraft-land=true actors=" + _world3D.ActorCount);
    }

    private async Task VerifyWorldDeploymentsAsync(string directory)
    {
        _clock.SetTiltedBattleView(true);
        foreach (var side in new[] { "self", "enemy" })
        foreach (var defense in new[] { 3, 6, 10 })
        {
            ResetDemo();
            var unit = _state.SelfHand.First(c => c.Definition.CardType == "unit") with
                { Uid = "world-deployment", OwnerSide = side, Zone = "hand", Health = defense, EffectiveDefense = defense };
            var before = _state with { MatchId = $"world-deploy-{side}-{defense}",
                SelfHand = side == "self" ? [unit] : [], SelfHandCount = side == "self" ? 1 : 0, EnemyHandCount = side == "enemy" ? 1 : 0 };
            var placed = unit with { Zone = "support", SlotIndex = 3 };
            var after = before with { SelfHand = [], SelfHandCount = 0, EnemyHandCount = 0,
                SelfLine = side == "self" ? before.SelfLine.Append(placed).ToArray() : before.SelfLine,
                EnemyLine = side == "enemy" ? before.EnemyLine.Append(placed).ToArray() : before.EnemyLine };
            ApplyProjection(before, new());
            var playback = PresentSequenceAsync(new(before.MatchId, [new UiDeploymentPresentation(placed, after)], after), new());
            var frames = 0;
            while (!playback.IsCompleted)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var actor = _sequence.GetChildren().OfType<BattleCard>().FirstOrDefault(c => c.Uid == unit.Uid);
                if (actor is null) continue;
                if (!actor.WorldPresentation || !actor.WorldRendered || actor.WorldCenter is null)
                    throw new InvalidOperationException("Deployment switched out of the shared perspective world");
                frames++;
                if (frames == 8) GetViewport().GetTexture().GetImage().SavePng($"{directory}/deployment-3d-{side}-{defense}.png");
            }
            await playback;
            if (frames == 0) throw new InvalidOperationException("Deployment 3D continuity was not sampled");
        }
        GD.Print("DEPLOYMENT_3D_CONTINUITY_OK self-enemy all-tiers shared-perspective-throughout");
    }
}
