using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kards.Ui.Core;

public static class CosmeticModel
{
    public const int MaximumBytes = 20 * 1024 * 1024;
    public static string Inspect(byte[] bytes)
    {
        if (bytes.Length is < 20 or > MaximumBytes || !bytes.AsSpan(0, 4).SequenceEqual("glTF"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) != bytes.Length
            || !bytes.AsSpan(16, 4).SequenceEqual("JSON"u8))
            throw new InvalidDataException("需要不超过 20 MiB、内嵌材质的 GLB 2.0 模型。");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        if (length > bytes.Length - 20) throw new InvalidDataException("GLB 数据不完整。");
        using var doc = JsonDocument.Parse(bytes.AsMemory(20, (int)length));
        foreach (var key in new[] { "buffers", "images" })
            if (doc.RootElement.TryGetProperty(key, out var resources))
                foreach (var resource in resources.EnumerateArray())
                    if (resource.TryGetProperty("uri", out var uri) && !(uri.GetString() ?? "").StartsWith("data:", StringComparison.Ordinal))
                        throw new InvalidDataException("请导出材质和纹理全部内嵌的 GLB，不支持外部文件引用。");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

public sealed partial class CosmeticLibrary
{
    public string ModelPath(string id)
    {
        if (!CosmeticImage.IsId(id)) throw new InvalidDataException("无效的桌饰模型编号。");
        return Path.Combine(_root, "models", id + ".glb");
    }
    public bool HasModel(string id) => CosmeticImage.IsId(id) && File.Exists(ModelPath(id));
    public string CacheModel(byte[] glb)
    {
        var hash = CosmeticModel.Inspect(glb);
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        var path = ModelPath(hash);
        if (!File.Exists(path)) File.WriteAllBytes(path, glb);
        return hash;
    }
    public CosmeticAsset ImportModel(string name, byte[] glb, byte[] thumbnail)
    {
        var hash = CacheModel(glb);
        var image = CacheImage(thumbnail);
        var asset = new CosmeticAsset("decoration", string.IsNullOrWhiteSpace(name) ? "自定义桌饰" : name.Trim()[..Math.Min(name.Trim().Length, 48)], image, Prop: new(Model: hash));
        _assets.RemoveAll(a => a.Kind == "decoration" && a.Image == image);
        _assets.Add(asset); Save(Profile); return asset;
    }
}
