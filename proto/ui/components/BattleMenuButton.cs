using Godot;

namespace Kards.Ui;

public partial class BattleMenuButton : MenuButton
{
    public override void _Draw()
    {
        var center = Size / 2; var points = new List<Vector2>();
        for (var i = 0; i < 32; i++)
            points.Add(center + Vector2.FromAngle(i * Mathf.Tau / 32) * (i % 4 is 1 or 2 ? 17 : 13));
        DrawColoredPolygon(points.ToArray(), new("e6e2d3"));
        DrawCircle(center, 7, new("20201a"));
    }
}
public partial class BattleHistoryButton : Button
{
    public override void _Draw()
    {
        for (var i = 0; i < 3; i++) DrawRect(new Rect2(3, 3 + i * 9, 27, 3), new("e1ddcc"));
    }
}
