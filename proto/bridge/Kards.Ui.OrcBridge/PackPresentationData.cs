using System.Text.Json;
using Orc.Packages;

namespace Kards.Ui.OrcBridge;

/// <summary>Display-only text for cards whose effects are already compiled inline, plus native selector plans.</summary>
internal static class PackPresentationData
{
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
        if(!File.Exists(path))return result;
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        foreach(var item in doc.RootElement.EnumerateObject())
        {
            if(!content.Definitions.Any(c=>c.Id==content.Prefix+item.Name))throw new InvalidDataException("选靶声明引用了不存在的卡："+item.Name);
            result.Add(content.Prefix+item.Name,item.Value.EnumerateArray()
                .Select(e=>e.ValueKind==JsonValueKind.String?e.GetString()!:e.GetRawText()).ToArray());
        }
        return result;
    }
}
