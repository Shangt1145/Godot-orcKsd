using System.Reflection;
using System.Text.Json;
using Kards.Ui.Contracts;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.EffectParsing.Templates;

namespace Kards.Ui.OrcBridge;

public sealed record PackEffectBinding(string PrefabId, string EffectName);
public sealed record PackKeywordDecl(string Id, string Label, bool Battle, IReadOnlyList<PackEffectBinding> Bindings);

/// <summary>Parsed pack.json. Kind "pack" carries the vocabulary; "expansion" may only add units on top of one.</summary>
public sealed record PackManifest(
    string Id, string Name, string Kind, int Version,
    IReadOnlyList<string> DependsOn, IReadOnlyList<PackKeywordDecl> Keywords);

/// <summary>
/// One loaded pack: engine data plus UI projections. The raw engine ids stay untouched; every
/// UI-facing id is namespaced as "pack/&lt;packId&gt;/&lt;cardId&gt;" so packs can never collide with the
/// shipped catalog or DIY cards, mirroring the "diy/" convention.
/// </summary>
public sealed class PackContent
{
    public PackManifest Manifest { get; }
    public string RootDirectory { get; }
    public IReadOnlyList<CardDefinitionEntry> Definitions { get; }
    public IReadOnlyList<EffectSnapshot> InlinePrefabs { get; }
    public IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> Declarations { get; }
    public string? EffectsDirectory { get; }
    public IReadOnlyList<UiCardDefinition> Projections { get; }
    public IReadOnlyList<string> RegisteredKeywords { get; }

    internal PackContent(PackManifest manifest, string rootDirectory,
        IReadOnlyList<CardDefinitionEntry> definitions, IReadOnlyList<EffectSnapshot> inlinePrefabs,
        IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> declarations,
        string? effectsDirectory, IReadOnlyList<UiCardDefinition> projections,
        IReadOnlyList<string> registeredKeywords)
    {
        Manifest = manifest; RootDirectory = rootDirectory; Definitions = definitions;
        InlinePrefabs = inlinePrefabs; Declarations = declarations; EffectsDirectory = effectsDirectory;
        Projections = projections; RegisteredKeywords = registeredKeywords;
    }

    /// <summary>Definitions re-id'd to their UI namespace — the form decks and the match consume.</summary>
    public IReadOnlyList<CardDefinitionEntry> PrefixedDefinitions =>
        [.. Definitions.Select(e => new CardDefinitionEntry(Prefixed(e.Id), e.Definition))];

    /// <summary>Declarations re-keyed to the prefixed card id, same shape the match registry expects.</summary>
    public IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> PrefixedDeclarations =>
        [.. Declarations.Select(d => (Prefixed(d.CardId), d.PrefabIds))];

    public string Prefixed(string cardId) => $"pack/{Manifest.Id}/{cardId}";
}

/// <summary>
/// Card-pack loading over the engine's own distribution pipeline (batch 3/7 surfaces): manifest →
/// precheck (CardDistributionValidator, double-zero gate) → load (CardDataLoader) → keyword
/// registration (KeywordRegistry open registration + data-shell effect bindings) → match attach
/// (declarations, inline prefabs, effect-library directories). Rules stay in the engine; this file
/// only orchestrates admission and never rewrites engine data.
///
/// Lifecycle invariant: KeywordRegistry is process-global and KeywordEffectAssembly verifies every
/// declared binding against the match's PrefabManager at initialisation. Therefore an enabled pack
/// is part of the process vocabulary: its keywords stay registered while enabled, and EVERY match
/// created in the process attaches the pack's effect libraries (see AttachToMatch via the
/// CreateNativeAsync funnel). Unregistering mid-session would break in-match keyword grants.
/// </summary>
public static class PackSystem
{
    private static readonly object Gate = new();
    private static readonly List<PackContent> Enabled = [];
    private static readonly Dictionary<string, string> KeywordOwner = new(StringComparer.Ordinal);

