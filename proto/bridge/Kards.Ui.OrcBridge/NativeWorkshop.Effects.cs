using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Orc.Core;
using Orc.Game.Migration;

namespace Kards.Ui.OrcBridge;

public sealed record WorkshopEffect(NativeWorkshopAsset Asset, string Name, string Description, string[] Occasions, OrcEffectDraft[]? Rules);

public sealed partial class NativeWorkshop
{
    public IReadOnlyList<WorkshopEffect> Effects() => Assets().Where(a => a.Kind == "Effect").Select((asset, index) =>
    {
        var document = JsonNode.Parse(Read(asset.Path));
        // 两种落盘形态并存：v3 预制体 { root: { mainTrigger: { stableKey, hooks } } }；
        // v4 组件 { asset: { id, members[], mounts[] } }——触发时机在 mounts[].target，形如 "engine#unit.deployed"。
        var root = document?["root"] ?? document?["asset"];
        var isComponent = document?["asset"] is not null;
        var record = (Presentation()["effectAuthoring"] as JsonObject)?[asset.Id] as JsonObject;
        var label = root?["mainTrigger"]?["stableKey"]?.ToString();
        var name = record?["name"]?.ToString()
                   ?? (isComponent ? asset.AppId : string.IsNullOrWhiteSpace(label) ? $"效果 {index + 1}" : label.Split('·')[0]);
        var hooks = RawHooks(root).Select(HookName).Distinct().ToArray();
        return new WorkshopEffect(asset, name, record?["description"]?.ToString() ?? label?.Split('·', 2).ElementAtOrDefault(1) ?? "", hooks,
            record?["rules"] is JsonArray rules ? JsonSerializer.Deserialize<OrcEffectDraft[]>(rules.ToJsonString()) : null);
    }).ToArray();

    /// <summary>原始挂载名：v3 读 <c>mainTrigger.hooks</c>，v4 读 <c>asset.mounts[].target</c>。</summary>
    private static string[] RawHooks(JsonNode? root)
        => root?["mainTrigger"]?["hooks"] is JsonArray legacy
            ? legacy.Select(n => n?.ToString() ?? "").ToArray()
            : root?["mounts"] is JsonArray mounts
                ? mounts.Select(m => m?["target"]?.ToString() ?? "").ToArray()
                : [];

    /// <summary>v4 的挂载目标带引擎命名空间前缀（<c>engine#unit.deployed</c>），展示前剥掉。</summary>
    private static string HookName(string hook)
        => hook.StartsWith("engine#", StringComparison.Ordinal) ? hook["engine#".Length..] : hook;

    public void SaveEffectInfo(string id, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new InvalidDataException("效果名称需要 1–80 个字符。");
        RequireEffect(id);
        var presentation = Presentation();
        if (presentation["effectAuthoring"] is not JsonObject effects) { effects = new(); presentation["effectAuthoring"] = effects; }
        if (effects[id] is not JsonObject record) { record = new(); effects[id] = record; }
        record["name"] = name.Trim(); record["description"] = description; SavePresentation(presentation);
    }

    private static string Occasion(string hook) => hook switch
    {
        "turn.start" => "回合开始", "turn.end" => "回合结束", "unit.deployed" => "单位部署后",
        "card.played" => "打出卡牌后", "card.died" => "卡牌被消灭后", "card.damaged" => "受到伤害后",
        "unit.position.changed" => "单位移动后", "unit.acted" => "单位行动后", "unit.damage.dealt" => "造成伤害后",
        "counter.triggered" => "反制触发后", "counter.disarmed" => "反制解除后", _ => "其他触发时机"
    };

    /// <summary>
    /// 把本包的旧效果资产（<c>effects/*.prefab.json</c>，schemaVersion 3）补成 v4 组件资产
    /// （<c>effects-v4/*.component.json</c>，schemaVersion 4）。
    /// <para>
    /// **为什么每次落盘都要跑**：v4 的对局装载面只认 <c>effects-v4/</c>——<see cref="Orc.Packages.CardPackAssembler"/>
    /// 见到旧格式目录直接判该包失败（必需包＝不开局）；而工坊的作者面仍整体按 v3 快照工作（效果编辑器、规则 DSL、
    /// <see cref="Effects"/> 的读面全部读 <c>mainTrigger.hooks</c>）。所以"作者面 v3、装载面 v4"由这里在写入后
    /// 立刻补齐：两套目录并存是引擎定的过渡形态（迁移器常量注释：旧资产不删除，两套各自可装载）。
    /// </para>
    /// <para>
    /// **迁移失败＝整包失败**（沿用迁移器自身的原子约定：任一资产失败就一项都不落盘）：抛出去让作者当场看见
    /// 是哪条规则用了 v4 承载不了的旧写法，而不是打完一局才发现这张卡是哑弹。
    /// </para>
    /// </summary>
    /// <param name="packRoot">卡包根目录（工坊的临时根）。</param>
    private static void MigrateEffectsToV4(string packRoot)
    {
        // 没有 effects/ 就不碰：MigratePackToDisk 对空输入也会 CreateDirectory(输出目录)，
        // 会凭空造出一个空的 effects-v4/ 并把装载面切到 v4 分支（无害但会误导排查）。
        if (!Directory.Exists(Path.Combine(packRoot, Orc.Packages.PackageScanner.EffectsDirectoryName)))
        {
            return;
        }

        var report = AssetSchemaMigrator.MigratePackToDisk(packRoot);
        if (report.Ok) return;
        throw new InvalidDataException(
            "效果资产无法转成 v4 组件格式（整包未落盘）：" + string.Join("；", report.Failures));
    }

