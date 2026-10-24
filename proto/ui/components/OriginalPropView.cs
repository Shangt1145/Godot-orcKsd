using Godot;

namespace Kards.Ui;

/// <summary>Displays the original mesh and original texture atlas, loaded from raw GLB payloads.</summary>
public partial class OriginalPropView : SubViewportContainer
{
    private string _hash="";
    private SubViewport? _viewport;
    public bool Bind(string hash)
    {
        var parts=OriginalCosmetics.PropParts(hash);
        Visible=parts.Length>0;
        if(_hash==hash)return Visible;
        _hash=hash;
        if(_viewport is not null){RemoveChild(_viewport);_viewport.QueueFree();_viewport=null;}
        if(!Visible)return false;
        Stretch=true;MouseFilter=MouseFilterEnum.Ignore;
        _viewport=new SubViewport{Size=new(512,512),TransparentBg=true,OwnWorld3D=true,RenderTargetUpdateMode=SubViewport.UpdateMode.WhenVisible};AddChild(_viewport);
        var model=new Node3D();_viewport.AddChild(model);
        foreach(var path in parts)
        {
            var document=new GltfDocument();var state=new GltfState();
            if(document.AppendFromBuffer(Godot.FileAccess.GetFileAsBytes(path),"",state)!=Error.Ok)throw new InvalidDataException("原版桌饰模型无法加载："+path);
            model.AddChild(document.GenerateScene(state));
        }
        Aabb? bounds=null;
        void Collect(Node node)
        {
            if(node is MeshInstance3D mesh){var box=mesh.GlobalTransform*mesh.GetAabb();bounds=bounds?.Merge(box)??box;}
            foreach(var child in node.GetChildren())Collect(child);
        }
        Collect(model);
        if(bounds is not {} boxBounds)throw new InvalidDataException("原版桌饰缺少网格："+hash);
        var center=boxBounds.GetCenter();var radius=Math.Max(.01f,boxBounds.Size.Length());
        var camera=new Camera3D{Projection=Camera3D.ProjectionType.Orthogonal,Size=radius*1.15f,Near=.001f,Position=center+new Vector3(0,radius*1.2f,radius*.8f),Current=true};
        _viewport.AddChild(camera);camera.LookAt(center,Vector3.Up);
        _viewport.AddChild(new DirectionalLight3D{RotationDegrees=new(-50,-25,0),LightEnergy=1.7f});
        _viewport.AddChild(new WorldEnvironment{Environment=new Godot.Environment{AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=Colors.White,AmbientLightEnergy=.55f}});
        return true;
    }
}
