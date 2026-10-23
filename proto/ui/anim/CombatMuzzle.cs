using Godot;
namespace Kards.Ui;
public partial class CombatMuzzle : CombatEffect
{
    public float Radius { get; set; }
    public bool Quiet { get; set; }
    public override void _Draw()
    {
        var fade = (1 - Progress) * (Quiet ? .35f : 1);
        for (var i = 0; i < 7; i++)
        {
            var direction = Vector2.FromAngle(i * Mathf.Tau / 7 + .3f);
            var side = direction.Orthogonal() * Radius * .13f;
            DrawColoredPolygon([side, direction * Radius * (i % 2 == 0 ? 1 : .6f), -side], new Color(1, .66f, .19f, fade));
        }
        DrawCircle(Vector2.Zero, Radius * .17f, new Color(1, .95f, .76f, fade));
    }
}
