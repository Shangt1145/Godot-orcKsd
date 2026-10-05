using System.Collections.ObjectModel;
using System.Text.Json;
using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

public sealed class CardCatalog
{
    public IReadOnlyList<UiCardDefinition> Cards { get; private set; } = Array.Empty<UiCardDefinition>();
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

    public void Load(string directory, Func<string, string>? resolveArt = null)
    {
        var cards = new Dictionary<string, UiCardDefinition>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("cards", out var entries))
                continue; // _meta.json
            if (entries.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"cards must be an object: {file}");
            foreach (var entry in entries.EnumerateObject())
            {
                var e = entry.Value;
                var keywords = e.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.Array
                    ? kw.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
                var values = new Dictionary<string, int>();
                if (e.TryGetProperty("kwValues", out var kv) && kv.ValueKind == JsonValueKind.Object)
                    foreach (var pair in kv.EnumerateObject())
                        if (pair.Value.ValueKind == JsonValueKind.Number && pair.Value.TryGetInt32(out var n))
                            values[pair.Name] = n;
                var rawArt = String(e, "art");
                var card = new UiCardDefinition
                {
                    CardId = entry.Name,
                    Name = String(e, "name", entry.Name),
                    NameEn = String(e, "nameEn"),
                    CardType = String(e, "cardType", "unknown"),
                    UnitType = String(e, "unitType"),
                    Set = String(e, "set", String(doc.RootElement, "nation")),
                    Rarity = String(e, "rarity", "common"),
                    ArtPath = resolveArt?.Invoke(rawArt) ?? rawArt,
                    Text = String(e, "text"),
                    Flavor = String(e, "flavor"),
                    Cost = Number(e, "cost"),
                    BaseAttack = Number(e, "attack"),
                    BaseDefense = Number(e, "defense"),
                    BaseOpCost = Number(e, "opCost"),
                    IsToken = Bool(e, "token"),
                    HasVariableStats = Bool(e, "variableStats"),
                    Keywords = Array.AsReadOnly(keywords),
                    KeywordValues = new ReadOnlyDictionary<string, int>(values)
                };
                if (!cards.TryAdd(entry.Name, card))
                    throw new InvalidDataException($"Duplicate card ID: {entry.Name}");
                if (card.CardType == "unknown")
                    warnings.Add($"{entry.Name}: missing cardType; retained as unknown");
            }
        }
        Cards = Array.AsReadOnly(cards.Values.OrderBy(x => x.Set, StringComparer.Ordinal).ThenBy(x => x.Cost).ThenBy(x => x.CardId, StringComparer.Ordinal).ToArray());
        Warnings = warnings.AsReadOnly();
    }

    public IEnumerable<UiCardDefinition> Filter(string search = "", string set = "", string type = "", string unitType = "", string rarity = "", string keyword = "") =>
        Cards.Where(c => (set.Length == 0 || c.Set == set) && (type.Length == 0 || c.CardType == type)
            && (unitType.Length == 0 || c.UnitType == unitType) && (rarity.Length == 0 || c.Rarity == rarity)
            && (keyword.Length == 0 || c.Keywords.Contains(keyword))
            && (search.Length == 0 || $"{c.Name} {c.NameEn} {c.CardId} {c.Text}".Contains(search, StringComparison.OrdinalIgnoreCase)));

    public static string ResolveArtResource(string raw)
    {
        // Published cards use ../<art-root>/... relative to the app/game URL, not nations/.
        var suffix = raw.Replace('\\', '/');
        while (suffix.StartsWith("../", StringComparison.Ordinal))
            suffix = suffix[3..];
        if (suffix.StartsWith('/') || suffix.Contains(':') || suffix.Split('/').Any(p => p is ".." or "."))
            throw new InvalidDataException($"Unsafe art path: {raw}");
        return suffix.Length == 0 ? "" : "res://proto/art/" + suffix;
    }
    private static string String(JsonElement e, string key, string fallback = "") =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
    private static int? Number(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static bool Bool(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
}