    public WorkshopEffect CreateEffect(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new InvalidDataException("效果名称需要 1–80 个字符。");
        var id = "effect." + Guid.NewGuid().ToString("N");
        var relative = "effects/" + id + ".prefab.json";
        var source = WorkshopPackageArchive.Child(Editor.Session!.TemporaryRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, new JsonObject { ["schemaVersion"] = 3, ["root"] = new JsonObject { ["id"] = id, ["version"] = 1,
            ["mainTrigger"] = new JsonObject { ["id"] = "t.main", ["stableKey"] = name.Trim(), ["kind"] = "passive", ["viewType"] = "Orc.Cards.CardEventView", ["hooks"] = new JsonArray(), ["events"] = new JsonArray(), ["version"] = 1 },
            ["otherTriggers"] = new JsonArray(), ["wrappers"] = new JsonArray(), ["injects"] = new JsonArray(), ["judicatorWrappers"] = new JsonArray() } }.ToJsonString(_json));
        var asset = Editor.CreateAsset(AssetKind.Effect, id, relative);
        // 新效果是空触发器（无成员），迁移照样要跑：包里只要存在 effects/ 而没有 effects-v4/，
        // 对局装载就会判整包失败——"刚建了个效果还没写规则"不该让整个包打不开。
        MigrateEffectsToV4(Editor.Session!.TemporaryRoot);
        var presentation = Presentation();
        if (presentation["effectAuthoring"] is not JsonObject effects) { effects = new(); presentation["effectAuthoring"] = effects; }
        effects[asset.LocalId] = new JsonObject { ["name"] = name.Trim(), ["description"] = "", ["rules"] = new JsonArray() }; SavePresentation(presentation);
        return Effects().Single(e => e.Asset.Id == asset.LocalId);
    }