    /// <summary>Built-in engine keyword ids, read once via reflection over the KeywordIds constants —
    /// no hardcoded second list to drift.</summary>
    private static readonly IReadOnlySet<string> BuiltInKeywords = typeof(KeywordIds)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ReservedPrefabIds = new HashSet<string>(StringComparer.Ordinal)
    {
        KeywordEffectAssembly.BlitzDeploySetPrefabId, KeywordEffectAssembly.MobilizeAccrualPrefabId,
        KeywordEffectAssembly.MobilizeLossPrefabId,
    };

    public static IReadOnlyList<PackContent> EnabledPacks { get { lock (Gate) return [.. Enabled]; } }

    public const string CardFileDirectory = "cards";
    public const string EffectFileDirectory = "effects";
    private const string ManifestFile = "pack.json";

    // ------------------------------------------------------------------ manifest

    public static PackManifest ReadManifest(string packRoot)
    {
        var path = Path.Combine(packRoot, ManifestFile);
        if (!File.Exists(path)) throw new InvalidDataException("缺少 pack.json——不是有效的卡包目录");
        JsonElement root;
        try { using var doc = JsonDocument.Parse(File.ReadAllText(path)); root = doc.RootElement.Clone(); }
        catch (JsonException e) { throw new InvalidDataException($"pack.json 无法解析：{e.Message}"); }
        int Schema() => root.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
        if (Schema() != 1) throw new InvalidDataException("pack.json schemaVersion 必须为 1");
        var id = Text(root, "id") ?? throw new InvalidDataException("pack.json 缺少 id");
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9._-]{1,63}\z"))
            throw new InvalidDataException($"卡包 id 无效（2–64 位字母数字点横线下划线）：{id}");
        if (id.Equals("diy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("卡包 id 不能占用 diy 命名空间");
        var name = Text(root, "name") ?? throw new InvalidDataException("pack.json 缺少 name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40) throw new InvalidDataException("卡包名称须为 1–40 字");
        var kind = Text(root, "kind") ?? throw new InvalidDataException("pack.json 缺少 kind");
        if (kind is not ("pack" or "expansion")) throw new InvalidDataException("kind 必须为 pack 或 expansion");
        var version = root.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number && ver.TryGetInt32(out var vn) ? vn : 0;
        if (version != 1) throw new InvalidDataException("pack.json version 必须为 1");
        var depends = root.TryGetProperty("dependsOn", out var dep) && dep.ValueKind == JsonValueKind.Array
            ? dep.EnumerateArray().Select(x => x.GetString()).ToArray() : [];
        if (depends.Any(d => string.IsNullOrWhiteSpace(d))) throw new InvalidDataException("dependsOn 含空白依赖");
        if (kind == "pack" && depends.Length > 0) throw new InvalidDataException("卡包（pack）不允许声明依赖");
        if (kind == "expansion" && depends.Length != 1)
            throw new InvalidDataException("扩展包（expansion）必须且只能依赖一个卡包");

        var keywords = new List<PackKeywordDecl>();
        if (root.TryGetProperty("keywords", out var kws) && kws.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in kws.EnumerateArray())
            {
                var kid = Text(item, "id") ?? throw new InvalidDataException("卡包词条缺少 id");
                if (!System.Text.RegularExpressions.Regex.IsMatch(kid, @"\A[^/\\\s]{1,32}\z") || kid.Contains('.'))
                    throw new InvalidDataException($"卡包词条 id 无效（1–32 位、不含点与路径符）：{kid}");
                if (!seen.Add(kid)) throw new InvalidDataException($"卡包词条 id 重复：{kid}");
                if (BuiltInKeywords.Contains(kid)) throw new InvalidDataException($"卡包词条 '{kid}' 与引擎内建词条重名");
                var label = Text(item, "label") ?? kid;
                if (string.IsNullOrWhiteSpace(label) || label.Length > 16) throw new InvalidDataException($"词条 '{kid}' 显示名须为 1–16 字");
                var battle = item.TryGetProperty("battle", out var b) && b.ValueKind == JsonValueKind.True;
                var bindings = new List<PackEffectBinding>();
                if (item.TryGetProperty("bindings", out var bds) && bds.ValueKind == JsonValueKind.Array)
                    foreach (var binding in bds.EnumerateArray())
                    {
                        var prefabId = Text(binding, "prefabId") ?? throw new InvalidDataException($"词条 '{kid}' 绑定缺少 prefabId");
                        var effectName = Text(binding, "effectName") ?? throw new InvalidDataException($"词条 '{kid}' 绑定缺少 effectName");
                        bindings.Add(new(prefabId, effectName));
                    }
                if (bindings.Count == 0) throw new InvalidDataException($"词条 '{kid}' 没有任何效果绑定——无行为词条不允许发布");
                keywords.Add(new(kid, label, battle, bindings.AsReadOnly()));
            }
        }
        return new(id, name, kind, version, depends.AsReadOnly(), keywords.AsReadOnly());
    }

    private static string? Text(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ------------------------------------------------------------------ load

    /// <summary>
    /// Validates and loads a pack directory. On success the pack's keywords are registered into the
    /// process vocabulary (idempotent for this pack). On any failure nothing is left registered.
    /// </summary>
    public static PackContent Load(string packRoot, IReadOnlyList<PackContent> enabledPacks)
    {
        var manifest = ReadManifest(packRoot);
        if (manifest.Kind == "expansion")
        {
            var basePack = enabledPacks.FirstOrDefault(p => p.Manifest.Id == manifest.DependsOn[0]);
            if (basePack is null)
                throw new InvalidDataException($"扩展包依赖的卡包 '{manifest.DependsOn[0]}' 未启用——请先启用基包");
            if (basePack.Manifest.Kind != "pack")
                throw new InvalidDataException("扩展包只能基于卡包（pack），不能基于另一个扩展包");
            if (enabledPacks.Any(p => p.Manifest.Id == manifest.Id))
                throw new InvalidDataException($"卡包 '{manifest.Id}' 已启用，不能重复装载");
        }
        var cardsDirectory = Path.Combine(packRoot, CardFileDirectory);
        var effectsDirectory = Path.Combine(packRoot, EffectFileDirectory);
        if (!Directory.Exists(cardsDirectory)) throw new InvalidDataException("缺少 cards/ 目录");
        lock (Gate) Enabled.RemoveAll(p => p.Manifest.Id == manifest.Id); // re-load = replace

        var report = CardDistributionValidator.Validate([cardsDirectory],
            Directory.Exists(effectsDirectory) ? new[] { effectsDirectory } : null);
        if (report.ErrorCount > 0 || report.WarningCount > 0)
            throw new InvalidDataException("分发预检未达双零："
                + string.Join("；", report.Errors.Select(e => e.Message)
                    .Concat(report.Warnings.Select(w => w.Message)).Take(6)));

        EnableKeywords(manifest, manifest.Id);
        try
        {
            var load = CardDataLoader.LoadDirectory(cardsDirectory);
            if (manifest.Kind == "expansion")
            {
                var commands = load.Definitions.Where(d => d.Definition.Category != CardCategory.Unit)
                    .Select(d => d.Id).ToArray();
                if (commands.Length > 0)
                    throw new InvalidDataException("扩展包只能添加单位卡，发现非单位卡：" + string.Join("、", commands));
            }

            var libraryIds = LibraryPrefabIds(effectsDirectory);
            var ownIds = libraryIds.Concat(load.Prefabs.Select(p => p.Root.Id)).ToHashSet(StringComparer.Ordinal);
            var basePack = manifest.Kind == "expansion"
                ? enabledPacks.First(p => p.Manifest.Id == manifest.DependsOn[0]) : null;
            CheckPrefabCollisions(manifest, ownIds, enabledPacks);
            CheckReferences(manifest, load, ownIds, basePack);

            var projections = load.Definitions.Select(e => Project(manifest, e)).ToArray();
            var content = new PackContent(manifest, packRoot, load.Definitions, load.Prefabs,
                load.EffectDeclarations.Select(d => (d.CardId, (IReadOnlyList<string>)d.PrefabIds)).ToArray(),
                Directory.Exists(effectsDirectory) ? effectsDirectory : null,
                projections, [.. manifest.Keywords.Select(k => k.Id)]);
            lock (Gate) Enabled.Add(content); // now part of the process vocabulary
            return content;
        }
        catch
        {
            DisableKeywords(manifest.Id);
            throw;
        }
    }

    /// <summary>Unregisters a pack's keywords (failure cleanup or UI disable). Missing ids are fine.</summary>
    public static void DisableKeywords(string packId)
    {
        lock (Gate)
        {
            foreach (var (keyword, owner) in KeywordOwner.Where(p => p.Value == packId).ToArray())
            {
                KeywordRegistry.Unregister(keyword);
                KeywordOwner.Remove(keyword);
            }
            Enabled.RemoveAll(p => p.Manifest.Id == packId);
        }
    }

    private static void EnableKeywords(PackManifest manifest, string packId)
    {
        lock (Gate)
        {
            // Fresh start per load: unregistering clears the engine's effect bindings with the
            // keyword, so re-loading the same pack can never hit "duplicate declaration" refusals.
            foreach (var (keyword, owner) in KeywordOwner.Where(p => p.Value == packId).ToArray())
            {
                KeywordRegistry.Unregister(keyword);
                KeywordOwner.Remove(keyword);
            }
            foreach (var keyword in manifest.Keywords)
            {
                if (KeywordOwner.TryGetValue(keyword.Id, out var owner) && owner != packId)
                    throw new InvalidDataException($"词条 '{keyword.Id}' 已被卡包 '{owner}' 注册");
                try { KeywordRegistry.Register(keyword.Id, (_, _) => new PlainKeywordComponent(keyword.Id), keyword.Battle); }
                catch (InvalidOperationException e) { throw new InvalidDataException($"词条 '{keyword.Id}' 注册失败：{e.Message}"); }
                KeywordOwner[keyword.Id] = packId;
            }
            foreach (var keyword in manifest.Keywords)
                KeywordRegistry.DeclareEffectBindings(keyword.Id,
                    keyword.Bindings.Select(b => new KeywordEffectBinding(b.PrefabId, b.EffectName)));
        }
    }

    // ------------------------------------------------------------------ checks

    private static IReadOnlySet<string> LibraryPrefabIds(string? effectsDirectory)
    {
        if (effectsDirectory is null || !Directory.Exists(effectsDirectory)) return new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(effectsDirectory, "*.prefab.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try { using var doc = JsonDocument.Parse(File.ReadAllText(file)); ids.Add(doc.RootElement.GetProperty("root").GetProperty("id").GetString()!); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException) { throw new InvalidDataException($"效果库文件无法解析：{Path.GetFileName(file)}"); }
        }
        return ids;
    }

    private static void CheckPrefabCollisions(PackManifest manifest, IReadOnlySet<string> ownIds, IReadOnlyList<PackContent> enabledPacks)
    {
        var reserved = new HashSet<string>(ReservedPrefabIds, StringComparer.Ordinal);
        foreach (var other in enabledPacks)
            foreach (var id in other.OwnPrefabIds())
                if (ownIds.Contains(id))
                    throw new InvalidDataException($"效果 id '{id}' 与已启用卡包 '{other.Manifest.Id}' 冲突");
        if (ownIds.Overlaps(reserved))
            throw new InvalidDataException("效果 id 与引擎保留的词条效果键冲突：" +
                string.Join("、", ownIds.Intersect(reserved)));
    }

    private static IReadOnlySet<string> OwnPrefabIds(this PackContent pack)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in pack.InlinePrefabs.Select(p => p.Root.Id)) ids.Add(id);
        foreach (var id in LibraryPrefabIds(pack.EffectsDirectory)) ids.Add(id);
        return ids;
    }

