using System.Text.Json;

namespace Kards.Ui.Core;

/// <summary>Presentation category only. Never executes effects or changes game state.</summary>
public static class EffectClassifier
{
    public static IReadOnlyDictionary<string, string> CategoryLabels
    {
        get;
    } = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>
    {
        ["destroy"] = "消灭",
        ["fire"] = "伤害",
        ["heal"] = "治疗",
        ["intel"] = "情报",
        ["buff"] = "增益",
        ["debuff"] = "削弱",
        ["summon"] = "调度",
        ["supply"] = "补给",
        ["generic"] = "指令"
    });
    private static readonly (string Category, string Ops)[] Rules = [
        ("destroy","destroy destroyAll silence suppressHQ mill discard discardAll discardFromDeck discardChosenHand loseDefense"),
        ("fire","damage damageAll damageHQ damageSplit fight setDefense debuff debuffTemp"),
        ("heal","heal healAll healHQ armorBonus hqArmor hqMaxUp"),
        ("intel","intel reveal"),
        ("buff","buff buffAll buffCardsInPiles buffDeckAndHand grant grantAll grantMod grantEffect aura auraBuff hqKeyword hqEnchant setStats setHandCost setOpCost opCostMod opCostModAll handOpCostMod globalOpCostMod gainKreditSlot gainKredits nextTurnSlots nextTurnKredits"),
        ("debuff","pin unpin loseKredits loseKreditSlots removeKeyword suppress cannotAct"),
        ("summon","summon summonFromHand deckToField handToField handUnitToFrontline move returnToHand retreat upgradeSelf transformHandCard reposition"),
        ("supply","draw drawUntil discover develop addCardToHand deckToHand shuffleIn shuffleInUntil shuffleRandomSet copyToDeck copyHandCard chooseOne chooseHandCard chooseFromHand randomPick")];
    public static IReadOnlyList<string> CollectOps(JsonElement tree)
    {
        var result = new List<string>();
        Visit(tree, result, 0);
        return result;
    }
    private static void Visit(JsonElement tree, List<string> result, int depth)
    {
        if (depth > 8)
            return;
        if (tree.ValueKind == JsonValueKind.Array)
            foreach (var e in tree.EnumerateArray())
                Visit(e, result, depth + 1);
        if (tree.ValueKind != JsonValueKind.Object)
            return;
        if (tree.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.String)
            result.Add(op.GetString()!);
        foreach (var prop in tree.EnumerateObject())
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                Visit(prop.Value, result, depth + 1);
    }
    public static string ClassifyOps(IEnumerable<string> ops)
    {
        var names = ops.Select(o => o.Split('.').Last()).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in Rules)
            if (rule.Ops.Split(' ').Any(names.Contains))
                return rule.Category;
        return "generic";
    }
}
