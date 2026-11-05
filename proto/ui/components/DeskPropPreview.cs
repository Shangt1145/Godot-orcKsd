using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class DeskPropPreview : SubViewportContainer
{
    private SubViewport _viewport = null!;
    private DeskPropInstance? _prop;
    private string _image = "";
    private DeskPropSettings? _settings;
    public string[] Animations => _prop?.Animations ?? [];
    public bool HasProp => _prop is not null;
    public void Initialize()
    {
        Stretch = true; MouseFilter = MouseFilterEnum.Stop; MouseDefaultCursorShape = CursorShape.PointingHand;
        TooltipText = "点击预览桌饰演出";
        _viewport = new SubViewport { Size = new(512, 512), OwnWorld3D = true, TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible, Msaa3D = Viewport.Msaa.Msaa2X };
        AddChild(_viewport);
        var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 3.8f, Position = new(0, 4, 3), Current = true };
        camera.Basis = Basis.LookingAt(new Vector3(0, .65f, 0) - camera.Position, Vector3.Up); _viewport.AddChild(camera);
        _viewport.AddChild(new DirectionalLight3D { RotationDegrees = new(-55, -25, 0), LightEnergy = 1.4f });
        _viewport.AddChild(new WorldEnvironment { Environment = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White, AmbientLightEnergy = .65f } });
    }
    public void Bind(string image, DeskPropSettings settings)
    {
        if (_settings == settings && _image == image) return;
        _image = image; _settings = settings;
        if (_prop is not null) { _viewport.RemoveChild(_prop); _prop.QueueFree(); _prop = null; }
        if (image.Length == 0 && settings.Model.Length == 0) return;
        _prop = DeskPropInstance.Load(image, settings);
        if (_prop is not null) _viewport.AddChild(_prop);
    }
    public bool PlayClick() => _prop?.Click() == true;
    public async Task<byte[]> ThumbnailAsync()
    {
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        for (var i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var bytes = _viewport.GetTexture().GetImage().SavePngToBuffer();
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible;
        return bytes;
    }
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) { PlayClick(); AcceptEvent(); }
    }
}
