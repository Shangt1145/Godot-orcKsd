using Godot;
using System.Text.Json;

namespace Kards.Ui;

internal sealed record BattleWeaponProfile(string Trace, int Shots, double Interval, double Travel, float Flash)
{
    private static readonly Dictionary<string, BattleWeaponProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rifle"] = new("bullet", 3, .065, .13, 22),
        ["machinegun"] = new("bullet", 6, .045, .12, 18),
        ["autocannon"] = new("cannon", 4, .12, .19, 30),
        ["cannon"] = new("cannon", 1, .24, .24, 42),
        ["artillery"] = new("shell", 1, .43, .43, 49),
        ["rocket"] = new("rocket", 3, .12, .34, 28),
        ["bomb"] = new("bomb", 1, .30, .30, 0),
        ["fighter"] = new("bullet", 4, .065, .13, 22),
        ["flame"] = new("flame", 1, .45, .45, 20)
    };
    private static Dictionary<string, string>? _overrides;
    internal static void Override(string id, string? style)
    {
        if (style is not null && !Profiles.ContainsKey(style)) throw new ArgumentException("未知武器演出类型", nameof(style));
        _overrides ??= new(StringComparer.Ordinal);
        if (style is null) _overrides.Remove(id); else _overrides[id] = style;
    }
    public static BattleWeaponProfile Resolve(UiCardView card)
    {
        if (_overrides is null)
        {
            _overrides = new(StringComparer.Ordinal);
            const string path = "res://proto/data/battle-weapons.json";
            if (Godot.FileAccess.FileExists(path))
                try
                {
                    using var json = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(path));
                    foreach (var item in json.RootElement.GetProperty("cards").EnumerateObject())
                        if (item.Value.GetString() is { } name && Profiles.ContainsKey(name)) _overrides[item.Name] = name;
                }
                catch (Exception e) { GD.PushWarning("武器演出配置未载入：" + e.Message); }
        }
        if (_overrides.TryGetValue(card.CardId, out var selected)) return Profiles[selected];
        var kind = card.Definition.UnitType?.ToLowerInvariant() ?? "infantry";
        return Profiles[kind switch
        {
            "bomber" => "bomb", "tank" or "cruiser" or "landcruiser" or "land_cruiser" => "cannon",
            "fighter" or "spacefighter" or "space_fighter" or "space" => "fighter",
            "artillery" => "artillery", _ => "rifle"
        }];
    }
}
