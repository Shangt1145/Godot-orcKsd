using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Orc.Game;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

/// <summary>Explicitly reviewed source declarations. Structural compilation alone is never admission.</summary>
public static class VerifiedCardPool
{
    public const string ReviewVersion = "6B1-2026-10-07";
    public const int ValidationDeckSize = 24; // Nine complete definitions cannot fill the normal 30-card policy.
    private sealed record Review(string CardId, string Sha256);
    private static readonly IReadOnlyDictionary<string, string> Reviews = LoadReviews();

    private static IReadOnlyDictionary<string, string> LoadReviews()
    {
        using var stream = typeof(VerifiedCardPool).Assembly.GetManifestResourceStream("Kards.Ui.OrcBridge.verified-cards.json")
            ?? throw new InvalidDataException("Missing reviewed card manifest.");
        return JsonSerializer.Deserialize<Review[]>(stream)!.ToDictionary(r => r.CardId, r => r.Sha256, StringComparer.Ordinal);
    }

    private static string DefinitionSignature(UiCardDefinition c) => JsonSerializer.Serialize(new
    {
        c.CardId, c.Name, c.NameEn, c.CardType, c.UnitType, c.Set, c.Rarity, c.Text,
        c.Cost, c.BaseAttack, c.BaseDefense, c.BaseOpCost, c.IsToken, c.HasVariableStats,
        c.Keywords, Values = c.KeywordValues.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray()
    });

    public static (IReadOnlyList<CardDefinitionEntry> Entries, UiCardPoolReport Report) Compile(
        IReadOnlyList<UiCardDefinition> cards, string sourceDirectory)
    {
        // Source declarations remain in the import layer, not in UI definitions or runtime effects.
        var sourceCatalog = new CardCatalog(); sourceCatalog.Load(sourceDirectory);
        var definitions = sourceCatalog.Cards.ToDictionary(c => c.CardId, StringComparer.Ordinal);
        var sources = new Dictionary<string, (string Hash, bool Effects)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("cards", out var entries)) continue;
            foreach (var entry in entries.EnumerateObject())
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Value.GetRawText())));
                var effects = !entry.Value.TryGetProperty("effects", out var effectsValue)
                    || effectsValue.ValueKind != JsonValueKind.Array || effectsValue.GetArrayLength() != 0;
                if (!sources.TryAdd(entry.Name, (hash, effects))) throw new InvalidDataException($"Duplicate source card: {entry.Name}");
            }
        }
        var accepted = new List<CardDefinitionEntry>(); var rows = new List<UiCardPoolRow>(); var structural = 0;
        foreach (var card in cards.OrderBy(c => c.CardId, StringComparer.Ordinal))
        {
            var representable = CardPoolCompiler.TryCompile(card, out var compiled, out var reason);
            if (representable) structural++;
            var support = UiCardSupport.Unreviewed;
            if (card.IsToken) { support = UiCardSupport.GeneratedOnly; reason = "Generated cards cannot enter normal decks."; }
            else if (!representable) { support = UiCardSupport.Unsupported; }
            else if (!sources.TryGetValue(card.CardId, out var source) || !definitions.TryGetValue(card.CardId, out var original))
            { support = UiCardSupport.SourceChanged; reason = "Source declaration is missing."; }
            else if (DefinitionSignature(card) != DefinitionSignature(original))
            { support = UiCardSupport.SourceChanged; reason = "UI definition differs from its source declaration."; }
            else if (source.Effects) { support = UiCardSupport.Unsupported; reason = "Effect declarations are not assembled in this stage."; }
            else if (card.CardType != "unit" || card.HasVariableStats || card.Cost is null or < 0
                || card.BaseAttack is null or < 0 || card.BaseDefense is null or <= 0 || card.BaseOpCost is null or < 0
                || !Enum.TryParse<UnitType>(card.UnitType, true, out var unitType) || !Enum.IsDefined(unitType))
            { support = UiCardSupport.Unsupported; reason = "Incomplete stats, variable stats, category or unit type."; }
            else if (!Reviews.TryGetValue(card.CardId, out var reviewedHash))
            { reason = "Not in the reviewed semantic support set."; }
            else if (reviewedHash != source.Hash)
            { support = UiCardSupport.SourceChanged; reason = "Reviewed source changed; review it before admission."; }
            else { support = UiCardSupport.Verified; reason = "Reviewed base stats and supported engine keywords; no scripted effects."; accepted.Add(compiled!); }
            rows.Add(new(card.CardId, card.Name, support, reason!));
        }
        return (accepted.AsReadOnly(), new(ReviewVersion, cards.Count, structural, rows.AsReadOnly()));
    }
}
