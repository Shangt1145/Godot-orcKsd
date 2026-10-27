using System.Text.Json;
using Orc.Packages;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 卡包的**展示层**数据（卡面文本、风味、选靶声明）。
///
/// <para>
/// 这三份文件是装饰，不是规则：它们和卡定义对不上时，只能丢掉那一条并留告警，
/// **绝不能让整包卡消失**。历史故障（2026-10-26 玩家反馈）：`display-texts.json` 里一条
/// 指向"已变非法/已删除的卡"的孤儿键直接抛异常，连锁导致工坊一览、单卡编辑、图鉴、构筑、
/// 开局五处全挂，而所有自救入口都排在这行之后——玩家在 UI 里没有任何出路。
/// </para>
///
/// <para>
/// 不变量：<see cref="Texts"/> / <see cref="Plans"/> / <see cref="Flavor"/> 对任何输入都不抛
/// （OOM 除外），拿不准就退化到"少一条展示数据"。
/// </para>
/// </summary>
internal static class PackPresentationData
{
    public static string Flavor(string root, string localId, ICollection<string>? warnings = null)
    {
        var path = Path.Combine(root, "workshop.presentation.json");
        if (!File.Exists(path)) return "";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (Exception error) when (IsRecoverable(error))
        {
            warnings?.Add($"workshop.presentation.json 无法解析（已忽略风味与作者数据）：{error.Message}");
            return "";
        }
        using (doc)
        {
            return doc.RootElement.TryGetProperty("cards", out var cards) && cards.TryGetProperty(localId, out var card)
                && card.TryGetProperty("flavor", out var flavor) ? flavor.GetString() ?? "" : "";
        }
    }

    public static IReadOnlyDictionary<string,string> Texts(CardPackContent content, ICollection<string>? warnings = null)
    {
        var result = new Dictionary<string,string>(content.Texts, StringComparer.Ordinal);
        var path = Path.Combine(content.RootDirectory,"display-texts.json");
        if (!File.Exists(path)) return result;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (Exception error) when (IsRecoverable(error))
        {
            // 整个文件坏掉也只丢展示文本：卡定义与规则文本不受影响。
            warnings?.Add($"display-texts.json 无法解析（已忽略卡面文本，规则不变）：{error.Message}");
            return result;
        }
        using(doc)
        foreach(var item in doc.RootElement.EnumerateObject())
        {
            if(!content.Definitions.Any(c=>c.Id==content.Prefix+item.Name))
            {
                // 孤儿键：卡要么变非法（Definition=null）、要么被删/改名、要么只活在历史版本里。
                warnings?.Add($"卡面文本引用了不存在的卡：{item.Name}（已忽略该条，其余卡正常）");
                continue;
            }
            if(result.ContainsKey(item.Name))
            {
                warnings?.Add($"卡面文本与规则文本重复：{item.Name}（以规则文本 texts.json 为准）");
                continue;
            }
            result[item.Name] = item.Value.ValueKind switch
            {
                JsonValueKind.String => item.Value.GetString() ?? "",
                JsonValueKind.Null => "",
                _ => item.Value.GetRawText(),
            };
        }
        return result;
    }

    public static IReadOnlyDictionary<string,string[]> Plans(CardPackContent content, ICollection<string>? warnings = null)
    {
        var result=new Dictionary<string,string[]>(StringComparer.Ordinal);
        var path=Path.Combine(content.RootDirectory,"desktop-effects.json");
        if(File.Exists(path))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
            catch (Exception error) when (IsRecoverable(error))
            {
                warnings?.Add($"desktop-effects.json 无法解析（已忽略选靶声明）：{error.Message}");
                doc = null!;
            }
            if (doc is not null)
            using (doc)
            foreach(var item in doc.RootElement.EnumerateObject())
            {
                if(!content.Definitions.Any(c=>c.Id==content.Prefix+item.Name))
                {
                    warnings?.Add($"选靶声明引用了不存在的卡：{item.Name}（已忽略该条）");
                    continue;
                }
                if(item.Value.ValueKind!=JsonValueKind.Array)
                {
                    warnings?.Add($"选靶声明的值不是数组：{item.Name}（已忽略该条）");
                    continue;
                }
                result[content.Prefix+item.Name]=item.Value.EnumerateArray()
                    .Select(e=>e.ValueKind==JsonValueKind.String?e.GetString()!:e.GetRawText()).ToArray();
            }
        }
        var presentation = Path.Combine(content.RootDirectory,"workshop.presentation.json");
        if(File.Exists(presentation))
        {
            JsonDocument metadata;
            try { metadata = JsonDocument.Parse(File.ReadAllText(presentation)); }
            catch (Exception error) when (IsRecoverable(error))
            {
                warnings?.Add($"workshop.presentation.json 无法解析（已忽略作者声明图）：{error.Message}");
                return result;
            }
            using(metadata)
            if(metadata.RootElement.TryGetProperty("effectAuthoring",out var effects))
                foreach(var declaration in content.Declarations)
                {
                    var graphs=new List<string>();
                    foreach(var prefab in declaration.PrefabIds)
                    {
                        var id=prefab.StartsWith(content.Prefix,StringComparison.Ordinal)?prefab[content.Prefix.Length..]:prefab;
                        if(effects.TryGetProperty(id,out var effect)&&effect.TryGetProperty("rules",out var rules)
                           &&rules.ValueKind==JsonValueKind.Array)
                            foreach(var rule in rules.EnumerateArray())
                                if(rule.TryGetProperty("DesktopJson",out var graph)&&graph.ValueKind==JsonValueKind.String) graphs.Add(graph.GetString()!);
                    }
                    if(graphs.Count>0) result[declaration.CardId]=(result.GetValueOrDefault(declaration.CardId)??[]).Concat(graphs).ToArray();
                }
        }
        return result;
    }

    /// <summary>展示层文件可能已被玩家手工改坏：读/解析失败都是"少一条装饰"，不是致命错误。</summary>
    private static bool IsRecoverable(Exception error) =>
        error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException;
}
