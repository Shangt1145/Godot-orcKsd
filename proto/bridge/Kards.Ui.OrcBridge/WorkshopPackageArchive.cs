using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Kards.Ui.OrcBridge;

/// <summary>Filesystem transport only. Rule validation remains in Orc.Packages.</summary>
internal static class WorkshopPackageArchive
{
    internal const long MaxBytes = 256L * 1024 * 1024;
    internal const int MaxFiles = 8192;
    internal static string Key(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant()[..32];

    internal static string Child(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')
            || p.Any(c => c < 32 || "<>:\"|?*".Contains(c))
            || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(p.Split('.')[0], StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("卡包包含非法路径：" + relative);
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("卡包路径越界：" + relative);
        return path;
    }

    internal static string Extract(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > MaxFiles) throw new InvalidDataException("卡包超过 8192 个条目。");
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            var path = Child(destination, directory ? name.TrimEnd('/') : name);
            if (!seen.Add(path)) throw new InvalidDataException("卡包路径重复（含大小写冲突）：" + name);
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not 0 and not 0x8000 and not 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("卡包不能包含链接：" + name);
            if (directory) { Directory.CreateDirectory(path); continue; }
            total = checked(total + entry.Length);
            if (total > MaxBytes) throw new InvalidDataException("卡包解压后超过 256 MiB。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = File.Create(path);
            var buffer = new byte[81920]; long written = 0; int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > entry.Length || written > MaxBytes) throw new InvalidDataException("卡包大小异常。");
                output.Write(buffer, 0, read);
            }
            if (written != entry.Length) throw new InvalidDataException("卡包文件不完整：" + name);
        }
        if (File.Exists(Path.Combine(destination, "manifest.json"))) return destination;
        var folders = Directory.GetDirectories(destination);
        if (Directory.GetFiles(destination).Length == 0 && folders.Length == 1 && File.Exists(Path.Combine(folders[0], "manifest.json"))) return folders[0];
        throw new InvalidDataException("ZIP 根目录或唯一外层文件夹内必须有 manifest.json（一次导入一个卡包）。");
    }

    internal static IEnumerable<string> Files(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).OrderBy(p => p, StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("卡包不能包含链接：" + entry);
            if ((attributes & FileAttributes.Directory) != 0)
            { foreach (var file in Files(entry)) yield return file; }
            else yield return entry;
        }
    }

    internal static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Files(source))
        {
            var destination = Child(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    internal static string Fingerprint(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Files(root))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/')));
            hash.AppendData([0]); hash.AppendData(File.ReadAllBytes(file)); hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void Export(string root, string destination)
    {
        var files = Files(root).ToArray();
        var full = Path.GetFullPath(destination);
        if (full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导出位置不能位于卡包自身目录中。");
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
                foreach (var file in files) zip.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
