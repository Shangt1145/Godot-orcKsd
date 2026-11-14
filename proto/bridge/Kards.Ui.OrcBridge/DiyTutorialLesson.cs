using System.Text.Json.Nodes;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>The small, deliberately constrained card taught by the first workshop lesson.</summary>
public static class DiyTutorialLesson
{
    public const string CardName = "补给步兵";
    public const string DisplayText = "闪击。部署：抽 1 张牌。";
    public static bool AttributesReady(NativeCardDocument card) =>
        card.Name == CardName && card.CardType == "unit" && card.Unit == "infantry"
        && card.Number("factionCost", "kredits") == 2
        && card.Number("battleStats", "attack") == 2
        && card.Number("battleStats", "defense") == 3
        && card.Number("battleStats", "operationCost") == 1;

    public static bool KeywordReady(NativeCardDocument card) =>
        card.Component("keywords")?["attributes"] is JsonArray list && list.Any(k => k?.ToString() == "blitz");

    public static bool BlockEffectReady(HandlerDsl.HandlerDocument document, string signal, string guard)
    {
        return signal == "unit.deployed" && guard == "Self"
            && document.Actions is [{ Op: "draw" } draw]
            && draw.Values.TryGetValue("count", out var count)
            && count.Kind == HandlerDsl.HandlerValueKind.Literal && count.LiteralType == "number" && count.Number == 1
            && !draw.Values.ContainsKey("source");
    }
}
