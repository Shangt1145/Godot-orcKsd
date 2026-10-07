using Kards.Ui.Contracts;
using Orc.Game;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

/// <summary>Why a catalog card could not become an engine definition.</summary>
public sealed record CardPoolRejection(string CardId, string Reason);

/// <summary>
/// Turns the UI's card catalog into engine definitions so a real match can be dealt from it.
///
/// Two vocabularies meet here and they are not the same. The catalog speaks the game's names
/// (iron/bronze/silver/gold, nation folders); the engine speaks its own enums
/// (Standard/Limited/Special/Elite, Germany/Soviet/...). A card is skipped rather than guessed at:
/// a wrongly mapped faction would silently change how a card behaves in play, which is worse than
/// it not being offered.
/// </summary>
public static class CardPoolCompiler
{
    /// <summary>
    /// Compiles every catalog card the engine can represent. Cards with an unmapped faction or
    /// category are reported instead of thrown, so one odd card cannot cost us the whole pool.
    /// </summary>
    public static (IReadOnlyList<CardDefinitionEntry> Entries, IReadOnlyList<CardPoolRejection> Rejected)
        Compile(IReadOnlyList<UiCardDefinition> catalog)
    {
        var entries = new List<CardDefinitionEntry>();
        var rejected = new List<CardPoolRejection>();
        foreach (var card in catalog)
        {
            if (TryCompile(card, out var entry, out var reason)) entries.Add(entry!);
            else rejected.Add(new CardPoolRejection(card.CardId, reason!));
        }
        return (entries, rejected);
    }

    public static bool TryCompile(UiCardDefinition card, out CardDefinitionEntry? entry, out string? reason)
    {
        entry = null; reason = null;
        if (!TryCategory(card.CardType, out var category))
        {
            reason = $"unknown card type '{card.CardType}'";
            return false;
        }
        if (!TryFaction(card.Set, out var faction))
        {
            // 'misc', '天气' and the mode folders are not nations; the engine has no home for them yet.
            reason = $"set '{card.Set}' is not a faction";
            return false;
        }
        // A card the engine cannot fully represent is rejected, not trimmed. Dropping an unknown
        // keyword would hand the engine a card that behaves differently from the printed one.
        var keywords = Keywords(card, out var unknownKeywords).ToArray();
        if (unknownKeywords.Count > 0)
        {
            reason = $"keywords the engine does not implement: {string.Join(", ", unknownKeywords)}";
            return false;
        }
        // Orders have no stats. The engine's simplified constructor still wants numbers, so zero
        // stands in; it never reads them for a non-unit category.
        var definition = new CardDefinition(
            card.Name.Length > 0 ? card.Name : card.CardId,
            deployCost: card.Cost ?? 0,
            operateCost: card.BaseOpCost ?? 0,
            attack: card.BaseAttack ?? 0,
            defense: card.BaseDefense ?? 0,
            category: category,
            keywords: keywords,
            unitTypes: UnitTypes(card),
            faction: faction,
            rarity: RarityOf(card),
            tags: card.IsToken ? ["token"] : null);
        entry = new CardDefinitionEntry(card.CardId, definition);
        return true;
    }

