using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public enum BattleCardMode { Hand, Field, Hq, Hidden, Inspect }

/// <summary>Battle-only paper presentation; catalogue and legacy gallery retain their existing component.</summary>
public partial class BattleCard : Control
{
    public UiCardView? View { get; private set; }
    public BattleCardMode Mode { get; private set; }
    public string Uid => View?.Uid ?? "";
    public bool Selected { get; set; }
    public bool Available { get; set; }
    public bool Targeted { get; set; }
    public Vector2 RestPosition { get; set; }
    public float RestRotation { get; set; }
    public Vector2 FlightOffset { get; private set; }
    public float PaperShear { get; set; }
    public float DeploymentElevation { get; set; }
    private float _flightTilt;
    public void SetFlightPose(Vector2 offset, float tilt) { FlightOffset = offset; _flightTilt = tilt; QueueRedraw(); }
    private Texture2D? _texture;
    private AtlasTexture? _illustration;
    private AtlasTexture? _unitIcon, _nationIcon;
    private Texture2D? _back;
    private Texture2D? _hqMap;
    private static readonly Font Numbers = new SystemFont { FontNames = ["Impact", "Bahnschrift Condensed"], AllowSystemFallback = true };
    public void UpdateView(UiCardView view) { View = view; QueueRedraw(); }
    public event Action<BattleCard, Vector2>? Pressed;
    public event Action<BattleCard, bool>? Hovered;
    private bool _hover;
    private bool _inputBound;
    public void Bind(UiCardView? view, BattleCardMode mode, TextureCache cache)
    {
        View = view;
        Mode = view?.Visibility != Visibility.Full ? BattleCardMode.Hidden : mode;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        if (Mode == BattleCardMode.Hidden) _back = cache.Get("res://proto/assets/battle/card-back-v2.png");
        if (Mode == BattleCardMode.Hq) _hqMap = cache.Get("res://proto/assets/battle/hq-map-v2.png");
        if (Mode != BattleCardMode.Hidden && view is not null)
        {
            _texture = cache.Get(view.Definition.ArtPath);
            if (_texture is not null)
            {
                var dimensions = _texture.GetSize();
                _illustration = new AtlasTexture { Atlas = _texture, Region = new Rect2(dimensions.X * .027f, dimensions.Y * .143f, dimensions.X * .946f, dimensions.Y * .518f), FilterClip = true };
                _unitIcon = new AtlasTexture { Atlas = _texture, Region = new Rect2(dimensions.X * .423f, dimensions.Y * .690f, dimensions.X * .154f, dimensions.Y * .090f), FilterClip = true };
                _nationIcon = new AtlasTexture { Atlas = _texture, Region = new Rect2(dimensions.X * .858f, dimensions.Y * .024f, dimensions.X * .105f, dimensions.Y * .083f), FilterClip = true };
            }
        }
        if (!_inputBound)
        {
            MouseEntered += () => { _hover = true; Hovered?.Invoke(this, true); QueueRedraw(); };
            MouseExited += () => { _hover = false; Hovered?.Invoke(this, false); QueueRedraw(); };
            _inputBound = true;
        }
        QueueRedraw();
    }
    private Color NationColor => View?.Definition.Set switch
    {
        "UN" => new("4f645b"), "deran" => new("686053"), "USG" => new("506783"), "av76" => new("825747"), _ => new("686046")
    };
    public override void _Draw()
    {
        var xAxis = Vector2.FromAngle(_flightTilt);
        var yAxis = Vector2.FromAngle(_flightTilt + MathF.PI / 2 + PaperShear);
        DrawSetTransformMatrix(new Transform2D(xAxis, yAxis, PivotOffset + FlightOffset - xAxis * PivotOffset.X - yAxis * PivotOffset.Y));
        var bounds = new Rect2(Vector2.Zero, Size);
        var elevation = Math.Clamp(DeploymentElevation, 0, 1);
        DrawStyleBox(UiStyles.Box(new Color(0, 0, 0, .36f + elevation * .12f), Colors.Transparent, 4, 0),
            new Rect2(new Vector2(5 + elevation * 4, 7 + elevation * 16), Size));
        var edge = Selected || Targeted ? new Color("d9c281") : _hover ? new Color("e0dcc5") : new Color("a9a186");
        var paper = UiStyles.Box(new Color("c6bea3"), edge, 3, 0);
        paper.BorderWidthLeft = paper.BorderWidthTop = paper.BorderWidthRight = paper.BorderWidthBottom = Selected || Targeted ? 3 : 1;
        DrawStyleBox(paper, bounds);
        if (Mode == BattleCardMode.Hidden)
        {
            if (_back is not null) { DrawTextureRect(_back, bounds, false); return; }
            DrawRect(new Rect2(5, 5, Size.X - 10, Size.Y - 10), new Color("303b37"));
            DrawRect(new Rect2(10, 10, Size.X - 20, Size.Y - 20), new Color("b0a578"), false, 1);
            var c = Size / 2;
            DrawColoredPolygon([c + new Vector2(0, -20), c + new Vector2(18, 0), c + new Vector2(0, 20), c + new Vector2(-18, 0)], new Color("b0a578"));
            DrawLine(c + new Vector2(-7, 0), c + new Vector2(7, 0), new Color("303b37"), 3);
            return;
        }
        if (Mode is BattleCardMode.Hand or BattleCardMode.Inspect)
        {
            if (_texture is not null) DrawTextureRect(_texture, bounds.Grow(-3), false);
            else
            {
                DrawRect(new Rect2(5, 5, Size.X - 10, 28), NationColor);
                Text(View?.Definition.Name ?? "卡牌", new Rect2(9, 9, Size.X - 18, 22), 15, new Color("eee7ce"));
                Text(View?.Definition.Text ?? "", new Rect2(10, 60, Size.X - 20, 30), 11, new Color("373b32"));
            }
            if (Available) DrawRect(new Rect2(5, 5, 31, 31), new Color("9c873a"), false, 2);
            if (View is { } face && ((face.IsCounterArmed && face.OwnerSide == "self") || face.EffectiveCost != face.Definition.Cost))
            {
                var cost = new Rect2(5, 5, Size.X * .17f, Size.Y * .13f);
                var armed = face.IsCounterArmed && face.OwnerSide == "self";
                DrawRect(cost, new Color(armed ? "d5b54b" : "353b31"));
                Text(face.EffectiveCost?.ToString() ?? "—", cost, (int)(Size.X * .13f), new Color(armed ? "343a30" : "dec775"));
            }
            return;
        }
        if (Mode == BattleCardMode.Hq)
        {
            DrawRect(bounds.Grow(-5), new Color("b8b298"));
            if (_hqMap is not null) DrawTextureRect(_hqMap, bounds.Grow(-5), false);
            Text(View?.Definition.Name ?? "总部", new Rect2(6, 10, Size.X - 10, 22), 14, new Color("494738"));
            Text(View?.Definition.Name ?? "总部", new Rect2(5, 9, Size.X - 10, 22), 14, new Color("eee6cc"));
            BattleSymbols.Compass(this, new(Size.X / 2, Size.Y * .42f), Size.X * .23f, new("e9e4ce"));
            Badge(Size.X / 2, Size.Y - 42, View?.Health ?? View?.EffectiveDefense, true, 24);
            return;
        }
        if (_illustration is not null) DrawTextureRect(_illustration, new Rect2(5, 25, Size.X - 10, Size.Y - 56), false);
        else DrawRect(new Rect2(5, 25, Size.X - 10, Size.Y - 56), NationColor.Darkened(.2f));
        DrawRect(new Rect2(5, 5, Size.X - 10, 20), NationColor);
        DrawRect(new Rect2(5, 5, 23, 20), new Color("353b31"));
        Text(View?.EffectiveOpCost?.ToString() ?? "—", new Rect2(5, 3, 23, 24), 17, Available ? new Color("dec775") : new Color("e9e6cf"));
        if (_nationIcon is not null) DrawTextureRect(_nationIcon, new Rect2(Size.X - 24, 7, 16, 16), false);
        Badge(22, Size.Y - 23, View?.EffectiveAttack, false, 14);
        Badge(Size.X - 22, Size.Y - 23, View?.Health ?? View?.EffectiveDefense, true, 14);
        var type = View?.Definition.UnitType switch { "infantry" => "步", "tank" => "坦", "artillery" => "炮", "fighter" => "歼", "bomber" => "轰", "cruiser" or "ship" => "舰", "spacefighter" or "space_fighter" => "空", "landcruiser" or "land_cruiser" or "space" => "陆", "structure" => "筑", _ => "?" };
        if (_unitIcon is not null) DrawTextureRect(_unitIcon, new Rect2(Size.X / 2 - 15, Size.Y - 37, 30, 28), false);
        else Text(type, new Rect2(Size.X / 2 - 13, Size.Y - 35, 26, 25), 16, new Color("454b3c"));
        if (View?.Keywords.Count > 0)
            Text("◆", new Rect2(Size.X - 19, 32, 17, 20), 13, new Color("e3d5a5"));
        if (View?.IsSuppressed == true) DrawRect(bounds.Grow(-4), new Color(.15f, .16f, .14f, .45f));
    }
    private void Badge(float x, float y, int? value, bool shield, float r)
    {
        var center = new Vector2(x, y);
        var ink = new Color("3f4538");
        if (shield)
        {
            if (Mode == BattleCardMode.Hq)
            {
                var outer = r + 2;
                DrawColoredPolygon([center + new Vector2(-outer, -outer), center + new Vector2(outer, -outer), center + new Vector2(outer, outer * .38f), center + new Vector2(0, outer), center + new Vector2(-outer, outer * .38f)], new("e2dcc3"));
            }
            DrawColoredPolygon([center + new Vector2(-r, -r), center + new Vector2(r, -r), center + new Vector2(r, r * .38f), center + new Vector2(0, r), center + new Vector2(-r, r * .38f)], ink);
        }
        else
        {
            var arch = UiStyles.Box(ink, Colors.Transparent, 0, 0);
            arch.CornerRadiusTopLeft = arch.CornerRadiusTopRight = (int)r;
            DrawStyleBox(arch, new(x - r, y - r, r * 2, r * 2));
        }
        var baseValue = shield ? View?.Definition.BaseDefense : View?.Definition.BaseAttack;
        var tint = value < baseValue ? new Color("d66142") : value > baseValue ? new Color("89ae66") : new Color("ece7d2");
        Text(value?.ToString() ?? "—", new Rect2(x - r, y - r - 2, r * 2, r * 2 + 4), (int)r + 5, tint);
    }
    private void Text(string text, Rect2 box, int size, Color color)
    {
        var font = text.All(c => char.IsDigit(c) || c is '—') ? Numbers : GetThemeDefaultFont();
        var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        DrawString(font, new Vector2(box.Position.X + Math.Max(0, (box.Size.X - width) / 2), box.Position.Y + size), text, HorizontalAlignment.Left, box.Size.X, size, color);
    }
    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse)
        { Pressed?.Invoke(this, GetGlobalTransformWithCanvas() * mouse.Position); AcceptEvent(); }
    }
}
