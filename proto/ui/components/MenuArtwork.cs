using Godot;

namespace Kards.Ui;

/// <summary>Reuses local presentation assets without changing card or engine data.</summary>
public partial class MenuBackdrop : Control
{
    private Texture2D? _desk;
    public void Initialize(TextureCache textures)
    {
        _desk = textures.Get("res://proto/assets/battle/tabletop-v2.png");
        MouseFilter = MouseFilterEnum.Ignore;
        Resized += QueueRedraw;
    }
    public override void _Draw()
    {
        DrawRect(new(Vector2.Zero, Size), MenuStyles.Bg);
        if (_desk is not null) DrawTextureRect(_desk, new(Vector2.Zero, Size), false, new Color(.35f, .40f, .31f, .10f));
        DrawRect(new(Vector2.Zero, Size), new Color(.055f, .085f, .06f, .35f));
    }
}

public partial class MenuDeckCover : Control
{
    private Texture2D? _back;
    private string _nation = "";
    public void Initialize(TextureCache textures, string nation, float width = 170)
    {
        _back = textures.Get("res://proto/assets/battle/card-back-v2.png"); _nation = nation;
        CustomMinimumSize = new(width, width * 1.38f);
        MouseFilter = MouseFilterEnum.Ignore; Resized += QueueRedraw;
    }
    public override void _Draw()
    {
        var card = new Rect2(new(6, 3), new(Size.X - 17, Size.Y - 15));
        DrawRect(new(card.Position + new Vector2(9, 10), card.Size), new("090e0dcc"));
        DrawRect(new(card.Position + new Vector2(5, 5), card.Size), new("8b8061"));
        if (_back is not null) DrawTextureRect(_back, card, false);
        else DrawRect(card, new("424e35"));
        DrawRect(new(card.Position + new Vector2(14, 21), new(card.Size.X - 28, 28)), new("182016dd"));
        var font = GetThemeDefaultFont();
        DrawString(font, card.Position + new Vector2(18, 41), _nation.ToUpperInvariant(), HorizontalAlignment.Center, card.Size.X - 36, 15, MenuStyles.MenuInk);
    }
}

public partial class MenuHero : Control
{
    private Texture2D? _map, _back;
    private Texture2D[] _faces = [];
    public void SetCards(TextureCache textures, IEnumerable<string> paths)
    { _faces = paths.Select(textures.Get).OfType<Texture2D>().Take(3).ToArray(); QueueRedraw(); }
    private Tween? _entrance;
    private float _reveal = 1;
    private Vector2 _pointer;
    private bool _reduced;
    internal float Reveal => _reveal;
    internal bool IsAnimating => _entrance?.IsRunning() == true;
    private readonly GradientTexture2D _shade = new()
    {
        Width = 4, Height = 256, FillFrom = new(.5f, 0), FillTo = new(.5f, 1),
        Gradient = new Gradient { Colors = [new Color(.045f, .075f, .05f, 0), new Color(.045f, .075f, .05f, .94f)], Offsets = [0, 1] }
    };
    public void Initialize(TextureCache textures)
    {
        _map = textures.Get("res://proto/assets/battle/hq-map-v2.png");
        _back = textures.Get("res://proto/assets/battle/card-back-v2.png");
        MouseFilter = MouseFilterEnum.Ignore; Resized += QueueRedraw;
        VisibilityChanged += () => { if (!IsVisibleInTree()) Settle(); };
    }
    internal void PlayEntrance(bool reduced)
    {
        Settle(); _reduced = reduced;
        if (reduced) return;
        _reveal = 0; QueueRedraw();
        _entrance = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _entrance.TweenMethod(Callable.From<float>(value => { _reveal = value; QueueRedraw(); }), 0f, 1f, .46);
    }
    internal void SetReducedMotion(bool reduced) { _reduced = reduced; if (reduced) Settle(); }
    internal void Settle() { _entrance?.Kill(); _entrance = null; _reveal = 1; _pointer = Vector2.Zero; QueueRedraw(); }
    public override void _Process(double delta)
    {
        if (!IsVisibleInTree() || _reduced || Size.X < 1 || Size.Y < 1) return;
        var mouse = GetLocalMousePosition();
        var target = new Rect2(Vector2.Zero, Size).HasPoint(mouse) ? (mouse / Size - new Vector2(.5f, .5f)) * 16 : Vector2.Zero;
        if (_pointer.DistanceSquaredTo(target) < .0025f) return;
        _pointer = _pointer.Lerp(target, 1 - Mathf.Exp((float)-delta * 9)); QueueRedraw();
    }
    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        DrawRect(rect, new("202e23"));
        if (_map is not null) DrawTextureRectRegion(_map, rect, new Rect2(0, 340, 1086, 600), new Color(.45f, .52f, .38f));
        DrawRect(rect, new Color(.055f, .095f, .065f, .65f));
        // A map route and physical card fan, drawn in Godot and independent of game rules.
        var route = new Vector2[] { new(Size.X * .60f, Size.Y * .80f), new(Size.X * .72f, Size.Y * .65f), new(Size.X * .83f, Size.Y * .25f) };
        for (var i = 0; i < route.Length - 1; i++)
        {
            var segment = Math.Clamp(_reveal * 2 - i, 0, 1);
            if (segment > 0) DrawLine(route[i], route[i].Lerp(route[i + 1], segment), new("d3b77970"), 2, true);
        }
        for (var i = 0; i < route.Length; i++)
        {
            var alpha = Math.Clamp(_reveal * 3 - i, 0, 1);
            DrawArc(route[i], 10, 0, Mathf.Tau, 32, new Color(new Color("d3b77980"), alpha * .5f), 1.5f, true);
            DrawCircle(route[i], 3, new Color(MenuStyles.Gold, alpha));
        }
        if (_back is not null)
        {
            var width = Math.Min(192, Size.X * .21f); var height = width * 1.38f;
            var center = new Vector2(Size.X * .81f, Size.Y * .48f) + _pointer;
            for (var i = -1; i <= 1; i++)
            {
                DrawSetTransform(center + new Vector2(i * width * .43f * _reveal, Math.Abs(i) * 25 + (1 - _reveal) * 35), i * .16f * _reveal);
                var card = new Rect2(-width / 2, -height / 2, width, height);
                DrawRect(new(card.Position + new Vector2(7, 9), card.Size), new("080d0bcc"));
                var face = _faces.Length > 0 ? _faces[(i + 1) % _faces.Length] : _back;
                DrawTextureRect(face, card, false, new Color(i == 0 ? Colors.White : new Color(.82f, .84f, .72f), .25f + .75f * _reveal));
            }
            DrawSetTransform(Vector2.Zero);
        }
        // Keep the bottom caption legible on both light and dark areas of the map.
        DrawTextureRect(_shade, new(0, Size.Y * .58f, Size.X, Size.Y * .42f), false);
        DrawRect(rect, MenuStyles.MenuLine, false, 1);
    }
    public override void _ExitTree() => _entrance?.Kill();
}
