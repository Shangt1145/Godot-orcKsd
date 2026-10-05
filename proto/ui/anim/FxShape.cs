using Godot;

namespace Kards.Ui;

public partial class FxShape : Node2D
{
    public string Shape { get; init; } = "dot";
    public Color Tint { get; init; } = UiStyles.Gold;
    public float Radius { get; init; } = 12;
    public override void _Draw()
    {
        if (Shape == "ring")
            DrawArc(Vector2.Zero, Radius, 0, Mathf.Tau, 48, Tint, 2, true);
        else if (Shape == "aircraft")
            DrawColoredPolygon([new(26, 0), new(4, -4), new(-6, -20), new(-12, -20), new(-7, -4), new(-24, -3), new(-28, -10), new(-30, -10), new(-26, 0), new(-30, 10), new(-28, 10), new(-24, 3), new(-7, 4), new(-12, 20), new(-6, 20), new(4, 4)], Tint);
        else if (Shape == "tracer")
            DrawLine(new(-Radius, 0), new(Radius, 0), Tint, 3, true);
        else if (Shape == "diamond")
            DrawColoredPolygon([new(0, -Radius), new(Radius, 0), new(0, Radius), new(-Radius, 0)], Tint);
        else
            DrawCircle(Vector2.Zero, Radius, Tint);
    }
}
