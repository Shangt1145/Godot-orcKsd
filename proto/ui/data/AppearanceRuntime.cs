using System.IO;
using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>导入的完整卡面：原始字节 + 规范扩展名 + 用于编辑器预览的解码图像。</summary>
public sealed record CardSkinImage(byte[] Bytes, string Extension, Image Preview);

/// <summary>Presentation only; never mutates UiCardView, rules, deck data, or engine definitions.</summary>
public static class AppearanceRuntime
{
    public static CosmeticLibrary? Library { get; private set; }
    public static Func<IReadOnlyDictionary<int,CosmeticProfile>>? RemoteProfiles { get; set; }
    public static Func<int>? ViewerSeat { get; set; }
    public static bool Observer { get; set; }
    public static Func<CosmeticProfile>? LocalProfile { get; set; }
    public static void Configure(CosmeticLibrary library) { Library=library; ResetRemote(); ResetCardSkins(); }
    public static void ResetRemote() { RemoteProfiles=null; ViewerSeat=null; Observer=false; LocalProfile=null; }
    public static CosmeticProfile Profile(string side)
    {
        if(side=="self" && !Observer) return LocalProfile?.Invoke() ?? Library?.Profile??new();
        if(RemoteProfiles is null) return new();
        var seat=ViewerSeat?.Invoke()??0;
        return RemoteProfiles().GetValueOrDefault(side=="self"?seat:1-seat)??new();
    }
    public static Func<string, Kards.Ui.Contracts.CardSkin?>? CardSkinSource { get; set; }
    private static readonly Dictionary<string, Kards.Ui.Contracts.CardSkin?> CardSkins = new();
    public static void ResetCardSkins() => CardSkins.Clear();
    public static Kards.Ui.Contracts.CardSkin? Skin(string cardId)
    {
        if (!CardSkins.TryGetValue(cardId, out var skin)) CardSkins[cardId] = skin = CardSkinSource?.Invoke(cardId);
        return skin;
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
    /// <summary>
    /// 工坊完整卡面导入：字节原样保留，不缩放、不重编码，不限制宽高比、像素尺寸、文件体积或图片格式。
    /// 外观装扮仍走 <see cref="ImportImage"/>（2 MiB / 1024px 口径），两条通道互不干扰。
    /// 只保留两项必要处理：Android 文档要从 Godot 文件流读字节；无扩展名文件按文件头补一个后缀。
    /// </summary>
    public static CardSkinImage ReadCardSkinImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("请选择完整卡面图片。");
        var isDocument = path.StartsWith("content://", StringComparison.OrdinalIgnoreCase);
        byte[] bytes;
        if (isDocument)
        {
            using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read)
                ?? throw new IOException("无法读取所选图片：" + Godot.FileAccess.GetOpenError());
            bytes = file.GetBuffer((long)file.GetLength());
        }
        else
        {
            if (!File.Exists(path)) throw new InvalidDataException("完整卡面文件不存在。");
            bytes = File.ReadAllBytes(path);
        }
        if (bytes.Length == 0) throw new InvalidDataException("完整卡面文件为空。");
        var extension = isDocument ? "" : Kards.Ui.Contracts.CardSkinFile.NormalizeExtension(Path.GetExtension(path));
        Godot.Image image;
        if (extension.Length == 0)
        {
            var probe = new Godot.Image();
            extension = probe.LoadPngFromBuffer(bytes) == Error.Ok ? ".png"
                : probe.LoadJpgFromBuffer(bytes) == Error.Ok ? ".jpg"
                : probe.LoadWebpFromBuffer(bytes) == Error.Ok ? ".webp" : "";
            if (extension.Length == 0) throw new InvalidDataException("完整卡面无法读取，请选择可解码的图片文件。");
            image = probe;
        }
        else
        {
            image = Godot.Image.LoadFromFile(path) ?? throw new InvalidDataException("完整卡面无法读取，请选择可解码的图片文件。");
            if (image.IsEmpty()) throw new InvalidDataException("完整卡面无法读取，请选择可解码的图片文件。");
        }
        return new(bytes, extension, image);
    }
    public static byte[] ImportImage(string path)
    {
        if(!path.StartsWith("content://",StringComparison.OrdinalIgnoreCase)&&new FileInfo(path).Length>20*1024*1024) throw new InvalidDataException("请选择不超过 20 MB 的图片。");
        var image=PlatformFiles.LoadImage(path);
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
