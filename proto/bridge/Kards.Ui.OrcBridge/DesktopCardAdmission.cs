using System.Text.Json;
using Orc.Game.Cards;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

internal static class DesktopCardAdmission
{
    private static readonly HashSet<string> Fields=new(StringComparer.Ordinal)
    {"id","name","cardType","unitType","cost","attack","defense","opCost","effects","text","set","nation","rarity","art","file","src","custom","flavor","effectStatus","compileWarnings","kwMap","kwValues","keywords"};
    public static IReadOnlyList<KeywordDeclaration> Read(DiyCardDraft draft)
    {
        if(draft.DesktopCardJson is not { } text)return [];
        using var doc=JsonDocument.Parse(text);var root=doc.RootElement;
        foreach(var field in root.EnumerateObject())if(!Fields.Contains(field.Name))throw new InvalidDataException("OrC-KSD 卡面字段尚无无损执行适配："+field.Name);
        var result=new List<KeywordDeclaration>();
        if(root.TryGetProperty("kwMap",out var map))foreach(var k in map.EnumerateObject())
        {
            if(k.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("词条开关必须是布尔值："+k.Name);
            if(!k.Value.GetBoolean())continue;
            var catalog=DesktopOrc.Catalog.GetProperty("keywords");
            var cn=catalog.TryGetProperty(k.Name,out var item)&&item.TryGetProperty("cn",out var label)?label.GetString()!:k.Name;
            if(WorkshopAuthoring.TryKeyword(cn,out var authored))cn=authored;
            else if(!KeywordIds.All.Contains(cn,StringComparer.Ordinal))throw new InvalidDataException("OrC-KSD 尚未接入词条："+cn);
            int? value=null;
            if(root.TryGetProperty("kwValues",out var values)&&values.TryGetProperty(k.Name,out var parameter))value=parameter.GetInt32();
            result.Add(new(cn,value));
        }
        return result;
    }
}
