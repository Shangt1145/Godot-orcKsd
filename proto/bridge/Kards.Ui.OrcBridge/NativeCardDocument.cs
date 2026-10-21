using System.Text.Json.Nodes;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.UI;

namespace Kards.Ui.OrcBridge;

/// <summary>Lossless authoring document. Only explicitly edited fields are patched; unknown data remains in place.</summary>
public sealed class NativeCardDocument
{
    private readonly List<string> _history = [];
    private int _position;
    public JsonObject Root { get; private set; }
    public string Id => Root["id"]?.ToString() ?? "";
    public string Name => Root["name"]?.ToString() ?? "";
    public bool CanUndo => _position > 0;
    public bool CanRedo => _position + 1 < _history.Count;
    public NativeCardDocument(string json) { Root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("原生卡牌声明为空。"); _history.Add(Root.ToJsonString()); }
    public string Json => Root.ToJsonString();
    public JsonObject? Component(string id) => (Root["components"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c => c["component"]?.ToString() == id);
    public string String(string component, string field, string fallback = "") => Component(component)?[field]?.ToString() ?? fallback;
    public int Number(string component, string field, int fallback = 0) => int.TryParse(String(component, field), out var number) ? number : fallback;
    public IReadOnlyList<string> Types => (Component("typeCategory")?["type"] as JsonArray)?.Select(n => n?.ToString() ?? "").ToArray() ?? [];
    public string CardType => Types.Contains("countermeasure") || Types.Contains("counter") ? "counter" : Types.Contains("order") ? "order" : Types.Any(t => UnitType.All.Contains(t)) ? "unit" : "unknown";
    public string Unit => Types.FirstOrDefault(t => UnitType.All.Contains(t)) ?? "infantry";
    public void Rename(string name) => Change(() => Root["name"] = name);
    public void Set(string component, string field, JsonNode? value) => Change(() => EnsureComponent(component)[field] = value?.DeepClone());
    public void SetType(string cardType, string unit)
    {
        Change(() =>
        {
            var retained = Types.Where(t => t is not "order" and not "counter" and not "countermeasure" && !UnitType.All.Contains(t)).ToList();
            retained.Insert(0, cardType == "unit" ? unit : cardType == "counter" ? "countermeasure" : cardType);
            EnsureComponent("typeCategory")["type"] = new JsonArray(retained.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
            if (cardType == "unit" && Component("battleStats") is null)
                EnsureComponent("battleStats").Add("defense", 1);
        });
    }
    public void Undo() { if (CanUndo) Root = JsonNode.Parse(_history[--_position])!.AsObject(); }
    public void Redo() { if (CanRedo) Root = JsonNode.Parse(_history[++_position])!.AsObject(); }
    private JsonObject EnsureComponent(string id)
    {
        if (Component(id) is { } existing) return existing;
        if (Root["components"] is not JsonArray components) { components = new(); Root["components"] = components; }
        var created = new JsonObject { ["component"] = id }; components.Add(created); return created;
    }
    private void Change(Action mutation)
    {
        var before = Json; mutation(); var after = Json; if (before == after) return;
        _history.RemoveRange(_position + 1, _history.Count - _position - 1); _history.Add(after);
        if (_history.Count > 80) _history.RemoveAt(0); _position = _history.Count - 1;
    }
    public (Orc.UI.Contracts.UiCardDefinition? Card, IReadOnlyList<string> Diagnostics) Preview(string appId, string artPath = "", string text = "", string flavor = "")
    {
        try
        {
            if (!CardDataJson.TryDeserialize(Json, out var body, out var warnings, out var error)) return (null, new[] { error ?? "无法解析卡牌" });
            return (StaticCardProjection.Project(appId, new CardDefinition(body!.Name, body.Components), _ => artPath, text: text) with { Flavor = flavor }, warnings.ToArray());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, new[] { ex.Message }); }
    }
}
