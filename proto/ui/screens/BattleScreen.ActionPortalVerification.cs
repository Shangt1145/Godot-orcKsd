using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    public bool IsIncomingCardFlying(string uid) => _sequence.IsDrawing(uid);
    public Vector2 DeploymentGapPoint(int ordinal)
    {
        var gap = InteractionActions.DeploymentGaps.Single(g => g.Ordinal == ordinal);
        _dragDeploymentGaps = InteractionActions.DeploymentGaps;
        return new(GapX(gap), SelfArea.Position.Y + FieldCardSize.Y / 2);
    }
    public IReadOnlyList<string> RenderedSupportUids() => _cards.Values
        .Where(c => c.View?.OwnerSide == "self" && c.Mode is BattleCardMode.Field or BattleCardMode.Hq
            && (c.View.IsHq || c.View.Zone == "support"))
        .OrderBy(c => c.RestPosition.X).Select(c => c.Uid).ToArray();
    public int DeploymentMarkerCount => _slotHints.Count;
    public bool PendingActorForVerification(string uid) => _interaction.IsPending(uid);

    /// <summary>Use the moving actor's actual transformed position, not its eventual hand position.</summary>
    public async Task DragFlyingCardAsync(string uid, Vector2 destination)
    {
        var card = _cards[uid];
        var start = card.GetGlobalTransformWithCanvas() * (card.Size / 2);
        GetViewport().PushInput(new InputEventMouseMotion { Position = start, GlobalPosition = start }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        start = card.GetGlobalTransformWithCanvas() * (card.Size / 2);
        GetViewport().PushInput(new InputEventMouseButton { Position = start, GlobalPosition = start,
            ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_pressed != card || _sequence.IsDrawing(uid))
            throw new Exception("Pressing the flying draw did not transfer its actor to the gesture.");
        var finish = _canvas.GetGlobalTransformWithCanvas() * destination;
        GetViewport().PushInput(new InputEventMouseMotion { Position = finish, GlobalPosition = finish,
            Relative = finish - start, ButtonMask = MouseButtonMask.Left }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().PushInput(new InputEventMouseButton { Position = finish, GlobalPosition = finish,
            ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