    private static void CheckReferences(PackManifest manifest, CardDataLoadResult load,
        IReadOnlySet<string> ownIds, PackContent? basePack)
    {
        var available = new HashSet<string>(ownIds, StringComparer.Ordinal);
        if (basePack is not null) available.UnionWith(basePack.OwnPrefabIds());
        var unresolved = load.EffectDeclarations
            .SelectMany(d => d.PrefabIds).Distinct(StringComparer.Ordinal)
            .Where(id => !ownIds.Contains(id) && !available.Contains(id)).ToArray();
        if (unresolved.Length > 0)
            throw new InvalidDataException("引用的效果 id 不在（本包 ∪ 基包）集合内：" + string.Join("、", unresolved));

        var knownKeywords = new HashSet<string>(BuiltInKeywords, StringComparer.Ordinal);
        foreach (var keyword in manifest.Keywords) knownKeywords.Add(keyword.Id);
        if (basePack is not null)
            foreach (var keyword in basePack.Manifest.Keywords) knownKeywords.Add(keyword.Id);
        var unknownKeywords = load.Definitions.SelectMany(d => d.Definition.Keywords).Select(k => k.Id)
            .Distinct(StringComparer.Ordinal).Where(id => !knownKeywords.Contains(id)).ToArray();
        if (unknownKeywords.Length > 0)
            throw new InvalidDataException("卡牌引用的词条未注册：" + string.Join("、", unknownKeywords));

        // The engine's card-data keyword component maps attributes through a static built-in table and
        // silently parks anything else in UnmappedAttributes. For packs that silent trim is a rule
        // leak, so it is upgraded to an explicit refusal: card JSON can only declare built-in keywords;
        // pack keywords enter play through granting effects, not declarations.
        var unmapped = load.Definitions.Where(d => d.Definition.UnmappedAttributes.Count > 0)
            .Select(d => $"{d.Id}（{string.Join("、", d.Definition.UnmappedAttributes)}）").ToArray();
        if (unmapped.Length > 0)
            throw new InvalidDataException("卡牌声明了引擎无法映射的词条标识（会被静默忽略，故拒绝装载）：" + string.Join("；", unmapped));
    }

