using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Effects;
using Orc.Packages.Editing;

namespace Kards.Ui.OrcBridge.HandlerDsl;

/// <summary>
/// 把积木画布产出的 DSL 文档**落成资产**（S10 编辑出口／D11／R28）。
/// <para>
/// 三步，缺一不可：
/// <list type="number">
///   <item><b>写 DSL 文档</b>——<c>handler-dsl/&lt;name&gt;.hdsl.json</c>。它不是资产：没有 id、不进 lock、
///     不 bump 任何版本，提交立刻成功（引擎有意如此，不是漏了版本追踪）。</item>
///   <item><b>写草案</b>——<c>modings/&lt;name&gt;.draft.moding.json</c>。作者编辑的**永远是草案**，
///     <c>asset.extends</c> 指向父效果，成员用 <c>handler.dsl</c> 引用第 1 步的文档。</item>
///   <item><b>摊平</b>——产物落到 <c>effects-v4/&lt;id&gt;.component.json</c>，csx 由 DSL **现场生成**，
///     带 <c>generated</c> 来源标记（双哈希 ＋ 行号映射）。</item>
/// </list>
/// </para>
/// <para>
/// 为什么单独一个类而不是塞进 <c>NativeWorkshop</c>：这三步是**纯文件与引擎 API**，不碰 Godot。
/// 放进工坊类就得把整个界面拖进测试；这里只要求一个已 <c>BeginSession</c> 的 <see cref="AssetEditor"/>
/// 和它当前会话的临时包根。
/// </para>
/// </summary>
public sealed class HandlerDslAuthoring
{
    /// <summary>DSL 文档目录（相对包根，<c>/</c> 分隔）。</summary>
    public const string DslDirectory = "handler-dsl";

    /// <summary>草案目录（相对包根）。</summary>
    public const string DraftDirectory = "modings";

    /// <summary>DSL 文档后缀（引擎 <c>AssetEditorSession</c> 的提交白名单里就是这个）。</summary>
    public const string DslSuffix = ".hdsl.json";

    /// <summary>草案后缀（引擎 <c>ModingFileSuffix</c> 同形）。</summary>
    public const string DraftSuffix = ".draft.moding.json";

    private static readonly Lazy<HandlerDslEnvironment> SharedEnvironment = new(HandlerDslAssembly.Create);

