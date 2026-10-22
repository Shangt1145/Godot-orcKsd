using Godot;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>Client-owned deck identity, shared by the shelf and selected-deck preview.</summary>
public partial class SetupDeckBack : Control
{
    private Texture2D? _paper, _printed, _icon;
    private Color _ink = new("304357");
    private string _nation = "OrC";
    private string _faction = "";
    public static string NationLabel(string id) => DiyCardCompiler.Nations.FirstOrDefault(n=>string.Equals(n.Id,id,StringComparison.OrdinalIgnoreCase))?.Label ?? WorkshopVisuals.NationName(id);
    private bool _boundResize;
    public bool Selected { get; set; }
    public bool Hovered { get; set; }
    public void Bind(ConstructedDeck? deck, TextureCache textures)
    {
        var id = deck?.Options.MainNation ?? "";
        _faction = id.ToLowerInvariant();
        _nation = deck?.Options.UnrestrictedNations == true ? "联合部队" : NationLabel(id);
        _icon = WorkshopVisuals.Icon(id);
        _ink = _faction switch { "germany" => new("414a42"), "soviet" => new("702f32"), "japan" => new("8a6a32"),
            "usa" => new("233f5a"), "britain" => new("686447"), _ => WorkshopVisuals.ColorOf(id).Darkened(.35f) };
        _paper=MenuMaterials.Surface(new Color("cfc19a")); _printed=MenuMaterials.Surface(_ink);
        if (!_boundResize) { Resized += Redraw; _boundResize = true; } QueueRedraw();
    }
    private void Redraw() => QueueRedraw();
    public override void _Draw()
    {
        var h = Math.Min(Size.Y - 12, (Size.X - 12) * 1.46f); if(h <= 0)return;
        var w = h / 1.46f; var card = new Rect2((Size.X-w)/2, 4, w, h);
        DrawStyleBox(new StyleBoxFlat { BgColor = new Color("0a0909aa"), CornerRadiusTopLeft=5,CornerRadiusTopRight=5,CornerRadiusBottomLeft=5,CornerRadiusBottomRight=5,
            ShadowColor=new Color("000000aa"),ShadowSize=8,ShadowOffset=new(4,6) }, card);
        if(_paper is not null) DrawTextureRect(_paper,card,false);
        var inside = card.Grow(-5); if(_printed is not null) DrawTextureRect(_printed,inside,false,Hovered?new Color(1.12f,1.12f,1.08f):Colors.White);
        var cream = new Color("dfd2a0");
        if(_faction == "usa")
        {
            DrawColoredPolygon([inside.Position+new Vector2(w*.28f,0),inside.Position+new Vector2(w*.52f,0),inside.End-new Vector2(w*.28f,0),inside.End-new Vector2(w*.52f,0)],new Color(cream,.13f));
        }
        if(_faction == "japan") DrawRect(new Rect2(inside.Position.X+w*.45f,inside.Position.Y,w*.15f,inside.Size.Y),new Color(cream,.13f));
        DrawRect(card.Grow(-9),cream,false,1); DrawRect(card.Grow(-12),new Color(cream,.55f),false,1);
        // Fixed print wear: stable across redraw, scroll and screenshots.
        for(var i=0;i<80;i++)
        {
            var x = card.Position.X + 13 + ((i*37)%157)/157f*(w-26);
            var y = card.Position.Y + 14 + ((i*71)%233)/233f*(h-28);
            DrawLine(new(x,y),new(x+2+i%7,y-1),new Color(cream,.10f),.7f);
        }
        foreach(var p in new[]{card.Position+new Vector2(15,15),new Vector2(card.End.X-15,card.Position.Y+15),card.End-new Vector2(15,15),new Vector2(card.Position.X+15,card.End.Y-15)})
            DrawRect(new Rect2(p-new Vector2(3,3),new Vector2(6,6)),cream,false,1);
        var center = card.GetCenter();
        DrawArc(center,w*.27f,0,Mathf.Tau,64,new Color(cream,.65f),1.5f,true);
        if(_icon is not null) DrawTextureRect(_icon,new Rect2(center-new Vector2(w*.19f,w*.19f),new Vector2(w*.38f,w*.38f)),false);
        else if(_faction == "germany")
        {
            var r=w*.23f;
            DrawColoredPolygon([center+new Vector2(-r,-r*.55f),center+new Vector2(-r*.4f,-r*.4f),center+new Vector2(-r*.55f,-r),center+new Vector2(r*.55f,-r),center+new Vector2(r*.4f,-r*.4f),center+new Vector2(r,-r*.55f),center+new Vector2(r,r*.55f),center+new Vector2(r*.4f,r*.4f),center+new Vector2(r*.55f,r),center+new Vector2(-r*.55f,r),center+new Vector2(-r*.4f,r*.4f),center+new Vector2(-r,r*.55f)],cream);
            DrawLine(center-new Vector2(r*.65f,0),center+new Vector2(r*.65f,0),_ink,2);
            DrawLine(center-new Vector2(0,r*.65f),center+new Vector2(0,r*.65f),_ink,2);
        }
        else if(_faction == "britain")
        {
            var r=w*.22f;
            DrawColoredPolygon([center+new Vector2(-r,r*.35f),center+new Vector2(-r,-r*.55f),center+new Vector2(-r*.45f,0),center+new Vector2(0,-r),center+new Vector2(r*.45f,0),center+new Vector2(r,-r*.55f),center+new Vector2(r,r*.35f)],cream);
            DrawRect(new Rect2(center.X-r,center.Y+r*.55f,r*2,r*.2f),cream);
            foreach(var offset in new[]{-.7f,0,.7f}) { DrawCircle(center+new Vector2(offset*r,-r*.65f),r*.105f,cream); DrawCircle(center+new Vector2(offset*r,r*.12f),r*.07f,_ink); }
            DrawLine(center+new Vector2(0,-r*1.3f),center+new Vector2(0,-r*.86f),cream,2);
            DrawLine(center+new Vector2(-r*.16f,-r*1.12f),center+new Vector2(r*.16f,-r*1.12f),cream,2);
        }
        else if(_faction == "japan")
        {
            for(var i=0;i<12;i++)
            {
                var a=i*Mathf.Tau/12;var radial=new Vector2(Mathf.Cos(a),Mathf.Sin(a));var tangent=new Vector2(-radial.Y,radial.X);
                var petal=Enumerable.Range(0,20).Select(k=>{var t=k*Mathf.Tau/20;return center+radial*(w*.155f+Mathf.Cos(t)*w*.095f)+tangent*Mathf.Sin(t)*w*.026f;}).ToArray();
                DrawColoredPolygon(petal,cream);
            }
            DrawCircle(center,w*.04f,cream);
        }
        else
        {
            var star = new Vector2[10];
            for(var i=0;i<10;i++){var a=-Mathf.Pi/2+i*Mathf.Pi/5;star[i]=center+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*(i%2==0?w*.21f:w*.09f);}
            DrawColoredPolygon(star,cream);
            if(_faction is "soviet" or "usa")
                for(var side=-1;side<=1;side+=2)for(var i=0;i<7;i++)
                {
                    var a=Mathf.Pi*.12f+i*Mathf.Pi*.095f;var p=center+new Vector2(side*Mathf.Sin(a),Mathf.Cos(a))*w*.28f;
                    DrawColoredPolygon([p,p+new Vector2(side*w*.045f,-w*.045f),p+new Vector2(side*w*.05f,w*.018f),p+new Vector2(0,w*.04f)],new Color(cream,.8f));
                }
        }
        for(var i=0;i<20;i++)
        {
            var p=center+new Vector2(((i*23)%41-20)/100f*w,((i*17)%37-18)/100f*w);
            DrawLine(p,p+new Vector2(w*.018f,-.3f),new Color(_ink,.35f),.8f);
        }
        for(var i=0;i<24;i++)
        {
            var x=card.Position.X+((i*61)%199)/199f*w;var y=card.Position.Y+((i*107)%283)/283f*h;
            if(i%2==0)x=card.Position.X+3;else y=card.End.Y-4;
            DrawLine(new(x,y),new(x+2+i%4,y-1),new Color("eee0b6"),1);
        }
        var font=MenuMaterials.Heading; var label=_nation.Length>8?_nation[..8]:_nation; var fs=Math.Clamp((int)(w/12),12,18);
        var tw=font.GetStringSize(label,HorizontalAlignment.Left,-1,fs).X;
        DrawString(font,new(center.X-tw/2,center.Y+w*.43f),label,HorizontalAlignment.Left,-1,fs,cream);
        DrawRect(card.Grow(-1),Selected||Hovered?new Color("edbd59"):new Color("8d886e"),false,Selected?3:1);
        if(Selected)
        {
            var x=card.Position.X-9;var y=card.Position.Y+30;
            DrawColoredPolygon([new(x,y),new(x+52,y),new(x+44,y+17),new(x+52,y+34),new(x,y+34)],new("d5902a"));
            DrawString(font,new(x+13,y+25),"★",HorizontalAlignment.Left,-1,25,new("fff0c6"));
        }
    }
}

public partial class SetupSelectionTable : Control
{
    private Texture2D? _wood;
    public void Initialize(TextureCache textures) { _wood=textures.Get("res://proto/assets/battle/tabletop-v2.png"); Resized+=QueueRedraw; }
    public override void _Draw()
    {
        var rect=new Rect2(Vector2.Zero,Size); DrawRect(rect,new("392f23"));
        if(_wood is not null) DrawTextureRectRegion(_wood,rect,new Rect2(_wood.GetWidth()*.18f,_wood.GetHeight()*.17f,_wood.GetWidth()*.62f,_wood.GetHeight()*.64f),new Color(.75f,.72f,.65f));
        DrawRect(rect,new Color(0,0,0,.18f));
        for(var i=0;i<18;i++) {var a=(18-i)*.009f;DrawRect(new Rect2(i,0,1,Size.Y),new Color(0,0,0,a));DrawRect(new Rect2(Size.X-i,0,1,Size.Y),new Color(0,0,0,a));}
    }
}
