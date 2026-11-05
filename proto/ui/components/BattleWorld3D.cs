using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>Shared perspective world. Screen-authored formation anchors are calibrated onto the desk;
/// cards have independent depth, thickness and altitude. No rules or target eligibility live here.</summary>
public partial class BattleWorld3D : SubViewportContainer
{
    private readonly Dictionary<BattleCard, WorldCard> _cards = new();
    private readonly Dictionary<string, (string Hash, DeskPropInstance Root, DeskPropSettings Settings)> _props = new();
    private readonly Dictionary<string, (string Hash, DeskPropSettings Settings)> _queuedProps = new();
    private StandardMaterial3D _deskMaterial = null!;
    private Texture2D _defaultDesk = null!;
    private Vector3 _defaultDeskUv;
    private SubViewport _world = null!;
    private Node3D _root = null!;
    private Camera3D _camera = null!;
    private TextureCache _textures = null!;
    private Control _sources = null!;
    public int ActorCount => _cards.Count;
    public int PropCount => _props.Count;
    public Camera3D Camera => _camera;
    internal string TableclothHash { get; private set; } = "";
    internal DeskPropInstance? PropFor(string side) => _props.TryGetValue(side, out var prop) ? prop.Root : null;

    public void SetTiltedView(bool tilted)
    {
        _camera.Position = tilted ? new(0, 13.5f, 8.8f) : new(0, 16.1f, 0);
        _camera.Basis = Basis.LookingAt(-_camera.Position, tilted ? Vector3.Up : Vector3.Forward);
        if (!IsInsideTree()) return;
        foreach (var (side, prop) in _props)
            prop.Root.Position = DeskPoint(new(165, side == "self" ? 495 : 165), .02f);
        foreach (var (source, actor) in _cards)
            if (GodotObject.IsInstanceValid(source) && !source.IsQueuedForDeletion()) Sync(source, actor);
    }

    public float AltitudeOf(BattleCard source) => _cards[source].Root.Position.Y;

