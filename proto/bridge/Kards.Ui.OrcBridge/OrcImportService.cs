using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kards.Ui.Contracts;
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
    int Cost, int OpCost, int Attack, int Defense, string Text);

/// <summary>单行导入结果。Complete＝零诊断且编译成功；否则为待核对（诊断 + 原文）。</summary>
public sealed record OrcImportOutcome(
    string Id, string Name, bool Complete,
    UiCardDefinition? Card, string? EngineCardJson,
    string SourceText, IReadOnlyList<string> Diagnostics)
{
    public static OrcImportOutcome Pending(OrcImportRow row, IReadOnlyList<string> diagnostics) =>
        new(row.Id, row.Name, false, null, null, row.Text, diagnostics);
}

public static class OrcImportService
{
    private static readonly Wordbook Book = Wordbook.Create(null);

    public static IReadOnlyList<OrcImportOutcome> ImportBatch(IEnumerable<OrcImportRow> rows,
        IReadOnlyList<UiCardDefinition> catalog)
    {
        var outcomes = new List<OrcImportOutcome>();
        foreach (var row in rows)
        {
            try { outcomes.Add(ImportOne(row, catalog)); }
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
        var (cardType, unitType) = NormalizeType(row.Type)
            ?? throw new InvalidDataException("反制卡不在当前导入编译面（unit/order）内");
        var compile = OrcParser.Compile(row.Text, cardType, Book);
        if (compile.Diagnostics.Count > 0)
            return OrcImportOutcome.Pending(row, compile.Diagnostics.Select(d => d.ToString()).ToList());

        // 效果编译：每 op 一个触发实例（独立 CompileDsl/独立 snapshot）后并入同一 effects 组件。
        // 不做"同槽位多 op"：引擎只对 grant 做了块化，其余 op 模板同 handler 重复声明 var runtime
        // 会编译冲突（引擎面限制，禁改引擎）——逐 op 实例语义等价且天然顺序执行。
        var snapshots = new List<EffectSnapshot>();
        var snapshotJsons = new List<JsonElement>();
        foreach (var parsedEffect in compile.Effects)
        foreach (var op in parsedEffect.Ops)
        {
            var (snapshot, snapshotText) = CompileOrcEffect(row.Id, parsedEffect with { Ops = [op] });
            snapshots.Add(snapshot);
            using var doc = JsonDocument.Parse(snapshotText);
            snapshotJsons.Add(doc.RootElement.Clone());
        }

        var cardId = "orc/" + row.Id;
        var native = new CardDefinition(row.Name.Trim(), row.Cost,
            cardType == "unit" ? row.OpCost : 0, cardType == "unit" ? row.Attack : 0,
            cardType == "unit" ? row.Defense : 0,
            category: cardType == "unit" ? CardCategory.Unit : CardCategory.Command,
            unitTypes: cardType == "unit" ? [unitType] : [],
            keywords: compile.Keywords.Select(k => new KeywordDeclaration(k.Id,
                k.Quantified ? k.Value : null)).ToArray(),
            faction: Faction.Soviet, // 国家面待导入元数据补全（语料 nation 字段），默认苏联占位并在诊断面提示
            rarity: Rarity.Standard);
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
            Cost = row.Cost, OpCost = row.OpCost, Attack = row.Attack, Defense = row.Defense,
            Keywords = [.. compile.Keywords.Select(k => k.Id)],
        };
        var card = DiyCardCompiler.Preview(draft, catalog);
        return new(row.Id, row.Name, true, card, json, row.Text, []);
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
        _ => ("unit", "infantry"),
    };

    /// <summary>编译单个触发效果：引擎触发模板（嵌入资源）+ op 资产 → EffectSnapshot。</summary>
    internal static (EffectSnapshot Snapshot, string JsonText) CompileOrcEffect(
        string rowId, OrcParsedEffect effect)
    {
        var driver = new EffectCompilationDriver();
        driver.UseEffectAssets([TriggerTemplate(effect.TemplateId)], OpsAssets());
        var dsl = new DslEffectInstance(effect.TemplateId,
            new Dictionary<string, DslSlotFill> { [effect.Slot] = new(effect.Ops) });
        var result = driver.CompileDsl(DslJson.Serialize(dsl),
            rowId.Replace('/', '.') + "." + effect.TemplateId + ".effect");
        if (result.Report.Failures.Count > 0 || result.Report.UnresolvedTraces.Count > 0
            || result.Report.NeedsCsxTraces.Count > 0 || result.Report.PlaceholderConditionTraces.Count > 0
            || result.Artifacts.Count != 1)
            throw new InvalidDataException("效果无法完整编译：" + string.Join("；", result.Report.Failures
                .Select(f => f.Reason)
                .Concat(result.Report.UnresolvedTraces.Select(t => t.Reason))
                .Concat(result.Report.NeedsCsxTraces.Select(t => $"{t.Location} 需要补全脚本：{t.Script}"))
                .Concat(result.Report.PlaceholderConditionTraces.Select(t => $"{t.Location} 条件未实现：{t.RawText}"))));
        return (result.Artifacts[0].Snapshot, result.Artifacts[0].JsonText);
    }

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
