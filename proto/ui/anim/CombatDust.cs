using Godot;

namespace Kards.Ui;

// Independent lobes avoid turning the explosion atlas grey for every kind of impact.
public partial class CombatDust : CombatEffect
{
    public float Diameter { get; set; }
    public Color Tint { get; set; }
    public bool Quiet { get; set; }
    public override void _Draw()
    {
        var p = Progress;
        for (var i = 0; i < 9; i++)
        {
            var direction = Vector2.FromAngle(i * 2.399963f);
            var radius = Diameter * (.10f + .055f * (i % 3)) * (Quiet ? 1 : .6f + p);
            var center = direction * Diameter * (Quiet ? .12f : .08f + p * .22f) + new Vector2(0, Quiet ? 0 : -p * 18);
            for (var layer = 4; layer >= 1; layer--)
                DrawCircle(center, radius * layer / 4f, new Color(Tint.R * .35f, Tint.G * .36f, Tint.B * .34f,
                    Tint.A * Math.Min(1, p * 12) * MathF.Pow(1 - p, 1.2f) * .11f));
        }
    }
}
