using System.Text.Json;
using Kards.Ui.OrcBridge.OrcText;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Data.Components;

namespace Kards.Ui.OrcBridge;

public sealed record OfficialCompiledCard(string Id, string Name, string EngineJson, string Text, string[] Graphs);

/// <summary>Strict full-card admission for official source records. No dropped mechanics or placeholder effects.</summary>
public static class OfficialCardCompiler
{
    public static OfficialCompiledCard Compile(JsonElement card, string packId)
    {
        Orc.Plugins.BuiltIn.BuiltInPlugin.RegisterProcessDefaults();
        var known = new HashSet<string>(["id","set","text","type","image","title","rarity","faction","kredits","reserved","import_id",
            "attack","defense","operationCost","attributes","permanent_pool","can_create","exile"], StringComparer.Ordinal);
        foreach (var field in card.EnumerateObject()) if (!known.Contains(field.Name)) throw new InvalidDataException("未知卡面字段：" + field.Name);
        string Str(string key) => card.GetProperty(key).GetString()!;
        int Num(string key) => card.TryGetProperty(key, out var value) && value.TryGetInt32(out var number) && number >= 0
            ? number : throw new InvalidDataException("属性缺失或不是有效常量：" + key);
        var id = Str("id");
        if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')) throw new InvalidDataException("卡牌编号无效");
        var type = Str("type");
        if (type != "order" && !UnitType.TryNormalize(type, out _)) throw new InvalidDataException("未完整支持的卡牌类型：" + type);
        if (card.TryGetProperty("exile", out var exile) && exile.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
            throw new InvalidDataException("流亡机制尚未接入完整官方卡准入");
        if (card.TryGetProperty("can_create", out var created) && created.ValueKind == JsonValueKind.Array && created.GetArrayLength() > 0)
            throw new InvalidDataException("生成/老兵引用卡池尚未完整准入");
        var text = card.GetProperty("text").TryGetProperty("zh-Hans", out var t) ? t.GetString() ?? "" : "";
        var name = card.GetProperty("title").TryGetProperty("zh-Hans", out var n) ? n.GetString() ?? id : id;
        var attributes = card.TryGetProperty("attributes", out var attrs) ? attrs.EnumerateArray().Select(a => a.GetString()!).ToArray() : [];
        using var keywordJson = JsonDocument.Parse(JsonSerializer.Serialize(new {component="keywords", attributes}));
        var keywords = KeywordsDefinition.Read(keywordJson.RootElement);
        if (keywords.UnmappedAttributes.Count > 0 || keywords.BecomesVeteran is not null || keywords.VeteranOf is not null)
            throw new InvalidDataException("词条或升级链未完整支持：" + string.Join(",", attributes));
        var cost = Num("kredits"); var unit = type != "order";
        var attack = unit ? Num("attack") : 0; var defense = unit ? Num("defense") : 0; var op = unit ? Num("operationCost") : 0;
        if (unit && defense == 0) throw new InvalidDataException("单位防御必须大于零");
        var compiled = OrcInspector.CompileText("pack." + packId + "." + id, text, unit ? "unit" : "order");
        if (!compiled.ParseComplete || !compiled.AllCompiled) throw new InvalidDataException(string.Join("；", compiled.Problems));
        var declarations = keywords.Keywords.Concat(compiled.Parse.Keywords.Select(k => new KeywordDeclaration(k.Id, k.Quantified ? k.Value : null)))
            .GroupBy(k => k.Id, StringComparer.Ordinal).Select(g => {
                if (g.Select(k => k.Value).Distinct().Count() > 1) throw new InvalidDataException("词条参值冲突：" + g.Key);
                return g.First(); }).ToArray();
        var definition = new CardDefinition(name, cost, op, attack, defense,
            category: unit ? CardCategory.Unit : CardCategory.Command, unitTypes: unit ? [type] : [],
            keywords: declarations,
            faction: Faction.TryNormalize(Str("faction"), out var faction) ? faction : throw new InvalidDataException("国籍未支持"),
            rarity: Rarity.TryNormalize(Str("rarity"), out var rarity) ? rarity : throw new InvalidDataException("稀有度未支持"));
        var components = definition.Components.ToList(); var inline = new List<JsonElement>();
        var engine = new LogicEngine { ScriptEvaluator = NativeScriptHost.Evaluator };
        foreach (var effect in compiled.Effects)
        {
            foreach (var snapshot in effect.Snapshots)
            {
                var instance = DynamicEffectFactory.Instantiate(engine, snapshot);
                if (!instance.Success) throw new InvalidDataException("原生效果无法实例化：" + instance.Error);
            }
            using var document = JsonDocument.Parse(effect.JsonText!);
            inline.AddRange(document.RootElement.EnumerateArray().Select(e => e.Clone()));
        }
        if (inline.Count > 0)
        {
            using var component = JsonDocument.Parse(JsonSerializer.Serialize(new {component="effects", inline}));
            components.Add(EffectsDefinition.Read(component.RootElement));
        }
        var json = CardDataJson.Serialize(new CardDataBody(id, name, components), out var warnings);
        if (warnings.Count > 0) throw new InvalidDataException(string.Join("；", warnings));
        if (!CardDataJson.TryDeserialize(json, out _, out var readWarnings, out var error) || readWarnings.Count > 0)
            throw new InvalidDataException(error ?? string.Join("；", readWarnings));
        return new(id, name, json, text, compiled.Parse.Effects.Select(e => e.DesktopJson!).ToArray());
    }
}
