using Godot;

namespace Kards.Ui;

public partial class CombatFlame : CombatEffect
{
    public Vector2 To { get; set; }
    public bool Quiet { get; set; }
    public override void _Draw()
    {
        var envelope = Math.Min(1, Progress * 8) * Math.Min(1, (1 - Progress) * 5);
        for (var i = 0; i < 18; i++)
        {
            var p = i / 17f;
            var at = To * p + To.Normalized().Orthogonal() * (Quiet ? 0 : MathF.Sin(i * 2.3f + Progress * 39) * (2 + p * 8));
            var r = (4 + p * 13) * envelope;
            DrawCircle(at, r, new Color(.9f, .18f, .025f, .18f * envelope));
            DrawCircle(at, r * .55f, new Color(1, .65f, .12f, .5f * envelope));
        }
    }
}
