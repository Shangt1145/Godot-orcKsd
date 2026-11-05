using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>Data-only GLB or image decoration shared by the gallery and battle world.</summary>
public partial class DeskPropInstance : Node3D
{
    private Node3D _visual = null!;
    private Tween? _motion;
    public DeskPropSettings Settings { get; private set; } = new();
    public Aabb Bounds { get; private set; }
    public bool IsPerforming => _motion?.IsRunning() == true || Players(this).Any(p => p.IsPlaying());
    public string[] Animations => Players(this).SelectMany(p => p.GetAnimationList()).Where(n => n != "RESET").Distinct().ToArray();
    public static DeskPropInstance? Load(string image, DeskPropSettings settings)
    {
        settings.Checked();
        var result = new DeskPropInstance { Settings = settings };
        var visual = new Node3D(); result.AddChild(visual); result._visual = visual;
        var geometry = new Node3D(); visual.AddChild(geometry);
        var library = AppearanceRuntime.Library;
        var paths = settings.Model.Length > 0 && library?.HasModel(settings.Model) == true
            ? new[] { library.ModelPath(settings.Model) } : OriginalCosmetics.PropParts(image);
        foreach (var path in paths)
        {
            var doc = new GltfDocument(); var state = new GltfState();
            if (doc.AppendFromBuffer(Godot.FileAccess.GetFileAsBytes(path), "", state) != Error.Ok)
            { result.Free(); throw new InvalidDataException("桌饰模型无法加载：" + Path.GetFileName(path)); }
            geometry.AddChild(doc.GenerateScene(state));
        }
        if (paths.Length == 0 && AppearanceRuntime.Load(image) is { } texture)
        {
            var ratio = texture.GetWidth() / (float)Math.Max(1, texture.GetHeight());
            geometry.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new(ratio, 1) },
                MaterialOverride = new StandardMaterial3D { AlbedoTexture = texture, Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = .8f } });
        }
        Aabb? bounds = null;
        void Visit(Node node, Transform3D transform)
        {
            if (node is Node3D spatial) transform *= spatial.Transform;
            if (node is MeshInstance3D mesh) { var box = transform * mesh.GetAabb(); bounds = bounds?.Merge(box) ?? box; }
            foreach (var child in node.GetChildren()) Visit(child, transform);
        }
        Visit(geometry, Transform3D.Identity);
        if (bounds is not { } b) { result.Free(); return null; }
        var scale = 2.3f * settings.Size / Math.Max(.01f, b.Size.Length());
        geometry.Position = -new Vector3(b.GetCenter().X, b.Position.Y, b.GetCenter().Z) * scale;
        geometry.Scale = Vector3.One * scale;
        visual.RotationDegrees = new(0, settings.Yaw, 0);
        result.Bounds = visual.Transform * new Aabb(new(-b.Size.X * scale / 2, 0, -b.Size.Z * scale / 2), b.Size * scale);
        foreach (var player in Players(result)) { player.Autoplay = ""; player.Stop(); }
        return result;
    }
    public bool Click()
    {
        if (Settings.Click == "none") return false;
        if (Settings.Click == "auto")
            foreach (var player in Players(this))
            {
                var name = Settings.Animation.Length > 0 ? player.GetAnimationList().FirstOrDefault(n => n == Settings.Animation)
                    : player.GetAnimationList().FirstOrDefault(n => n != "RESET");
                if (name is null) continue;
                player.Stop(); player.GetAnimation(name).LoopMode = Animation.LoopModeEnum.None;
                player.Play(name); player.Advance(0); return true;
            }
        if (_motion is not null && GodotObject.IsInstanceValid(_motion)) _motion.Kill();
        _visual.Position = Vector3.Zero; _visual.Rotation = new(0, Mathf.DegToRad(Settings.Yaw), 0);
        _motion = CreateTween();
        _motion.TweenMethod(Callable.From<float>(p =>
        {
            var arc = MathF.Sin(p * MathF.PI);
            _visual.Position = Settings.Click == "lift" ? new(0, arc * .5f, 0) : Vector3.Zero;
            _visual.Rotation = new(0, Mathf.DegToRad(Settings.Yaw) + (Settings.Click == "spin" ? p * Mathf.Tau : 0),
                Settings.Click is "auto" or "rock" ? MathF.Sin(p * Mathf.Tau * 2) * arc * .14f : 0);
        }), 0f, 1f, .65);
        return true;
    }
    private static IEnumerable<AnimationPlayer> Players(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is AnimationPlayer player) yield return player;
            foreach (var nested in Players(child)) yield return nested;
        }
    }
    public override void _ExitTree() { if (_motion is not null && GodotObject.IsInstanceValid(_motion)) _motion.Kill(); }
}
