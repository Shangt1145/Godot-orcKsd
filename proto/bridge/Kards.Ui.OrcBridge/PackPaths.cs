using System.Security.Cryptography;
using System.Text;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 卡包目录的**本地**路径/摘要工具（无网络语义）。
/// <para>
/// **为什么本仓库自带这一份**：这些函数原本挂在引擎的 <c>Orc.Protocol.NetworkPackageArchive</c> 上，
/// 但那个类型属于「卡包传输」这条只在本地分支存在的功能（<c>ServerHost.PackTransfer</c> 的
/// <c>pool.offer</c>／<c>pool.chunk</c>／<c>pool.commit</c>／<c>pool.fetch</c> 六个方法），新引擎里整条线不存在。
/// </para>
/// <para>
/// 工坊打包本身**不依赖传输**：它只需要"把包 id 变成安全的目录名""算内容摘要""列文件""防路径穿越"。
/// 所以这里只保留这四个纯本地工具，把传输相关（<c>Zip</c>／<c>Extract</c>／分块常量）一并去掉——
/// 需要它们的那条网络路径已按新引擎的口径明确停用（见 <c>RemoteMatchFeed.Packs.cs</c>）。
/// </para>
/// </summary>
internal static class PackPaths
{
    /// <summary>把任意 id 转成安全的目录名（SHA-256 十六进制，大写，与旧实现逐字节一致）。</summary>
    public static string Key(string value) => Hash(Encoding.UTF8.GetBytes(value));

    /// <summary>字节摘要（SHA-256 十六进制，大写）。</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>拼接包内相对路径并**拒绝越界**（绝对路径、<c>..</c>、Windows 保留名、非法字符）。</summary>
    public static string Child(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')
                || p.Any(c => c < 32 || "<>:\"|?*".Contains(c))
                || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
                    .Contains(p.Split('.')[0], StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("卡包包含非法路径：" + relative);
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("卡包路径越界。");
        return path;
    }

    /// <summary>列出包内全部文件（序稳定；遇到链接＝拒绝——包内不允许重定向）。</summary>
    public static IEnumerable<string> Files(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).OrderBy(p => p, StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("卡包不能包含链接。");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var file in Files(entry)) yield return file;
            }
            else
            {
                yield return entry;
            }
        }
    }

    /// <summary>内容摘要：逐文件累加「相对路径 ＋ 0 ＋ 内容 ＋ 0」，顺序稳定。</summary>
    public static string Digest(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Files(root))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/')));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(file));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
