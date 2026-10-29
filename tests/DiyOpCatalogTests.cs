using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

/// <summary>
/// P4：动作选择器的"诚实分区"。
///
/// <para>
/// 这里守的不是"分区好看"，而是三件会悄悄坏掉的事：**目录被裁剪**（玩家以为这版没这个能力）、
/// **分区和编译器脱钩**（名单手写，引擎补上机制后还挂着"预览"）、**表头与行号错位**
/// （UI 靠 <c>_pickerRowOps</c> 的索引对齐，分区顺序一旦不成立就会添加错动作）。
/// </para>
/// </summary>
public sealed class DiyOpCatalogTests(ITestOutputHelper output)
{
    [Fact]
    public void 动作目录一个不少地分区且分区与编译器一致()
    {
        var all = DiyOpCatalog.All;

        // 1. 不裁剪：选择器里能选的动作 == 词表里的全部动作。
        Assert.Equal(InspectorVocabulary.Operations.Length, all.Count);
        Assert.Equal(all.Count, all.Select(e => e.Op).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(InspectorVocabulary.Operations.OrderBy(o => o, StringComparer.Ordinal),
            all.Select(e => e.Op).OrderBy(o => o, StringComparer.Ordinal));

        // 2. 记账：2026-10-27 的诚实状态是 161 个动作里 57 个真能跑、104 个是预览，
        //    和计划文档 P4 一致。引擎补上机制后这三个数会变——**变了就回来改这里和计划文档**，
        //    别把它改成"自动生成、没人再看"的报表：数字变了却没人知道，正是这次要收口的病。
        //
        //    2026-10-29 切到 v4 后实测 55 / 106。**少掉的这 2 个不是"引擎没了这个机制"**，是目录里
        //    几个动作的默认声明本身坏了（见下面第 2.1 条把它们钉住）：`conditional`／`repeat` 的必填
        //    字段 initial 是 JS 的 `undefined`，请求体里就成了字符串 "undefined"，OrC 侧再 JSON.parse
        //    直接 SyntaxError；`setVar` 缺目录键（KeyNotFound）。三者都属工具链缺陷、待修，
        //    修好之后这里会红，提醒把数字改回去。
        Assert.Equal(161, all.Count);
        Assert.Equal(55, DiyOpCatalog.Alpha.Count);
        Assert.Equal(106, DiyOpCatalog.Preview.Count);

        // 2.1 把"工具链缺陷"和"引擎确实没有"分开记：这三条是前者，修好了本用例必须红。
        Assert.Contains(DiyOpCatalog.Preview, e => e.Op == "conditional" && e.Problem.Contains("undefined"));
        Assert.Contains(DiyOpCatalog.Preview, e => e.Op == "repeat" && e.Problem.Contains("undefined"));
        Assert.Contains(DiyOpCatalog.Preview, e => e.Op == "setVar");

        // 3. 判据来自编译器：alpha ⇔ 默认声明过得了 DesktopNativeLowering，没有第三态。
        foreach (var entry in all)
        {
            var problem = DiyCapability.OperationProblem(entry.Op);
            Assert.Equal(entry.Stage == DiyOpStage.Alpha, problem.Length == 0);
            if (entry.Stage == DiyOpStage.Alpha) Assert.Empty(entry.Problem);
            else Assert.NotEmpty(entry.Problem);
        }

        // 4. 分区顺序：所有 alpha 都在所有 preview 之前——表头只画一次，靠的就是这条。
        var firstPreview = all.ToList().FindIndex(e => e.Stage == DiyOpStage.Preview);
        Assert.True(firstPreview > 0, "预览分区不该为空，否则分区说明没有意义。");
        Assert.All(all.Skip(firstPreview), e => Assert.Equal(DiyOpStage.Preview, e.Stage));

        Assert.Equal(all.Count, DiyOpCatalog.Alpha.Count + DiyOpCatalog.Preview.Count);
        output.WriteLine($"目录 {all.Count} 个：alpha {DiyOpCatalog.Alpha.Count} / 预览 {DiyOpCatalog.Preview.Count}");
    }

    /// <summary>
    /// 玩家视角的那句话必须能算出来，而不是写死在 UI 里：数字变了说明引擎能力变了，
    /// 这时候 <c>ALPHA_CLOSEOUT_PLAN</c> 的 P4 章节也得跟着改。这条用例的作用就是把这句话钉住。
    /// </summary>
    [Fact]
    public void 分区数字可复算且互斥()
    {
        var alpha = DiyOpCatalog.Alpha;
        var preview = DiyOpCatalog.Preview;
        Assert.All(alpha, e => Assert.Equal(DiyOpStage.Alpha, e.Stage));
        Assert.All(preview, e => Assert.Equal(DiyOpStage.Preview, e.Stage));
        Assert.Empty(alpha.Select(e => e.Op).Intersect(preview.Select(e => e.Op), StringComparer.Ordinal));
        Assert.All(alpha.Concat(preview), e => Assert.False(string.IsNullOrWhiteSpace(e.Group)));
        output.WriteLine($"alpha={alpha.Count} preview={preview.Count} total={DiyOpCatalog.All.Count}");
        foreach (var group in alpha.GroupBy(e => e.Group).OrderByDescending(g => g.Count()))
            output.WriteLine($"  {group.Key}: alpha {group.Count()} / 预览 {preview.Count(p => p.Group == group.Key)}");
    }
}
