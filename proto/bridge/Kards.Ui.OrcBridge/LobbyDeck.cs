using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 大厅用卡组构建：从磁盘卡包现场组一副**服务端认得的**合法卡组。
///
/// <para>
/// **存在的理由**：玩家自建卡组用的是 catalog id（<c>av76/command/-1</c>），
/// 而房间填充要的是应用层全名（<c>pack/{包id}/{局部id}</c>）——两套体系当前没有映射表，
/// 所以联机侧只能先从卡包直接组卡。等映射落地后本类可退役。
/// </para>
///
/// <para>
/// 三条硬规则（来自 <c>DeckRules</c> 与实测）：39 张、单一主国（盟国留空）、
/// <b>同名上限按卡名累计</b>；另外缺 <c>effects</c> 组件的指令卡会被引擎静默丢弃，必须排除。
/// </para>
/// </summary>
public static class LobbyDeck
{
    /// <summary>官方卡组张数（总部随玩家自动生成，不占位）。</summary>
    public const int DefaultSize = 39;

    /// <summary>组卡的产出：包 id ＋ 应用层全名卡组 ＋ 主国。</summary>
    public sealed record Result(string PackageId, string Faction, IReadOnlyList<string> CardIds);

    /// <summary>从 <paramref name="packsRoot"/> 下挑一个包组卡（优先 <c>builtin</c>，其次按目录名序）。</summary>
    /// <param name="requiredLocalId">
    /// 保证这张卡在卡组里（验证探针用：某些卡要特定手牌才能触发选靶/多步选择，随机发牌等不到）。
    /// 卡池里没有就忽略；有但没被选中，就替换掉最后一张（张数与国籍都不变）。
    /// </param>
    /// <exception cref="InvalidOperationException">没有任何包能凑出合法卡组；消息含逐包原因。</exception>
    public static Result Build(string packsRoot, int size = DefaultSize, string? requiredLocalId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packsRoot);
        if (!Directory.Exists(packsRoot))
            throw new DirectoryNotFoundException($"卡包根目录不存在：{packsRoot}");

        var packages = Directory.EnumerateDirectories(packsRoot)
            .Select(directory => (Directory: directory, Manifest: ReadManifest(directory)))
            .Where(pair => pair.Manifest is not null)
            .OrderBy(pair => Path.GetFileName(pair.Directory) == "builtin" ? 0 : 1)
            .ThenBy(pair => pair.Directory, StringComparer.Ordinal)
            .ToArray();

        if (packages.Length == 0)
            throw new InvalidOperationException($"卡包根目录下没有可用的包（缺 manifest.json）：{packsRoot}");

