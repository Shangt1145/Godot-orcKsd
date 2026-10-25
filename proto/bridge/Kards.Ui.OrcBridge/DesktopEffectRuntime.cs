using System.Text.Json;
using Orc.Cards;
using Orc.Game.Cards;
using Orc.Game.Effects;

namespace Kards.Ui.OrcBridge;

/// <summary>Editor keyword names are translated before entering the native authoring runtime.</summary>
public static class DesktopEffectRuntime
{
    public static string Keyword(string key)
    {
        if(WorkshopAuthoring.TryKeyword(key,out var custom))return custom;
        var catalog=DesktopOrc.Catalog.GetProperty("keywords");
        return catalog.TryGetProperty(key,out var item)?item.GetProperty("cn").GetString()!:key;
    }
    public static void ValidateFilter(JsonElement filter)=>AuthoringRuntime.ValidateFilter(filter);
    public static bool Matches(Card card,string json)=>AuthoringRuntime.Matches(card,json);
}