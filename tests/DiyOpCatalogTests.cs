using System.Text.Json;
using System.Text.Json.Nodes;
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
        //    2026-10-29 切到 v4 后实测 55 / 106。少掉的 2 个**不是"引擎没了这个机制"**，
        //    是 `InspectorVocabulary.Action` 自己把默认声明拼坏了：它按目录的字段表逐字段填默认值，
        //    可那张表是**按原语汇总的并集**（`heal` 里列着 `attack`／`defense`，那是它兄弟变体的参数），
        //    而 OrC 侧的正常化器是**按"哪些字段在场"认变体**的——塞满并集就把形状撑坏了。
        //    改成"先拼精简形状、过不了编译器才退回全量"之后 **62 / 99**：条件、循环、变量这三个
        //    复杂 DIY 最需要的东西回来了，另有 4 个同类（全字段 optional）也一并转正。
        Assert.Equal(161, all.Count);
        Assert.Equal(62, DiyOpCatalog.Alpha.Count);
        Assert.Equal(99, DiyOpCatalog.Preview.Count);

        // 2.1 这 7 个曾经是"工具链缺陷"而不是"引擎确实没有"，修好后必须留在 alpha 区。
        //    钉住它们是为了防止回退：条件与循环是复杂 DIY 最需要的东西，掉了不会有人立刻发现。
        foreach (var op in new[] { "conditional", "repeat", "setVar", "removeKeyword", "reveal", "setStats", "upgradeSelf" })
            Assert.Contains(DiyOpCatalog.Alpha, e => e.Op == op);

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

    /// <summary>
    /// 种子里必须真的带着这个动作的关键字段——否则"能编过"可能是靠一个空形状蒙过去的。
    ///
    /// <para>
    /// 这条**不是** <c>OperationProblem</c> 的复述：判据是"声明里有没有那个字段"，与编译器怎么答无关。
    /// 防的是一种很隐蔽的坏法——某天有人把 <see cref="InspectorVocabulary.Action"/> 的退回条件写松了，
    /// <c>{"op":"conditional"}</c> 这种空壳又被当成能跑，分区数字还是绿的，玩家的条件却静默失效。
    /// </para>
    /// </summary>
    [Fact]
    public void 转正动作的种子里带着关键字段()
    {
        (string Op, string Field)[] required =
        [
            ("conditional", "condition"), ("conditional", "then"), ("conditional", "else"),
            ("repeat", "times"), ("repeat", "actions"),
            ("setVar", "name"), ("setVar", "value"),
            ("reveal", "target"), ("setStats", "target"), ("upgradeSelf", "target"),
        ];
        foreach (var (op, field) in required)
        {
            Assert.Equal(DiyOpStage.Alpha, DiyOpCatalog.All.Single(e => e.Op == op).Stage);
            Assert.True(InspectorVocabulary.Action(op).ContainsKey(field), $"{op} 的种子缺关键字段 {field}");
        }

        // 种子必须稳定：连着问两次不能给出不同形状（缓存/顺序都不该影响玩家的草稿）。
        foreach (var entry in DiyOpCatalog.Alpha)
            Assert.Equal(InspectorVocabulary.Action(entry.Op).ToJsonString(), InspectorVocabulary.Action(entry.Op).ToJsonString());
    }

    /// <summary>
    /// 复杂 DIY 的判据：把条件、循环、变量**叠在一张卡里**，整张效果必须编成没有错误项的程序。
    ///
    /// <para>
    /// 光有"单个动作能过 lowering"不够——玩家的真实写法是"如果……就重复 N 次……并把结果记进变量"。
    /// 这三样以前全在预览区，等于复杂 DIY 根本不存在。这条用例守的就是"它真的能写出来"：
    /// 走的是 <see cref="DiyCapability.CheckEffect"/>（内部 <c>OrcEffectEditing.Compile</c>），
    /// 和卡库保存时的编译是同一条路。
    /// </para>
    /// </summary>
    [Fact]
    public void 条件循环变量叠在一张卡里能编成程序()
    {
        // 「重复 2 次：把 hits 记成 1」
        var repeat = InspectorVocabulary.Action("repeat");
        repeat["times"] = JsonValue.Create(2);
        repeat["actions"] = new JsonArray(new JsonObject
        {
            ["op"] = "setVar", ["name"] = "hits", ["value"] = JsonValue.Create(1),
        });

        // 「如果自身攻击 > 0，就抽一张；否则走上面那个循环」
        var conditional = InspectorVocabulary.Action("conditional");
        conditional["condition"] = JsonNode.Parse(
            """{"test":"compare","left":{"expr":"read","of":"self","path":"attack"},"cmp":">","right":0}""")!;
        conditional["then"] = new JsonArray(InspectorVocabulary.Action("drawOne"));
        conditional["else"] = new JsonArray(repeat);

        var effect = new JsonObject
        {
            ["trigger"] = "deploy",
            ["targets"] = new JsonArray(),
            ["actions"] = new JsonArray(conditional),
        };

        var errors = DiyCapability.CheckEffect(JsonSerializer.SerializeToElement(effect));
        output.WriteLine($"嵌套效果编译：{errors.Count} 个错误" + (errors.Count == 0 ? "" : " → " + string.Join("；", errors)));
        Assert.Empty(errors);
    }
}
