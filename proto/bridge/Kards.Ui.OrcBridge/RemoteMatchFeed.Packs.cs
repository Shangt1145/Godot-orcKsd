using System.Text.Json.Nodes;
using Kards.Ui.Core;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 联机卡包同步（房主 ↔ 客人）。
/// <para>
/// **2026-10-28 v4 迁移：整条传输线已停用。** 原实现走的是服务端六个方法
/// （<c>pool.manifest</c>／<c>pool.offer</c>／<c>pool.chunk</c>／<c>pool.commit</c>／<c>pool.fetch</c>／<c>pool.ack</c>），
/// 它们只存在于本地分支的 <c>ServerHost.PackTransfer.cs</c>；新引擎（<c>origin/main</c>）里没有这个文件，
/// 也就没有对端。按「一切以新引擎为准，本地的不管」的裁定，本仓库不再假装能同步。
/// </para>
/// <para>
/// **玩家侧后果（说清楚，不藏）**：联机对局只能用**双方都装好的**卡包；工坊自建卡包不能随对局传过去。
/// 因此带 DIY 卡组开房时，客人若没有对应卡包会停在「卡包未对齐」，而不是像以前那样自动下载。
/// 这条不是本层的临时降级开关——是能力本身在新引擎里不存在，恢复的前提是引擎把 <c>pool.*</c> 加回来。
/// </para>
/// </summary>
public sealed partial class RemoteMatchFeed
{
    /// <summary>本机/对端是否支持卡包传输。v4 恒为 false（对端没有这套 RPC）。</summary>
    public bool SupportsPackTransfer => false;

    /// <summary>双方卡池是否已对齐。传输停用后不再自动对齐，只能靠双方装同一批包。</summary>
    public bool PacksAligned { get; private set; }

    /// <summary>同步状态文案（界面直接显示）。</summary>
    public string PackSyncStatus { get; private set; } = "等待卡包同步";

    private IReadOnlyList<string> _localPackRoots = [];
    private IReadOnlyList<string> _networkDeck = [];
    private readonly Dictionary<string, (string Root, JsonObject Info)> _localNetworkPacks = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _networkArt = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _networkTexts = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _networkFlavor = new(StringComparer.Ordinal);

    /// <summary>登记本局要用的本地卡包（保留调用面：界面/远程订阅仍按原签名调用）。</summary>
    public void ConfigurePackTransfer(NetworkWorkshopSession session, IReadOnlyList<string> deck)
    {
        _localPackRoots = session.Roots;
        _networkDeck = deck;
    }

    /// <summary>
    /// 同步卡包。v4 下**不做任何网络往返**：只把状态写成"不可用"，并检查本机是否具备开局所需卡包，
    /// 好让玩家在开局前就看到"缺哪个包"，而不是进对局后拿到一堆空牌。
    /// </summary>
    public Task SynchronizePacksAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        PacksAligned = false;
        if (_networkDeck.Count == 0)
        {
            PackSyncStatus = "本局不使用工坊卡包";
            return Task.CompletedTask;
        }

        // 本地包索引（仅用于给出"缺哪个包"的明确提示；不参与传输）。
        foreach (var root in _localPackRoots)
        {
            try
            {
                var manifestPath = Path.Combine(root, "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject manifest) continue;
                var id = manifest["id"]?.ToString();
                if (string.IsNullOrEmpty(id) || _localNetworkPacks.ContainsKey(id)) continue;
                _localNetworkPacks[id] = (root, manifest);
                LoadNetworkArt(root, id);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                GameLog.Warn("pack", "卡包索引失败：" + root + " · " + e.Message);
            }
        }

        var needed = _networkDeck.Where(id => id.StartsWith("pack/", StringComparison.Ordinal))
            .Select(id => id.Split('/')[1]).Distinct(StringComparer.Ordinal).ToArray();
        var missing = needed.Where(id => !_localNetworkPacks.ContainsKey(id)).ToArray();
        PackSyncStatus = missing.Length == 0
            ? "卡包齐备（联机卡包传输已停用，需双方各自安装）"
            : "本机缺少卡包：" + string.Join("、", missing) + "（联机卡包传输已停用，请让对方单独发给你）";
        PacksAligned = missing.Length == 0;
        return Task.CompletedTask;
    }

    private void LoadNetworkArt(string root, string id)
    {
        var textPath = Path.Combine(root, "display-texts.json");
        if (File.Exists(textPath) && JsonNode.Parse(File.ReadAllText(textPath)) is JsonObject texts)
            foreach (var (localId, text) in texts) _networkTexts["pack/" + id + "/" + localId] = text?.ToString() ?? "";
        var path = Path.Combine(root, "workshop.presentation.json");
        if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path))?["cards"] is not JsonObject cards) return;
        foreach (var (localId, node) in cards)
        {
            _networkFlavor["pack/" + id + "/" + localId] = node?["flavor"]?.ToString() ?? "";
            if (node?["art"]?.GetValue<string>() is { Length: > 0 } relative)
            {
                var full = PackPaths.Child(root, relative);
                if (File.Exists(full)) _networkArt["pack/" + id + "/" + localId] = (node["artKind"]?.ToString() == "full" ? "packfull://" : "packart://") + full;
            }
        }
    }

    private string? ResolveNetworkArt(string id) => _networkArt.TryGetValue(id, out var art) ? art : ArtPathResolver?.Invoke(id);
}
