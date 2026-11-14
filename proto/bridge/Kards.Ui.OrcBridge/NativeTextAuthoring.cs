using System.Text.Json.Nodes;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Data.Components;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

public sealed record NativeTextAuthoringResult(string? CardJson, IReadOnlyList<string> Problems)
{
    public bool Success => CardJson is not null;
}

public static class NativeTextAuthoring
{
    /// <summary>Complete text and native compilation must succeed before the atomic card save starts.</summary>
    public static NativeTextAuthoringResult Apply(NativeWorkshop workshop, NativeWorkshopAsset asset,
        string cardJson, string text, string flavor, bool append, IReadOnlyList<UiCardDefinition> catalog)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return new(null, ["请先输入卡牌效果文字。"]);
            var document = new NativeCardDocument(cardJson);
            var parsed = OrcInspector.CompileText(asset.AppId, text, document.CardType, catalog);
            if (!parsed.ParseComplete || !parsed.AllCompiled) return new(null, parsed.Problems);
            var incoming = parsed.Parse.Effects.Select(OrcEffectEditing.ToDraft).ToArray();
            var previous = workshop.CardPresentation(asset.Id).EditableRules;
            var rules = append ? previous.Concat(incoming).ToArray() : incoming;

            // Use the engine's own keyword writer, including quantified declarations. Keep opaque attributes.
            if (parsed.Parse.Keywords.Count > 0)
            {
                var declarations = parsed.Parse.Keywords.Select(k => new KeywordDeclaration(k.Id, k.Quantified ? k.Value : null)).ToArray();
                var faction = Faction.TryNormalize(document.String("factionCost", "faction"), out var normalized) ? normalized : Faction.Germany;
                var definition = new CardDefinition("解析词条", 0, 0, 1, 1, keywords: declarations, unitTypes: [UnitType.Infantry], faction: faction, rarity: Rarity.Standard);
                var encoded = CardDataJson.Serialize(new CardDataBody("parsed", "解析词条", definition.Components), out var warnings);
                if (warnings.Count > 0) return new(null, warnings);
                var component = JsonNode.Parse(encoded)!["components"]!.AsArray().OfType<JsonObject>().Single(c => c["component"]?.ToString() == "keywords");
                var attributes = (document.Component("keywords")?["attributes"] as JsonArray ?? new()).Select(a => a!.ToString()).ToList();
                var changedIds = declarations.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
                attributes.RemoveAll(attribute =>
                {
                    using var json = System.Text.Json.JsonDocument.Parse(new JsonObject { ["component"] = "keywords", ["attributes"] = new JsonArray(attribute) }.ToJsonString());
                    return KeywordsDefinition.Read(json.RootElement).Keywords.Any(k => changedIds.Contains(k.Id));
                });
                attributes.AddRange(component["attributes"]!.AsArray().Select(a => a!.ToString()));
                document.Set("keywords", "attributes", new JsonArray(attributes.Distinct(StringComparer.Ordinal).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()));
            }
            workshop.SaveCardDocument(asset, document.Json, text, flavor, rules);
            return new(workshop.Read(asset.Path), []);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return new(null, [error.Message]); }
    }
}
