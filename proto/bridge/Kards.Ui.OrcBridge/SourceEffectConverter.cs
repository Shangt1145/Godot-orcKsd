using System.Text.Json;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 批次 1 转换器：把源声明的**非交互**效果翻译成 DIY DSL 草稿，走
/// <see cref="DiyCardCompiler.CompileEffects"/> 的原生编译链（编译 + 实例化全过才准入）。
///
/// v1 受限面：触发只收 deploy（单位）／order（指令）；op 只收九种已验证原语；
/// 目标只收 self/all；数值必须为字面量。任何超出该面的形状（ref/random 选靶、filter、
/// 条件、chooseOne、动态数值、death/turnEnd 等触发）都显式拒绝并给出原因，
/// 交回 <see cref="VerifiedCardPool"/> 标 Unsupported——绝不静默裁剪语义。
/// </summary>
internal static class SourceEffectConverter
{
    public static bool TryConvert(JsonElement effects, string cardType, out DiyEffectDraft[] drafts, out string? reason)
    {
        drafts = []; reason = null;
        if (effects.ValueKind != JsonValueKind.Array || effects.GetArrayLength() == 0)
        { reason = "effect declaration is not a non-empty array"; return false; }
        var expectedTrigger = cardType == "unit" ? "deploy" : "order";
        var result = new List<DiyEffectDraft>();
        foreach (var effect in effects.EnumerateArray())
        {
            foreach (var key in effect.EnumerateObject())
                if (key.Name is not ("trigger" or "actions" or "targets"))
                { reason = $"effect block carries unsupported field '{key.Name}' (conditions and filters are not convertible)"; return false; }
            if (effect.TryGetProperty("targets", out var targets)
                && (targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() != 0))
            { reason = "interactive targets belong to batch 2"; return false; }
            var trigger = Text(effect, "trigger");
            if (trigger != expectedTrigger)
            { reason = $"trigger '{trigger}' is outside the non-interactive surface (expected '{expectedTrigger}')"; return false; }
            if (!effect.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            { reason = "effect block has no actions array"; return false; }
            foreach (var action in actions.EnumerateArray())
                if (!TryAction(action, cardType, result, ref reason)) return false;
        }
        drafts = [.. result];
        return true;
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedKeys =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["draw"] = new HashSet<string>(StringComparer.Ordinal) { "op", "count" },
            ["gainKredits"] = new HashSet<string>(StringComparer.Ordinal) { "op", "amount", "side" },
            ["gainKreditSlot"] = new HashSet<string>(StringComparer.Ordinal) { "op", "amount", "side" },
            ["damage"] = new HashSet<string>(StringComparer.Ordinal) { "op", "amount", "target" },
            ["buff"] = new HashSet<string>(StringComparer.Ordinal) { "op", "attack", "defense", "target", "duration" },
            ["pin"] = new HashSet<string>(StringComparer.Ordinal) { "op", "target", "amount" },
            ["silence"] = new HashSet<string>(StringComparer.Ordinal) { "op", "target" },
            ["opCostMod"] = new HashSet<string>(StringComparer.Ordinal) { "op", "amount", "target", "duration" },
            ["destroy"] = new HashSet<string>(StringComparer.Ordinal) { "op", "target" },
        };

    private static bool TryAction(JsonElement action, string cardType, List<DiyEffectDraft> result, ref string? reason)
    {
        if (action.ValueKind != JsonValueKind.Object) { reason = "action is not an object"; return false; }
        var op = Text(action, "op");
        if (!AllowedKeys.TryGetValue(op, out var allowed))
        { reason = $"op '{op}' has no non-interactive conversion"; return false; }
        foreach (var key in action.EnumerateObject())
            if (!allowed.Contains(key.Name))
            { reason = $"op '{op}' carries unsupported field '{key.Name}'"; return false; }

        string? Target()
        {
            if (!action.TryGetProperty("target", out var t)) return null;
            if (t.ValueKind != JsonValueKind.Object || t.TryGetProperty("filter", out _)) return null;
            var sel = Text(t, "sel"); var side = Text(t, "side");
            return (sel, side) switch
            {
                ("all", "enemy") => "enemyUnits",
                ("all", "friendly") => "friendlyUnits",
                ("all", "both") => "bothUnits",
                ("all", "self") => cardType == "unit" ? "self" : null,
                _ => null // ref/random/one targets belong to batch 2 (interactive targeting)
            };
        }
        bool OrderSelfForbidden(string target) => cardType == "order" && target == "self";
        int? Number(string key)
        {
            if (!action.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n)) return null;
            return n;
        }

        switch (op)
        {
            case "draw":
                var count = Number("count") ?? 1;
                if (count is < 1 or > 10) { reason = "draw count must be 1-10"; return false; }
                result.Add(new() { Op = "draw", Amount = count });
                return true;
            case "gainKredits" or "gainKreditSlot":
                var amount = Number("amount");
                if (amount is null or < 1 or > 10) { reason = $"{op} amount must be a literal 1-10"; return false; }
                if (Text(action, "side", "self") != "self") { reason = $"{op} must stay with the owner"; return false; }
                result.Add(new() { Op = op == "gainKredits" ? "gainPoint" : "gainSlot", Amount = amount.Value });
                return true;
            case "damage":
                var damageTarget = Target();
                var damage = Number("amount");
                if (damageTarget is null || OrderSelfForbidden(damageTarget))
                { reason = "damage target is outside the self/all surface"; return false; }
                if (damage is null or < 1 or > 99) { reason = "damage amount must be a literal 1-99"; return false; }
                result.Add(new() { Op = "damage", Target = damageTarget, Amount = damage.Value });
                return true;
            case "buff":
                var buffTarget = Target();
                var attack = Number("attack") ?? 0; var defense = Number("defense") ?? 0;
                var buffDuration = Text(action, "duration");
                if (buffTarget is null || OrderSelfForbidden(buffTarget))
                { reason = "buff target is outside the self/all surface"; return false; }
                if (buffDuration is not ("" or "turn")) { reason = $"buff duration '{buffDuration}' is unsupported"; return false; }
                if (attack is < -20 or > 20 || defense is < -20 or > 20 || (attack == 0 && defense == 0))
                { reason = "buff values must be -20..20 and not both zero"; return false; }
                result.Add(new() { Op = "buff", Target = buffTarget, Attack = attack, Defense = defense, Until = buffDuration == "turn" ? "turnEnd" : "" });
                return true;
            case "pin":
                var pinTarget = Target();
                if (pinTarget is null || OrderSelfForbidden(pinTarget))
                { reason = "pin target is outside the self/all surface"; return false; }
                if ((Number("amount") ?? 1) != 1) { reason = "native pin template only supports one turn"; return false; }
                result.Add(new() { Op = "pin", Target = pinTarget, Amount = 1 });
                return true;
            case "silence":
                var silenceTarget = Target();
                if (silenceTarget is null || OrderSelfForbidden(silenceTarget))
                { reason = "silence target is outside the self/all surface"; return false; }
                result.Add(new() { Op = "silence", Target = silenceTarget });
                return true;
            case "opCostMod":
                var costTarget = Target();
                var cost = Number("amount");
                var costDuration = Text(action, "duration");
                if (costTarget is null || OrderSelfForbidden(costTarget))
                { reason = "opCostMod target is outside the self/all surface"; return false; }
                if (costDuration is not ("" or "turn")) { reason = $"opCostMod duration '{costDuration}' is unsupported"; return false; }
                if (cost is null or < -20 or > 20 or 0) { reason = "opCostMod amount must be a literal -20..20 and not zero"; return false; }
                result.Add(new() { Op = "costMod", Target = costTarget, Amount = cost.Value, Until = costDuration == "turn" ? "turnEnd" : "" });
                return true;
            case "destroy":
                var destroyTarget = Target();
                if (destroyTarget is null || OrderSelfForbidden(destroyTarget))
                { reason = "destroy target is outside the self/all surface"; return false; }
                result.Add(new() { Op = "destroy", Target = destroyTarget });
                return true;
            default:
                reason = $"op '{op}' has no non-interactive conversion";
                return false;
        }
    }

    private static string Text(JsonElement e, string key, string fallback = "") =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : fallback;
}
