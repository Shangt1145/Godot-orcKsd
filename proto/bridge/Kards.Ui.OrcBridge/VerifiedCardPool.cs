using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Data.Components;

namespace Kards.Ui.OrcBridge;

/// <summary>Explicitly reviewed source declarations. Structural compilation alone is never admission.</summary>
public static class VerifiedCardPool
{
    public const string ReviewVersion = "B1-2026-10-14";
    public const int ValidationDeckSize = 24; // Retained for the 6B1 fixture; production now uses DeckRules.DeckSize.
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

    public static (IReadOnlyList<CardDefinitionEntry> Entries, UiCardPoolReport Report,
        IReadOnlyDictionary<string, JsonElement> Effects) Compile(
        IReadOnlyList<UiCardDefinition> cards, string sourceDirectory)
    {
        var (entries, report, plans, _) = CompileInternal(cards, sourceDirectory);
        return (entries, report, plans);
    }

    /// <summary>同 <see cref="Compile"/>，并额外交出转换卡的原生效果快照——
    /// 对局装配走 <c>CardEffectRegistry</c> 声明 + <c>Prefabs.RegisterPrefab</c>（见 OrcMatchSession），
    /// 定义上的 EffectsDefinition 组件只有磁盘装载路径消费，内存池路径必须显式登记。</summary>
    internal static (IReadOnlyList<CardDefinitionEntry> Entries, UiCardPoolReport Report,
        IReadOnlyDictionary<string, JsonElement> Plans,
        IReadOnlyDictionary<string, EffectSnapshot> ConvertedEffects) CompileInternal(
        IReadOnlyList<UiCardDefinition> cards, string sourceDirectory)
    {
        // Source declarations remain in the import layer, not in UI definitions or runtime effects.
        var sourceCatalog = new CardCatalog(); sourceCatalog.Load(sourceDirectory);
        var definitions = sourceCatalog.Cards.ToDictionary(c => c.CardId, StringComparer.Ordinal);
        var sources = new Dictionary<string, (string Hash, bool Effects, JsonElement Declaration)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("cards", out var entries)) continue;
            foreach (var entry in entries.EnumerateObject())
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Value.GetRawText())));
                var effects = !entry.Value.TryGetProperty("effects", out var effectsValue)
                    || effectsValue.ValueKind != JsonValueKind.Array || effectsValue.GetArrayLength() != 0;
                if (!sources.TryAdd(entry.Name, (hash, effects, entry.Value.Clone()))) throw new InvalidDataException($"Duplicate source card: {entry.Name}");
            }
        }
        var accepted = new List<CardDefinitionEntry>(); var rows = new List<UiCardPoolRow>(); var structural = 0;
        var plans = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var convertedEffects = new Dictionary<string, EffectSnapshot>(StringComparer.Ordinal);
        foreach (var card in cards.OrderBy(c => c.CardId, StringComparer.Ordinal))
        {
            DiyEffectDraft[]? drafts = null;
            var representable = CardPoolCompiler.TryCompile(card, out var compiled, out var reason);
            if (representable) structural++;
            var support = UiCardSupport.Unreviewed;
            if (card.IsToken) { support = UiCardSupport.GeneratedOnly; reason = "Generated cards cannot enter normal decks."; }
            else if (!representable) { support = UiCardSupport.Unsupported; }
            else if (!sources.TryGetValue(card.CardId, out var source) || !definitions.TryGetValue(card.CardId, out var original))
            { support = UiCardSupport.SourceChanged; reason = "Source declaration is missing."; }
            else if (DefinitionSignature(card) != DefinitionSignature(original))
            { support = UiCardSupport.SourceChanged; reason = "UI definition differs from its source declaration."; }
            else if (source.Effects && !SourceEffectAssembly.Supports(card.CardId)
                && !SourceEffectConverter.TryConvert(source.Declaration.GetProperty("effects"), card.CardType,
                    out drafts, out var convertReason))
            {
                // 批次 1：转换器接不下的形状显式拒绝（原因带上为什么）。
                // 转换可行时不进本分支——自然落穿统计/审核门，Verified 时在终点装配。
                support = UiCardSupport.Unsupported;
                reason = "Effect declaration has no reviewed complete engine adapter: " + convertReason;
            }
            else if (card.HasVariableStats || card.Cost is null or < 0
                || (card.CardType == "unit" && (card.BaseAttack is null or < 0 || card.BaseDefense is null or <= 0 || card.BaseOpCost is null or < 0
                || !UnitType.TryNormalize(card.UnitType, out _))))
            { support = UiCardSupport.Unsupported; reason = "Incomplete stats, variable stats, category or unit type."; }
            else if (!Reviews.TryGetValue(card.CardId, out var reviewedHash))
            { reason = "Not in the reviewed semantic support set."; }
            else if (reviewedHash != source.Hash)
            { support = UiCardSupport.SourceChanged; reason = "Reviewed source changed; review it before admission."; }
            else
            {
                support = UiCardSupport.Verified;
                var entry = compiled!;
                if (drafts is not null)
                {
                    (entry, var snapshot) = AssembleConverted(card.CardId, card.CardType, entry, drafts);
                    convertedEffects.Add(card.CardId, snapshot);
                    reason = "Converted non-interactive source effects compiled and instantiated through the engine DSL.";
                }
                else
                    reason = source.Effects ? "Reviewed source effects assembled through engine targeting and runtime." : "Reviewed base stats and engine keywords.";
                accepted.Add(entry);
                // Only hand-reviewed interactive adapters get plans; converted cards run on their
                // own effects component, and adding plans here would fire the effect twice.
                if (source.Effects && SourceEffectAssembly.Supports(card.CardId))
                    plans.Add(card.CardId, source.Declaration.GetProperty("effects").Clone());
            }
            rows.Add(new(card.CardId, card.Name, support, reason!));
        }
        return (accepted.AsReadOnly(), new(ReviewVersion, cards.Count, structural, rows.AsReadOnly()), plans,
            convertedEffects.AsReadOnly());
    }

    /// <summary>
    /// 把转换草稿编译成原生效果组件并注入卡定义：先完整编译（模板/op 资产/绑定缺一即抛），
    /// 再实例化验证，最后以 <c>EffectsDefinition</c> 组件重建定义并交出快照供对局装配登记。
    /// </summary>
    private static (CardDefinitionEntry Entry, EffectSnapshot Snapshot) AssembleConverted(
        string cardId, string cardType, CardDefinitionEntry entry, DiyEffectDraft[] drafts)
    {
        var (snapshot, jsonText) = DiyCardCompiler.CompileEffects(cardType, drafts, cardId.Replace('/', '.'));
        var engine = new LogicEngine { ScriptEvaluator = NativeScriptHost.Evaluator };
        var instance = DynamicEffectFactory.Instantiate(engine, snapshot);
        if (!instance.Success)
            throw new InvalidDataException($"Converted effects of {cardId} failed to instantiate: {instance.ErrorCategory} · {instance.Error}");
        using var snapshotJson = JsonDocument.Parse(jsonText);
        using var effectsJson = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            component = "effects", inline = new[] { snapshotJson.RootElement.Clone() }
        }));
        var definition = new CardDefinition(entry.Definition.Name,
            entry.Definition.Components.Append(EffectsDefinition.Read(effectsJson.RootElement)));
        return (new(cardId, definition), snapshot);
    }
}
