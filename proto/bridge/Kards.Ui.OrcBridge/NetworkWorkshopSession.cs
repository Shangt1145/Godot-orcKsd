using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Orc.Protocol;
using Orc.Core;

namespace Kards.Ui.OrcBridge;

/// <summary>Freezes enabled native packs and compiles compatibility drafts into session-only native packages.</summary>
public sealed class NetworkWorkshopSession
{
    public IReadOnlyList<string> Roots { get; private init; } = [];
    public IReadOnlyDictionary<string, string> CardIds { get; private init; } = new Dictionary<string, string>();
    public string Directory { get; private init; } = "";
    public IReadOnlyList<string> MapDeck(IReadOnlyList<string> ids) => ids.Select(id => CardIds.GetValueOrDefault(id, id)).ToArray();

    public static NetworkWorkshopSession Prepare(string sessionRoot, IReadOnlyList<string> nativeRoots,
        IReadOnlyList<DiyCardDraft> drafts, IReadOnlyList<UiCardDefinition> catalog, IReadOnlyList<string> deck,
        string? artDirectory = null)
    {
        CleanupExpired(sessionRoot);
        var directory = Path.Combine(sessionRoot, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in nativeRoots)
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "manifest.json")))!;
            var id = manifest["id"]!.ToString();
            var target = Path.Combine(directory, PackPaths.Key(id));
            // The shipped first-party sample has canonical v4 metadata. Do not transport
            // obsolete v3 files left by an older generated asset directory.
            if (id == "orc.bulwark" && Directory.Exists(Path.Combine(source, "effects-v4"))
                && manifest["metadata"]!.AsArray().All(m => !m!.ToString().Replace('\\', '/').StartsWith("effects/", StringComparison.Ordinal)))
            {
                System.IO.Directory.CreateDirectory(target);
                foreach (var file in WorkshopPackageArchive.Files(source).Where(f => !f.Replace('\\', '/').StartsWith("effects/", StringComparison.Ordinal)))
                {
                    var destination = WorkshopPackageArchive.Child(target, file);
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(WorkshopPackageArchive.Child(source, file), destination, true);
                }
            }
            else WorkshopPackageArchive.Copy(source, target);
            if (!roots.TryAdd(id, target)) throw new InvalidDataException("重复卡包：" + id);
        }
        // IDs depend on the authored identity, never on machine paths or connection order.
        string Package(DiyCardDraft c) => c.PackId.StartsWith("pack/", StringComparison.Ordinal) ? c.PackId[5..] : "workshop.loose";
        var ids = drafts.ToDictionary(c => c.RuntimeId, c => c.SourceCardId.Length > 0 ? c.SourceCardId : "pack/" + Package(c) + "/card." + PackPaths.Key(c.Id)[..32].ToLowerInvariant(), StringComparer.Ordinal);
        if (deck.FirstOrDefault(id => id.StartsWith("diy/", StringComparison.Ordinal) && !ids.ContainsKey(id)) is { } missing)
            throw new InvalidDataException("找不到所选 DIY 卡牌的可执行草稿：" + missing);
        var selected = drafts.Where(c => deck.Contains(c.RuntimeId)).Select(Package).ToHashSet(StringComparer.Ordinal);
        // Bring the complete selected family: expansions inherit vocabulary from the base pack.
        foreach (var c in drafts)
            if (selected.Contains(Package(c))) selected.Add(Kards.Ui.Core.DeckCardPacks.Family(c.PackId).Replace("pack/", ""));
        foreach (var c in drafts)
            if (selected.Contains(Kards.Ui.Core.DeckCardPacks.Family(c.PackId).Replace("pack/", ""))) selected.Add(Package(c));
        var chosen = drafts.Where(c => selected.Contains(Package(c))).ToArray();
        foreach (var group in chosen.GroupBy(Package))
        {
            var id = group.Key;
            if (!roots.TryGetValue(id, out var root))
            {
                root = Path.Combine(directory, PackPaths.Key(id)); System.IO.Directory.CreateDirectory(root); roots.Add(id, root);
                var manifest = new JsonObject { ["schemaVersion"] = 2, ["id"] = id, ["kind"] = "cardpack",
                    ["guid"] = StableGuid("package/" + id), ["version"] = "1.0.0", ["name"] = WorkshopAuthoring.PackNames.GetValueOrDefault("pack/" + id, "工坊自建卡包"), ["metadata"] = new JsonArray() };
                Save(root, "manifest.json", manifest);
            }
            var packageManifest = Read(root, "manifest.json");
            var metadata = packageManifest["metadata"]!.AsArray();
            var formalLock = ReadOptional(root, "lock.formal.json");
            if (formalLock.Count == 0) { formalLock["schemaVersion"] = 1; formalLock["packageId"] = id; formalLock["domain"] = "formal"; formalLock["assets"] = new JsonArray(); }
            var texts = ReadOptional(root, "display-texts.json");
            var plans = ReadOptional(root, "desktop-effects.json");
            var presentation = ReadOptional(root, "workshop.presentation.json");
            if (presentation["cards"] is not JsonObject) presentation["cards"] = new JsonObject();
            var nations = (presentation["nations"] as JsonArray ?? new()).OfType<JsonObject>()
                .ToDictionary(n => n["id"]!.ToString(), n => n.DeepClone(), StringComparer.Ordinal);
            foreach (var n in WorkshopAuthoring.NetworkNations(group.First().PackId)) nations[n.Id] = new JsonObject { ["id"] = n.Id, ["name"] = n.Name };
            presentation["nations"] = new JsonArray(nations.Values.ToArray());
            var family = Kards.Ui.Core.DeckCardPacks.Family("pack/" + id);
            if (family != "pack/" + id) presentation["basePackId"] = family.StartsWith("pack/") ? family[5..] : family;
            // 阵营声明随包落盘（factions.json）：卡定义的 faction 是引擎**进程级值域**的硬校验项。
            // 只在宿主进程里 RegisterCustom 是不够的——桌面联机的服务端是独立子进程，
            // 那边没有这些阵营，于是本包每张卡都在装载期被判失败（卡池里一张都没有，读面还报"一切正常"）。
            // 清单取三处并集：① 游戏自带的独立国家（av76/deran/usg/un/nsf，不是引擎常量表里的）；
            // ② 工坊自建国家；③ 这批卡实际用到的国家（作者改了国家但没进声明时兜底）。
            var declared = Kards.Ui.Core.DeckNations.CustomNations
                .Select(Kards.Ui.Core.DeckNations.Canonical)
                .Concat(WorkshopAuthoring.NetworkNations(group.First().PackId).Select(n => n.Id))
                .Concat(family != "pack/" + id ? WorkshopAuthoring.NetworkNations(family).Select(n => n.Id) : [])
                .Concat(group.Select(draft => Kards.Ui.Core.DeckNations.Canonical(draft.Nation)))
                .Where(nation => nation.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(nation => nation, StringComparer.Ordinal)
                .ToArray();
            Save(root, "factions.json", new JsonObject {
                ["schemaVersion"] = 1,
                ["factions"] = new JsonArray(declared
                    .Select(n => (JsonNode)new JsonObject { ["id"] = n, ["major"] = true }).ToArray()),
            });
            // 词条名字：规则在 network-vocabulary.json（只有引擎 id），名字只能进展示层。
            // 少了这一段，收包人的卡面把自定义词条显示成 "◇ kw-76e1c7658f9a…"。
            var keywordNames = (presentation["keywords"] as JsonArray ?? new()).OfType<JsonObject>()
                .ToDictionary(k => k["id"]!.ToString(), k => k.DeepClone(), StringComparer.Ordinal);
            foreach (var name in WorkshopAuthoring.NetworkKeywordNames(new[] { group.First().PackId, family }))
                keywordNames[name!["id"]!.ToString()] = name.DeepClone();
            if (keywordNames.Count > 0) presentation["keywords"] = new JsonArray(keywordNames.Values.ToArray());
            foreach (var draft in group)
            {
                var validation = DiyCardCompiler.Validate(draft, catalog);
                if (!validation.Success)
                {
                    if (deck.Contains(draft.RuntimeId)) throw new InvalidDataException(draft.Name + "：" + string.Join("；", validation.Errors));
                    continue; // Unfinished cards remain drafts.
                }
                var compiled = DiyCardCompiler.Compile(draft.Snapshot(), catalog);
                var mapped = ids[draft.RuntimeId]; var localId = mapped[(id.Length + 6)..];
                var relative = "cards/" + localId + ".card.json";
                if (draft.SourceCardId.Length > 0)
                {
                    var meta = metadata.Select(n => n!.ToString()).Select(path => Read(root, path))
                        .FirstOrDefault(m => (m["assets"] as JsonArray)?.Any(a => a?["kind"]?.ToString() == "card" && a?["id"]?.ToString() == localId) == true)
                        ?? throw new InvalidDataException("找不到被修改卡牌的原生元数据：" + draft.SourceCardId);
                    relative = meta["source"]!.ToString();
                }
                else
                {
                    var metaPath = relative + ".meta.json";
                    var assetGuid = AssetIdentity.DeriveAssetGuid(Guid.Parse(packageManifest["guid"]!.ToString()), AssetKind.Card, localId, new AssetVersion(1, 0, 0)).ToString();
                    metadata.Add(metaPath);
                    formalLock["assets"]!.AsArray().Add(new JsonObject { ["guid"] = assetGuid, ["kind"] = "card", ["id"] = mapped, ["version"] = "1.0.0" });
                    Save(root, metaPath, new JsonObject { ["schemaVersion"] = 1, ["packageId"] = id, ["source"] = relative,
                        ["assets"] = new JsonArray(new JsonObject { ["guid"] = assetGuid, ["kind"] = "card", ["id"] = localId, ["version"] = "1.0.0", ["dependsOn"] = new JsonArray(), ["conflictsWith"] = new JsonArray() }) });
                }
                var card = JsonNode.Parse(compiled.CardJson)!;
                ReplaceReferences(card, ids); card["id"] = localId; Save(root, relative, card);
                texts[localId] = compiled.Card.Text;
                // 结构化单目标效果的声明图是编译期现生成的：导出的 desktop-effects.json 里必须
                // 带着同一份文本，否则对方装包后预打出捕获找不到声明——他的客户端不会问，
                // 引擎退回无头选靶，玩家看到的就是"我这张卡只打第一个"。
                var graphs = draft.OrcEffects.Where(e => e.DesktopJson is not null).Select(e => e.DesktopJson!)
                    .Concat(OrcStructuredGraph.Graphs(draft.OrcEffects))
                    .Concat(WorkshopAuthoring.KeywordGraphs(draft)).ToArray();
                if (graphs.Length > 0) { var graphArray = JsonSerializer.SerializeToNode(graphs)!; ReplaceReferences(graphArray, ids); plans[localId] = graphArray; }
                var entry = (presentation["cards"]![localId] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();
                entry["flavor"] = draft.Flavor;
                if (draft.ArtFile.Length > 0 && artDirectory is not null)
                {
                    var source = PackPaths.Child(artDirectory, draft.ArtFile);
                    if (!File.Exists(source)) throw new InvalidDataException("缺少自建卡图：" + draft.Name);
                    var art = "art/" + PackPaths.Hash(File.ReadAllBytes(source)) + ".png";
                    System.IO.Directory.CreateDirectory(Path.Combine(root, "art")); File.Copy(source, PackPaths.Child(root, art), true);
                    entry["art"] = art; entry["artKind"] = draft.ArtImportMode == "full" ? "full" : "illustration";
                }
                if (draft.AlternateArt is { } skin)
                {
                    skin.Checked();
                    if (artDirectory is null) throw new InvalidDataException("缺少完整异画：" + draft.Name);
                    var source = PackPaths.Child(artDirectory, skin.Art);
                    if (!File.Exists(source)) throw new InvalidDataException("缺少完整异画：" + draft.Name);
                    var relativeSkin = "art/alt-" + PackPaths.Hash(File.ReadAllBytes(source)) + CardSkinFile.ExtensionOf(skin.Art);
                    System.IO.Directory.CreateDirectory(Path.Combine(root, "art")); File.Copy(source, PackPaths.Child(root, relativeSkin), true);
                    entry["alternateArt"] = JsonSerializer.SerializeToNode(skin with { Art = relativeSkin });
                }
                presentation["cards"]![localId] = entry;
            }
            Save(root, "manifest.json", packageManifest); Save(root, "display-texts.json", texts);
            Save(root, "lock.formal.json", formalLock);
            Save(root, "desktop-effects.json", plans); Save(root, "workshop.presentation.json", presentation);
            // One owner per vocabulary, including the base of an expansion.
            var vocabularyPacks = new[] { group.First().PackId, family }.Distinct().ToArray();
            var vocabulary = WorkshopAuthoring.NetworkVocabulary(vocabularyPacks); ReplaceReferences(vocabulary, ids);
            Save(root, "network-vocabulary.json", vocabulary);
        }
        // A modified package must have a content version that changes with its payload.
        foreach (var group in chosen.GroupBy(Package))
        {
            var root = roots[group.Key]; var manifest = Read(root, "manifest.json");
            manifest["version"] = PackPaths.Digest(root)[..32]; Save(root, "manifest.json", manifest);
        }
        return new() { Directory = directory, Roots = roots.Values.ToArray(), CardIds = ids };
    }
    private static string StableGuid(string id) => new Guid(Convert.FromHexString(PackPaths.Key(id)[..32])).ToString();
    private static void CleanupExpired(string sessionRoot)
    {
        if (!System.IO.Directory.Exists(sessionRoot)) return;
        var parent = Path.GetFullPath(sessionRoot) + Path.DirectorySeparatorChar;
        foreach (var candidate in System.IO.Directory.GetDirectories(sessionRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(candidate), "N", out _) || !Path.GetFullPath(candidate).StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0 || System.IO.Directory.GetCreationTimeUtc(candidate) > DateTime.UtcNow.AddHours(-24)) continue;
            try { _ = PackPaths.Files(candidate).Count(); System.IO.Directory.Delete(candidate, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { } catch (InvalidDataException) { }
        }
    }
    private static JsonObject Read(string root, string path) => JsonNode.Parse(File.ReadAllText(PackPaths.Child(root, path)))!.AsObject();
    private static JsonObject ReadOptional(string root, string path) => File.Exists(Path.Combine(root, path)) ? Read(root, path) : new();
    private static void Save(string root, string path, JsonNode value)
    {
        var target = PackPaths.Child(root, path); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, value.ToJsonString());
    }
    private static void ReplaceReferences(JsonNode node, IReadOnlyDictionary<string, string> ids)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
            { if (pair.Value is JsonValue value && value.TryGetValue<string>(out var s)) obj[pair.Key] = ReplaceString(s, ids); else if (pair.Value is not null) ReplaceReferences(pair.Value, ids); }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
            { if (array[i] is JsonValue value && value.TryGetValue<string>(out var s)) array[i] = ReplaceString(s, ids); else if (array[i] is { } child) ReplaceReferences(child, ids); }
    }
    private static string ReplaceString(string value, IReadOnlyDictionary<string, string> ids)
    {
        if (ids.TryGetValue(value, out var mapped)) return mapped;
        if (value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('['))
            try { var nested = JsonNode.Parse(value)!; ReplaceReferences(nested, ids); return nested.ToJsonString(); } catch (JsonException) { }
        // Compiler-generated C# uses quoted card IDs. Replace complete string literals only.
        foreach (var pair in ids) value = value.Replace(JsonSerializer.Serialize(pair.Key), JsonSerializer.Serialize(pair.Value), StringComparison.Ordinal);
        return value;
    }
}
