using Godot;

namespace Kards.Ui;

public static class BattleSymbols
{
    public static void Compass(CanvasItem canvas, Vector2 center, float radius, Color color)
    {
        for (var i = 0; i < 8; i++)
        {
            var direction = Vector2.FromAngle(i * Mathf.Pi / 4);
            var normal = new Vector2(-direction.Y, direction.X);
            var r = i % 2 == 0 ? radius : radius * .62f;
            canvas.DrawColoredPolygon([center, center + normal * radius * .11f + direction * radius * .22f, center + direction * r], color);
            canvas.DrawColoredPolygon([center, center - normal * radius * .11f + direction * radius * .22f, center + direction * r], color.Darkened(.35f));
        }
    }
    public static void Skull(CanvasItem canvas, Vector2 center, float size, Color color)
    {
        canvas.DrawCircle(center + new Vector2(0, -size * .14f), size * .43f, color);
        canvas.DrawRect(new Rect2(center + new Vector2(-size * .25f, 0), new(size * .5f, size * .48f)), color);
        var dark = new Color("242821");
        canvas.DrawCircle(center + new Vector2(-size * .18f, -size * .09f), size * .13f, dark);
        canvas.DrawCircle(center + new Vector2(size * .18f, -size * .09f), size * .13f, dark);
        canvas.DrawColoredPolygon([center + new Vector2(0, size * .1f), center + new Vector2(-size * .07f, size * .23f), center + new Vector2(size * .07f, size * .23f)], dark);
        for (var i = -1; i <= 1; i++) canvas.DrawLine(center + new Vector2(i * size * .12f, size * .32f), center + new Vector2(i * size * .12f, size * .49f), dark, 1.5f);
    }
}
