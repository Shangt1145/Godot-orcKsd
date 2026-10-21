using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

internal static class WorkshopVisuals
{
    internal static IReadOnlySet<string> NativeCardIds { get; set; } = new HashSet<string>();
    private static readonly Dictionary<string,Texture2D?> Icons=new();
    public static void Reset()=>Icons.Clear();
    public static Color ColorOf(string id)=>WorkshopAuthoring.Nations.FirstOrDefault(n=>n.Id==id) is { } n?new Color(n.Color):new Color("526b61");
    public static Texture2D? Icon(string id)
    {
        if(Icons.TryGetValue(id,out var icon))return icon;
        if(Icons.Count>=64)Icons.Clear();
        icon=WorkshopAuthoring.Nations.FirstOrDefault(n=>n.Id==id) is {IconFile.Length:>0} n?DiyArtStore.Load(n.IconFile):null;Icons[id]=icon;return icon;
    }
    public static string NationName(string id)=>WorkshopAuthoring.NationLabel(id);
}

/// <summary>Offline layout detection; returns an editable normalized illustration rectangle.</summary>
internal static class CardArtExtractor
{
    public static DiyArtRegion Detect(Image image)
    {
        var w=image.GetWidth();var h=image.GetHeight();if(w<48||h<64)return new(0,0,1,1);
        // Search for the broad visual/text transitions rather than depending on a card id or nation.
        var small=(Image)image.Duplicate();small.Resize(120,180,Image.Interpolation.Bilinear);small.Convert(Image.Format.Rgb8);
        var scores=new double[180];
        for(var y=1;y<179;y++)
        {
            double total=0;for(var x=8;x<112;x++){var a=small.GetPixel(x,y-1);var b=small.GetPixel(x,y+1);total+=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);}scores[y]=total/104;
        }
        var top=Enumerable.Range(17,22).OrderByDescending(y=>scores[y]).First();
        var bottom=Enumerable.Range(102,30).OrderByDescending(y=>scores[y]).First();
        // Most printed cards use a title band and illustration above the rules. The preview
        // always exposes these bounds for correction, including unusual layouts and orders.
        var y0=scores[top]>.06?top/180f:.145f;var y1=scores[bottom]>.06?bottom/180f:.635f;
        // Printed badges and stat medallions often straddle the lower illustration edge.
        // Keep an interior margin so those decorations are not mistaken for editable art.
        y1-=.07f;
        return new(.03f,y0,.94f,Math.Clamp(y1-y0,.28f,.55f));
    }
    public static Image Crop(Image image,DiyArtRegion region)
    {
        var x=Math.Clamp((int)Math.Round(region.X*image.GetWidth()),0,image.GetWidth()-1);
        var y=Math.Clamp((int)Math.Round(region.Y*image.GetHeight()),0,image.GetHeight()-1);
        var w=Math.Clamp((int)Math.Round(region.Width*image.GetWidth()),1,image.GetWidth()-x);
        var h=Math.Clamp((int)Math.Round(region.Height*image.GetHeight()),1,image.GetHeight()-y);
        return image.GetRegion(new(x,y,w,h));
    }
}