    // ------------------------------------------------------------------ projection

    private static UiCardDefinition Project(PackManifest manifest, CardDefinitionEntry entry)
    {
        var d = entry.Definition;
        var isUnit = d.Category == CardCategory.Unit;
        string Token(string engineId) => DiyCardCompiler.KeywordToken(engineId);
        return new UiCardDefinition
        {
            CardId = $"pack/{manifest.Id}/{entry.Id}",
            Name = d.Name,
            CardType = isUnit ? "unit" : "order",
            UnitType = d.UnitTypes.Count > 0 ? d.UnitTypes[0].ToString().ToLowerInvariant() : "",
            Set = "pack:" + manifest.Name,
            Rarity = d.Rarity switch { Rarity.Standard => "iron", Rarity.Limited => "bronze", Rarity.Special => "silver", Rarity.Elite => "gold", _ => "common" },
            Cost = d.DeployCost,
            BaseAttack = isUnit ? d.Attack : null,
            BaseDefense = isUnit ? d.Defense : null,
            BaseOpCost = isUnit ? d.OperateCost : null,
            Keywords = [.. d.Keywords.Select(k => Token(k.Id))],
            KeywordValues = d.Keywords.Where(k => k.Value is { }).ToDictionary(k => Token(k.Id), k => k.Value!.Value),
        };
    }

    // ------------------------------------------------------------------ match attach

    /// <summary>
    /// Registers a match's pack side: effect declarations into the card registry, inline prefabs and
    /// effect-library directories into the match PrefabManager. Must run before match initialisation
    /// (registration precedes card loading — the engine's documented pipeline order).
    /// </summary>
    internal static void AttachToMatch(CardEffectRegistry registry, LogicEngine engine, IReadOnlyList<PackContent> packs)
    {
        foreach (var pack in packs)
        {
            foreach (var (cardId, prefabIds) in pack.PrefixedDeclarations)
                registry.DeclarePrefab(cardId, prefabIds);
            foreach (var snapshot in pack.InlinePrefabs)
                engine.Prefabs.RegisterPrefab(snapshot);
            if (pack.EffectsDirectory is { } directory)
            {
                var report = engine.Prefabs.LoadDirectory(directory);
                if (report.Failures.Count > 0)
                    throw new InvalidDataException($"卡包 '{pack.Manifest.Id}' 效果库装载失败：" + string.Join("；", report.Failures));
            }
        }
    }
}
