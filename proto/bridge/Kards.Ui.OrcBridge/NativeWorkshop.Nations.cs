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
    /// <summary>
    /// 登记本包声明的国家（**只登记，不写盘**）。
    /// <para>
    /// 刻意不在这里补 <c>factions.json</c>：这个方法是"打开/发现包"的读路径，而包可能是别人的
    /// （导入的第三方包）。读一次就往人家目录里塞文件，会改掉包指纹、也会让
    /// "导入-重启-导出逐字节一致"的承诺失效（`ImportRestartExportAndDuplicateRetainExactFiles` 实测红）。
    /// 落盘只发生在**我们自己写这个包**的时候：新建国家、保存卡包、以及联机会话包。
    /// </para>
    /// </summary>
    internal static void RegisterNations(string root)
    {
        foreach (var nation in ReadNations(root)) Faction.RegisterCustom(nation.Id);
    }

    /// <summary>
    /// 把本包声明的国家写成引擎可读的 <c>factions.json</c>（自描述声明）。
    /// <para>
    /// **为什么必须落盘**：卡定义的 <c>faction</c> 是引擎**进程级值域**的硬校验项。只在宿主进程里
    /// <c>RegisterCustom</c>，换一个进程装载这个包（桌面联机＝独立服务端子进程）那些阵营就不存在，
    /// 该包每张卡都会在装载期被判失败——卡池里一张都没有，而读面还报"一切正常"。
    /// 声明随包走，任何进程装它都自洽；导出的 zip 也一样。
    /// </para>
    /// <para>幂等：内容没变不写盘（卡包指纹按文件内容算，不做无意义改写）。</para>
    /// </summary>
    internal static void SaveFactions(string root, IReadOnlyList<UiDiyChoice>? nations = null)
    {
        nations ??= ReadNations(root);
        var path = Path.Combine(root, "factions.json");
        // 游戏自带的独立国家（av76/deran/usg/un/nsf）不在引擎常量表里，但每张工坊卡都可能用它们，
        // 所以声明里始终带上；工坊自建国家追加在后面。
        var declared = Kards.Ui.Core.DeckNations.CustomNations
            .Select(Kards.Ui.Core.DeckNations.Canonical)
            .Concat(nations.Select(n => n.Id))
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var document = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["factions"] = new JsonArray(declared
                .Select(id => (JsonNode)new JsonObject { ["id"] = id, ["major"] = true }).ToArray()),
        };
        var text = document.ToJsonString();
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text);
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