    /// <param name="editor">已 <c>BeginSession</c> 的编辑面（提交走它，才能落到会话的临时包）。</param>
    /// <param name="packRoot">会话临时包根（摊平器的 <c>packRoot</c>）。</param>
    public HandlerDslAuthoring(AssetEditor editor, string packRoot)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);
        Editor = editor;
        PackRoot = Path.GetFullPath(packRoot);
    }

    /// <summary>编辑面（提交入口）。</summary>
    public AssetEditor Editor { get; }

    /// <summary>会话临时包根。</summary>
    public string PackRoot { get; }

    /// <summary>写 DSL 文档，返回包内相对路径（<c>handler-dsl/&lt;name&gt;.hdsl.json</c>）。</summary>
    /// <exception cref="ArgumentException">名字含路径分隔符或非法字符。</exception>
    /// <exception cref="AssetEditorException">引擎拒绝（内容不合法等）。</exception>
    public string CommitDsl(string name, string json)
    {
        var path = DslDirectory + "/" + Slug(name) + DslSuffix;
        Editor.Commit(path, json);
        return path;
    }

    /// <summary>写草案，返回包内相对路径（<c>modings/&lt;name&gt;.draft.moding.json</c>）。</summary>
    public string CommitDraft(string name, string json)
    {
        var path = DraftDirectory + "/" + Slug(name) + DraftSuffix;
        Editor.Commit(path, json);
        return path;
    }

    /// <summary>
    /// 摊平草案并**原子写入**产物。失败＝一个字节都不写（引擎 R8 第 10 条），既有产物保持完好。
    /// </summary>
    /// <param name="draftRelativePath"><see cref="CommitDraft"/> 的返回值。</param>
    /// <param name="environment">DSL 装配（缺省＝进程内共享的那一份，含编译器与目录）。</param>
    public ModingFlattenResult Flatten(string draftRelativePath, HandlerDslEnvironment? environment = null)
        => ModingFlattener.FlattenAndWrite(PackRoot, draftRelativePath, environment ?? SharedEnvironment.Value);

    /// <summary>`flatten --check`：草案是否落后于父／DSL 改过没重新摊平。</summary>
    public ModingFlattenCheckResult Check(string draftRelativePath)
        => ModingFlattener.Check(PackRoot, draftRelativePath);

    /// <summary>
    /// 造一份草案 JSON（<c>schemaVersion: 4</c>，<c>kind: moding</c>）。
    /// <para>
    /// 三个 id 别搞混：<paramref name="draftId"/> 是草案自己，<paramref name="productId"/> 是摊平后的产物
    /// （缺省＝去掉 <c>.draft</c>），<paramref name="parentId"/> 是**必填**的父效果——没有它就不是草案，而是组件资产。
    /// </para>
    /// </summary>
    /// <param name="draftId">草案资产 id，例 <c>my-fire-support.draft</c>。</param>
    /// <param name="parentId">父效果 id，例 <c>diy.fire-support.effect</c>。</param>
    /// <param name="memberName">被覆盖的成员名，例 <c>onPlayed</c>。</param>
    /// <param name="dslPath"><see cref="CommitDsl"/> 的返回值。</param>
    /// <param name="viewFields">成员视图字段（缺省＝只声明 <c>Card</c>，够跑 draw／damage 一类）。</param>
    /// <param name="mounts">**新增**的挂载点；缺省＝空（沿用父效果已有的挂载）。
    /// <para>
    /// ⚠ 不要把父效果已有的挂载再写一遍：摊平器把「父的挂载」和「草案的挂载」**拼接**，不去重。
    /// 重复一条就等于把 handler 挂两遍——`draw 2` 会变成抽 4 张，而产物看起来完全正常
    /// （本仓库的真实事故，见 <c>HandlerDslAuthoringTests.摊平出来的牌在真实对局里真的生效</c>）。
    /// </para></param>
    /// <param name="productId">产物 id；null＝把 <paramref name="draftId"/> 的 <c>.draft</c> 去掉。</param>
    /// <param name="dslJson">DSL 文档原文。给了就**自动算 <c>dslHash</c>**——强烈建议给：
    /// 引擎的 <c>flatten --check</c> 把「没记哈希」判成陈旧（<c>DslHash is null</c> 即入差异项），
    /// 不记的草案永远显示「请重新摊平」，作者会被一条永远消不掉的警告追着跑。</param>
    /// <param name="dslHash">手算好的哈希（<paramref name="dslJson"/> 为空时才用）。</param>
    public static string BuildDraft(
        string draftId,
        string parentId,
        string memberName,
        string dslPath,
        IReadOnlyList<HandlerViewField>? viewFields = null,
        IReadOnlyList<HandlerMount>? mounts = null,
        string? productId = null,
        string? dslJson = null,
        string? dslHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentException.ThrowIfNullOrWhiteSpace(dslPath);

        var hash = dslJson is not null ? HandlerContent.Hash(dslJson) : dslHash;

        var fields = viewFields ?? [new HandlerViewField("Card", "entity", false)];
        var handler = new JsonObject
        {
            ["id"] = draftId + "#" + memberName,
            ["entry"] = "HandleAsync",
            ["dsl"] = dslPath,
        };
        if (!string.IsNullOrWhiteSpace(hash))
            handler["dslHash"] = hash;

        var member = new JsonObject
        {
            ["name"] = memberName,
            ["visibility"] = new JsonArray("callable", "mountable"),
            ["view"] = new JsonObject
            {
                ["fields"] = new JsonArray(fields.Select(f => (JsonNode)new JsonObject
                {
                    ["name"] = f.Name,
                    ["type"] = f.Type,
                    ["writable"] = f.Writable,
                    ["required"] = false,
                }).ToArray()),
            },
            ["handler"] = handler,
        };

        // 缺省**不声明**挂载：父效果的 mounts 会被摊平器带进产物，这里再写一遍就是挂两遍。
        var mountList = mounts ?? [];
        var asset = new JsonObject
        {
            ["kind"] = "moding",
            ["id"] = draftId,
            ["product"] = new JsonObject { ["id"] = productId ?? ProductIdOf(draftId) },
            ["extends"] = parentId,
            ["requires-host"] = "any",
            ["active-when"] = "loaded",
            ["one-shot"] = false,
            ["private-fields"] = new JsonArray(),
            ["members"] = new JsonArray(member),
            ["mounts"] = new JsonArray(mountList.Select(m => (JsonNode)new JsonObject
            {
                ["target"] = m.Target,
                ["member"] = m.Member,
            }).ToArray()),
            ["wraps"] = new JsonArray(),
        };

        return new JsonObject { ["schemaVersion"] = 4, ["asset"] = asset }.ToJsonString(Options);
    }

    /// <summary>草案 id → 产物 id（去掉末尾的 <c>.draft</c>）。</summary>
    public static string ProductIdOf(string draftId)
        => draftId.EndsWith(".draft", StringComparison.Ordinal) ? draftId[..^".draft".Length] : draftId;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>名字只允许字母数字与 <c>- _ .</c>：它会被拼进路径，绝不能带分隔符或 <c>..</c>。</summary>
    private static string Slug(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains("..", StringComparison.Ordinal)
            || name.Contains('/') || name.Contains('\\')
            || name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                $"名字只能包含字母、数字与 - _ .（它会被拼进包内路径）：'{name}'", nameof(name));
        }

        return name;
    }
}

/// <summary>挂载点（草案里 <c>mounts[]</c> 的一项）。</summary>
/// <param name="Target">信号地址，例 <c>engine#card.played</c>。</param>
/// <param name="Member">被挂载的成员名。</param>
public sealed record HandlerMount(string Target, string Member);
