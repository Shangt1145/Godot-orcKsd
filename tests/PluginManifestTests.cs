using Kards.Ui.OrcBridge;
using Orc.Plugins.BuiltIn;
using Orc.UI;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 移动端启动失败的回归：两个内置插件（orc.builtin.core / orc.builtin.ui）只认**磁盘清单**。
/// 桌面构建把清单作为 Content 复制到 &lt;输出&gt;/package/plugins，所以 AppContext.BaseDirectory 可用；
/// Android 导出的 APK 不带这些松散文件（只剩程序集），启动即抛
/// 「内置插件清单缺失：package/plugins/orc.builtin.core/manifest.json」。
/// 现在清单在构建期从引擎仓库嵌入，磁盘缺失时物化到可写目录再交给引擎。
/// </summary>
public sealed class PluginManifestTests
{
    [Fact]
    public void EmbeddedManifestsCanStandInForTheBuildOutputDirectory()
    {
        var root = PluginManifests.Materialize();
        Assert.True(File.Exists(Path.Combine(root, "orc.builtin.core", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(root, "orc.builtin.ui", "manifest.json")));
        // 引擎的两个装载器都只读磁盘清单：能被它们接受，才算真的可用（id/kind/guid 全部校验通过）。
        Assert.Equal(BuiltInPluginPackage.PackageId, BuiltInPluginPackage.Load(root).Manifest.Id);
        Assert.Equal(UiPluginPackage.PackageId, UiPluginPackage.Load(root).Manifest.Id);
    }

    [Fact]
    public void ResolvedRootIsAlwaysOneTheEngineAccepts()
    {
        // 桌面走构建输出，移动端走物化目录：两条路径都必须给出引擎能装载的插件根。
        var root = PluginManifests.ResolveRoot();
        Assert.Equal(BuiltInPluginPackage.PackageId, BuiltInPluginPackage.Load(root).Manifest.Id);
        Assert.Equal(UiPluginPackage.PackageId, UiPluginPackage.Load(root).Manifest.Id);
    }

    [Fact]
    public void MaterializedRootIsStableAndOutsideTheBuildOutput()
    {
        var first = PluginManifests.Materialize();
        var second = PluginManifests.Materialize();
        Assert.Equal(first, second); // 内容没变就不换目录，反复启动不堆垃圾。
        Assert.NotEqual(Path.Combine(AppContext.BaseDirectory, "package", "plugins"), first);
    }

    [Fact]
    public void HostStartsFromMaterializedManifests()
    {
        var root = PluginManifests.Materialize();
        OrcEngineHost.Reset();
        try
        {
            // 模拟移动端：把插件根指到构建输出之外的那个目录，宿主仍须建起来。
            Assert.NotNull(OrcEngineHost.Initialize(builtInPluginRoot: root, uiPluginRoot: root));
        }
        finally
        {
            OrcEngineHost.Reset();
        }
    }
}
