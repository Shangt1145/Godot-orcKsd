using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kards.Ui.OrcBridge;

/// <summary>Versioned editing hints for existing authoring formats; never implements rule execution.</summary>
public sealed record InspectorField(string Key,string Type,string Label,bool Optional=true,JsonNode? Initial=null);
public static class InspectorSchema
{
    public const int Version=1;
    private static InspectorField F(string key,string type,bool optional=true,JsonNode? initial=null)=>new(key,type,Label(key),optional,initial);
    public static string Label(string key)=>key switch
    {
        "Op"=>"执行什么","Ops"=>"依次执行的动作","Template"=>"发生时机","Guard"=>"事件范围","Hook"=>"事件标识",
        "Sel"=>"选择方式","Side"=>"所属一方","SelZone"=>"所在区域","SelCount"=>"选择数量","Filter"=>"对象筛选",
        "Amount"=>"数值","Count"=>"数量","Attack"=>"攻击力变化","Defense"=>"防御力变化","Until"=>"持续到何时",
        "Keyword"=>"词条","Value"=>"词条参数","Name"=>"引用的卡牌","OpZone"=>"移动到哪里",
        "Condition"=>"兼容条件","ConditionRaw"=>"兼容条件参数","CondUnitTypes"=>"兵种范围","CondComparison"=>"比较关系","CondValue"=>"比较数量",
        "Counter"=>"计数器名称","AmountFromCounter"=>"数值来自计数器","PrefabJson"=>"原生预制体",
        "Target"=>"作用于谁","Duration"=>"持续到何时","extraTypes"=>"全部兵种","key"=>"记录名称","operator"=>"计算方式",
        _=>InspectorPresentation.Field(key.Length>0?char.ToLowerInvariant(key[0])+key[1..]:key)
    };
    public static InspectorPart Part(string type)=>type switch
    {
        "value"=>InspectorPart.Value,"condition" or "predicate"=>InspectorPart.Condition,"query" or "selector"=>InspectorPart.Query,
        "filter"=>InspectorPart.Filter,"trigger"=>InspectorPart.Trigger,"native-effect"=>InspectorPart.NativeEffect,"native-op"=>InspectorPart.NativeOperation,
        "legacy-effect"=>InspectorPart.LegacyEffect,"actions" or "effectList" or "options" or "values" or "queries" or "conditions" or "strings" or "keywords" or "cardIds" or "native-ops" or "native-types" or "targets" or "data-list"=>InspectorPart.List,
        _=>InspectorPart.Record
    };
    public static IReadOnlyList<InspectorField> Fields(string schema,JsonObject node)
    {
        if(schema is "action" or "condition-library")
        {
            var op=node["op"]?.ToString()??"";var table=schema=="action"?"ops":"conditions";
            if(!InspectorVocabulary.Catalog.GetProperty("vocabulary").GetProperty(table).TryGetProperty(op,out var definition))return [];
            return definition.GetProperty("fields").EnumerateArray().Select(f=>
            {
                var key=f.GetProperty("name").GetString()!;var type=f.GetProperty("type").GetString()!;
                if(schema=="condition-library"&&key=="items"&&op is "and" or "or")type="conditions";
                if(schema=="condition-library"&&key=="item"&&op=="not")type="condition";
                if(key is "keywords" or "removeKeywords")type="keywords";if(key=="cardIds")type="cardIds";
                return new InspectorField(key,type,InspectorPresentation.Field(key),!f.TryGetProperty("optional",out var optional)||optional.GetBoolean(),f.TryGetProperty("initial",out var seed)?JsonNode.Parse(seed.GetRawText()):null);
            }).ToArray();
        }
        if(schema=="data")return node.Select(p=>new InspectorField(p.Key,p.Value?.GetValueKind() switch{JsonValueKind.Number=>"value",JsonValueKind.True or JsonValueKind.False=>"boolean",JsonValueKind.Object=>"data",JsonValueKind.Array=>"data-list",null=>"value",_=>"text"},"数据字段 · "+p.Key,false)).ToArray();
        if(schema=="native-op")
        {
            var info=OrcEffectEditing.Op(node["Op"]?.ToString()??"");var fields=new List<InspectorField>{F("Op","native-operation",false)};
            if(info is null)return fields;
            if(info.Target)fields.AddRange([F("Sel","native-selection",false),F("Side","native-side"),F("SelZone","native-zone"),F("SelCount","integer"),F("Filter","native-filter")]);
            if(info.Amount)fields.Add(F("Amount","integer",false));if(info.Count)fields.Add(F("Count","integer",false));
            if(info.AttackDefense)fields.AddRange([F("Attack","integer",false),F("Defense","integer",false)]);
            if(info.Keyword)fields.AddRange([F("Keyword","native-keyword",false),F("Value","integer")]);
            if(info.Name)fields.Add(F("Name","native-card",false));if(info.Until)fields.Add(F("Until","native-until"));if(info.Zone)fields.Add(F("OpZone","native-op-zone",false));
            if(info.Id.StartsWith("counter.",StringComparison.Ordinal)||info.Id is "damage" or "buff")
            {fields.Add(F("Counter","counter-name",info.Id is "damage" or "buff"&&node["AmountFromCounter"]?.GetValueKind()!=JsonValueKind.True));if(info.Id is "damage" or "buff")fields.Add(F("AmountFromCounter","boolean"));}
            fields.Add(F("Condition","native-condition"));
            if(node["Condition"]?.ToString()==OrcEffectEditing.OwnerControlsType)fields.AddRange([F("CondUnitTypes","native-types",false),F("CondComparison","native-comparison",false),F("CondValue","integer",false)]);
            else if(node["Condition"] is {} condition&&condition.ToString().Length>0)fields.Add(F("ConditionRaw",condition.ToString()==OrcEffectEditing.EventCardFilter?"event-card-filter":"text"));
            // Non-current but known DTO parameters remain editable and are never discarded on op change.
            foreach(var key in node.Where(p=>p.Value is not null&&p.Value.ToString()!="false").Select(p=>p.Key))
                if(!fields.Any(f=>f.Key==key)&&NativeOpFields.TryGetValue(key,out var type))fields.Add(F(key,type));
            return fields;
        }
        return schema switch
        {
            "filter"=>[F("unitType","unitType"),F("cardType","cardType"),F("set","text"),F("rarity","rarity"),F("minCost","value"),F("maxCost","value"),F("keyword","keyword"),F("where","predicate"),F("excludeSelf","boolean")],
            "native-filter"=>[F("UnitType","native-unit-type"),F("Keyword","native-keyword"),F("Faction","text"),F("ExcludeSelf","boolean")],
            "cardPool"=>[F("cardIds","cardIds"),F("filter","filter"),F("where","predicate"),F("side","side"),F("zone","zone")],
            "effect"=>[F("trigger","trigger",false),F("targets","targets"),F("condition","condition"),F("actions","actions",false),F("else","actions"),F("once","boolean"),F("oncePerTurn","boolean"),F("eventSide","eventSide"),F("selfOnly","boolean"),F("listenZone","listenZone"),F("firstEvent","boolean")],
            "target"=>[F("id","text",false),F("kind","kind"),F("side","side"),F("zone","zone"),F("prompt","text"),F("filter","filter")],
            "option"=>[F("label","text",false),F("test","predicate"),F("actions","actions",false)],
            "prefab"=>[F("PrefabJson","code",false)],
            "native-dsl-condition"=>[F("kind","text",false),F("unitTypes","native-types"),F("comparison","native-comparison"),F("value","integer"),F("raw","text")],
            "legacy-effect"=>[F("Op","legacy-operation",false),F("Target","legacy-target",false),F("Amount","integer",false),F("Attack","integer"),F("Defense","integer"),F("Until","native-until")],
            _=>[]
        };
    }
    private static readonly IReadOnlyDictionary<string,string> NativeOpFields=new Dictionary<string,string>
    {
        ["Sel"]="native-selection",["Side"]="native-side",["SelZone"]="native-zone",["SelCount"]="integer",["Filter"]="native-filter",["Amount"]="integer",["Count"]="integer",["Attack"]="integer",["Defense"]="integer",["Keyword"]="native-keyword",["Value"]="integer",["Name"]="native-card",["Until"]="native-until",["OpZone"]="native-op-zone",["Condition"]="native-condition",["ConditionRaw"]="text",["CondUnitTypes"]="native-types",["CondComparison"]="native-comparison",["CondValue"]="integer",["Counter"]="counter-name",["AmountFromCounter"]="boolean"
    };
    public static IReadOnlyList<string> Choices(string type)=>type switch
    {
        "native-operation"=>OrcEffectEditing.Ops.Select(o=>o.Id).ToArray(),"native-selection"=>OrcEffectEditing.Sels.Select(o=>o.Id).ToArray(),
        "native-side"=>OrcEffectEditing.Sides.Select(o=>o.Id).ToArray(),"native-zone"=>OrcEffectEditing.SelZones.Select(o=>o.Id).ToArray(),
        "native-op-zone"=>OrcEffectEditing.OpZones.Select(o=>o.Id).ToArray(),"native-until"=>OrcEffectEditing.UntilChoices.Select(o=>o.Id).ToArray(),
        "native-keyword"=>OrcEffectEditing.KeywordChoices.Select(o=>o.Id).ToArray(),"native-unit-type" or "native-types"=>OrcEffectEditing.UnitTypes.Where(o=>o.Id.Length>0).Select(o=>o.Id).ToArray(),
        "native-condition"=>OrcEffectEditing.Conditions.Select(o=>o.Id).ToArray(),"native-comparison"=>OrcEffectEditing.Comparisons.Select(o=>o.Id).ToArray(),
        "legacy-operation"=>DiyCardCompiler.Operations.Select(o=>o.Id).ToArray(),"legacy-target"=>DiyCardCompiler.Targets.Select(o=>o.Id).ToArray(),
        "rarity"=>DiyCardCompiler.Rarities.Select(o=>o.Id).ToArray(),"side"=>["self","friendly","enemy","both","any"],
        "zone"=>InspectorVocabulary.Choices("zone"),_=>InspectorVocabulary.Choices(type)
    };
    public static string ChoiceLabel(string type,string value)=>type switch
    {
        "native-operation"=>OrcEffectEditing.Op(value)?.Label??value,"legacy-operation"=>DiyCardCompiler.Operations.FirstOrDefault(o=>o.Id==value)?.Label??value,
        "legacy-target"=>DiyCardCompiler.Targets.FirstOrDefault(o=>o.Id==value)?.Label??value,"native-condition"=>value.Length==0?"无额外条件":OrcEffectEditing.Conditions.FirstOrDefault(o=>o.Id==value)?.Label??value,
        "native-until"=>OrcEffectEditing.UntilChoices.FirstOrDefault(o=>o.Id==value)?.Label??value,"native-unit-type" or "native-types"=>OrcEffectEditing.UnitTypes.FirstOrDefault(o=>o.Id==value)?.Label??value,
        _=>InspectorPresentation.Value(value)
    };
    public static JsonNode? Initial(InspectorField field)=>field.Type switch
    {
        "integer" or "value" or "predicate" or "condition" or "trigger"=>null,"boolean"=>JsonValue.Create(false),
        "actions" or "effectList" or "options" or "values" or "queries" or "conditions" or "strings" or "keywords" or "cardIds" or "native-ops" or "native-types" or "targets"=>new JsonArray(),
        "filter" or "native-filter" or "cardPool"=>new JsonObject(),_=>field.Initial?.DeepClone()??JsonValue.Create("")
    };
}
