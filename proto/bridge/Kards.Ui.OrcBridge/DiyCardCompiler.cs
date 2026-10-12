using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Data.Components;
using Orc.Game.EffectParsing;
using Orc.Game.EffectParsing.Compilation;
using Orc.Game.EffectParsing.Dsl;
using Orc.Game.EffectParsing.Templates;

namespace Kards.Ui.OrcBridge;

internal sealed record CompiledDiyCard(DiyCardDraft Draft, UiCardDefinition Card,
    CardDefinitionEntry Entry, EffectSnapshot? Effect, string CardJson);

/// <summary>Finite authoring surface over native definitions/DSL. Never interprets editable prose as rules.</summary>
public static class DiyCardCompiler
{
    public static IReadOnlyList<UiDiyChoice> Nations { get; } =
        [new("av76", "苏联"), new("deran", "德国"), new("USG", "美国"), new("UN", "英国")];
    public static IReadOnlyList<UiDiyChoice> Rarities { get; } =
        [new("iron", "基础"), new("bronze", "限定"), new("silver", "特殊"), new("gold", "精英")];
    public static IReadOnlyList<UiDiyChoice> UnitTypes { get; } = Enum.GetValues<UnitType>()
        .Select(t => new UiDiyChoice(t.ToString().ToLowerInvariant(), t switch
        {
            UnitType.Infantry => "步兵", UnitType.Tank => "坦克", UnitType.Artillery => "火炮",
            UnitType.Fighter => "战斗机", UnitType.Bomber => "轰炸机", _ => t.ToString()
        })).ToArray();
    private static readonly IReadOnlyDictionary<string, string> KeywordMap = new Dictionary<string, string>
    {
        ["blitz"] = KeywordIds.Blitz, ["fury"] = KeywordIds.Fury, ["smokescreen"] = KeywordIds.SmokeScreen,
        ["ambush"] = KeywordIds.Ambush, ["mobilize"] = KeywordIds.Mobilize,
        ["pincer"] = KeywordIds.Pincer, ["armor"] = KeywordIds.Armor, ["covert"] = KeywordIds.Covert
    };
    private static readonly object KeywordGate = new();
    private static volatile IReadOnlyList<UiDiyChoice> _keywords = KeywordMap.Select(p => new UiDiyChoice(p.Key, p.Value)).ToArray();
    private static volatile IReadOnlyDictionary<string, string> _packKeywords = new Dictionary<string, string>();

    /// <summary>Engine keyword id → UI token. Built-ins map to their DIY token; pack keywords (already
    /// engine ids) pass through, so DIY cards can carry pack vocabulary.</summary>
    public static string KeywordToken(string engineId)
    {
        lock (KeywordGate)
        {
            var pack = _packKeywords.FirstOrDefault(p => p.Value == engineId).Key;
            if (pack is { Length: > 0 }) return pack;
            return KeywordMap.FirstOrDefault(p => p.Value == engineId).Key is { Length: > 0 } token ? token : engineId;
        }
    }

    /// <summary>Installs the enabled packs' keyword vocabulary (pack id → engine id, identity map).
    /// Called by the UI whenever the enabled pack set changes; DIY validation accepts those ids.</summary>
    public static void SetPackKeywords(IReadOnlyDictionary<string, string> packKeywords)
    {
        lock (KeywordGate)
        {
            _packKeywords = packKeywords;
            _keywords = KeywordMap.Select(p => new UiDiyChoice(p.Key, p.Value))
                .Concat(packKeywords.Select(p => new UiDiyChoice(p.Key, p.Value))).ToArray();
        }
    }

