using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>Logical board-space overlay. Forecast values are adapter projections.</summary>
public partial class BattleAim : Control
{
    public Vector2 From { get; set; }
    public Vector2 To { get; set; }
    public bool Moving { get; set; }
    public UiAttackPreview? Preview { get; set; }
    public override void _Draw()
    {
        var distance = From.DistanceTo(To);
        if (!Visible || distance < 20) return;
        var direction = (To - From).Normalized();
        var normal = new Vector2(-direction.Y, direction.X);
        var ink = Moving ? new Color("bcbfaf") : new Color("9b2826");
        Vector2[] points;
        if (distance < 45f)
        {
            // The head is 30px wide at the shoulder, so a short drag cannot carry it without the
            // shoulder edges crossing the shaft — Godot then rejects the polygon outright
            // ("Invalid polygon data, triangulation failed"). A wedge reads as direction instead.
            // Verified across all lengths >= 20 and every angle; see tools/aim_polygon_probe.py.
            var half = MathF.Min(6f, distance * .28f);
            points = [From + normal * half, To, From - normal * half];
        }
        else
        {
            var end = To - direction * 14;
            var shoulder = end - direction * 27;
            points = new Vector2[] { From + normal * 6, shoulder + normal * 6, shoulder + normal * 15, end, shoulder - normal * 15, shoulder - normal * 6, From - normal * 6 };
        }
        DrawColoredPolygon(points, ink);
        DrawPolyline(points.Append(points[0]).ToArray(), new Color("efe7cf"), 2.5f, true);
        if (Preview is { } p)
        {
            Forecast(To + new Vector2(0, -55), p.DamageToDefender, p.DefenderDies);
            if (p.DamageToAttacker > 0 || p.AttackerDies) Forecast(From + new Vector2(0, -55), p.DamageToAttacker, p.AttackerDies);
        }
    }
    private void Forecast(Vector2 at, int? damage, bool fatal)
    {
        DrawColoredPolygon([at + new Vector2(-19, -18), at + new Vector2(19, -18), at + new Vector2(19, 9), at + new Vector2(0, 24), at + new Vector2(-19, 9)], new Color("342c26"));
        var text = fatal ? "" : damage?.ToString() ?? "?";
        var font = GetThemeDefaultFont();
        var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, 25).X;
        DrawString(font, at + new Vector2(-width / 2, 8), text, HorizontalAlignment.Left, -1, 25, new Color("f0ead4"));
        if (fatal) BattleSymbols.Skull(this, at, 26, new("f0ead4"));
        if (fatal && damage is not null)
            DrawString(font, at + new Vector2(23, 8), damage.ToString(), HorizontalAlignment.Left, -1, 19, new Color("f0ead4"));
    }
}
