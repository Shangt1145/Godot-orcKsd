using System.Text.Json;
using Orc.Packages;

namespace Kards.Ui.OrcBridge;

/// <summary>Display-only text for cards whose effects are already compiled inline, plus native selector plans.</summary>
internal static class PackPresentationData
{
    public static string Flavor(string root, string localId)
    {
        var path = Path.Combine(root, "workshop.presentation.json");
        if (!File.Exists(path)) return "";
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("cards", out var cards) && cards.TryGetProperty(localId, out var card)
            && card.TryGetProperty("flavor", out var flavor) ? flavor.GetString() ?? "" : "";
    }
    public static IReadOnlyDictionary<string,string> Texts(CardPackContent content)
    {
        var result = new Dictionary<string,string>(content.Texts, StringComparer.Ordinal);
        var path = Path.Combine(content.RootDirectory,"display-texts.json");
        if (!File.Exists(path)) return result;
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        foreach(var item in doc.RootElement.EnumerateObject())
        {
            if(!content.Definitions.Any(c=>c.Id==content.Prefix+item.Name))throw new InvalidDataException("卡面文本引用了不存在的卡："+item.Name);
            if(result.ContainsKey(item.Name))throw new InvalidDataException("卡面文本同时声明了文本编译与已编译效果："+item.Name);
            result.Add(item.Name,item.Value.GetString()!);
        }
        return result;
    }
    public static IReadOnlyDictionary<string,string[]> Plans(CardPackContent content)
    {
        var result=new Dictionary<string,string[]>(StringComparer.Ordinal);
        var path=Path.Combine(content.RootDirectory,"desktop-effects.json");
        if(File.Exists(path))
        {
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        foreach(var item in doc.RootElement.EnumerateObject())
        {
            if(!content.Definitions.Any(c=>c.Id==content.Prefix+item.Name))throw new InvalidDataException("选靶声明引用了不存在的卡："+item.Name);
            result.Add(content.Prefix+item.Name,item.Value.EnumerateArray()
                .Select(e=>e.ValueKind==JsonValueKind.String?e.GetString()!:e.GetRawText()).ToArray());
        }
        }
        var presentation = Path.Combine(content.RootDirectory,"workshop.presentation.json");
        if(File.Exists(presentation))
        {
            using var metadata=JsonDocument.Parse(File.ReadAllText(presentation));
            if(metadata.RootElement.TryGetProperty("effectAuthoring",out var effects))
                foreach(var declaration in content.Declarations)
                {
                    var graphs=new List<string>();
                    foreach(var prefab in declaration.PrefabIds)
                    {
                        var id=prefab.StartsWith(content.Prefix,StringComparison.Ordinal)?prefab[content.Prefix.Length..]:prefab;
                        if(effects.TryGetProperty(id,out var effect)&&effect.TryGetProperty("rules",out var rules))
                            foreach(var rule in rules.EnumerateArray())
                                if(rule.TryGetProperty("DesktopJson",out var graph)&&graph.ValueKind==JsonValueKind.String) graphs.Add(graph.GetString()!);
                    }
                    if(graphs.Count>0) result[declaration.CardId]=(result.GetValueOrDefault(declaration.CardId)??[]).Concat(graphs).ToArray();
                }
        }
        return result;
    }
}