    public static IReadOnlyList<UiDiyChoice> Keywords { get { lock (KeywordGate) return _keywords; } }
    public static IReadOnlyList<UiDiyChoice> Operations { get; } =
        [new("draw", "抽牌"), new("gainPoint", "获得指挥点"), new("gainSlot", "获得指挥点槽"),
         new("damage", "单位伤害"), new("buff", "攻防增益"), new("pin", "压制"),
         new("silence", "抑制"), new("costMod", "行动费变化"), new("destroy", "消灭")];
    public static IReadOnlyList<UiDiyChoice> Targets { get; } =
        [new("self", "本单位"), new("friendlyUnits", "全体友方单位"), new("enemyUnits", "全体敌方单位"),
         new("friendlyUnit", "单个友方单位"), new("enemyUnit", "单个敌方单位"), new("anyUnit", "单个单位"),
         new("bothUnits", "所有单位")];
    public static IReadOnlyList<UiDiyChoice> Durations { get; } =
        [new("", "持续"), new("turnEnd", "本回合结束"), new("nextOwnerTurnStart", "下个己方回合开始")];
    private static readonly Lazy<OpTemplateCatalog> Ops = new(() =>
    {
        // Godot loads managed assemblies from bytes; Assembly.Location can be empty.
        // Freeze the current engine's op assets in the bridge and materialize only those assets.
        var assembly = typeof(DiyCardCompiler).Assembly;
        const string prefix = "Kards.Ui.OrcBridge.DiyOps.";
        var assets = assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal).Select(n =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return (File: n[prefix.Length..], Content: reader.ReadToEnd());
            }).ToArray();
        if (assets.Length == 0) throw new InvalidDataException("缺少引擎效果编译资产，请重新构建项目");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", assets.Select(a => a.File + "\n" + a.Content)))));
        var directory = Path.Combine(Path.GetTempPath(), "OrC-KSD.UI", "diy-ops-" + hash);
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        foreach (var asset in assets)
        {
            var path = Path.Combine(directory, asset.File);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, asset.Content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true); paths.Add(path);
        }
        var ops = OpTemplateCatalog.LoadFiles(paths, out var failures);
        if (failures.Count > 0) throw new InvalidDataException(string.Join("；", failures.Select(f => f.Error)));
        return ops;
    });
    private static EffectTemplate Template(string type)
    {
        // An effect-host actor plus the payload.self guard restricts each trigger to its own card.
        // The stock deploy_basic uses eventCard; reusing it would make every host respond to every deployment.
        var hook = type == "unit" ? "unit.deployed" : "card.played";
        return EffectTemplateJson.Deserialize(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, id = "diy_" + type, actorFrom = "effectHost",
            slots = new[] { new { name = "on_event", target = "mainTrigger.events[e1]" } },
            root = new
            {
                mainTrigger = new { id = "t_diy", stableKey = "diy." + type, kind = "passive",
                    viewType = "Orc.Cards.CardEventView", hooks = new[] { hook },
                    events = new[] { new { id = "e1", entry = "HandleAsync" } } },
                otherTriggers = Array.Empty<object>(), modings = Array.Empty<object>(), injects = Array.Empty<object>()
            }
        }));
    }
    public static UiCardDefinition Preview(DiyCardDraft draft, IReadOnlyList<UiCardDefinition> catalog)
    {
        return new UiCardDefinition
        {
            CardId = draft.Id, Name = draft.Name, Set = draft.Nation, Rarity = draft.Rarity,
            CardType = draft.CardType, UnitType = draft.CardType == "unit" ? draft.UnitType : "",
            Cost = draft.Cost, BaseAttack = draft.CardType == "unit" ? draft.Attack : null,
            BaseDefense = draft.CardType == "unit" ? draft.Defense : null, BaseOpCost = draft.CardType == "unit" ? draft.OpCost : null,
            ArtPath = catalog.FirstOrDefault(c => c.CardId == draft.ArtCardId)?.ArtPath ?? "",
            Flavor = draft.Flavor, Text = Text(draft), Keywords = draft.Keywords.ToArray(),
            KeywordValues = draft.Keywords.Contains("armor") ? new Dictionary<string, int> { ["armor"] = draft.Armor } : new()
        };
    }
    public static UiDiyValidation Validate(DiyCardDraft draft, IReadOnlyList<UiCardDefinition> catalog)
    {
        try
        {
            var card = Compile(draft, catalog);
            return new(card.Card, card.CardJson, []);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or FormatException)
        { return new(null, null, [e.Message]); }
    }
    internal static CompiledDiyCard Compile(DiyCardDraft draft, IReadOnlyList<UiCardDefinition> catalog)
    {
        DiyCardLibrary.CheckShape(draft);
        if (catalog.Any(c => c.CardId == draft.Id && !c.CardId.StartsWith("diy/", StringComparison.Ordinal)))
            throw new InvalidDataException("DIY 卡牌不能覆盖原始卡牌");
        if (!Nations.Any(n => n.Id == draft.Nation) || !Rarities.Any(r => r.Id == draft.Rarity)
            || draft.CardType is not ("unit" or "order"))
            throw new InvalidDataException("当前 DIY 支持已列出的国家、稀有度、单位与指令");
        if (draft.Cost is < 0 or > 99 || draft.OpCost is < 0 or > 99 || draft.Attack is < 0 or > 99
            || draft.Defense is < 1 or > 99 || draft.Armor is < 0 or > 3)
            throw new InvalidDataException("费用与攻击须为 0–99，防御须为 1–99，重甲须为 0–3");
        if (draft.Keywords.Any(k => !KnownKeyword(k)))
            throw new InvalidDataException("含未接入的词条：" + string.Join("、", draft.Keywords.Where(k => !KnownKeyword(k))));
        if (draft.CardType == "order" && (draft.Keywords.Length != 0 || draft.Effects.Length == 0))
            throw new InvalidDataException("指令必须有打出效果，不能附带单位词条");
        if (!Enum.TryParse<UnitType>(draft.UnitType, true, out var unitType) || !Enum.IsDefined(unitType))
            throw new InvalidDataException("兵种不受引擎支持");
        foreach (var effect in draft.Effects) CheckEffect(draft.CardType, effect);
        var native = new CardDefinition(draft.Name.Trim(), draft.Cost,
            draft.CardType == "unit" ? draft.OpCost : 0, draft.CardType == "unit" ? draft.Attack : 0,
            draft.CardType == "unit" ? draft.Defense : 0,
            category: draft.CardType == "unit" ? CardCategory.Unit : CardCategory.Command,
            unitTypes: draft.CardType == "unit" ? [unitType] : [],
            keywords: draft.Keywords.Select(k => new KeywordDeclaration(EngineKeywordId(k), k == "armor" ? draft.Armor : null)),
            faction: draft.Nation switch { "av76" => Faction.Soviet, "deran" => Faction.Germany, "USG" => Faction.USA, _ => Faction.Britain },
            rarity: draft.Rarity switch { "iron" => Rarity.Standard, "bronze" => Rarity.Limited, "silver" => Rarity.Special, _ => Rarity.Elite });
        EffectSnapshot? snapshot = null;
        var components = native.Components.ToList();
        if (draft.Effects.Length > 0)
        {
            var (compiled, compiledJson) = CompileEffects(draft.CardType, draft.Effects, draft.Id.Replace('/', '.'));
            snapshot = compiled;
            var engine = new LogicEngine { ScriptEvaluator = NativeScriptHost.Evaluator };
            var instance = DynamicEffectFactory.Instantiate(engine, snapshot);
            if (!instance.Success) throw new InvalidDataException($"效果无法实例化：{instance.ErrorCategory} · {instance.Error}");
            using var snapshotJson = JsonDocument.Parse(compiledJson);
            using var effectsJson = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                component = "effects", inline = new[] { snapshotJson.RootElement.Clone() }
            }));
            components.Add(EffectsDefinition.Read(effectsJson.RootElement));
        }
        var json = CardDataJson.Serialize(new CardDataBody(draft.Id, native.Name, components), out var warnings);
        if (warnings.Count != 0) throw new InvalidDataException(string.Join("；", warnings));
        if (!CardDataJson.TryDeserialize(json, out var body, out var readWarnings, out var error))
            throw new InvalidDataException(error);
        if (readWarnings.Count != 0) throw new InvalidDataException(string.Join("；", readWarnings));
        var roundTrip = new CardDefinition(body!.Name, body.Components);
        return new(draft.Snapshot(), Preview(draft with { Name = native.Name }, catalog),
            new(draft.Id, roundTrip), snapshot, json);
    }
    /// <summary>
    /// Compiles an effect draft sequence against the frozen op-template assets. Shared by DIY cards
    /// and pack card conversion — the only effect path that reaches real matches.
    /// </summary>
    internal static (EffectSnapshot Snapshot, string JsonText) CompileEffects(string cardType, IReadOnlyList<DiyEffectDraft> effects, string compileId)
    {
        foreach (var effect in effects) CheckEffect(cardType, effect);
        var template = Template(cardType);
        var driver = new EffectCompilationDriver();
        driver.UseEffectAssets([template], Ops.Value);
        var ops = effects.Select(e => ToDsl(e, cardType)).ToArray();
        var dsl = new DslEffectInstance(template.Id, new Dictionary<string, DslSlotFill> { ["on_event"] = new(ops) });
        var result = driver.CompileDsl(DslJson.Serialize(dsl), compileId.Replace('/', '.') + ".effect");
        if (result.Report.Failures.Count > 0 || result.Report.UnresolvedTraces.Count > 0
            || result.Report.NeedsCsxTraces.Count > 0 || result.Report.PlaceholderConditionTraces.Count > 0
            || result.Artifacts.Count != 1)
            throw new InvalidDataException("效果无法完整编译：" + string.Join("；", result.Report.Failures.Select(f => f.Reason)
                .Concat(result.Report.Inputs.Where(i => i.FileFailureReason is not null).Select(i => i.FileFailureReason!))
                .Concat(result.Report.UnresolvedTraces.Select(t => t.Reason))
                .Concat(result.Report.NeedsCsxTraces.Select(t => $"{t.Location} 需要补全脚本：{t.Script}"))
                .Concat(result.Report.PlaceholderConditionTraces.Select(t => $"{t.Location} 条件未实现：{t.RawText}"))));
        return (result.Artifacts[0].Snapshot, result.Artifacts[0].JsonText);
    }

    private static bool KnownKeyword(string token)
    {
        lock (KeywordGate)
            return KeywordMap.ContainsKey(token) || _packKeywords.ContainsKey(token);
    }
    private static string EngineKeywordId(string token)
    {
        lock (KeywordGate)
            return KeywordMap.TryGetValue(token, out var engine) ? engine
                : _packKeywords.TryGetValue(token, out var pack) ? pack
                : throw new InvalidDataException("含未接入的词条：" + token);
    }
    private static void CheckEffect(string type, DiyEffectDraft e)
    {
        if (!Operations.Any(o => o.Id == e.Op) || !Targets.Any(t => t.Id == e.Target) || !Durations.Any(d => d.Id == e.Until))
            throw new InvalidDataException("效果包含未知操作、目标或期限");
        if (e.Op is not ("buff" or "costMod") && e.Until.Length != 0)
            throw new InvalidDataException("只有攻防增益和行动费修饰可指定期限");
        if (e.Op is "draw" or "gainPoint" or "gainSlot")
        {
            if (e.Amount is < 1 or > 10 || e.Target != "self") throw new InvalidDataException("抽牌与资源效果的数量须为 1–10，归属为自己");
        }
        else
        {
            if (type == "order" && e.Target == "self") throw new InvalidDataException("指令的单位效果须选择友方或敌方单位");
            if (e.Op == "damage" && e.Amount is < 1 or > 99)
                throw new InvalidDataException("伤害须为 1–99");
            if (e.Op == "pin" && e.Amount != 1)
                throw new InvalidDataException("当前原生压制模板只支持 1 回合");
            if (e.Op == "costMod" && (e.Amount is < -20 or > 20 || e.Amount == 0))
                throw new InvalidDataException("行动费变化须为 -20–20，不能为 0");
            if (e.Op == "buff" && (e.Attack is < -20 or > 20 || e.Defense is < -20 or > 20 || e.Attack == 0 && e.Defense == 0))
                throw new InvalidDataException("增益攻防须为 -20–20，不能同时为 0");
        }
    }
    private static DslOp ToDsl(DiyEffectDraft e, string type) => new(e.Op,
        target: e.Target switch
        {
            "self" => new("self"),
            "friendlyUnits" => new("all", "friendly"),
            "enemyUnits" => new("all", "enemy"),
            "friendlyUnit" => new("one", "friendly"),
            "enemyUnit" => new("one", "enemy"),
            "anyUnit" => new("one", "both"),
            "bothUnits" => new("all", "both"),
            _ => throw new InvalidDataException("未知选靶：" + e.Target)
        },
        amount: e.Op is "damage" or "gainPoint" or "gainSlot" or "costMod" ? e.Amount : null,
        count: e.Op == "draw" ? e.Amount : null, attack: e.Op == "buff" ? e.Attack : null,
        defense: e.Op == "buff" ? e.Defense : null,
        condition: new DslCondition(DslCondition.PayloadSelf, type == "unit" ? "Unit" : "Card"), until: e.Until.Length == 0 ? null : e.Until);
    private static string Text(DiyCardDraft draft)
    {
        string Target(DiyEffectDraft e) => Targets.FirstOrDefault(t => t.Id == e.Target)?.Label ?? e.Target;
        string Amount(int n) => n >= 0 ? "+" + n : n.ToString();
        var lines = draft.Effects.Select(e => e.Op switch
        {
            "draw" => $"抽 {e.Amount} 张牌", "gainPoint" => $"获得 {e.Amount} 个指挥点",
            "gainSlot" => $"获得 {e.Amount} 个指挥点槽", "damage" => $"对{Target(e)}造成 {e.Amount} 点伤害",
            "buff" => $"{Target(e)}获得 {Amount(e.Attack)}/{Amount(e.Defense)}",
            "pin" => $"压制{Target(e)} {e.Amount} 回合", "silence" => $"抑制{Target(e)}",
            "costMod" => $"{Target(e)}行动费 {Amount(e.Amount)}", "destroy" => $"消灭{Target(e)}", _ => $"未支持：{e.Op}"
        } + (e.Until.Length == 0 ? "" : $"，直到{Durations.FirstOrDefault(d => d.Id == e.Until)?.Label ?? e.Until}"));
        return draft.Effects.Length == 0 ? "" : (draft.CardType == "unit" ? "部署：" : "打出：") + string.Join("；", lines) + "。";
    }
}