        var failures = new List<string>();
        foreach (var (directory, manifest) in packages)
        {
            var packageId = manifest!["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(packageId)) continue;

            // 跨包同名会和"同名上限"互相挤占，所以一副卡组只取一个包。
            var cards = ReadCards(directory);
            if (cards.Count == 0) { failures.Add($"{packageId}: 无可组卡的卡定义"); continue; }

            // 指定了必带卡就以它所在国为主国——否则"想要这张卡"和"单一主国"会打架
            // （16 张选靶卡分散在苏/美/英/日，德国一张都没有）。
            var requiredFaction = string.IsNullOrEmpty(requiredLocalId)
                ? null
                : cards.Where(card => card.LocalId == requiredLocalId)
                    .Select(card => card.Faction)
                    .FirstOrDefault();

            // 直接按国籍收窄卡池，而不是只在排序里给个"优先"——
            // 后者会在主国卡池凑不满时静默回落，产出"Germany + 苏联卡"这种混编卡组。
            Composed? composed = null;
            if (!string.IsNullOrEmpty(requiredFaction))
            {
                // 国籍比较必须忽略大小写：卡包里 Germany/germany、Soviet/soviet 是混着写的，
                // 严格比较会把同一国拆成两个不足 39 张的池子，逼出混编卡组。
                var scoped = cards
                    .Where(card => string.Equals(card.Faction, requiredFaction, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                composed = Compose(scoped, size);
            }
            composed ??= Compose(cards, size, requiredFaction);

            if (composed is null)
            {
                failures.Add($"{packageId}: {(requiredFaction is null ? "" : $"{requiredFaction} ")}卡池凑不满 {size} 张");
                continue;
            }

            if (!string.IsNullOrEmpty(requiredLocalId)
                && cards.FirstOrDefault(card => card.LocalId == requiredLocalId) is { LocalId: not null } required)
            {
                // 补足到同名上限，而不是无脑放满：卡池里本来就可能已经入选了这张卡，
                // 直接覆盖末尾 N 张会变成 6 张（服务端会以"超过上限"拒收整个卡组）。
                var allowed = Math.Min(required.CopyLimit, size);
                var existing = composed.CardIds.Count(id => id == requiredLocalId);
                var missing = Math.Max(0, allowed - existing);
                for (var i = size - 1; i >= 0 && missing > 0; i--)
                {
                    if (composed.CardIds[i] == requiredLocalId) continue;
                    composed.CardIds[i] = requiredLocalId;
                    missing--;
                }
            }

            // 服务端卡池的键是应用层 ID，房间填充不做裸 id 解析，必须补前缀。
            var cardIds = composed.CardIds.Select(localId => $"pack/{packageId}/{localId}").ToArray();
            return new Result(packageId, composed.Faction, cardIds);
        }

        throw new InvalidOperationException("没有能凑出合法卡组的包：" + string.Join("；", failures));
    }

    private static JsonObject? ReadManifest(string directory)
    {
        var path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private readonly record struct LobbyCard(string LocalId, string Name, string Faction, int CopyLimit);

    private static List<LobbyCard> ReadCards(string packageDirectory)
    {
        var cards = new List<LobbyCard>();
        var cardsDirectory = Path.Combine(packageDirectory, "cards");
        if (!Directory.Exists(cardsDirectory)) return cards;

        foreach (var file in Directory.EnumerateFiles(cardsDirectory, "*.card.json"))
        {
            JsonObject? body;
            try
            {
                body = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            if (body is null) continue;

            var localId = body["id"]?.GetValue<string>() ?? "";
            var name = body["name"]?.GetValue<string>() ?? localId;
            var faction = "";
            var limit = 4;
            var isOrder = false;
            var hasEffects = false;

            if (body["components"] is JsonArray components)
            {
                foreach (var node in components)
                {
                    if (node is not JsonObject component) continue;
                    switch (component["component"]?.GetValue<string>())
                    {
                        case "factionCost":
                            faction = component["faction"]?.GetValue<string>() ?? "";
                            break;
                        case "tagData":
                            limit = (component["rarity"]?.GetValue<string>() ?? "").ToLowerInvariant() switch
                            {
                                "elite" => 1, "special" => 2, "limited" => 3, _ => 4,
                            };
                            break;
                        case "typeCategory":
                            if (component["type"] is JsonArray types)
                                foreach (var type in types)
                                    if (type?.GetValue<string>() == "order") isOrder = true;
                            break;
                        case "effects":
                            hasEffects = true;
                            break;
                    }
                }
            }

            // 缺 effects 的指令卡引擎不生成定义（静默丢弃），放进卡组必然 deck-invalid。
            if (isOrder && !hasEffects) continue;
            if (localId.Length > 0 && faction.Length > 0)
                cards.Add(new LobbyCard(localId, name, faction, limit));
        }

        return cards;
    }

    private sealed record Composed(string Faction, List<string> CardIds);

    /// <summary>选一国凑 <paramref name="size"/> 张；凑不满返回 null。</summary>
    /// <param name="preferredFaction">优先尝试的国家（调用方指定必带卡时用它的国籍）。</param>
    private static Composed? Compose(List<LobbyCard> cards, int size, string? preferredFaction = null)
    {
        var factions = cards.GroupBy(card => card.Faction, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => string.Equals(group.Key, preferredFaction, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal);

        foreach (var faction in factions)
        {
            var pool = faction.OrderBy(card => card.LocalId, StringComparer.Ordinal).ToArray();
            var deck = new List<string>(size);
            var usedByName = new Dictionary<string, int>(StringComparer.Ordinal);

            while (deck.Count < size)
            {
                var progressed = false;
                foreach (var card in pool)
                {
                    var used = usedByName.GetValueOrDefault(card.Name);
                    if (used >= card.CopyLimit) continue;
                    deck.Add(card.LocalId);
                    usedByName[card.Name] = used + 1;
                    progressed = true;
                    if (deck.Count == size) break;
                }
                if (!progressed) break;
            }

            if (deck.Count == size) return new Composed(faction.Key, deck);
        }

        return null;
    }
}
