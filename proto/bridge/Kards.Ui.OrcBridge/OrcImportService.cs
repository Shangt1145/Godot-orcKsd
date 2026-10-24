using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge.OrcText;
using Orc;
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

// ─────────────────────────────────────────────────────────────────────────────
// OrC 批量导入（移植自 Kards-Desktop 的 import-cards 流程）：
// 行集 → OrcParser 解析 → 零诊断卡经引擎 DSL 面编译成真卡（引擎触发模板 + op 资产），
// 带诊断卡保留原文与诊断（待核对）。成功卡不塞九 op 的 DiyEffectDraft——效果面是
// 引擎 21 op DSL，直接走 EffectCompilationDriver（与 DIY/卡池转换同一唯一编译通道）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一行导入源卡（字段取官方语料口径；Type 兼容英文枚举与中文）。</summary>
public sealed record OrcImportRow(string Id, string Name, string Type,
    int Cost, int OpCost, int Attack, int Defense, string Text,string? DesktopJson=null);

/// <summary>单行导入结果。Complete＝零诊断且编译成功；否则为待核对（诊断 + 原文）。
/// Entry/Snapshots 供对局装配（pool 条目 + 效果注册）；Pending 两项为空。</summary>
public sealed record OrcImportOutcome(
    string Id, string Name, bool Complete,
    UiCardDefinition? Card, string? EngineCardJson,
    string SourceText, IReadOnlyList<string> Diagnostics,
    CardDefinitionEntry? Entry = null, IReadOnlyList<EffectSnapshot>? Snapshots = null)
{
    public string? DesktopEffectsJson {get;init;}
    public static OrcImportOutcome Pending(OrcImportRow row, IReadOnlyList<string> diagnostics) =>
        new(row.Id, row.Name, false, null, null, row.Text, diagnostics);
}

public static class OrcImportService
{

    public static IReadOnlyList<OrcImportOutcome> ImportBatch(IEnumerable<OrcImportRow> rows,
        IReadOnlyList<UiCardDefinition> catalog)
    {
        var rowList=rows.ToArray();
        var names=catalog.Concat(rowList.Select(r=>DiyCardCompiler.Preview(new DiyCardDraft {Id=r.Id,Name=r.Name,CardType=NormalizeType(r.Type)?.Item1??"counter",UnitType=NormalizeType(r.Type)?.Item2??"",Cost=r.Cost,Attack=r.Attack,Defense=r.Defense,OpCost=r.OpCost},catalog))).GroupBy(c=>c.CardId).Select(g=>g.Last()).ToArray();
        var outcomes = new List<OrcImportOutcome>();
        foreach (var row in rowList)
        {
            try { outcomes.Add(ImportOne(row, names)); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException
                or JsonException or ArgumentException or InvalidOperationException or FormatException
                or OrcReject)
            {
                outcomes.Add(OrcImportOutcome.Pending(row, [e.Message]));
            }
        }
        return outcomes;
    }

