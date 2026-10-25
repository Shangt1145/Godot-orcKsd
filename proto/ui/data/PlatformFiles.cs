using Godot;

namespace Kards.Ui;

/// <summary>Android SAF document URIs are streams, never System.IO paths.</summary>
public static class PlatformFiles
{
    public static string ReadLocalCopy(string path, string extension)
    {
        if (!path.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) return path;
        var cache = ProjectSettings.GlobalizePath("user://document-cache"); System.IO.Directory.CreateDirectory(cache);
        var target = System.IO.Path.Combine(cache, Guid.NewGuid().ToString("N") + extension);
        using var source = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read)
            ?? throw new System.IO.IOException("无法读取所选文档：" + Godot.FileAccess.GetOpenError());
        if (source.GetLength() > 256UL * 1024 * 1024) throw new System.IO.InvalidDataException("导入文件不能超过 256 MiB。");
        using var output = System.IO.File.Create(target);
        while (source.GetPosition() < source.GetLength())
        {
            var bytes = source.GetBuffer((long)Math.Min(1024UL * 1024, source.GetLength() - source.GetPosition()));
            if (bytes.Length == 0) throw new System.IO.IOException("文档读取中断，请重新选择文件。");
            output.Write(bytes);
        }
        return target;
    }
    public static void WriteDocument(string destination, Action<string> writer, string extension)
    {
        if (!destination.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) { writer(destination); return; }
        var cache = ProjectSettings.GlobalizePath("user://document-cache"); System.IO.Directory.CreateDirectory(cache);
        var temporary = System.IO.Path.Combine(cache, Guid.NewGuid().ToString("N") + extension);
        try
        {
            writer(temporary);
            using var source = System.IO.File.OpenRead(temporary);
            using var output = Godot.FileAccess.Open(destination, Godot.FileAccess.ModeFlags.Write)
                ?? throw new System.IO.IOException("无法写入所选文档：" + Godot.FileAccess.GetOpenError());
            var buffer = new byte[1024 * 1024]; int read;
            while ((read = source.Read(buffer)) > 0) output.StoreBuffer(read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray());
            output.Flush();
            if (output.GetError() != Error.Ok) throw new System.IO.IOException("导出文档未能完整写入。");
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }
    public static Image LoadImage(string path)
    {
        if (!path.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            return Image.LoadFromFile(path) ?? throw new System.IO.InvalidDataException("无法读取图片。");
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read)
            ?? throw new System.IO.IOException("无法读取所选图片。");
        if (file.GetLength() > 32UL * 1024 * 1024) throw new System.IO.InvalidDataException("图片不能超过 32 MiB。");
        var bytes = file.GetBuffer((long)file.GetLength()); var image = new Image();
        var error = bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 ? image.LoadPngFromBuffer(bytes)
            : bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 216 ? image.LoadJpgFromBuffer(bytes) : image.LoadWebpFromBuffer(bytes);
        if (error != Error.Ok) throw new System.IO.InvalidDataException("请选择有效的 PNG、JPEG 或 WebP 图片。");
        return image;
    }
}
