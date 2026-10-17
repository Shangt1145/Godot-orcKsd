using System.Text;
using System.Text.Json;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge.OrcText;

namespace Kards.Ui.OrcBridge;

/// <summary>The original Desktop syntax compiler and Inspector model, isolated from match execution.
/// Runs inside the Godot process without Node or a browser. Native effects still compile and run in OrC-KSD.</summary>
public static class DesktopOrc
{
    private static readonly object Gate = new();
    private static Microsoft.ClearScript.V8.V8ScriptEngine? _runtime;
    private static readonly string[] Sources = ["engine", "primitives-extra", "primitives", "effects", "effect-primitives",
        "compiler", "effect-contract", "orc", "card-import", "effect-inspector"];
    private static string Resource(string name)
    {
        var assembly=typeof(DesktopOrc).Assembly;
        var resource=assembly.GetManifestResourceNames().FirstOrDefault(n=>n.Replace('\\','/')=="Kards.Ui.Desktop."+name);
        using var source=(resource is null ? null : assembly.GetManifestResourceStream(resource))
            ?? throw new InvalidDataException("缺少 OrC 资源："+name);
        using var reader=new StreamReader(source,Encoding.UTF8);return reader.ReadToEnd();
    }
    public static string TutorialText=>Resource("inspector-tutorial.txt");
    public static JsonElement Request(object request)
    {
        lock(Gate)
        {
            if(_runtime is null)
            {
                // No host objects, filesystem, process or network APIs are exposed to source scripts.
                var engine=new Microsoft.ClearScript.V8.V8ScriptEngine();
                try
                {
                    engine.Execute("var console={log(){},warn(){},error(){}};");
                    foreach(var name in Sources)engine.Execute(name+".js",Resource("js/"+name+".js"));
                    _runtime=engine;
                }
                catch{engine.Dispose();throw;}
            }
            _runtime.Execute("var request=JSON.parse("+JsonSerializer.Serialize(JsonSerializer.Serialize(request))+");");
            using var timer=new System.Threading.Timer(_=>{try{_runtime?.Interrupt();}catch(ObjectDisposedException){}},null,TimeSpan.FromSeconds(15),Timeout.InfiniteTimeSpan);
            try
            {
                var json=(string)_runtime.Evaluate("JSON.stringify("+Resource("request.js")+")");
                using var result=JsonDocument.Parse(json);return result.RootElement.Clone();
            }
            catch(Microsoft.ClearScript.ScriptEngineException e){throw new InvalidDataException("OrC 解析失败："+e.Message,e);}
        }
    }
    public static void Stop(){lock(Gate){_runtime?.Dispose();_runtime=null;}}
    public static JsonElement CompileGraph(string text,string cardType,IReadOnlyList<UiCardDefinition>? catalog=null,string? cardJson=null) =>
        Request(new {method="compile",text,card=cardJson is null?(object)new {cardType}:JsonSerializer.Deserialize<JsonElement>(cardJson),pool=(catalog??[]).Select(CardMetadata).ToArray()});
    internal static object CardMetadata(UiCardDefinition c) => new
    { id=c.CardId,name=c.Name,cardType=c.CardType,unitType=c.UnitType,cost=c.Cost,attack=c.BaseAttack,defense=c.BaseDefense,opCost=c.BaseOpCost,set=c.Set,rarity=c.Rarity,text=c.Text };

    internal static OrcCompileResult Compile(string text,string cardType,IReadOnlyList<UiCardDefinition>? catalog=null,string? cardJson=null)
    {
        var root=CompileGraph(text,cardType,catalog,cardJson);
        var effects=root.GetProperty("effects").EnumerateArray().Select(e=>new OrcParsedEffect(
            e.TryGetProperty("trigger",out var t)?t.GetString()??"unknown":"unknown","on_event",[],DesktopJson:e.GetRawText())).ToArray();
        var diagnostics=root.GetProperty("diagnostics").EnumerateArray().Select(d=>new OrcDiagnostic(
            d.GetProperty("code").GetString()!,d.GetProperty("message").GetString()!)).ToList();
        var keywords=new List<OrcKeywordDecl>();
        var map=root.GetProperty("kwMap");var values=root.GetProperty("kwValues");
        var keywordCatalog=Catalog.GetProperty("keywords");
        foreach(var pair in map.EnumerateObject())
        {
            var cn=keywordCatalog.TryGetProperty(pair.Name,out var k)&&k.TryGetProperty("cn",out var label)?label.GetString()!:pair.Name;
            var id=cn=="守护"?Orc.Game.Cards.KeywordIds.Guard:cn;
            var quantified=values.TryGetProperty(pair.Name,out var value);
            keywords.Add(new(id,quantified?value.GetInt32():1,quantified));
        }
        return new(root.GetProperty("source").GetString()!,effects,keywords,diagnostics) {DesktopResultJson=root.GetRawText()};
    }
    private static readonly Lazy<JsonElement> CatalogValue=new(()=>Request(new {method="catalog"}));
    public static JsonElement Catalog=>CatalogValue.Value;
}
