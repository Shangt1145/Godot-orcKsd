using Orc.Game.Cards;
using Orc.Packages;
using Orc.Plugins.BuiltIn;
using Orc.UI;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui.OrcBridge;

/// <summary>收藏页/构筑页用的卡包描述：显示名（manifest 的 `name` 附加字段，引擎读宽忽略）、
/// 基包归属（`dependsOn` 恰一条＝扩展包；空＝基包）、卡数。</summary>
public sealed record PackDescriptor(string Id, string Name, string? BasePackId, int CardCount);

/// <summary>
/// 前端持有的**引擎宿主**（迁移 S8）：进程内单例，负责按新资产模型完成"发现 → 解析 → 冷加载"——
/// 注入两个**代码入口包**（引擎内置插件 `orc.builtin.core` ＋ UI 插件 `orc.builtin.ui`），
/// 并在给定基目录下按约定发现卡包（`&lt;基目录&gt;/packs/&lt;包&gt;/manifest.json`）。
/// 对局创建一律经 <see cref="Snapshot"/> 冻结的资产目录（对局内不可变）。
/// </summary>
public static class OrcEngineHost
{
    private static readonly object Gate = new();
    private static EngineHost? _host;

    /// <summary>已初始化的宿主（未初始化＝null）。</summary>
    public static EngineHost? Current
    {
        get { lock (Gate) return _host; }
    }

    /// <summary>
    /// 原始卡池国家（av76 / deran / usg / un）是**独立国家**，不是苏/德/美/英的别名——
    /// 把它们登记为引擎自定义主国。幂等；<see cref="Initialize"/> 会自动调用。
    /// 未登记时卡牌定义会因「国籍未定义」被拒绝编译，构筑配置也过不了对局创建期的校验。
    /// </summary>
    public static void RegisterSourceNations()
    {
        lock (Gate)
        {
            foreach (var nation in DeckNations.SourceNations)
            {
                if (!Faction.IsDefined(nation)) Faction.RegisterCustom(nation);
            }
        }
    }

