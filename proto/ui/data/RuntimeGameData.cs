using Godot;

namespace Kards.Ui;

/// <summary>OrC-KSD reads physical package files. Exported PCK data is materialized in user data.</summary>
internal static class RuntimeGameData
{
    private static string? _root;
    public static string Directory(string name)
    {
        if (_root is null)
        {
            if (OS.HasFeature("editor")) _root = ProjectSettings.GlobalizePath("res://proto/data");
            else
            {
                var files = ReadTree("res://proto/data", "").ToArray();
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                foreach (var (path, bytes) in files)
                { hash.AppendData(System.Text.Encoding.UTF8.GetBytes(path)); hash.AppendData([0]); hash.AppendData(bytes); }
                // Changed or removed package files get a different cache; stale definitions cannot
                // survive an update and silently re-enter the admitted pool.
                _root = ProjectSettings.GlobalizePath("user://runtime-data/" + Convert.ToHexString(hash.GetHashAndReset()));
                foreach (var (path, bytes) in files)
                {
                    var target = System.IO.Path.Combine(_root, path);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    System.IO.File.WriteAllBytes(target + ".tmp", bytes);
                    System.IO.File.Move(target + ".tmp", target, overwrite: true);
                }
                if (!System.IO.Directory.Exists(System.IO.Path.Combine(_root, "nations")))
                    throw new InvalidDataException("游戏包缺少卡牌数据，请重新导出完整游戏。");
            }
        }
        return System.IO.Path.Combine(_root, name);
    }
    private static IEnumerable<(string Path, byte[] Bytes)> ReadTree(string source, string relative)
    {
        foreach (var file in DirAccess.GetFilesAt(source).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal))
        {
            var bytes = Godot.FileAccess.GetFileAsBytes(source + "/" + file);
            if (bytes.Length == 0) throw new InvalidDataException("游戏数据读取失败：" + file);
            yield return (System.IO.Path.Combine(relative, file), bytes);
        }
        foreach (var folder in DirAccess.GetDirectoriesAt(source).OrderBy(f => f, StringComparer.Ordinal))
            foreach (var file in ReadTree(source + "/" + folder, System.IO.Path.Combine(relative, folder))) yield return file;
    }
}