    public string SaveEffect(WorkshopEffect effect, string name, string description, IReadOnlyList<OrcEffectDraft> rules)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new InvalidDataException("效果名称需要 1–80 个字符。");
        if (rules.Count != 1) throw new InvalidDataException("每个效果需要一条完整规则；可在规则内组合多个条件和动作。");
        var outcome = OrcEffectEditing.Compile("workshop." + effect.Asset.Id, rules).Single();
        if (outcome.Error is not null) throw new InvalidDataException(outcome.Error);
        if (outcome.Snapshots.Count != 1) throw new InvalidDataException("这条规则无法作为一个独立效果保存，请使用通用图形编排。");
        var compiled = JsonNode.Parse(Orc.Cards.PrefabJson.SerializeForDisk(outcome.Snapshots[0]))!.AsObject();
        compiled["root"]!["id"] = effect.Asset.Id;
        var root = Editor.Session!.TemporaryRoot;
        var before = WorkshopPackageArchive.Files(root).ToDictionary(p => p, p => File.ReadAllBytes(Path.Combine(root, p)));
        var backup = Path.Combine(_root, "backups", Guid.NewGuid().ToString("N") + "-effect");
        WorkshopPackageArchive.Copy(root, backup);
        try
        {
            Commit(effect.Asset.Path, compiled.ToJsonString(_json));
            // 作者面写的是 v3 快照（规则 DSL／图形编排的产物），装载面只认 v4 组件资产：
            // 这一步把刚写下的快照就地转成 effects-v4/<assetId>.component.json。
            // 卡定义里的效果 id 不变（迁移器保留 root.id），所以卡→效果的声明无需改写。
            MigrateEffectsToV4(root);
            var presentation = Presentation();
            if (presentation["effectAuthoring"] is not JsonObject effects) { effects = new(); presentation["effectAuthoring"] = effects; }
            if (effects[effect.Asset.Id] is not JsonObject record) { record = new(); effects[effect.Asset.Id] = record; }
            record["name"] = name.Trim(); record["description"] = description; record["rules"] = JsonSerializer.SerializeToNode(rules);
            SavePresentation(presentation); return backup;
        }
        catch
        {
            foreach (var file in WorkshopPackageArchive.Files(root).Where(p => !before.ContainsKey(p))) File.Delete(Path.Combine(root, file));
            foreach (var item in before) File.WriteAllBytes(Path.Combine(root, item.Key), item.Value); CheckpointRecovery(); throw;
        }
    }

    // ---------- v4 组件资产（effects-v4/*.component.json）的读面 ----------
    //
    // 引擎的编辑面（Orc.Packages.Editing.AssetEditorSession）只索引 v3 的 effects/*.prefab.json：
    // Commit 只收 *.card.json / *.prefab.json，CreateAsset 的落点也硬编码 effects/<id>.prefab.json。
    // 而 v4 的对局装载面只认 effects-v4/*.component.json（CardPackAssembler 见旧格式目录直接判包失败）。
    // 两者之间由 MigrateEffectsToV4 在每次写入后补桥——那条路走的是"作者面 v3 → 装载面 v4"。
    //
    // 但**只有 v4 目录**的包（引擎自带 samples/card-pack-demo、card-pack-space-war 都是）在编辑面里
    // 一条效果都列不出来：玩家既看不见它们，也改不了名称与说明。下面这几个成员就是给这类资产补一个
    // 读面：可见、可改展示信息、可删；**规则不可编辑**——v4 组件的规则由 members[].handler 的 csx 承载，
    // v3 的规则 DSL 表达不了，硬塞会把脚本丢掉（所以 WorkshopEffect.Rules 恒为 null，UI 据此只开名称/说明）。

    private const string ComponentSuffix = ".component.json";

    /// <summary>v4 组件目录的包内相对名（与引擎常量同源，不另写字面量）。</summary>
    private static string ComponentDirectoryName => Orc.Packages.PackageScanner.ComponentsDirectoryName;

    /// <summary>把 <c>effects-v4/</c> 里引擎编辑面看不见的组件补成 Effect 条目（同名 v3 预制体优先，它可编排规则）。</summary>
    private IEnumerable<NativeWorkshopAsset> ComponentAssets(IReadOnlyCollection<NativeWorkshopAsset> listed)
    {
        var directory = Path.Combine(Editor.Session!.TemporaryRoot, ComponentDirectoryName);
        if (!Directory.Exists(directory)) return [];

        var packageId = Editor.Session.PackageId;
        var taken = listed.Where(a => a.Kind == nameof(AssetKind.Effect)).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var version = Manifest(Editor.Session.TemporaryRoot).Version.ToString();
        var found = new List<NativeWorkshopAsset>();
        foreach (var file in Directory.EnumerateFiles(directory, "*" + ComponentSuffix, SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var appId = Path.GetFileName(file)[..^ComponentSuffix.Length];
            // 组件文件的 id 有两种惯例：引擎样例写全名（demo.rally），工坊迁移产物写本地 id（effect.xxx）。
            var localId = appId.StartsWith(packageId + ".", StringComparison.Ordinal) ? appId[(packageId.Length + 1)..] : appId;
            if (!taken.Add(localId)) continue;
            found.Add(new NativeWorkshopAsset(nameof(AssetKind.Effect), localId, appId, version,
                ComponentDirectoryName + "/" + Path.GetFileName(file)));
        }

        return found;
    }

    /// <summary>这条资产是不是 v4 组件（＝不在引擎编辑面的索引里，提交/删除都得自己动手）。</summary>
    private static bool IsComponentAsset(NativeWorkshopAsset asset)
        => asset.Kind == nameof(AssetKind.Effect)
           && asset.Path.StartsWith(ComponentDirectoryName + "/", StringComparison.Ordinal);

    private bool IsComponentAssetId(string id)
        => Assets().Any(a => IsComponentAsset(a) && a.Id == id);

    /// <summary>存在性校验：v3 走引擎（保留它原有的 <c>AssetNotFound</c> 语义），v4 走目录。</summary>
    private void RequireEffect(string id)
    {
        if (IsComponentAssetId(id)) return;
        _ = Editor.GetAsset(AssetKind.Effect, id);
    }

    /// <summary>删除 v4 组件：删文件 + 清展示信息（引擎的 DeleteAsset 只认索引里的资产）。</summary>
    private void DeleteComponentAsset(NativeWorkshopAsset asset)
    {
        var file = WorkshopPackageArchive.Child(Editor.Session!.TemporaryRoot, asset.Path);
        if (!File.Exists(file)) throw new InvalidDataException("效果资产不存在：" + asset.Path);
        File.Delete(file);
        var presentation = Presentation();
        (presentation["effectAuthoring"] as JsonObject)?.Remove(asset.Id);
        SavePresentation(presentation);
        CheckpointRecovery();
    }

    /// <summary>
    /// v4 组件的引用面：卡的 <c>components[]</c> 里按 id 声明（<c>{"component":"demo.rally"}</c>）。
    /// 与 v3 的 <c>DeleteImpact</c> 同语义——删之前先把引用它的卡列出来。
    /// </summary>
    private IReadOnlyList<NativeWorkshopAsset> ComponentImpact(NativeWorkshopAsset asset)
    {
        var root = Editor.Session!.TemporaryRoot;
        return Assets()
            .Where(a => a.Kind == nameof(AssetKind.Card) && CardDeclaresComponent(root, a.Path, asset.AppId))
            .ToArray();
    }

    private static bool CardDeclaresComponent(string root, string cardPath, string componentId)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(WorkshopPackageArchive.Child(root, cardPath)))?["components"] is not JsonArray components) return false;
            return components.OfType<JsonObject>().Any(entry =>
                string.Equals(entry["component"]?.ToString(), componentId, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false; // 卡源读不出来＝这条引用关系未知；不因此阻止删除，但也不假装引用过。
        }
    }
}
