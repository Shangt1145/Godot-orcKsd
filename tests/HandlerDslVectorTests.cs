using System.Reflection;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge.HandlerDsl;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 契约包里的 <c>check</c> 向量（<c>contracts/handler-dsl/v1/vectors/check/*.check.json</c>）：
/// **前端跑一遍，结论必须与 <c>expect</c> 逐字相等**（一个码 ＋ 一个节点路径）。
/// <para>
/// 这是 R26 第 3 条「同一份非法 DSL，前后端报出的码与节点路径一致」的客户端那一半。
/// 引擎侧的同名测试（<c>VectorTests.check向量_后端结论与期望一致</c>）跑同一批文件；
/// 两边都绿＝没有分歧，只有一边绿＝分歧被藏起来了。
/// </para>
/// <para>
/// 为什么值得单独一组：这些向量是**冻结**的（自带目录快照），所以它们测的是我们的判定口径，
/// 不是引擎今天的扫描结果。我们的 13 个错误码与契约 13 码同名，路径口径也必须一致——
/// 差一个 `actions[0].bind` 与 `actions[0].bind.card`，作者看到的提示就指不到出错的字段。
/// </para>
/// </summary>
public class HandlerDslVectorTests
{
    /// <summary>契约包根：与引擎检出同源，不复制副本（复制一份就是两份基准，必然漂移）。</summary>
    private static string VectorDirectory =>
        Path.Combine(EngineRoot(), "contracts", "handler-dsl", "v1", "vectors", "check");

    public static TheoryData<string> CheckVectorFiles
    {
        get
        {
            var data = new TheoryData<string>();
            if (!Directory.Exists(VectorDirectory)) return data;   // 缺检出时由下面的守卫测试报出来
            foreach (var file in Directory.GetFiles(VectorDirectory, "*.check.json")
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(file));
            }
            return data;
        }
    }

    [Fact]
    public void 向量目录存在且不是空集()
    {
        Assert.True(Directory.Exists(VectorDirectory), "找不到契约向量目录：" + VectorDirectory);
        var files = Directory.GetFiles(VectorDirectory, "*.check.json");
        Assert.True(files.Length >= 27, $"check 向量少于 27 条（实际 {files.Length}）：契约可能被裁过。");
    }

    [Theory]
    [MemberData(nameof(CheckVectorFiles))]
    public void check向量_前端结论与期望一致(string file)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorDirectory, file)))!;
        Assert.Equal("check", root["kind"]!.GetValue<string>());

        var catalog = HandlerCatalog.FromSnapshot(root["catalog"]);
        var document = HandlerDocument.FromJson(root["document"]!.ToJsonString(), catalog);
        var view = ViewOf(root["view"]);
        var expectCode = root["expect"]!["code"]!.GetValue<string>();
        var expectPath = root["expect"]!["nodePath"]!.GetValue<string>();

        var issues = HandlerDslValidator.Validate(document, catalog, view);

        // 「恰好一个」是契约要求（README：期望值不许写成「码属于集合 {a,b}」）。
        // 多报一个就等于把真结论藏在噪音里，所以这里不放宽。
        var issue = Assert.Single(issues);
        Assert.True(expectCode == issue.Code && expectPath == issue.NodePath,
            $"{file}：期望 ({expectCode}, {expectPath})，实际 ({issue.Code}, {issue.NodePath})");
    }

    [Fact]
    public void 向量的码都在我们的封闭集内()
    {
        // 出现集外码＝契约加了一码而前端没跟，或者我们造了个自创码。
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "graph-shape", "node-unknown", "pin-unknown", "pin-missing", "pin-type", "bind-scope",
            "write-forbidden", "loop-unbounded", "ask-in-loop", "schema-version",
            "expr-op-unknown", "expr-arity", "expr-type",
        };

        foreach (var file in Directory.GetFiles(VectorDirectory, "*.check.json"))
        {
            var root = JsonNode.Parse(File.ReadAllText(file))!;
            var code = root["expect"]!["code"]!.GetValue<string>();
            Assert.True(known.Contains(code), $"{Path.GetFileName(file)} 用了封闭集外的码：{code}");
        }
    }

    private static IReadOnlyDictionary<string, HandlerViewField> ViewOf(JsonNode? node)
    {
        var view = new Dictionary<string, HandlerViewField>(StringComparer.Ordinal);
        if (node?["fields"] is not JsonObject fields) return view;
        foreach (var (name, spec) in fields)
        {
            var type = spec?["type"]?.GetValue<string>() ?? "unknown";
            var writable = spec?["writable"]?.GetValue<bool>() ?? false;
            view[name] = new HandlerViewField(name, type, writable);
        }
        return view;
    }

    /// <summary>
    /// 引擎仓库根：由构建写进**桥接程序集**的元数据（与二进制一一对应，不依赖环境变量）。
    /// <para>
    /// 注意读的是 <see cref="HandlerDslAuthoring"/> 所在程序集——元数据挂在被构建的那一侧，
    /// 测试程序集自己没有这一条。
    /// </para>
    /// </summary>
    private static string EngineRoot()
    {
        var recorded = typeof(HandlerDslAuthoring).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "OrcEngineRoot")?.Value;
        if (!string.IsNullOrWhiteSpace(recorded) && Directory.Exists(recorded)) return recorded;

        var fromEnvironment = Environment.GetEnvironmentVariable("ORC_ENGINE_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment)) return fromEnvironment;

        throw new InvalidOperationException("找不到引擎仓库根（程序集元数据 OrcEngineRoot 与环境变量都不可用）。");
    }
}
