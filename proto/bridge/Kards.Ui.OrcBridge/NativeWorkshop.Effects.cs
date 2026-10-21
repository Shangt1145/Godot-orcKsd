using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Orc.Core;

namespace Kards.Ui.OrcBridge;

public sealed record WorkshopEffect(NativeWorkshopAsset Asset, string Name, string Description, string[] Occasions, OrcEffectDraft[]? Rules);

public sealed partial class NativeWorkshop
{
    public IReadOnlyList<WorkshopEffect> Effects() => Assets().Where(a => a.Kind == "Effect").Select((asset, index) =>
    {
        var root = JsonNode.Parse(Read(asset.Path))?["root"];
        var record = (Presentation()["effectAuthoring"] as JsonObject)?[asset.Id] as JsonObject;
        var label = root?["mainTrigger"]?["stableKey"]?.ToString();
        var name = record?["name"]?.ToString() ?? (string.IsNullOrWhiteSpace(label) ? $"效果 {index + 1}" : label.Split('·')[0]);
        var hooks = (root?["mainTrigger"]?["hooks"] as JsonArray ?? new()).Select(n => Occasion(n?.ToString() ?? "")).Distinct().ToArray();
        return new WorkshopEffect(asset, name, record?["description"]?.ToString() ?? label?.Split('·', 2).ElementAtOrDefault(1) ?? "", hooks,
            record?["rules"] is JsonArray rules ? JsonSerializer.Deserialize<OrcEffectDraft[]>(rules.ToJsonString()) : null);
    }).ToArray();

    public void SaveEffectInfo(string id, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new InvalidDataException("效果名称需要 1–80 个字符。");
        _ = Editor.GetAsset(AssetKind.Effect, id);
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
}