    /// <summary>
    /// 初始化（**幂等**；已初始化＝直接返回，不重建）。冷加载失败明细见 <see cref="EngineHost.LoadFailures"/>。
    /// </summary>
    /// <param name="baseDirectory">包基目录（约定：`&lt;base&gt;/plugins` 与 `&lt;base&gt;/packs`；null/不存在＝无磁盘包）。</param>
    /// <param name="uiPluginRoot">UI 插件包的插件根（缺省＝程序集输出目录下的 `package/plugins`）。</param>
    /// <param name="builtInPluginRoot">内置插件的插件根（缺省＝同上）。</param>
    public static EngineHost Initialize(
        string? baseDirectory = null,
        string? uiPluginRoot = null,
        string? builtInPluginRoot = null)
    {
        lock (Gate)
        {
            RegisterSourceNations();

            if (_host is not null)
            {
                return _host;
            }

            // 卡数据组件（六项）与内置词条是**进程级静态面**，由内置插件的进程相内容提供；
            // 消费方启动时先装配（幂等），保证任何路径（含无磁盘包）都可建局。
            BuiltInPlugin.RegisterProcessDefaults();

            var discovery = baseDirectory is not null && Directory.Exists(baseDirectory)
                ? PackageDiscovery.Discover(baseDirectory)
                : new PackageDiscoveryReport(Array.Empty<DiscoveredPackage>(), Array.Empty<string>());

            foreach (var package in discovery.Packages) NativeWorkshop.RegisterNations(package.RootDirectory);
            _host = EngineHost.FromDiscovery(
                discovery,
                evaluator: null,
                extraPackages:
                [
                    BuiltInPluginPackage.Load(builtInPluginRoot),
                    .. UiPluginPackage.Packages(uiPluginRoot),
                ]);

            var packages = _host.CreateSnapshot().CardPacks;
            var ids = packages.Select(p => p.Manifest.Id).ToHashSet(StringComparer.Ordinal);
            var parents = packages.ToDictionary(p => p.Manifest.Id,
                p => WorkshopPackagePresentation.Parent(p.RootDirectory,p.Manifest.Id) is { } parent && ids.Contains(parent) ? new[]{parent} : Array.Empty<string>(), StringComparer.Ordinal);
            var families = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var root = id; var visited = new HashSet<string>(StringComparer.Ordinal);
                while (visited.Add(root) && parents[root].Length == 1) root = parents[root][0];
                families["pack/" + id] = "pack/" + root;
            }
            DeckCardPacks.ConfigureFamilies(families);

            return _host;
        }
    }

    /// <summary>暖启动（UI 层专用）：确保宿主初始化与卡包发现完成，不向 UI 程序集暴露
    /// 引擎 <c>EngineHost</c> 返回类型——否则编译器会把 Orc.Packages 记进 UI 程序集引用表，
    /// 触发 UI 边界守卫。</summary>
    public static void WarmUp(string? baseDirectory = null) => Initialize(baseDirectory);
    public static IReadOnlyList<UiDiyChoice> NativeNations => Current?.CreateSnapshot().CardPacks
        .SelectMany(p => NativeWorkshop.ReadNations(p.RootDirectory)).DistinctBy(n => n.Id).ToArray() ?? [];

    /// <summary>Replace the effective card-pack set atomically; existing match snapshots stay frozen.</summary>
    public static void SetWorkshopRoots(IReadOnlyList<string> roots)
    {
        lock (Gate)
        {
            var host = Current ?? Initialize();
            if (host.CardPacksLocked) throw new InvalidOperationException("请先结束当前对局，再更改卡包。");
            var baseline = host.CreateSnapshot();
            try
            {
                foreach (var id in baseline.CardPacks.Select(p => p.Manifest.Id).Distinct()) host.UnloadCardPacks(id);
                foreach (var root in roots)
                {
                    NativeWorkshop.RegisterNations(root);
                    if (!host.TryLoadCardPack(root, out var failure)) throw new InvalidDataException(root + "：" + failure);
                }
            }
            catch { host.RestoreFrom(baseline); throw; }
        }
    }

    /// <summary>冻结当前宿主的一份资产快照（未初始化＝先按"无磁盘包"初始化）。</summary>
    public static AssetCatalogSnapshot Snapshot()
        => (Current ?? Initialize()).CreateSnapshot();

    /// <summary>
    /// 装载一个卡包目录的解析产物（卡定义／内联预制体／声明／文本）。
    /// 引擎 I3 拆分把 <c>CardPackLoader</c> 删掉了（扫描＝<see cref="PackageScanner"/>、注册＝
    /// <see cref="PackageRegistrar"/>），这里用一个**临时注册表**只取 <c>Content</c>：
    /// 它不参与对局装配（那仍走 <c>SnapshotPackageActivator</c> + <c>CardPackAssembler</c>），
    /// 只为图鉴/构筑/工坊取卡包内容，和原来的 Load 语义等价。
    /// </summary>
    public static CardPackContent LoadPackContent(string packageRoot, PackageManifest manifest)
        => LoadPackContent(packageRoot, manifest, out _);

    /// <summary>
    /// 同上，并交出**被引擎规则过滤掉的卡**（如"指令卡必须有内置效果"——卡面文本里有描述、
    /// 但没有可执行效果的空壳卡会被丢弃）。这些卡的文件仍在磁盘上，只是进不了卡池。
    /// </summary>
    public static CardPackContent LoadPackContent(string packageRoot, PackageManifest manifest,
        out IReadOnlyList<string> rejected)
    {
        var result = PackageRegistrar.Register(packageRoot, manifest, new AssetRegistry());
        // 只算卡类资产：效果/触发器资产的 Card 也恒为 null，混进来会让计数对不上。
        rejected = result.Assets
            .Where(a => a.Card is null && a.Metadata.Kind == Orc.Core.AssetKind.Card)
            .Select(a => a.AppId)
            .ToArray();
        return result.Content;
    }

    /// <summary>重置（测试/重开用）。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _host = null;
            _lastPacks = [];
            DeckCardPacks.ConfigureFamilies(new Dictionary<string, string>());
        }
    }

    private static IReadOnlyList<PackDescriptor> _lastPacks = [];
    private static IReadOnlyList<string> _lastRejected = [];

    /// <summary>
    /// 最近一次 <see cref="LoadPackCards"/> 中被引擎规则过滤掉的卡（文件在磁盘上、但进不了卡池）。
    /// 新引擎对指令/反制卡要求内置效果非空，只有卡面文本没有实现的空壳卡会被这里点名——
    /// 验证据此核对"文件数 = 卡池数 + 被过滤数"，避免把静默丢卡当成正常。
    /// </summary>
    public static IReadOnlyList<string> RejectedPackCards
    {
        get { lock (Gate) return _lastRejected; }
    }

    /// <summary>最近一次 <see cref="LoadPackCards"/> 装载出的卡包描述（未装载＝空；收藏页瓷砖/来源筛选消费）。</summary>
    public static IReadOnlyList<PackDescriptor> PackDescriptors    {
        get { lock (Gate) return _lastPacks; }
    }

    /// <summary>
    /// 装载全部卡包并投影为**图鉴/构筑**用的静态卡定义（含卡面包 `texts.json` 的卡面文本），
    /// 连同装载失败明细一并返回（单包失败＝隔离该包，不阻断其余）。
    /// </summary>
    /// <param name="baseDirectory">包基目录（约定：`&lt;base&gt;/plugins` 与 `&lt;base&gt;/packs`；null＝无磁盘包）。</param>
    /// <param name="artLookup">卡 id → 美术路径（可空＝无图，走文本回退）。</param>
    public static (IReadOnlyList<UiCardDefinition> Cards, IReadOnlyList<string> Failures,
        IReadOnlyList<PackDescriptor> Packs) LoadPackCards(
        string? baseDirectory = null,
        Func<string, string>? artLookup = null)
    {
        var host = Initialize(baseDirectory);
        var failures = host.LoadFailures.ToList();
        var snapshot = host.CreateSnapshot();
        Console.WriteLine($"[pack-load] base={baseDirectory} packs={snapshot.CardPacks.Count} runtimeCards={snapshot.RuntimeCards.Count} failures={failures.Count}");
        var cards = new List<UiCardDefinition>();
        var packs = new List<PackDescriptor>();
        var rejectedAll = new List<string>();
        foreach (var package in snapshot.CardPacks)
        {
            try
            {
                var content = LoadPackContent(package.RootDirectory, package.Manifest, out var rejected);
                rejectedAll.AddRange(rejected);
                var texts = PackPresentationData.Texts(content);
                _ = PackPresentationData.Plans(content);
                var prefix = content.Prefix;
                foreach (var entry in content.Definitions)
                {
                    var rawId = entry.Id.StartsWith(prefix, StringComparison.Ordinal)
                        ? entry.Id[prefix.Length..]
                        : entry.Id;
                    cards.Add(StaticCardProjection.Project(
                        entry.Id, entry.Definition, artLookup,
                        text: texts.TryGetValue(rawId, out var text) ? text : null) with { Flavor = PackPresentationData.Flavor(package.RootDirectory, rawId) });
                }

                // v1 的 dependsOn 已退役，依赖一律读 v2 的 requires（包 ID 列表）。
                var required = package.Manifest.RequiredPackages;
                packs.Add(new PackDescriptor(
                    package.Manifest.Id, ManifestName(package.RootDirectory, package.Manifest.Id),
                    WorkshopPackagePresentation.Parent(package.RootDirectory,package.Manifest.Id),
                    content.Definitions.Count));
                Console.WriteLine($"[pack-load] {package.Manifest.Id} defs={content.Definitions.Count} root={package.RootDirectory}");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failures.Add($"{package.Manifest.Id}@{package.Manifest.Version}：{e.Message}");
            }
        }

        foreach (var entry in snapshot.RuntimeCards)
        {
            cards.Add(StaticCardProjection.Project(entry.Id, entry.Definition, artLookup));
        }

        lock (Gate) { _lastPacks = packs; _lastRejected = rejectedAll; }
        return (cards, failures, packs);
    }

    /// <summary>读 manifest 的显示名（`name` 附加字段；缺失＝回退包 id）。引擎清单本身不携带显示名。</summary>
    private static string ManifestName(string rootDirectory, string fallback)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(rootDirectory, "manifest.json")));
            return doc.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == System.Text.Json.JsonValueKind.String
                ? name.GetString() ?? fallback : fallback;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return fallback;
        }
    }
}
