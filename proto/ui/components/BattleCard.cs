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
    public string? ChoiceCaption { get; set; }
    public bool DrawFaceDown { get; set; }
    public string BackOwnerSide { get; set; } = "enemy";
    private TextureCache? _appearanceTextures;
    public bool HoldHoverHitArea { get; set; }
    public override bool _HasPoint(Vector2 point) => new Rect2(Vector2.Zero, Size).HasPoint(point)
        || HoldHoverHitArea && Mode == BattleCardMode.Hand && new Rect2(RestPosition - Position, Size).HasPoint(point);
    private float _flightTilt;
    public void SetFlightPose(Vector2 offset, float tilt) { FlightOffset = offset; _flightTilt = tilt; QueueRedraw(); }
    private Texture2D? _texture;
    private AtlasTexture? _illustration;
    private AtlasTexture? _unitIcon, _nationIcon;
    private Texture2D? _back;
    private Texture2D? _hqMap;
    private Node2D? _faceRoot;
    private CardFace? _generatedFace;
    private Control? _faceOverlay;
    internal CardFace? GeneratedFace => _generatedFace;
    public bool UsesGeneratedFace => Mode is BattleCardMode.Hand or BattleCardMode.Inspect
        && View is { Visibility: Visibility.Full } view && (_texture is null
            || view.CardId.StartsWith("diy/", StringComparison.Ordinal)
            || view.Definition.ArtPath.StartsWith("diyart://", StringComparison.Ordinal)
            || view.Definition.ArtPath.StartsWith("packart://", StringComparison.Ordinal)
            || WorkshopVisuals.NativeCardIds.Contains(view.CardId)
            || Kards.Ui.OrcBridge.WorkshopAuthoring.EditedCardIds.Contains(view.CardId));
    private static readonly Font Numbers = new SystemFont { FontNames = ["Impact", "Bahnschrift Condensed"], AllowSystemFallback = true };
    public void UpdateView(UiCardView view) { View = view; _generatedFace?.SetDefinition(view.Definition); QueueRedraw(); }
    public event Action<BattleCard, Vector2>? Pressed;
    public event Action<BattleCard, bool>? Hovered;
    private bool _hover;
    private bool _inputBound;
    public void Bind(UiCardView? view, BattleCardMode mode, TextureCache cache)
    {
        _appearanceTextures = cache;
        _texture = null; _illustration = null; _unitIcon = null; _nationIcon = null;
        View = view;
        Mode = view?.Visibility != Visibility.Full ? BattleCardMode.Hidden : mode;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        _back = AppearanceRuntime.Image(OriginalCosmetics.DefaultBack(view?.Definition.Set),cache);
        if (Mode == BattleCardMode.Hq) _hqMap = AppearanceRuntime.Image(OriginalCosmetics.DefaultHq(view?.Definition.Set),cache);
        if (Mode != BattleCardMode.Hidden && view is not null)
        {
            _texture = cache.Get(view.Definition.ArtPath);
            if (_texture is not null)
            {
                var dimensions = _texture.GetSize();
                _illustration = new AtlasTexture { Atlas = _texture, Region = new Rect2(dimensions.X * .027f, dimensions.Y * .143f, dimensions.X * .946f, dimensions.Y * .518f), FilterClip = true };
                _unitIcon = new AtlasTexture { Atlas = _texture, Region = new Rect2(dimensions.X * .423f, dimensions.Y * .690f, dimensions.X * .154f, dimensions.Y * .090f), FilterClip = true };
                _nationIcon = new AtlasTexture { Atlas = _texture, Region = new Rect2(dimensions.X * .858f, dimensions.Y * .024f, dimensions.X * .105f, dimensions.Y * .083f), FilterClip = true };
                if (view.CardId.StartsWith("diy/", StringComparison.Ordinal)) { _unitIcon = null; _nationIcon = null; }
            }
        }
        if (UsesGeneratedFace && view is not null)
        {
            if (_faceRoot is null)
            {
                _faceRoot = new Node2D(); AddChild(_faceRoot);
                _generatedFace = new CardFace { MouseFilter = MouseFilterEnum.Ignore, NumericBaselineOffset = 10 }; _faceRoot.AddChild(_generatedFace);
                _faceOverlay = new Control { MouseFilter = MouseFilterEnum.Ignore }; _faceRoot.AddChild(_faceOverlay);
                _faceOverlay.Draw += () => DrawHandOverlays(_faceOverlay);
            }
            _generatedFace!.SetDefinition(view.Definition);
            _generatedFace.SetIllustration(_texture, view.Definition.ArtPath.StartsWith("diyart://", StringComparison.Ordinal)
                ? DiyArtStore.ZoomOf(view.CardId) : 100);
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
        var faceTransform = new Transform2D(xAxis, yAxis, PivotOffset + FlightOffset - xAxis * PivotOffset.X - yAxis * PivotOffset.Y);
        DrawSetTransformMatrix(faceTransform);
        if (_faceRoot is not null)
        {
            _faceRoot.Visible = UsesGeneratedFace && !DrawFaceDown;
            _faceRoot.Transform = faceTransform;
            _generatedFace!.Size = Size;
            _faceOverlay!.Size = Size;
            _faceOverlay.QueueRedraw();
        }
        var bounds = new Rect2(Vector2.Zero, Size);
        var elevation = Math.Clamp(DeploymentElevation, 0, 1);
        DrawStyleBox(UiStyles.Box(new Color(0, 0, 0, .36f + elevation * .12f), Colors.Transparent, 4, 0),
            new Rect2(new Vector2(5 + elevation * 4, 7 + elevation * 16), Size));
        var edge = Selected || Targeted ? new Color("d9c281") : _hover ? new Color("e0dcc5") : new Color("a9a186");
        var paper = UiStyles.Box(new Color("c6bea3"), edge, 3, 0);
        paper.BorderWidthLeft = paper.BorderWidthTop = paper.BorderWidthRight = paper.BorderWidthBottom = Selected || Targeted ? 3 : 1;
        DrawStyleBox(paper, bounds);
        if (Mode == BattleCardMode.Hidden || DrawFaceDown)
        {
            if (_appearanceTextures is {} appearance && AppearanceRuntime.Image(AppearanceRuntime.Profile(View?.OwnerSide ?? BackOwnerSide).CardBack,appearance) is {} customBack)
            { CosmeticPaper.DrawBack(this,customBack,bounds); return; }
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
            if (UsesGeneratedFace) return;
            if (_texture is not null) DrawTextureRect(_texture, bounds.Grow(-3), false);
            DrawHandOverlays(this);
            return;
        }
        DrawFieldOrHq(bounds);
    }

    private void DrawHandOverlays(Control canvas)
    {
        if (UsesGeneratedFace)
        {
            var edge = Selected || Targeted ? new Color("d9c281") : _hover ? new Color("e0dcc5") : new Color("a9a186");
            canvas.DrawRect(new Rect2(Vector2.Zero, Size), edge, false, Selected || Targeted ? 3 : 1);
        }
        if (Available) canvas.DrawRect(new Rect2(5, 5, 31, 31), new Color("9c873a"), false, 2);
        if (View is { } face && ((face.IsCounterArmed && face.OwnerSide == "self") || face.EffectiveCost != face.Definition.Cost))
        {
            var cost = new Rect2(5, 5, Size.X * .17f, Size.Y * .13f);
            var armed = face.IsCounterArmed && face.OwnerSide == "self";
            canvas.DrawRect(cost, new Color(armed ? "d5b54b" : "353b31"));
            TextOn(canvas, face.EffectiveCost?.ToString() ?? "—", cost, (int)(Size.X * .13f), new Color(armed ? "343a30" : "dec775"));
        }
        if (ChoiceCaption is { } caption)
        {
            var box = new Rect2(7, Size.Y * .76f, Size.X - 14, Size.Y * .24f - 7);
            canvas.DrawRect(box, new Color("d4ccb2"));
            // Draw the branch text on the paper, including during horizontal flips.
            var font = GetThemeDefaultFont();
            var textSize = (int)Math.Clamp(Size.X * .065f, 11, 17);
            var y = box.Position.Y + textSize + 2; var line = "";
            foreach (var ch in caption)
            {
                if (ch == '\n' || font.GetStringSize(line + ch, fontSize: textSize).X > box.Size.X - 8)
                {
                    canvas.DrawString(font, new(box.Position.X + 4, y), line, fontSize: textSize, modulate: new("34392e"));
                    line = ""; y += textSize + 3;
                }
                if (ch != '\n') line += ch;
                if (y > box.End.Y) break;
            }
            if (line.Length > 0 && y <= box.End.Y)
                canvas.DrawString(font, new(box.Position.X + 4, y), line, fontSize: textSize, modulate: new("34392e"));
        }
    }

    private void DrawFieldOrHq(Rect2 bounds)
    {
        if (Mode == BattleCardMode.Hq)
        {
            var profile = AppearanceRuntime.Profile(View?.OwnerSide ?? "self");
            var customHq = _appearanceTextures is {} appearance ? AppearanceRuntime.Image(profile.Headquarters,appearance) : null;
            DrawRect(bounds.Grow(-5), new Color("b8b298"));
            if (customHq is not null) DrawTextureRect(customHq,bounds.Grow(-5),false);
            else if (_hqMap is {} hqTexture) DrawTextureRect(hqTexture, bounds.Grow(-5), false);
            var name = profile.HeadquartersName.Length>0 ? profile.HeadquartersName : View?.Definition.Name ?? "总部";
            if(profile.HeadquartersName.Length>0||customHq is not null&&!OriginalCosmetics.IsHeadquarters(profile.Headquarters)) DrawRect(new Rect2(5,5,Size.X-10,32),new Color(0,0,0,.55f));
            var title=name.Length>12?name[..11]+"…":name;
            var titleSize=title.Length>7?10:14;
            if(profile.HeadquartersName.Length>0 || customHq is not null&&!OriginalCosmetics.IsHeadquarters(profile.Headquarters))
            {
                Text(title,new Rect2(6,10,Size.X-10,22),titleSize,new Color("494738"));
                Text(title,new Rect2(5,9,Size.X-10,22),titleSize,new Color("eee6cc"));
            }
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
        if (View is { } status)
        {
            var marks = new List<string>();
            if (status.IsVeteran) marks.Add("★");
            if (status.IsCovert) marks.Add("隐蔽");
            if (status.IsSilenced) marks.Add("抑制");
            if (status.IsSuppressed) marks.Add("压制");
            if (marks.Count > 0)
            {
                DrawRect(new Rect2(5, 28, Size.X - 10, 18), new Color(.12f, .14f, .10f, .85f));
                Text(string.Join(" · ", marks), new Rect2(5, 27, Size.X - 10, 18), 11, new Color("e3d5a5"));
            }
        }
    }
    private void Badge(float x, float y, int? value, bool shield, float r)
    {
        var center = new Vector2(x, y);
        var ink = new Color("3f4538");
        if (shield)
        {
            if(OriginalCosmetics.Ui("IconDefenceBase") is {} shieldTexture)
                DrawTextureRect(shieldTexture,new(x-r,y-r,r*2,r*2),false);
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
        => TextOn(this, text, box, size, color);

    private static void TextOn(Control canvas, string text, Rect2 box, int size, Color color)
    {
        var numeric = text.All(c => char.IsDigit(c) || c is '—');
        var font = numeric ? Numbers : canvas.GetThemeDefaultFont();
        var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        canvas.DrawString(font, new Vector2(box.Position.X + Math.Max(0, (box.Size.X - width) / 2), box.Position.Y + size + (numeric ? 3 : 0)), text, HorizontalAlignment.Left, box.Size.X, size, color);
    }
    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse)
        { Pressed?.Invoke(this, GetGlobalTransformWithCanvas() * mouse.Position); AcceptEvent(); }
    }
}
