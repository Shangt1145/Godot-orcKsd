using Godot;

namespace Kards.Ui;

/// <summary>Local card and map assets composed as a pack cover; no game state or input.</summary>
public partial class ArchiveArtwork : Control
{
    private Texture2D? _back, _map;
    private bool _expansion;
    private Texture2D[] _faces = [];
    public void Initialize(TextureCache textures, bool expansion = false, IEnumerable<string>? faces = null)
    {
        _back = textures.Get("res://proto/assets/battle/card-back-v2.png");
        _map = textures.Get("res://proto/assets/battle/hq-map-v2.png"); _expansion = expansion;
        _faces = faces?.Select(textures.Get).OfType<Texture2D>().Take(3).ToArray() ?? [];
        MouseFilter = MouseFilterEnum.Ignore; Resized += QueueRedraw;
    }
    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        DrawRect(rect, new("26372b"));
        if (_map is not null) DrawTextureRectRegion(_map, rect, new(50, 200, 900, 700), new Color(.38f, .47f, .32f));
        DrawRect(rect, new Color(.05f, .12f, .07f, .55f));
        var height = Math.Min(178, Size.Y * .80f); var width = height / 1.45f;
        for (var i = -1; i <= 1; i++)
        {
            DrawSetTransform(Size / 2 + new Vector2(i * width * .35f, Math.Abs(i) * 9), i * .12f);
            var card = new Rect2(-width / 2, -height / 2, width, height);
            DrawRect(new(card.Position + new Vector2(4, 6), card.Size), new("0d170dc0"));
            var texture = _faces.Length > 0 ? _faces[(i + 1) % _faces.Length] : _back;
            if (texture is not null) DrawTextureRect(texture, card, false, _expansion ? new Color(.70f, .78f, .75f) : new Color(.94f, .91f, .85f));
        }
        DrawSetTransform(Vector2.Zero);
        DrawLine(new(16, Size.Y - 1), new(Size.X - 16, Size.Y - 1), new("93815e"), 1);
    }
}