    public void Initialize(Control sources, TextureCache textures)
    {
        _sources = sources; _textures = textures;
        Size = new(1280, 720); Stretch = true; MouseFilter = MouseFilterEnum.Ignore;
        _world = new SubViewport { Size = new(1600, 900), OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
            Msaa3D = Viewport.Msaa.Msaa2X };
        AddChild(_world);
        _root = new Node3D(); _world.AddChild(_root);
        _camera = new Camera3D { Position = new(0, 13.5f, 8.8f), Fov = 43, Near = .05f, Far = 80, Current = true };
        // Initialize is called before BattleScreen enters the tree; LookAt requires
        // a global transform and silently leaves the camera unrotated in that case.
        _camera.Basis = Basis.LookingAt(-_camera.Position, Vector3.Up);
        _root.AddChild(_camera);
        _root.AddChild(new WorldEnvironment { Environment = new Godot.Environment {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new("24231e"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new("e5ddc8"), AmbientLightEnergy = .65f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled } });
        _root.AddChild(new DirectionalLight3D { RotationDegrees = new(-58, -28, 0),
            LightColor = new("fff0d4"), LightEnergy = 1.25f, ShadowEnabled = true,
            DirectionalShadowMaxDistance = 35 });
        var ramp = new Gradient();
        ramp.SetColor(0, new("30332d")); ramp.SetColor(1, new("514f40"));
        var wood = new NoiseTexture2D { Width = 512, Height = 512, Seamless = true,
            Noise = new FastNoiseLite { Frequency = .027f, FractalOctaves = 3 }, ColorRamp = ramp };
        Texture2D deskTexture = wood;
        // Reuse only the central wood, excluding the baked vignette and props.
        // The complete board render is never used as a background or projected scene.
        if (OriginalCosmetics.Ui("T_nui-board-render-default-2048")?.GetImage() is { } board)
        {
            if (board.IsCompressed()) board.Decompress();
            deskTexture = ImageTexture.CreateFromImage(board.GetRegion(new Rect2I(
                (int)(board.GetWidth() * .25f), (int)(board.GetHeight() * .16f),
                (int)(board.GetWidth() * .56f), (int)(board.GetHeight() * .66f))));
        }
        _defaultDesk = deskTexture;
        _defaultDeskUv = deskTexture == wood ? new(2, 8, 1) : new(2, 2, 1);
        _deskMaterial = new StandardMaterial3D { AlbedoTexture = deskTexture,
            Uv1Scale = _defaultDeskUv, Roughness = .88f };
        var desk = new MeshInstance3D { Position = new(0, -.16f, 0),
            Mesh = new BoxMesh { Size = new(35, .3f, 25) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new("393023"), Roughness = .9f } };
        _root.AddChild(desk);
        // BoxMesh packs six faces into an atlas. A separate top plane gives a
        // tablecloth the full image UVs instead of displaying one cropped face.
        _root.AddChild(new MeshInstance3D { Position = new(0, -.009f, 0),
            Mesh = new PlaneMesh { Size = new(35, 25) }, MaterialOverride = _deskMaterial });
        SetProcess(true);
    }

    private Vector2 Pixels(Vector2 board) => board * new Vector2(_world.Size.X / 1280f, _world.Size.Y / 720f);
    public Vector3 DeskPoint(Vector2 board, float height = 0)
    {
        var origin = _camera.ProjectRayOrigin(Pixels(board));
        var direction = _camera.ProjectRayNormal(Pixels(board));
        return origin + direction * ((height - origin.Y) / direction.Y);
    }
    public Vector2 Project(Vector3 world) => _camera.UnprojectPosition(world)
        / new Vector2(_world.Size.X / 1280f, _world.Size.Y / 720f);

    public override void _Process(double delta)
    {
        if (_sources is null || !IsVisibleInTree()) return;
        foreach (var (side, pending) in _queuedProps.ToArray()) { _queuedProps.Remove(side); SetProp(side, pending.Hash, pending.Settings); }
        var live = _sources.GetChildren().OfType<BattleCard>()
            .Concat(_sources.GetParent().GetChildren().OfType<BattleCard>())
            .Concat(_sources.GetParent().GetChildren().OfType<BattleSequence>()
                .SelectMany(sequence => sequence.GetChildren().OfType<BattleCard>().Where(c => c.WorldPresentation)))
            .Where(c => !c.IsQueuedForDeletion() && c.Visible && (c.Mode != BattleCardMode.Inspect || c.WorldPresentation)).ToHashSet();
        foreach (var source in _cards.Keys.Where(c => !live.Contains(c)).ToArray())
        {
            if (GodotObject.IsInstanceValid(source)) { source.WorldRendered = false; source.WorldHitTest = null; source.WorldCenter = null; source.QueueRedraw(); }
            _cards[source].Root.QueueFree(); _cards[source].Surface.QueueFree(); _cards.Remove(source);
        }
        foreach (var source in live)
        {
            if (!_cards.TryGetValue(source, out var actor))
            {
                actor = CreateCard(source); _cards.Add(source, actor);
                source.WorldRendered = true;
                source.WorldHitTest = global => Hit(source, GetGlobalTransformWithCanvas().AffineInverse() * global, out _);
                source.QueueRedraw();
            }
            Sync(source, actor);
        }
    }

    private WorldCard CreateCard(BattleCard source)
    {
        var surface = new SubViewport { Size = new(Math.Max(1, (int)(source.Size.X * 2)), Math.Max(1, (int)(source.Size.Y * 2))), TransparentBg = true,
            Disable3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Once };
        _world.AddChild(surface);
        var face = new BattleCard { Size = source.Size, SurfaceOnly = true, MouseFilter = MouseFilterEnum.Ignore };
        var canvas = new Node2D { Scale = Vector2.One * 2 }; surface.AddChild(canvas);
        face.BackOwnerSide = source.BackOwnerSide;
        face.Bind(source.View, source.Mode, _textures); canvas.AddChild(face);
        var root = new Node3D(); _root.AddChild(root);
        var paper = new MeshInstance3D { Mesh = new BoxMesh { Size = new(1, .025f, 1) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new("bfb69b"), Roughness = .85f } };
        root.AddChild(paper);
        var material = new StandardMaterial3D { AlbedoTexture = surface.GetTexture(),
            Roughness = .9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var top = new MeshInstance3D { Mesh = new PlaneMesh { Size = Vector2.One },
            Position = new(0, .014f, 0), MaterialOverride = material };
        root.AddChild(top);
        return new(root, surface, face, material);
    }

    private void Sync(BattleCard source, WorldCard actor)
    {
        var center = source.Position + source.Size / 2;
        var onDesk = DeskPoint(center);
        var width = DeskPoint(center + new Vector2(source.Size.X / 2, 0)).DistanceTo(DeskPoint(center - new Vector2(source.Size.X / 2, 0)));
        var length = DeskPoint(center + new Vector2(0, source.Size.Y / 2)).DistanceTo(DeskPoint(center - new Vector2(0, source.Size.Y / 2)));
        var layerHeight = Math.Clamp(source.ZIndex, 0, 200) * .0015f + source.GetIndex() * .0005f;
        var lift = source.Mode == BattleCardMode.Hand ? .45f : 1.5f;
        actor.Root.Position = onDesk + new Vector3(source.FlightOffset.X * .01f, .04f + layerHeight + source.DeploymentElevation * lift, source.FlightOffset.Y * .01f);
        actor.Root.Rotation = new(-source.DeploymentElevation * .13f, -source.Rotation, source.DeploymentElevation * MathF.Sin((float)Time.GetTicksMsec() * .003f) * .06f);
        actor.Root.Scale = new(width * source.Scale.X, 1, length * source.Scale.Y);
        actor.Root.Visible = source.Modulate.A > .01f && Math.Abs(source.Scale.X) > .001f && Math.Abs(source.Scale.Y) > .001f;
        actor.Material.AlbedoColor = source.Modulate;
        source.WorldCenter = Project(actor.Root.Position);
        var stamp = HashCode.Combine(HashCode.Combine(source.View, source.Mode, source.Size), source.Selected, source.Available, source.Targeted, source.DrawFaceDown, source.Modulate, source.ChoiceCaption, source.SurfaceHovered);
        if (stamp != actor.Stamp)
        {
            actor.Face.Size = source.Size;
            actor.Surface.Size = new(Math.Max(1, (int)(source.Size.X * 2)), Math.Max(1, (int)(source.Size.Y * 2)));
            actor.Face.CopySurface(source); actor.Surface.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            actor.Stamp = stamp;
        }
    }

    private bool Hit(BattleCard source, Vector2 screen, out Vector2 logical)
    {
        logical = screen;
        if (!GodotObject.IsInstanceValid(source) || source.IsQueuedForDeletion()) return false;
        if (!_cards.TryGetValue(source, out var actor)) return false;
        if (!actor.Root.Visible || Math.Abs(actor.Root.Scale.X) < .001f || Math.Abs(actor.Root.Scale.Z) < .001f) return false;
        var origin = actor.Root.GlobalTransform.AffineInverse() * _camera.ProjectRayOrigin(Pixels(screen));
        var direction = actor.Root.GlobalBasis.Inverse() * _camera.ProjectRayNormal(Pixels(screen));
        if (Math.Abs(direction.Y) < .00001f) return false;
        var t = (.014f - origin.Y) / direction.Y;
        if (t <= 0) return false;
        var local = origin + direction * t;
        if (Math.Abs(local.X) > .5f || Math.Abs(local.Z) > .5f) return false;
        logical = source.Position + new Vector2(local.X + .5f, local.Z + .5f) * source.Size;
        return true;
    }

    public Vector2 MapPointer(Vector2 screen)
    {
        foreach (var source in _cards.Keys.Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.MouseFilter != MouseFilterEnum.Ignore)
            .OrderByDescending(c => c.ZIndex).ThenByDescending(c => _cards[c].Root.Position.Y))
            if (Hit(source, screen, out var logical)) return logical;
        return screen;
    }

    public Vector2 ProjectAnchor(Vector2 logical)
    {
        foreach (var (source, actor) in _cards)
        {
            if (!GodotObject.IsInstanceValid(source) || source.IsQueuedForDeletion()) continue;
            if (source.MouseFilter == MouseFilterEnum.Ignore) continue;
            if (!new Rect2(source.Position, source.Size).HasPoint(logical)) continue;
            var uv = (logical - source.Position) / source.Size;
            return Project(actor.Root.GlobalTransform * new Vector3(uv.X - .5f, .014f, uv.Y - .5f));
        }
        return logical;
    }

    public void SetTablecloth(string hash)
    {
        TableclothHash = hash;
        var texture = AppearanceRuntime.Load(hash);
        _deskMaterial.AlbedoTexture = texture ?? _defaultDesk;
        _deskMaterial.Uv1Scale = texture is null ? _defaultDeskUv : Vector3.One;
    }

    public bool SetProp(string side, string hash, DeskPropSettings? settings = null)
    {
        settings ??= new();
        if (!IsInsideTree()) { _queuedProps[side] = (hash, settings); return hash.Length > 0; }
        // A hidden battle can retain its startup request until its first process frame.
        // A newer equipment choice must supersede that request immediately.
        _queuedProps.Remove(side);
        if (_props.TryGetValue(side, out var existing))
        {
            if (existing.Hash == hash && existing.Settings == settings) return true;
            RemoveChildProp(existing.Root); _props.Remove(side);
        }
        if (hash.Length == 0) return false;
        var root = DeskPropInstance.Load(hash, settings);
        if (root is null) return false;
        _root.AddChild(root);
        root.Position = DeskPoint(new(165, side == "self" ? 495 : 165), .02f);
        _props[side] = (hash, root, settings); return true;
    }

    private void RemoveChildProp(Node3D root) { _root.RemoveChild(root); root.QueueFree(); }

    public bool TryInteractProp(Vector2 board)
    {
        if (_cards.Keys.Any(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion()
            && c.MouseFilter != MouseFilterEnum.Ignore && Hit(c, board, out _))) return false;
        // Decoration-only feedback; it never submits a battle command.
        foreach (var prop in _props.Values)
        {
            Rect2? area = null;
            for (var i = 0; i < 8; i++)
            {
                var point = Project(prop.Root.GlobalTransform * prop.Root.Bounds.GetEndpoint(i));
                area = area?.Expand(point) ?? new Rect2(point, Vector2.Zero);
            }
            if (area?.Grow(8).HasPoint(board) == true) return prop.Root.Click();
        }
        return false;
    }

    public override void _ExitTree()
    {
        foreach (var source in _cards.Keys)
        {
            if (!GodotObject.IsInstanceValid(source)) continue;
            source.WorldRendered = false; source.WorldHitTest = null; source.WorldCenter = null;
        }
        _cards.Clear(); _props.Clear(); _queuedProps.Clear();
    }

    private sealed class WorldCard(Node3D root, SubViewport surface, BattleCard face, StandardMaterial3D material)
    {
        public Node3D Root = root;
        public SubViewport Surface = surface;
        public BattleCard Face = face;
        public StandardMaterial3D Material = material;
        public int Stamp = int.MinValue;
    }
}
