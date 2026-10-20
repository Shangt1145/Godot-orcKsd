using Godot;

namespace Kards.Ui;

/// <summary>Menu-only paper card. Choice motion never touches the battle choice controls.</summary>
public partial class WorkshopChoiceCard : Button
{
    private Texture2D? _map, _back;
    private Texture2D[] _faces = [];
    public string Caption { get; set; } = "";
    public string Description { get; set; } = "";
    public string Badge { get; set; } = "";
    private bool _faceDown;
    public bool FaceDown { get => _faceDown; set { _faceDown = value; QueueRedraw(); } }
    public void Initialize(TextureCache textures, string title, string description, string badge = "", IEnumerable<string>? faces = null)
    {
        Caption = title; Description = description; Badge = badge;
        _map = textures.Get("res://proto/assets/battle/hq-map-v2.png");
        _back = textures.Get("res://proto/assets/battle/card-back-v2.png");
        _faces = faces?.Select(textures.Get).OfType<Texture2D>().Take(3).ToArray() ?? [];
        CustomMinimumSize = new(226, 326); MouseDefaultCursorShape = CursorShape.PointingHand;
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled", "focus" }) AddThemeStyleboxOverride(state, new StyleBoxEmpty());
        MouseEntered += QueueRedraw; MouseExited += QueueRedraw; FocusEntered += QueueRedraw; FocusExited += QueueRedraw; Resized += QueueRedraw;
        TooltipText = title + "\n" + description;
    }
    public override void _Draw()
    {
        var rect = new Rect2(0, 0, Size.X, Size.Y);
        DrawStyleBox(MenuStyles.Box(new("080e0bb0"), Colors.Transparent, 3, 0), new(6, 9, Size.X, Size.Y));
        if (FaceDown && _back is not null) { DrawTextureRect(_back, rect, false); return; }
        var highlighted = IsHovered() || HasFocus();
        var paper = MenuStyles.Box(new("ddd4b9"), highlighted ? new("edc87c") : new("a69d82"), 3, 0);
        paper.BorderWidthLeft = paper.BorderWidthTop = paper.BorderWidthRight = paper.BorderWidthBottom = highlighted ? 3 : 2;
        DrawStyleBox(paper, rect);
        DrawRect(new(7, 7, Size.X - 14, 31), new("344735"));
        DrawString(GetThemeDefaultFont(), new(16, 29), Badge, fontSize: 12, modulate: new("e0d2a7"));
        var art = new Rect2(8, 44, Size.X - 16, Size.Y * .48f);
        DrawRect(art, new("394d36"));
        if (_map is not null) DrawTextureRectRegion(_map, art, new(20, 250, 1000, 650), new Color(.56f, .64f, .45f));
        if (_faces.Length > 0)
        {
            for (var i = 0; i < _faces.Length; i++)
            {
                var dimensions = _faces[i].GetSize();
                var scale = Math.Min(art.Size.X * .78f / dimensions.X, art.Size.Y * .82f / dimensions.Y);
                var size = dimensions * scale;
                var point = art.GetCenter() + new Vector2((i - (_faces.Length - 1) / 2f) * 24, 0);
                DrawSetTransform(point, (i - (_faces.Length - 1) / 2f) * .12f);
                DrawTextureRect(_faces[i], new(-size / 2, size), false);
            }
            DrawSetTransform(Vector2.Zero);
        }
        else
        {
            var c = art.GetCenter();
            DrawCircle(c, 35, new("d9c99522"));
            DrawArc(c, 35, 0, Mathf.Tau, 64, new("e0d3a780"), 1.5f, true);
            DrawColoredPolygon([c + new Vector2(0,-29),c + new Vector2(8,-8),c + new Vector2(29,0),c + new Vector2(8,8),c + new Vector2(0,29),c + new Vector2(-8,8),c + new Vector2(-29,0),c + new Vector2(-8,-8)],new("e1d5b1"));
        }
        var y = art.End.Y + 30;
        WriteWrapped(Caption, new(16, y), Size.X - 32, 23, new("2c372c"), 2);
        DrawLine(new(16, y + 29), new(Size.X - 16, y + 29), new("a69c7b"), 1);
        WriteWrapped(Description, new(16, y + 51), Size.X - 32, 13, new("59614e"), 3);
    }
    private void WriteWrapped(string text, Vector2 at, float width, int size, Color color, int limit)
    {
        var font = GetThemeDefaultFont(); var line = ""; var lines = 0;
        foreach (var ch in text)
        {
            if (ch == '\n' || font.GetStringSize(line + ch, fontSize: size).X > width)
            {
                DrawString(font, at, line, fontSize: size, modulate: color); at.Y += size + 5; line = "";
                if (++lines >= limit) return;
            }
            if (ch != '\n') line += ch;
        }
        if (line.Length > 0) DrawString(font, at, line, fontSize: size, modulate: color);
    }
}