    private static OrcImportOutcome ImportOne(OrcImportRow row, IReadOnlyList<UiCardDefinition> catalog)
    {
        Orc.Plugins.BuiltIn.BuiltInPlugin.RegisterProcessDefaults();
        var (cardType, unitType) = NormalizeType(row.Type)
            ?? throw new InvalidDataException("反制卡不在当前导入编译面（unit/order）内");
        var compile = DesktopOrc.Compile(row.Text, cardType, catalog,row.DesktopJson);
        var faction=Faction.Soviet;var rarity=Rarity.Standard;var nation=Faction.Soviet;var rarityToken="iron";
        if(row.DesktopJson is { } metadata)
        {
            using var metadataDoc=JsonDocument.Parse(metadata);var raw=metadataDoc.RootElement;
            var accepted=new[]{"id","name","type","cardType","unitType","cost","attack","defense","opCost","text","effects","nation","set","rarity","keywords","kwMap","kwValues","art","file","src","custom","official","expansion","reserved","importDiagnostics","effectStatus","compileWarnings","orc","token"};
            foreach(var field in raw.EnumerateObject())if(!accepted.Contains(field.Name,StringComparer.Ordinal))return OrcImportOutcome.Pending(row,["OrC-KSD 导入字段尚无无损执行适配："+field.Name]);
            foreach(var mechanism in new[]{"upgradeTo","baseForm","exileNation","aura","hqEffects","system"})
                if(raw.TryGetProperty(mechanism,out _))return OrcImportOutcome.Pending(row,["OrC-KSD 导入机制字段尚无无损适配："+mechanism]);
            if(raw.TryGetProperty("token",out var token)&&token.ValueKind==JsonValueKind.True)return OrcImportOutcome.Pending(row,["衍生卡已保留；普通构筑准入尚未适配衍生标记"]);
            var country=raw.TryGetProperty("nation",out var n)?n.GetString():raw.TryGetProperty("set",out var set)?set.GetString():null;
            // 中文国名→引擎阵营；卡池目录名（av76 / deran / USG / UN）是**独立国家**，
            // 只做写法归一，不再折到苏/德/美/英。
            faction=country switch{"德国" or "deran"=>Faction.Germany,"美国" or "USG"=>Faction.USA,"英国" or "UN"=>Faction.Britain,"苏联" or "自定义" or null=>Faction.Soviet,_=>DeckNations.Canonical(country)};
            if(!Faction.IsDefined(faction))return OrcImportOutcome.Pending(row,["OrC-KSD 导入国籍未适配："+country]);
            nation=faction;
            rarityToken=raw.TryGetProperty("rarity",out var r)&&r.ValueKind==JsonValueKind.String?r.GetString()!:"iron";
            rarity=rarityToken switch{"iron" or "Standard"=>Rarity.Standard,"bronze" or "Limited"=>Rarity.Limited,"silver" or "Special"=>Rarity.Special,"gold" or "Elite"=>Rarity.Elite,_=>rarityToken};
            if(!Rarity.All.Contains(rarity,StringComparer.Ordinal))return OrcImportOutcome.Pending(row,["OrC-KSD 导入稀有度未适配："+rarityToken]);
        }
        if(compile.DesktopResultJson is { } full)
        {
            using var original=JsonDocument.Parse(full);
            if(original.RootElement.GetProperty("cardFields").EnumerateObject().Any())
                return OrcImportOutcome.Pending(row,["OrC-KSD 卡面机制字段尚无无损适配："+original.RootElement.GetProperty("cardFields").GetRawText()]);
        }
        if (compile.Diagnostics.Count > 0)
            return OrcImportOutcome.Pending(row, compile.Diagnostics.Select(d => d.ToString()).ToList());

        // 效果编译：每 op 一个触发实例（独立 CompileDsl/独立 snapshot）后并入同一 effects 组件。
        // 不做"同槽位多 op"：引擎只对 grant 做了块化，其余 op 模板同 handler 重复声明 var runtime
        // 会编译冲突（引擎面限制，禁改引擎）——逐 op 实例语义等价且天然顺序执行。
        var snapshots = new List<EffectSnapshot>();
        var snapshotJsons = new List<JsonElement>();
        foreach (var rawEffect in compile.Effects)
        {
        var parsedEffect=rawEffect.DesktopJson is {Length:>0} graph ? DesktopNativeLowering.Lower(graph) : rawEffect;
        foreach (var op in parsedEffect.Ops)
        {
            var (snapshot, snapshotText) = CompileOrcEffect(row.Id, parsedEffect with { Ops = [op] });
            snapshots.Add(snapshot);
            using var doc = JsonDocument.Parse(snapshotText);
            snapshotJsons.Add(doc.RootElement.Clone());
        }
        }

        var cardId = "orc/" + row.Id;
        var native = new CardDefinition(row.Name.Trim(), row.Cost,
            cardType == "unit" ? row.OpCost : 0, cardType == "unit" ? row.Attack : 0,
            cardType == "unit" ? row.Defense : 0,
            category: cardType == "unit" ? CardCategory.Unit : CardCategory.Command,
            unitTypes: cardType == "unit" ? [unitType] : [],
            keywords: compile.Keywords.Select(k => new KeywordDeclaration(k.Id,
                k.Quantified ? k.Value : null)).ToArray(),
            faction: faction,rarity: rarity);
        var components = native.Components.ToList();
        if (snapshotJsons.Count > 0)
        {
            var engine = new LogicEngine { ScriptEvaluator = NativeScriptHost.Evaluator };
            foreach (var snapshot in snapshots)
            {
                var instance = DynamicEffectFactory.Instantiate(engine, snapshot);
                if (!instance.Success)
                    throw new InvalidDataException($"效果无法实例化：{instance.ErrorCategory} · {instance.Error}");
            }
            using var effectsJson = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                component = "effects", inline = snapshotJsons
            }));
            components.Add(EffectsDefinition.Read(effectsJson.RootElement));
        }

        var json = CardDataJson.Serialize(new CardDataBody(cardId, native.Name, components), out var warnings);
        if (warnings.Count != 0) throw new InvalidDataException(string.Join("；", warnings));
        if (!CardDataJson.TryDeserialize(json, out var body, out var readWarnings, out var error))
            throw new InvalidDataException(error);
        if (readWarnings.Count != 0) throw new InvalidDataException(string.Join("；", readWarnings));
        var roundTrip = new CardDefinition(body!.Name, body.Components);

        // 显示面复用 DIY 预览构造（draft 仅承载基础字段；效果面已在引擎卡内）。
        var draft = new DiyCardDraft
        {
            Id = cardId, Name = row.Name.Trim(), CardType = cardType, UnitType = unitType,
            Nation=nation,Rarity=rarityToken,
            Cost = row.Cost, OpCost = row.OpCost, Attack = row.Attack, Defense = row.Defense,
            Keywords = [.. compile.Keywords.Select(k => k.Id)],
        };
        var card = DiyCardCompiler.Preview(draft, catalog);
        // roundTrip（反序列化产物）作为 pool 条目——与对局装载走同一解析面，杜绝"预检能过、装载报错"。
        return new(row.Id, row.Name, true, card, json, row.Text, [],
            new CardDefinitionEntry(cardId, roundTrip), snapshots.AsReadOnly())
            {DesktopEffectsJson=JsonSerializer.Serialize(compile.Effects.Select(e=>JsonSerializer.Deserialize<JsonElement>(e.DesktopJson!)))};
    }

    /// <summary>
    /// 行集解析入口：接受纯行数组（OrcImportRow 字段口径）与官方语料 wrapper
    /// （{"cards":[{"json":{…}}]}）。语料卡缺字段时按导入默认值补齐（cost/opCost=1、攻防 0/1）。
    /// </summary>
    public static IReadOnlyList<OrcImportRow> ParseRows(string json)
    {
        var normalized=DesktopOrc.Request(new {method="importRows",text=json});
        return normalized.EnumerateArray().Select(c=>
        {
            string Str(string key)=>c.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
            int Num(string key,int fallback)=>c.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number?v.GetInt32():fallback;
            return new OrcImportRow(Str("id"),Str("name"),Str("cardType")=="unit"?Str("unitType"):Str("cardType"),Num("cost",1),Num("opCost",1),Num("attack",0),Num("defense",1),Str("text"),c.GetRawText());
        }).ToArray();
    }
    /// <summary>语料类型 → (编译卡类型, 引擎兵种)。反制卡返回 null（编译面外，待核对）。</summary>
    private static (string, string)? NormalizeType(string type) => type.Trim() switch
    {
        "order" or "指令" => ("order", "infantry"),
        "countermeasure" or "反制" => null,
        "infantry" or "步兵" => ("unit", "infantry"),
        "tank" or "坦克" => ("unit", "tank"),
        "artillery" or "火炮" or "炮兵" => ("unit", "artillery"),
        "fighter" or "战斗机" => ("unit", "fighter"),
        "bomber" or "轰炸机" => ("unit", "bomber"),
        "unit" or "单位" => ("unit", "infantry"),
        _ => null,
    };

    /// <summary>编译单个触发效果：引擎触发模板（嵌入资源）+ op 资产 → EffectSnapshot。</summary>
    internal static (EffectSnapshot Snapshot, string JsonText) CompileOrcEffect(
        string rowId, OrcParsedEffect effect)
    {
        // 触发模板双轨：Hook 非空＝桥内合成 effectHost 模板（语义守卫见 OrcTriggerGuard 注释）；
        // Hook 空（attack_basic 注入式特化视图）＝引擎原模板资产。
        EffectTemplate template;
        IReadOnlyList<DslOp> ops = effect.Ops;
        if (effect.Hook is { } hook)
        {
            if (hook == "card.died" && effect.Guard == OrcTriggerGuard.SelfCard)
                hook = Orc.Game.GameHooks.CardDying;
            template = EffectTemplateJson.Deserialize(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                id = "orc_" + effect.TemplateId,
                actorFrom = "effectHost",
                slots = new[] { new { name = effect.Slot, target = "mainTrigger.events[e1]" } },
                root = new
                {
                    mainTrigger = new
                    {
                        id = "t_orc_" + effect.TemplateId,
                        stableKey = "orc." + effect.TemplateId,
                        kind = "passive",
                        viewType = "Orc.Cards.CardEventView",
                        hooks = new[] { hook },
                        events = new[] { new { id = "e1", entry = "HandleAsync" } }
                    },
                    otherTriggers = Array.Empty<object>(),
                    modings = Array.Empty<object>(),
                    injects = Array.Empty<object>()
                }
            }));
            if (effect.Guard != OrcTriggerGuard.None)
            {
                var condition = effect.Guard switch
                {
                    OrcTriggerGuard.SelfUnit or OrcTriggerGuard.SelfCard
                        => new DslCondition(DslCondition.PayloadSelf,
                            effect.Guard == OrcTriggerGuard.SelfUnit ? "Unit" : "Card"),
                    OrcTriggerGuard.OwnerSame => new DslCondition(DslCondition.OwnerSame),
                    OrcTriggerGuard.OwnerSameByPlayer => new DslCondition(DslCondition.OwnerSameByPlayer),
                    _ => null,
                };
                if (condition is not null)
                    ops = effect.Ops.Select(op => CloneWithCondition(op, op.Condition is { } existing ? new DslCondition(DslCondition.AllKind,all:[condition,existing]) : condition)).ToArray();
            }
        }
        else template = TriggerTemplate(effect.TemplateId);

        var driver = new EffectCompilationDriver();
        driver.UseEffectAssets([template], OpsAssets());
        var dsl = new DslEffectInstance(template.Id,
            new Dictionary<string, DslSlotFill> { [effect.Slot] = new(ops) });
        var result = driver.CompileDsl(DslJson.Serialize(dsl),
            rowId.Replace('/', '.') + "." + effect.TemplateId + ".effect");
        if (result.Report.Failures.Count > 0 || result.Report.UnresolvedTraces.Count > 0
            || (!effect.NativeGenerated && result.Report.NeedsCsxTraces.Count > 0) || result.Report.PlaceholderConditionTraces.Count > 0
            || result.Artifacts.Count != 1)
            throw new InvalidDataException("效果无法完整编译：" + string.Join("；", result.Report.Failures
                .Select(f => f.Reason)
                .Concat(result.Report.UnresolvedTraces.Select(t => t.Reason))
                .Concat(result.Report.NeedsCsxTraces.Select(t => $"{t.Location} 需要补全脚本：{t.Script}"))
                .Concat(result.Report.PlaceholderConditionTraces.Select(t => $"{t.Location} 条件未实现：{t.RawText}"))));
        if(effect.NativeGenerated)
        {
            var instance=DynamicEffectFactory.Instantiate(new LogicEngine {ScriptEvaluator=NativeScriptHost.Evaluator},result.Artifacts[0].Snapshot);
            if(!instance.Success)throw new InvalidDataException("原生声明脚本无法实例化："+instance.Error);
        }
        return (result.Artifacts[0].Snapshot, result.Artifacts[0].JsonText);
    }

    /// <summary>DslOp 是普通类且 Condition 只读——按全量构造面克隆并换守卫。</summary>
    private static DslOp CloneWithCondition(DslOp op, DslCondition condition) => new(
        op.Op, target: op.Target, filter: op.Filter, amount: op.Amount, count: op.Count,
        attack: op.Attack, defense: op.Defense, keyword: op.Keyword, zone: op.Zone,
        script: op.Script, condition: condition, name: op.Name, nested: op.Nested,
        until: op.Until, field: op.Field, value: op.Value);

    /// <summary>引擎触发模板资产（嵌入自引擎仓库 EffectParsing/Templates/*.tpl.json）。</summary>
    internal static EffectTemplate TriggerTemplate(string templateId)
    {
        var assembly = typeof(OrcImportService).Assembly;
        const string prefix = "Kards.Ui.OrcBridge.OrcTpl.";
        var name = prefix + templateId + ".tpl.json";
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("缺少引擎触发模板资产：" + templateId);
        using var reader = new StreamReader(stream);
        return EffectTemplateJson.Deserialize(reader.ReadToEnd());
    }

    /// <summary>op 资产目录（与 DiyCardCompiler 同一嵌入资产——单一真源）。</summary>
    internal static OpTemplateCatalog OpsAssets() => DiyCardCompiler.SharedOps();
}
