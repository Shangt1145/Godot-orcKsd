using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Kards.Ui.Contracts;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

public sealed partial class NativeWorkshop
{
    public IReadOnlyList<UiDiyChoice> DraftNations => ReadNations(Editor.Session!.TemporaryRoot);
    internal static IReadOnlyList<UiDiyChoice> ReadNations(string root)
    {
        var file = Path.Combine(root, "workshop.presentation.json");
        if (!File.Exists(file)) return [];
        return (JsonNode.Parse(File.ReadAllText(file))?["nations"] as JsonArray ?? new()).OfType<JsonObject>()
            .Select(n => new UiDiyChoice(n["id"]?.ToString() ?? "", n["name"]?.ToString() ?? "")).ToArray();
    }
    internal static void RegisterNations(string root)
    {
        foreach (var nation in ReadNations(root)) Faction.RegisterCustom(nation.Id);
    }
    public UiDiyChoice CreateNation(string name)
    {
        name = name.Trim(); if (name.Length is < 1 or > 80) throw new InvalidDataException("国家名称需要 1–80 个字符。");
        var existing = DraftNations.FirstOrDefault(n => n.Label == name); if (existing is not null) return existing;
        var id = "nation." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Editor.Session!.PackageId + "/" + name)))[..20].ToLowerInvariant();
        var presentation = Presentation();
        if (presentation["nations"] is not JsonArray nations) { nations = new(); presentation["nations"] = nations; }
        nations.Add(new JsonObject { ["id"] = id, ["name"] = name }); Faction.RegisterCustom(id); SavePresentation(presentation);
        return new(id, name);
    }
}
