using System.Text.Json;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 扫描本机卡包目录，产出"本机实际装载了什么"的声明。
/// <para>
/// 只读 <c>manifest.json</c> 的 id/version/guid——这是**声明**，不是装载依据：
/// 服务端永远只从房主磁盘装载（<c>AdminApi</c> 的开局输入来自 <c>Host.Packs.PrepareAssembly()</c>），
/// 声明只用来做卡池对账，让"两边卡池不一样"在开局前就有一个名字。
/// 见 docs/design/POOL_ALIGNMENT_20261024.md。
/// </para>
/// </summary>
public static class PackDeclarations
{
    /// <summary>按目录名稳定排序读出每个含 manifest 的包；读不出来的包＝未声明，不阻断加入。</summary>
    public static IReadOnlyList<(string Id, string? Version, string? Guid)> Scan(string packsRoot)
    {
        if (string.IsNullOrWhiteSpace(packsRoot) || !Directory.Exists(packsRoot)) return [];

        var declared = new List<(string Id, string? Version, string? Guid)>();
        foreach (var directory in Directory.EnumerateDirectories(packsRoot).OrderBy(path => path, StringComparer.Ordinal))
        {
            var manifest = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                var bytes = File.ReadAllBytes(manifest);
                // 手写的 manifest 常带 UTF-8 BOM；带 BOM 交给解析器会直接失败。
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    bytes = bytes[3..];
                }

                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                var id = ReadString(root, "id");
                if (string.IsNullOrWhiteSpace(id)) id = Path.GetFileName(directory);
                declared.Add((id!, ReadString(root, "version"), ReadString(root, "guid")));
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException
                                              or UnauthorizedAccessException or NotSupportedException)
            {
                // 单个包读不出来不代表"没有卡池"，静默跳过即可——对账会把它显示成缺失项。
            }
        }

        return declared;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var node)
           && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
}
