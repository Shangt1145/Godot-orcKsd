using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>Presentation only; never mutates UiCardView, rules, deck data, or engine definitions.</summary>
public static class AppearanceRuntime
{
    public static CosmeticLibrary? Library { get; private set; }
    public static Func<IReadOnlyDictionary<int,CosmeticProfile>>? RemoteProfiles { get; set; }
    public static Func<int>? ViewerSeat { get; set; }
    public static bool Observer { get; set; }
    public static Func<CosmeticProfile>? LocalProfile { get; set; }
    public static void Configure(CosmeticLibrary library) { Library=library; ResetRemote(); }
    public static void ResetRemote() { RemoteProfiles=null; ViewerSeat=null; Observer=false; LocalProfile=null; }
    public static CosmeticProfile Profile(string side)
    {
        if(side=="self" && !Observer) return LocalProfile?.Invoke() ?? Library?.Profile??new();
        if(RemoteProfiles is null) return new();
        var seat=ViewerSeat?.Invoke()??0;
        return RemoteProfiles().GetValueOrDefault(side=="self"?seat:1-seat)??new();
    }
    public static Texture2D? Image(string id,TextureCache textures) => CosmeticImage.IsId(id)?textures.Get("cosmetic://"+id):null;
    public static Texture2D? Load(string id)
    {
        if(Library is null || !Library.HasImage(id)) return null;
        try
        {
            var bytes=Library.ReadImage(id);
            if(CosmeticImage.Inspect(bytes).Id!=id) return null;
            var image=new Image();
            return image.LoadPngFromBuffer(bytes)==Error.Ok?ImageTexture.CreateFromImage(image):null;
        }
        catch { return null; }
    }
    public static byte[] ImportImage(string path)
    {
        if(new FileInfo(path).Length>20*1024*1024) throw new InvalidDataException("请选择不超过 20 MB 的图片。");
        var image=Godot.Image.LoadFromFile(path);
        if(image is null || image.IsEmpty()) throw new InvalidDataException("图片无法读取，请使用 PNG、JPG 或 WebP。");
        var scale=Math.Min(1,1024f/Math.Max(image.GetWidth(),image.GetHeight()));
        if(scale<1) image.Resize(Math.Max(1,(int)(image.GetWidth()*scale)),Math.Max(1,(int)(image.GetHeight()*scale)),Godot.Image.Interpolation.Lanczos);
        image.Convert(Godot.Image.Format.Rgba8);
        var bytes=image.SavePngToBuffer();
        while(bytes.Length>CosmeticImage.MaximumBytes && Math.Max(image.GetWidth(),image.GetHeight())>64)
        { image.Resize(Math.Max(1,(int)(image.GetWidth()*.8)),Math.Max(1,(int)(image.GetHeight()*.8)),Godot.Image.Interpolation.Lanczos);bytes=image.SavePngToBuffer(); }
        CosmeticImage.Inspect(bytes);return bytes;
    }
}
