using Godot;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class SetupDeckBack : Control
{
    private TextureCache? _textures;
    private string? _deckId, _mainNation;
    private bool _boundResize;
    public bool Selected {get;set;}
    public bool Hovered {get;set;}
    // 国家显示名：先归一到规范国籍再查（「星盟」与「NSF」是同一个国家，显示同一个名字），
    // 否则卡池里的中文目录名会显示成中文、下拉框显示成 NSF，看着像两个国家。
    public static string NationLabel(string id)=>DiyCardCompiler.Nations.FirstOrDefault(n=>string.Equals(n.Id,DeckNations.Canonical(id),StringComparison.OrdinalIgnoreCase))?.Label??WorkshopVisuals.NationName(id);
    public void Bind(ConstructedDeck? deck,TextureCache textures)
    {
        _textures=textures;_deckId=deck?.Id;_mainNation=deck?.Options.MainNation;
        if(!_boundResize){Resized+=QueueRedraw;_boundResize=true;}QueueRedraw();
    }
    public override void _Draw()
    {
        if(_textures is null)return;
        var image=AppearanceRuntime.Library?.Resolve(_deckId,_mainNation).CardBack??"";
        if(image.Length==0)image=OriginalCosmetics.DefaultBack(_mainNation);
        var texture=AppearanceRuntime.Image(image,_textures);
        if(texture is null)return;
        var height=Math.Min(Size.Y-12,(Size.X-12)*1.404f);if(height<=0)return;
        var width=height/1.404f;
        CosmeticPaper.DrawBack(this,texture,new((Size.X-width)/2,4,width,height),Selected);
    }
}
public partial class SetupSelectionTable : Control
{
    private Texture2D? _wood;
    public void Initialize(TextureCache textures) { _wood=OriginalCosmetics.Ui("T_nui-board-render-default-2048"); Resized+=QueueRedraw; }
    public override void _Draw()
    {
        var rect=new Rect2(Vector2.Zero,Size); DrawRect(rect,new("392f23"));
        if(_wood is not null) DrawTextureRectRegion(_wood,rect,new Rect2(_wood.GetWidth()*.18f,_wood.GetHeight()*.17f,_wood.GetWidth()*.62f,_wood.GetHeight()*.64f),new Color(.75f,.72f,.65f));
        DrawRect(rect,new Color(0,0,0,.18f));
        for(var i=0;i<18;i++) {var a=(18-i)*.009f;DrawRect(new Rect2(i,0,1,Size.Y),new Color(0,0,0,a));DrawRect(new Rect2(Size.X-i,0,1,Size.Y),new Color(0,0,0,a));}
    }
}
