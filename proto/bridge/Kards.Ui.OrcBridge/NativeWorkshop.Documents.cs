using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Orc.Packages;

namespace Kards.Ui.OrcBridge;

public sealed record NativeCardPresentation(string Text, string Flavor, OrcEffectDraft[] EditableRules);

public sealed partial class NativeWorkshop
{
    public void SaveCardDocument(NativeWorkshopAsset asset, string json, string text, string flavor, IReadOnlyList<OrcEffectDraft>? rules = null)
    {
        // Compile before touching disk. Keep the source, sidecar and presentation as one save unit.
        var compiled = rules is null ? json : ComposeRules(json, asset.Id, rules);
        var root = Editor.Session!.TemporaryRoot;
        var before = WorkshopPackageArchive.Files(root).ToDictionary(p => p, p => File.ReadAllBytes(Path.Combine(root, p)));
        try
        {
            SaveCardPresentation(asset.Id, text, flavor);
            if (rules is not null) SaveRuleAuthoring(asset.Id, rules);
            Commit(asset.Path, compiled);
            // 保存后如果这张卡已不合规（不进 Definitions），顺手摘掉它留下的孤儿展示引用。
            // 放在同一个 save unit 里：清理失败会连卡一起回滚，不会留下半套状态。
            PruneOrphanPresentation(asset.Id);
        }
        catch
        {
            foreach (var file in WorkshopPackageArchive.Files(root).Where(p => !before.ContainsKey(p))) File.Delete(Path.Combine(root, file));
            foreach (var entry in before) File.WriteAllBytes(Path.Combine(root, entry.Key), entry.Value);
            CheckpointRecovery();
            throw;
        }
    }
    private JsonObject Presentation()
    {
        var file = Path.Combine(Editor.Session!.TemporaryRoot, "workshop.presentation.json");
        return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))!.AsObject() : new JsonObject { ["schemaVersion"] = 1 };
    }
    private void SavePresentation(JsonObject root)
    { File.WriteAllText(Path.Combine(Editor.Session!.TemporaryRoot, "workshop.presentation.json"), root.ToJsonString(_json)); CheckpointRecovery(); }
    public NativeCardPresentation CardPresentation(string localId)
    {
        var session = Editor.Session!;
        var content = PackageRegistrar.Register(session.TemporaryRoot, Manifest(session.TemporaryRoot), new AssetRegistry()).Content;
        var record = (Presentation()["cards"] as JsonObject)?[localId] as JsonObject;
        var warnings = new List<string>();
        var text = PackPresentationData.Texts(content, warnings).GetValueOrDefault(localId, "");
        CollectPresentationWarnings(warnings);
        return new(text, record?["flavor"]?.ToString() ?? "",
            record?["authoringEffects"] is JsonArray rules ? JsonSerializer.Deserialize<OrcEffectDraft[]>(rules.ToJsonString()) ?? [] : []);
    }
    public void SaveCardPresentation(string localId, string text, string flavor)
    {
        var content = PackageRegistrar.Register(Editor.Session!.TemporaryRoot, Manifest(Editor.Session.TemporaryRoot), new AssetRegistry()).Content;
        var current = PackPresentationData.Texts(content).GetValueOrDefault(localId, "");
        if (text != current)
        {
            if (content.Texts.ContainsKey(localId)) throw new InvalidDataException("该卡的 texts.json 同时用于规则编译，不能仅改显示文本。原声明保留，请在规则编辑中修改。");
            // 卡当前不合法（Definition=null）时不落盘：写下去就是一条孤儿键，
            // 下次 Register 之后会让整包展示层炸掉（2026-10-26 事故）。
            // 草稿语义保留——文本仍可编辑，只是等卡变合法后再写。
            if (!content.Definitions.Any(c => c.Id == content.Prefix + localId))
            {
                WarnPresentation($"「{localId}」当前不合法（不会进入卡池），卡面文本这次没有写入；补好效果后重新保存即可。");
            }
            else
            {
                var path = Path.Combine(Editor.Session.TemporaryRoot, "display-texts.json");
                var texts = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
                texts[localId] = text; File.WriteAllText(path, texts.ToJsonString(_json));
            }
        }
        var presentation = Presentation();
        if (presentation["cards"] is not JsonObject cards) { cards = new(); presentation["cards"] = cards; }
        if (cards[localId] is not JsonObject card) { card = new(); cards[localId] = card; }
        card["flavor"] = flavor; SavePresentation(presentation);
    }

    /// <summary>
    /// 卡在这一步变成字段级非法（`Definition=null`，例如单位改指令却没写 effects）之后，
    /// 它此前写下的卡面文本/选靶声明会变成孤儿键。在同一个保存单元里一并摘掉，
    /// 保证「保存」永远不会给包埋下一条能把整包拖死的脏数据。
    /// </summary>
    private void PruneOrphanPresentation(string localId)
    {
        var root = Editor.Session!.TemporaryRoot;
        var content = PackageRegistrar.Register(root, Manifest(root), new AssetRegistry()).Content;
        if (content.Definitions.Any(c => c.Id == content.Prefix + localId)) return;
        var removed = new List<string>();
        foreach (var name in new[] { "display-texts.json", "desktop-effects.json" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            JsonObject node;
            try { node = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); }
            catch (Exception error) when (error is System.Text.Json.JsonException or IOException)
            {
                WarnPresentation($"{name} 无法解析，未能清理「{localId}」的失效引用：{error.Message}");
                continue;
            }
            if (node.Remove(localId)) { File.WriteAllText(path, node.ToJsonString(_json)); removed.Add(name); }
        }
        WarnPresentation(removed.Count == 0
            ? $"「{localId}」当前不合法（不会进入卡池），请补全效果后再保存。"
            : $"「{localId}」当前不合法（不会进入卡池），已从 {string.Join("、", removed)} 移除其失效引用；补好效果后重新保存文本即可。");
    }

    /// <summary>Compile editable rules with the existing native authoring compiler, keeping imported opaque effects intact.</summary>
    public string ComposeRules(string cardJson, string localId, IReadOnlyList<OrcEffectDraft> drafts)
    {
        var presentation = Presentation();
        var previous = (presentation["cards"] as JsonObject)?[localId] as JsonObject;
        var owned = previous?["authoredPrefabIds"] is JsonArray ids ? ids.Select(v => v!.ToString()).ToHashSet(StringComparer.Ordinal) : [];
        var generated = new JsonArray();
        foreach (var outcome in OrcEffectEditing.Compile("pack." + Editor.Session!.PackageId + "." + localId, drafts))
        {
            if (outcome.Error is not null) throw new InvalidDataException(outcome.Error);
            foreach (var snapshot in outcome.Snapshots) generated.Add(JsonNode.Parse(Orc.Cards.PrefabJson.SerializeForDisk(snapshot)));
        }
        var root = JsonNode.Parse(cardJson)!.AsObject();
        var components = root["components"]!.AsArray();
        var effects = components.OfType<JsonObject>().FirstOrDefault(c => c["component"]?.ToString() == "effects");
        if (effects is null) { effects = new JsonObject { ["component"] = "effects" }; components.Add(effects); }
        var retained = (effects["inline"] as JsonArray ?? new()).Where(p => !owned.Contains(p?["root"]?["id"]?.ToString() ?? "")).Select(p => p?.DeepClone()).ToArray();
        effects["inline"] = new JsonArray(retained.Concat(generated.Select(n => n?.DeepClone())).ToArray());
        return root.ToJsonString();
    }

    public void SaveRuleAuthoring(string localId, IReadOnlyList<OrcEffectDraft> drafts)
    {
        var presentation = Presentation();
        if (presentation["cards"] is not JsonObject cards) { cards = new(); presentation["cards"] = cards; }
        if (cards[localId] is not JsonObject card) { card = new(); cards[localId] = card; }
        var previousGraphs = card["authoringEffects"] is JsonArray previous
            ? (JsonSerializer.Deserialize<OrcEffectDraft[]>(previous.ToJsonString()) ?? []).Where(d => d.DesktopJson is not null).Select(d => d.DesktopJson!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var planPath = Path.Combine(Editor.Session!.TemporaryRoot, "desktop-effects.json");
        var plans = File.Exists(planPath) ? JsonNode.Parse(File.ReadAllText(planPath))!.AsObject() : new JsonObject();
        var retained = (plans[localId] as JsonArray ?? new()).Where(p => !previousGraphs.Contains(p is JsonValue value && value.TryGetValue<string>(out var encoded) ? encoded : p!.ToJsonString())).Select(p => p?.DeepClone()).ToArray();
        var added = drafts.Where(d => d.DesktopJson is not null).Select(d => (JsonNode?)JsonValue.Create(d.DesktopJson!));
        plans[localId] = new JsonArray(retained.Concat(added).ToArray()); File.WriteAllText(planPath, plans.ToJsonString(_json));
        var ids = OrcEffectEditing.Compile("pack." + Editor.Session!.PackageId + "." + localId, drafts).SelectMany(o => o.Snapshots).Select(s => JsonNode.Parse(Orc.Cards.PrefabJson.SerializeForDisk(s))!["root"]!["id"]!.ToString()).ToArray();
        card["authoringEffects"] = JsonSerializer.SerializeToNode(drafts);
        card["authoredPrefabIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        SavePresentation(presentation);
    }
}
