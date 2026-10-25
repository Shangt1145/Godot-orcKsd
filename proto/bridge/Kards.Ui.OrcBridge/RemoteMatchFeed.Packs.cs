using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Kards.Ui.OrcBridge;

public sealed partial class RemoteMatchFeed
{
    public bool SupportsPackTransfer { get; private set; }
    public bool PacksAligned { get; private set; }
    public string PackSyncStatus { get; private set; } = "等待卡包同步";
    private readonly SemaphoreSlim _packSync = new(1, 1);
    private IReadOnlyList<string> _localPackRoots = [];
    private IReadOnlyList<string> _networkDeck = [];
    private string _networkCache = "";
    private string _syncedFingerprint = "";
    private readonly Dictionary<string, (string Root, JsonObject Info)> _localNetworkPacks = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _networkArt = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _networkTexts = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _networkFlavor = new(StringComparer.Ordinal);
    public void ConfigurePackTransfer(NetworkWorkshopSession session, IReadOnlyList<string> deck)
    {
        _localPackRoots = session.Roots; _networkDeck = deck;
        _networkCache = Path.Combine(session.Directory, "received");
    }
    private async Task<JsonNode> PackCallAsync(string method, JsonObject? args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var response = await _client.CallRawAsync(method, args ?? new JsonObject(), timeout.Token).ConfigureAwait(false);
        if (response.Error is { } error) throw new InvalidOperationException(error.Message);
        return response.Result ?? throw new InvalidDataException("卡包同步响应为空。");
    }
    public async Task SynchronizePacksAsync(CancellationToken ct = default)
    {
        if (_networkCache.Length == 0 || AdmissionState != "admitted" || !await _packSync.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            if (!SupportsPackTransfer) { PackSyncStatus = "房主版本不支持工坊卡包同步，请更新房主程序。"; return; }
            var manifest = await PackCallAsync("pool.manifest", null, ct).ConfigureAwait(false);
            if (manifest["lobby"]?.GetValue<bool>() != true && _syncedFingerprint.Length > 0) return;
            if (_localNetworkPacks.Count == 0)
                foreach (var root in _localPackRoots)
                {
                    var info = NetworkPackageArchive.Describe(root);
                    _localNetworkPacks.Add(info["id"]!.ToString(), (root, info));
                }
            var remote = manifest["packages"]!.AsArray().OfType<JsonObject>().ToDictionary(p => p["id"]!.ToString(), StringComparer.Ordinal);
            var needed = _networkDeck.Where(id => id.StartsWith("pack/", StringComparison.Ordinal)).Select(id => id.Split('/')[1]).ToHashSet(StringComparer.Ordinal);
            var dependencies = _localNetworkPacks.ToDictionary(p => p.Key, p => Dependencies(p.Value.Root, p.Key), StringComparer.Ordinal);
            bool expanded;
            do
            {
                expanded = false;
                foreach (var (id, local) in _localNetworkPacks)
                {
                    if (needed.Contains(id)) foreach (var dependency in dependencies[id]) expanded |= needed.Add(dependency);
                    var parent = WorkshopPackagePresentation.Parent(local.Root, id);
                    if (parent is not null && needed.Contains(parent)) expanded |= needed.Add(id);
                }
            } while (expanded);
            var ordered = new List<string>(); var visited = new HashSet<string>(); var visiting = new HashSet<string>();
            void Visit(string id)
            {
                if (!needed.Contains(id) || !_localNetworkPacks.ContainsKey(id) || visited.Contains(id)) return;
                if (!visiting.Add(id)) throw new InvalidDataException("卡包依赖形成循环：" + id);
                foreach (var dependency in dependencies[id]) Visit(dependency);
                visiting.Remove(id); visited.Add(id); ordered.Add(id);
            }
            foreach (var id in needed) Visit(id);
            foreach (var id in ordered)
            {
                var local = _localNetworkPacks[id];
                if (remote.TryGetValue(id, out var existing))
                {
                    if (existing["digest"]!.ToString() != local.Info["digest"]!.ToString())
                        throw new InvalidDataException("同名卡包内容不一致：" + local.Info["name"] + "。请双方统一版本后重新开房。");
                    continue;
                }
                if (manifest["allowGuestPacks"]?.GetValue<bool>() != true)
                    throw new InvalidOperationException("房主缺少卡包「" + local.Info["name"] + "」，请房主开启“接收好友卡包”。");
                PackSyncStatus = "正在上传「" + local.Info["name"] + "」…";
                var offer = await PackCallAsync("pool.offer", new JsonObject { ["package"] = local.Info.DeepClone() }, ct).ConfigureAwait(false);
                if (offer["exists"]?.GetValue<bool>() == true) continue;
                var bytes = NetworkPackageArchive.Zip(local.Root);
                for (int offset = 0; offset < bytes.Length; offset += NetworkPackageArchive.ChunkBytes)
                {
                    var block = bytes.AsSpan(offset, Math.Min(NetworkPackageArchive.ChunkBytes, bytes.Length - offset)).ToArray();
                    await PackCallAsync("pool.chunk", new JsonObject { ["offset"] = offset, ["data"] = Convert.ToBase64String(block), ["sha256"] = NetworkPackageArchive.Hash(block) }, ct).ConfigureAwait(false);
                }
                await PackCallAsync("pool.commit", new JsonObject(), ct).ConfigureAwait(false);
            }
            manifest = await PackCallAsync("pool.manifest", null, ct).ConfigureAwait(false);
            var fingerprint = manifest["fingerprint"]!.ToString();
            PacksAligned = false;
            if (_syncedFingerprint != fingerprint)
            {
                foreach (var info in manifest["packages"]!.AsArray().OfType<JsonObject>())
                {
                    var id = info["id"]!.ToString(); var digest = info["digest"]!.ToString(); string root;
                    if (_localNetworkPacks.TryGetValue(id, out var own) && own.Info["digest"]!.ToString() == digest) root = own.Root;
                    else
                    {
                        root = Path.Combine(_networkCache, NetworkPackageArchive.Key(id + "/" + digest));
                        if (!Directory.Exists(root) || NetworkPackageArchive.Digest(root) != digest)
                        {
                            PackSyncStatus = "正在接收「" + info["name"] + "」…";
                            var size = info["bytes"]!.GetValue<int>();
                            if (size < 1 || size > NetworkPackageArchive.MaxBytes) throw new InvalidDataException("远端卡包大小无效。");
                            using var stream = new MemoryStream();
                            while (stream.Length < size)
                            {
                                var response = await PackCallAsync("pool.fetch", new JsonObject { ["id"] = id, ["digest"] = digest, ["offset"] = stream.Length }, ct).ConfigureAwait(false);
                                var encoded = response["data"]!.ToString();
                                if (encoded.Length > 350000) throw new InvalidDataException("远端分块过大。");
                                var block = Convert.FromBase64String(encoded);
                                if (block.Length == 0 || stream.Length + block.Length > size || NetworkPackageArchive.Hash(block) != response["sha256"]!.ToString()) throw new InvalidDataException("远端卡包分块校验失败。");
                                stream.Write(block);
                            }
                            var bytes = stream.ToArray();
                            if (NetworkPackageArchive.Hash(bytes) != info["sha256"]!.ToString()) throw new InvalidDataException("远端卡包校验失败。");
                            var temporary = root + "." + Guid.NewGuid().ToString("N") + ".partial";
                            try
                            {
                                NetworkPackageArchive.Extract(bytes, temporary);
                                if (NetworkPackageArchive.Digest(temporary) != digest) throw new InvalidDataException("远端卡包内容校验失败。");
                                if (Directory.Exists(root)) Directory.Delete(root, true);
                                Directory.Move(temporary, root);
                            }
                            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
                        }
                    }
                    LoadNetworkArt(root, id);
                }
                if (manifest["lobby"]?.GetValue<bool>() == true && !IsObserver)
                    await PackCallAsync("pool.ack", new JsonObject { ["fingerprint"] = fingerprint }, ct).ConfigureAwait(false);
                _syncedFingerprint = fingerprint;
                if (_view is not null) await RefreshViewAsync(ct).ConfigureAwait(false);
            }
            PacksAligned = manifest["aligned"]?.GetValue<bool>() == true;
            PackSyncStatus = PacksAligned ? "双方卡池已对齐" : "本机卡包已同步，等待对方同步";
        }
        catch (Exception e) when (e is not OperationCanceledException) { PacksAligned = false; _syncedFingerprint = ""; PackSyncStatus = "卡包同步未完成：" + e.Message; }
        finally { _packSync.Release(); }
    }
    private static string[] Dependencies(string root, string id)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")))!;
        return (manifest["requires"] as JsonArray ?? new()).Select(n => n!.ToString())
            .Append(WorkshopPackagePresentation.Parent(root, id) ?? "").Where(s => s.Length > 0)
            .Select(s => s.StartsWith("pack/", StringComparison.Ordinal) ? s[5..] : s).Distinct(StringComparer.Ordinal).ToArray();
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
                var full = NetworkPackageArchive.Child(root, relative);
                if (File.Exists(full)) _networkArt["pack/" + id + "/" + localId] = (node["artKind"]?.ToString() == "full" ? "packfull://" : "packart://") + full;
            }
        }
    }
    private string? ResolveNetworkArt(string id) => _networkArt.TryGetValue(id, out var art) ? art : ArtPathResolver?.Invoke(id);
}
