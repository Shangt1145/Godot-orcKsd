using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 引擎内置的 CardTextEffectCompiler 默认按
/// <c>AppContext.BaseDirectory/EffectParsing/Templates/ops</c> 找 op 语句模板。
/// 安卓上 BaseDirectory 是空串，拼出来是相对路径、必然不存在，于是目录为空、
/// 每个 op 都报「op 语句模板 'xxx' 缺失（op 资产缺失）」。
/// 桥接层把模板嵌入并物化，宿主再把 CardTextEffectCompiler.OpTemplateDirectory 指过去。
/// </summary>
public sealed class EffectTemplateTests
{
    [Fact]
    public void MaterializedOpDirectoryCarriesEveryEngineTemplate()
    {
        var directory = DiyCardCompiler.SharedOpsDirectory;
        Assert.True(Directory.Exists(directory), "op 模板目录不存在：" + directory);

        var files = Directory.GetFiles(directory, "*.csx.tpl");
        Assert.NotEmpty(files);
        // costMod 是安卓日志里点名缺失的那个，必须真在。
        Assert.Contains(files, f => Path.GetFileName(f) == "costMod.csx.tpl");
    }

    [Fact]
    public void MaterializedOpDirectoryIsContentAddressedAndStable()
    {
        var first = DiyCardCompiler.SharedOpsDirectory;
        var second = DiyCardCompiler.SharedOpsDirectory;
        Assert.Equal(first, second); // 内容没变就不换目录，反复启动不堆垃圾。
    }
}