    /// <summary>
    /// The catalog speaks English keyword ids; the engine registers Chinese ones and rejects
    /// anything else fail-fast. Only ids with a real counterpart are mapped — the rest are reported
    /// so a card is never handed over with a keyword silently dropped, which would change how it
    /// plays. Magnitude variants (armor1/armor2) collapse onto the base keyword with their number
    /// as the declared value.
    /// </summary>
    private static readonly Dictionary<string, string> KeywordMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blitz"] = KeywordIds.Blitz,
        ["闪击"] = KeywordIds.Blitz,
        ["fury"] = KeywordIds.Fury,
        ["smokescreen"] = KeywordIds.SmokeScreen,
        ["ambush"] = KeywordIds.Ambush,
        ["mobilize"] = KeywordIds.Mobilize,
        ["pincer"] = KeywordIds.Pincer,
        ["armor"] = KeywordIds.Armor,
        ["重甲"] = KeywordIds.Armor,
    };

    /// <summary>
    /// Returns the engine keyword for a catalog id, or null when the engine has no counterpart.
    /// Numeric suffixes carry the magnitude: armor2 is the armor keyword with value 2.
    /// </summary>
    private static KeywordDeclaration? MapKeyword(string id, int? value)
    {
        if (KeywordMap.TryGetValue(id, out var mapped)) return new KeywordDeclaration(mapped, value);
        // armor1 / lightArmor2 / charge4: a base name plus a magnitude the engine declares as a value.
        var trimmed = id.TrimEnd("0123456789".ToCharArray());
        if (trimmed.Length > 0 && trimmed.Length != id.Length && KeywordMap.TryGetValue(trimmed, out var based))
        {
            var magnitude = value ?? int.Parse(id[(trimmed.Length)..], System.Globalization.CultureInfo.InvariantCulture);
            return new KeywordDeclaration(based, magnitude);
        }
        return null;
    }

    private static IEnumerable<KeywordDeclaration> Keywords(UiCardDefinition card, out List<string> unknown)
    {
        var declared = new List<KeywordDeclaration>();
        unknown = [];
        // The engine rejects a repeated keyword outright, and magnitude variants (armor, armor2)
        // collapse onto the same id. Keep the strongest: a card printed with armour 2 must not
        // silently become armour 1.
        var strongest = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var keyword in card.Keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword)) continue;
            // A keyword may carry a magnitude in the catalog's own value table.
            var value = card.KeywordValues.TryGetValue(keyword, out var n) ? n : (int?)null;
            var mapped = MapKeyword(keyword, value);
            if (mapped is null) { unknown.Add(keyword); continue; }
            var magnitude = mapped.Value.Value ?? 0;
            if (!strongest.TryGetValue(mapped.Value.Id, out var current) || magnitude > current)
                strongest[mapped.Value.Id] = magnitude;
        }
        foreach (var (id, magnitude) in strongest)
            declared.Add(new KeywordDeclaration(id, magnitude == 0 ? null : magnitude));
        return declared;
    }

    private static IEnumerable<UnitType>? UnitTypes(UiCardDefinition card)
    {
        if (string.IsNullOrWhiteSpace(card.UnitType)) return null;
        return Enum.TryParse<UnitType>(card.UnitType, ignoreCase: true, out var unit) ? [unit] : null;
    }

    private static bool TryCategory(string? cardType, out CardCategory category)
    {
        switch (cardType)
        {
            case "unit": category = CardCategory.Unit; return true;
            case "order": category = CardCategory.Command; return true;
            case "counter": category = CardCategory.Counter; return true;
            default: category = default; return false;
        }
    }

    /// <summary>
    /// The catalog's sets are nations; the engine's faction enum is a different axis. Only the four
    /// engine factions have a mapping, keyed by the two catalog sets that clearly mean each.
    /// </summary>
    private static bool TryFaction(string? set, out Faction faction)
    {
        switch (set?.ToLowerInvariant())
        {
            case "deran": faction = Faction.Germany; return true;
            case "av76": faction = Faction.Soviet; return true;
            case "usg": faction = Faction.USA; return true;
            case "un": faction = Faction.Britain; return true;
            default: faction = default; return false;
        }
    }

    /// <summary>
    /// The two rarity vocabularies are unrelated (bronze/silver/gold vs Standard/Limited/Elite),
    /// so no card is promoted to a special rarity on a guess. Everything lands on Standard unless
    /// the catalog already uses the engine's own words.
    /// </summary>
    private static Rarity RarityOf(UiCardDefinition card) =>
        Enum.TryParse<Rarity>(card.Rarity, ignoreCase: true, out var rarity) ? rarity : Rarity.Standard;
}
