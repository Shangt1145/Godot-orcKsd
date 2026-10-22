using Godot;

namespace Kards.Ui;

/// <summary>Small cached, deterministic print and fabric surfaces; no per-frame texture generation.</summary>
internal static class MenuMaterials
{
    private static readonly Dictionary<string, Texture2D> Cache = new();
    private static readonly Queue<string> Order = new();
    private static Font? _heading;
    public static Font Heading => _heading ??= ResourceLoader.Load<Font>("res://proto/assets/cardface/fonts/SourceHanSansCN-Bold.otf");
    private static float Hash(int x,int y,int seed)
    { unchecked { uint n=(uint)(x*374761393+y*668265263+seed*69069); n=(n^(n>>13))*1274126177; return (n^(n>>16))/(float)uint.MaxValue; } }
    private static float Noise(float x,float y,int seed)
    {
        var ix=(int)MathF.Floor(x);var iy=(int)MathF.Floor(y);var u=x-ix;var v=y-iy;u=u*u*(3-2*u);v=v*v*(3-2*v);
        return Mathf.Lerp(Mathf.Lerp(Hash(ix,iy,seed),Hash(ix+1,iy,seed),u),Mathf.Lerp(Hash(ix,iy+1,seed),Hash(ix+1,iy+1,seed),u),v);
    }
    public static Texture2D Surface(Color tint, bool cloth=false)
    {
        var key=tint.ToHtml()+cloth; if(Cache.TryGetValue(key,out var existing))return existing;
        const int w=256,h=384;var bytes=new byte[w*h*4];
        for(var y=0;y<h;y++)for(var x=0;x<w;x++)
        {
            var fine=Hash(x,y,13)-.5f;var cloud=Noise(x/49f,y/67f,21)-.5f;var grain=Noise(x/5f,y/2f,91)-.5f;
            var edge=Math.Min(Math.Min(x,w-1-x),Math.Min(y,h-1-y));
            var fade=MathF.Max(0,1-edge/13f);
            var weave=cloth?(((x%3==0)?.022f:-.008f)+((y%4==0)?.016f:-.006f)):0;
            var shade=cloth? .98f+cloud*.5f+fine*.22f+weave : .97f+cloud*.24f+fine*.11f+grain*.13f;
            var scratch=Noise(x/21f,y/1.3f,77)>.79f && Hash(x/13,y,17)>.85f;
            var worn=cloth?0:fade*(.09f+Hash(x/3,y/3,4)*.14f)+(scratch?.09f:0);
            var color=new Color(Math.Clamp(tint.R*shade+worn,0,1),Math.Clamp(tint.G*shade+worn*.85f,0,1),Math.Clamp(tint.B*shade+worn*.56f,0,1));
            var at=(y*w+x)*4;bytes[at]=(byte)(color.R*255);bytes[at+1]=(byte)(color.G*255);bytes[at+2]=(byte)(color.B*255);bytes[at+3]=255;
        }
        var image=Image.CreateFromData(w,h,false,Image.Format.Rgba8,bytes);var tex=ImageTexture.CreateFromImage(image);
        if(Cache.Count>=48) Cache.Remove(Order.Dequeue()); Cache[key]=tex;Order.Enqueue(key);return tex;
    }
    public static StyleBoxTexture Panel(Color tint, int padding=16, bool cloth=true) => new()
    { Texture=Surface(tint,cloth), ContentMarginLeft=padding,ContentMarginRight=padding,ContentMarginTop=padding,ContentMarginBottom=padding };
    public static void Button(Button button, bool primary=false, int padding=12)
    {
        var normal=primary?new Color("d6cfb9"):new Color("282d30");
        button.AddThemeStyleboxOverride("normal",Panel(normal,padding,!primary));
        button.AddThemeStyleboxOverride("hover",Panel(normal.Lightened(.13f),padding,!primary));
        button.AddThemeStyleboxOverride("pressed",Panel(primary?new Color("b8ac8c"):new Color("514731"),padding,!primary));
        button.AddThemeStyleboxOverride("disabled",Panel(new Color("282c2d"),padding));
        button.AddThemeFontOverride("font",Heading);
        button.AddThemeColorOverride("font_color",primary?new Color("292c2c"):new Color("e7decb"));
        button.AddThemeColorOverride("font_hover_color",primary?new Color("141818"):new Color("fff0c8"));
        button.AddThemeColorOverride("font_pressed_color",primary?new Color("242525"):new Color("edc47b"));
    }
    public static void Title(Label label) => label.AddThemeFontOverride("font",Heading);
}
