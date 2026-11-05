using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    internal async Task VerifyDeskAppearanceAsync(string directory, CosmeticAsset model, Func<CosmeticProfile> read, Action<CosmeticProfile> write)
    {
        async Task Frame() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw); }
        RefreshCosmetics(); for (var i = 0; i < 3; i++) await Frame();
        if (_world3D.TableclothHash != read().Tablecloth || _world3D.PropFor("self")?.Settings.Model != model.Prop?.Model)
            throw new Exception("Equipped tabletop did not reach battle");
        foreach (var tilted in new[] { false, true })
        {
            _clock.SetTiltedBattleView(tilted); await Frame();
            var prop = _world3D.PropFor("self")!;
            var point = _canvas.GetGlobalTransformWithCanvas() * _world3D.Project(prop.GlobalTransform * prop.Bounds.GetCenter());
            GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true }, true);
            GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
            await Frame(); if (!prop.IsPerforming) throw new Exception("Battle prop did not play on real mouse click");
            GetViewport().GetTexture().GetImage().SavePng(directory + (tilted ? "/battle-tilted.png" : "/battle-flat.png"));
        }
        var old = _world3D.PropFor("self");
        var original = AppearanceRuntime.Library!.Assets.First(a => a.BuiltIn && a.Kind == "decoration");
        write(read() with { Decoration = original.Image, Prop = new(Click: "spin", Size: 1.3f, Yaw: 45) });
        RefreshCosmetics(); await Frame();
        var replacement = _world3D.PropFor("self");
        if (replacement is null || ReferenceEquals(old, replacement) || replacement.Settings.Size != 1.3f || !replacement.Click())
            throw new Exception("Prop switch kept the old actor/settings");
        GetViewport().GetTexture().GetImage().SavePng(directory + "/battle-original-decoration.png");
        write(read() with { Decoration = "", Tablecloth = "", Prop = new() });
        RefreshCosmetics(); await Frame();
        if (_world3D.PropFor("self") is not null || _world3D.TableclothHash.Length != 0) throw new Exception("Unequip did not clear tabletop");
        GD.Print("DESK_BATTLE_VERIFY_OK model-animation mouse-click both-views replacement procedural-click unequip");
    }
}
