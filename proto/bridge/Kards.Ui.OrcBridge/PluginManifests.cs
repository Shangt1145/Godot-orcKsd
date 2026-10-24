using System.Security.Cryptography;
using System.Text;
using Orc.Plugins.BuiltIn;
using Orc.UI;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 内置插件清单的**运行期落点**。
///
/// <para>
/// 引擎的两个内置插件（<c>orc.builtin.core</c> / <c>orc.builtin.ui</c>）要求清单是**磁盘文件**：
/// <c>BuiltInPluginPackage.Load</c> / <c>UiPluginPackage.Load</c> 只在插件根目录下找
/// <c>&lt;id&gt;/manifest.json</c>。桌面构建里这两份清单由引擎工程以 Content 复制到
/// <c>&lt;输出&gt;/package/plugins/</c>，所以 <see cref="AppContext.BaseDirectory"/> 恰好可用；
/// **Android 导出后没有这个目录**——发布输出里的松散文件不进 APK，只剩程序集，
/// 启动时于是抛「内置插件清单缺失：package/plugins/orc.builtin.core/manifest.json」。
/// </para>
///
/// <para>
/// 因此构建期把引擎仓库里的两份清单**嵌入程序集**（单一真源仍是引擎仓库，不在本仓库另存副本），
/// 运行期发现磁盘缺失就物化到可写目录，再把该目录当作插件根交给引擎。
/// 两条路径产出的清单内容完全一致，桌面行为不变。
/// </para>
/// </summary>
internal static class PluginManifests
{
    private const string CoreResource = "Kards.Ui.OrcBridge.Plugins.orc.builtin.core.manifest.json";
    private const string UiResource = "Kards.Ui.OrcBridge.Plugins.orc.builtin.ui.manifest.json";

    /// <summary>
    /// 插件根：优先用构建输出自带的 <c>package/plugins</c>（桌面），否则物化嵌入清单（移动端）。
    /// 判定用「两份清单都在」而不是「目录在」——只拷了一半的目录同样不能启动。
    /// </summary>
    public static string ResolveRoot()
    {
        var onDisk = Path.Combine(AppContext.BaseDirectory, "package", "plugins");
        return Complete(onDisk) ? onDisk : Materialize();
    }

    private static bool Complete(string root)
        => File.Exists(Path.Combine(root, BuiltInPluginPackage.DirectoryName, "manifest.json"))
           && File.Exists(Path.Combine(root, UiPluginPackage.DirectoryName, "manifest.json"));

    /// <summary>
    /// 把嵌入的两份清单写到可写目录并返回该插件根。目录名按内容取哈希——清单升级后自然换目录，
    /// 不会读到上一次的旧清单。
    /// </summary>
    public static string Materialize()
    {
        var manifests = Read();
        var content = new StringBuilder();
        foreach (var (id, json) in manifests) content.Append(id).Append(':').Append(json).Append('\n');
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())))[..16].ToLowerInvariant();
        var root = Path.Combine(ScratchRoot(), key);
        foreach (var (id, json) in manifests)
        {
            var directory = Path.Combine(root, id);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "manifest.json");
            if (File.Exists(path) && File.ReadAllText(path) == json) continue;
            // 先写临时文件再换名：中途被杀不会留下半份 JSON 让下次启动误判。
            File.WriteAllText(path + ".tmp", json, new UTF8Encoding(false));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        return root;
    }

    /// <summary>
    /// 可写的落点：临时目录优先（Android 上是应用缓存目录），退到程序目录下的隐藏目录。
    /// <para>注意 <see cref="AppContext.BaseDirectory"/> 在 Android 上是**空串**（程序集从 APK 的
    /// assets 里读，没有对应的磁盘目录），所以不能把它直接拼成相对路径当代替品。</para>
    /// </summary>
    private static string ScratchRoot()
    {
        var candidates = new List<string> { Path.Combine(Path.GetTempPath(), "orc-ksd-plugins") };
        // Android 上 TMPDIR 未必指向应用缓存：再备一个应用私有数据目录（LocalApplicationData）。
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local)) candidates.Add(Path.Combine(local, "orc-ksd-plugins"));
        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, ".runtime-plugins"));
        }
        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or NotSupportedException or ArgumentException)
            {
                // 换下一个候选：Android 上临时目录正常可写，这里只是兜底。
            }
        }
        throw new InvalidOperationException("内置插件清单无处可写：临时目录与程序目录均不可写。");
    }

    private static (string Id, string Json)[] Read()
    {
        var assembly = typeof(PluginManifests).Assembly;
        return
        [
            (BuiltInPluginPackage.DirectoryName, Text(assembly, CoreResource)),
            (UiPluginPackage.DirectoryName, Text(assembly, UiResource)),
        ];
    }

    private static string Text(System.Reflection.Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"嵌入的插件清单缺失：{resourceName}。构建时应由 Kards.Ui.OrcBridge.csproj 从引擎仓库嵌入。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
