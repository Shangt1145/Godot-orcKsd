using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kards.Ui.OrcBridge.Updates;

/// <summary>发布页上的一个文件。</summary>
/// <param name="Name">文件名（我们靠它认补丁的来龙去脉）。</param>
/// <param name="Size">字节数（显示用；0＝发布页没给）。</param>
/// <param name="Url">下载地址。</param>
public sealed record ReleaseAsset(string Name, long Size, string Url);

/// <summary>一个发布。</summary>
/// <param name="Tag">原始 tag（可能带 <c>v</c> 前缀）。</param>
/// <param name="Version">规范化后的版本号（去掉 <c>v</c>）。</param>
/// <param name="Draft">草稿：匿名读不到，但拿到了也一律忽略。</param>
/// <param name="Notes">发行说明正文（对话框里给作者看的「这版改了什么」）。</param>
/// <param name="Assets">文件清单。</param>
public sealed record ReleaseInfo(string Tag, string Version, bool Draft, string Notes, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// 一个可用的更新：<b>优先增量补丁</b>（按本机 buildId 精确匹配），没有就退回完整包。
/// </summary>
/// <param name="Version">目标版本。</param>
/// <param name="Asset">要下载的那个文件。</param>
/// <param name="IsPatch">true＝增量补丁（几十 MB），false＝完整包（约 1.2 GB）。</param>
/// <param name="Notes">发行说明。</param>
/// <param name="ChecksumsUrl">发布页的 <c>SHA256.txt</c> 地址（没有＝null，只能跳过校验）。</param>
public sealed record UpdateCandidate(
    string Version, ReleaseAsset Asset, bool IsPatch, string Notes, string? ChecksumsUrl);

/// <summary>
/// 从 GitHub 发布页挑出「这台机器该下哪个文件」。
/// <para>
/// 全部是纯函数：输入是 API 的 JSON 文本，输出是一个候选。**不发请求**——网络与磁盘在
/// <see cref="UpdateDownloader"/> 里，这样挑选规则可以在没有网络的测试里钉死。
/// </para>
/// <para>
/// 为什么要按 buildId 认补丁：发版脚本产出的补丁名是
/// <c>OrC-KSD-&lt;版本&gt;-from-&lt;源构建12&gt;-to-&lt;目标构建12&gt;.orcupdate</c>，
/// 补丁是**逐字节**的差分，源构建对不上就不能用。版本号相同但构建不同（热修）也照样能认出来。
/// </para>
/// </summary>
public static class ReleaseFeed
{
    /// <summary>游戏仓库（不是引擎仓库——玩家下的是游戏包）。</summary>
    public const string Repository = "Shangt1145/Godot-orcKsd";

    /// <summary>
    /// 发布列表。**不能用 <c>/releases/latest</c>**：它只返回非预发布的最新版，
    /// 而我们所有版本都是预发布，那个接口会一直返回空。
    /// </summary>
    public static string ListUrl => "https://api.github.com/repos/" + Repository + "/releases?per_page=30";

    /// <summary>
    /// 静态清单：挂在固定 tag <c>channel</c> 的发布上，每次发版用 <c>--clobber</c> 覆盖。
    /// <para>
    /// 为什么要它：<see cref="ListUrl"/> 走 <c>api.github.com</c>，未登录按**出口 IP** 限
    /// 60 次/小时。被限流时更新检查会静默跳过（只写日志），玩家完全不知道为什么没提示——
    /// 而运营商 NAT 后面的玩家是一整个小区共用这一个额度。这条 URL 走发布页 CDN，
    /// 不受 API 配额约束，所以它是首选，API 只当退路。
    /// </para>
    /// </summary>
    public static string ManifestUrl =>
        "https://github.com/" + Repository + "/releases/download/channel/latest.json";

    private static readonly System.Text.RegularExpressions.Regex PatchPattern = new(
        @"^OrC-KSD-(?<version>.+?)-from-(?<from>[0-9A-Fa-f]{12})-to-[0-9A-Fa-f]{12}\.orcupdate$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 兼容合集：一个文件里装多个「源构建 → 本版」的变体，更新器自己挑能用的那个
    /// （<c>PatchBuilder.Select</c>）。名字里没有源构建，所以它**对所有 buildId 都成立**——
    /// 跨了几个版本的玩家靠它一条补丁升上来，不必下 1.2 GB 全量。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex CompatiblePattern = new(
        @"^OrC-KSD-(?<version>.+)-compatible-to-[0-9A-Fa-f]{12}\.orcupdate$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 完整包。版本段必须**长得像版本号**——这一条不是洁癖：发布页上还有
    /// <c>OrC-KSD-offline-updater-1.2.0-win-x64.zip</c>，用宽松的 <c>.+</c> 会把**更新器自己**
    /// 当成游戏包下下来（真数据实测踩到过：跨版本玩家会被引导去下 74 MB 的更新器）。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex FullPattern = new(
        @"^OrC-KSD-(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.\-]*)?)-win-x64\.zip$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析静态清单（<see cref="ManifestUrl"/>）。形状刻意做成「只有一个发布」，
    /// 其余与 <see cref="Parse"/> 一致，挑选规则一行都不用改。
    /// </summary>
    /// <exception cref="JsonException">不是对象，或缺 tag。</exception>
    public static IReadOnlyList<ReleaseInfo> ParseManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("清单不是对象。");
        if (!root.TryGetProperty("tag", out var tagNode) || tagNode.GetString() is not { Length: > 0 } tag)
            throw new JsonException("清单缺 tag。");

        var notes = root.TryGetProperty("notes", out var notesNode) ? notesNode.GetString() ?? "" : "";
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("name", out var nameNode) || nameNode.GetString() is not { Length: > 0 } name) continue;
                if (!item.TryGetProperty("url", out var urlNode) || urlNode.GetString() is not { Length: > 0 } url) continue;
                var size = item.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsed) ? parsed : 0;
                assets.Add(new ReleaseAsset(name, size, url));
            }
        }

        var version = tag.StartsWith('v') ? tag[1..] : tag;
        return [new ReleaseInfo(tag, version, Draft: false, notes, assets)];
    }

    /// <summary>解析 <c>/releases</c> 的 JSON。坏条目跳过，整体坏掉才抛。</summary>
    /// <exception cref="JsonException">不是 JSON 数组。</exception>
    public static IReadOnlyList<ReleaseInfo> Parse(string json)    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("发布列表不是数组。");

        var releases = new List<ReleaseInfo>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var tag = Text(item, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var assets = new List<ReleaseAsset>();
            if (item.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in list.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object) continue;
                    var name = Text(asset, "name");
                    var url = Text(asset, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    assets.Add(new ReleaseAsset(name, Number(asset, "size"), url));
                }
            }

            releases.Add(new ReleaseInfo(
                tag,
                Normalize(tag),
                item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
                Text(item, "body") ?? "",
                assets));
        }
        return releases;
    }

    /// <summary>
    /// 挑出该下的那个文件：**最新且比本机新**的发布里，优先 buildId 对得上的增量补丁，否则完整包。
    /// </summary>
    /// <param name="releases">发布列表。</param>
    /// <param name="currentVersion">本机版本（<c>VERSION</c>／构建清单里的 Version）。</param>
    /// <param name="currentBuildId">本机构建 id（构建清单里的 BuildId，64 位十六进制）。</param>
    /// <returns>没有更新的版本、或最新版里没有可用文件＝null。</returns>
    public static UpdateCandidate? Select(IReadOnlyList<ReleaseInfo> releases, string currentVersion, string currentBuildId)
    {
        ArgumentNullException.ThrowIfNull(releases);
        if (string.IsNullOrWhiteSpace(currentVersion)) return null;

        ReleaseInfo? newest = null;
        foreach (var release in releases)
        {
            if (release.Draft) continue;
            if (Compare(release.Version, currentVersion) <= 0) continue;
            if (newest is null || Compare(release.Version, newest.Version) > 0) newest = release;
        }
        if (newest is null) return null;

        var from = currentBuildId.Length >= 12 ? currentBuildId[..12] : currentBuildId;

        // 先找「从我这台机器的构建出发」的补丁：名字里的 from 必须逐字对上，
        // 差一位就是另一个字节布局，补丁根本打不上（打上了也是错的）。
        var checksums = newest.Assets
            .FirstOrDefault(asset => string.Equals(asset.Name, "SHA256.txt", StringComparison.OrdinalIgnoreCase))?.Url;

        foreach (var asset in newest.Assets)
        {
            var match = PatchPattern.Match(asset.Name);
            if (!match.Success) continue;
            if (!string.Equals(match.Groups["from"].Value, from, StringComparison.OrdinalIgnoreCase)) continue;
            return new UpdateCandidate(newest.Version, asset, IsPatch: true, newest.Notes, checksums);
        }

        // 次选：兼容合集。它覆盖多个源构建，比完整包小两个数量级。
        foreach (var asset in newest.Assets)
        {
            if (!CompatiblePattern.IsMatch(asset.Name)) continue;
            return new UpdateCandidate(newest.Version, asset, IsPatch: true, newest.Notes, checksums);
        }

        // 退路：完整包。发版规则里 1.2 GB 的全量包**不上传**，所以这条在正式发行里通常是空的；
        // 空就返回 null（＝不提示更新），绝不拿一个更旧的版本凑数。
        foreach (var asset in newest.Assets)
        {
            var match = FullPattern.Match(asset.Name);
            if (!match.Success) continue;
            // 名字里的版本必须与这个发布一致：发布页上可能同时挂着别版的包（历史资产迁移时见过）。
            if (!string.Equals(match.Groups["version"].Value, newest.Version, StringComparison.OrdinalIgnoreCase)) continue;
            return new UpdateCandidate(newest.Version, asset, IsPatch: false, newest.Notes, checksums);
        }

        return null;
    }

    /// <summary>从发布页的 <c>SHA256.txt</c> 里取某个文件的哈希；没有＝null。</summary>
    /// <param name="text">校验和文件正文（每行 <c>&lt;hash&gt;  &lt;相对路径&gt;</c>）。</param>
    /// <param name="assetName">文件名（匹配路径的最后一段）。</param>
    public static string? HashFor(string text, string assetName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var parts = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            var path = parts[1].Replace('\\', '/');
            if (!path.EndsWith("/" + assetName, StringComparison.Ordinal) && path != assetName) continue;
            return parts[0].ToUpperInvariant();
        }
        return null;
    }

    /// <summary>
    /// 版本比较（与 <c>docs/release/VERSIONING.md</c> 的优先级一致）：
    /// 三段数字 → 预发布种类（alpha &lt; beta &lt; 正式 &lt; hotfix）→ 序号。
    /// </summary>
    /// <returns>负数＝a 旧，0＝相同，正数＝a 新。</returns>
    public static int Compare(string a, string b)
    {
        var (left, right) = (ParseVersion(a), ParseVersion(b));
        var result = left.Major.CompareTo(right.Major);
        if (result != 0) return result;
        result = left.Minor.CompareTo(right.Minor);
        if (result != 0) return result;
        result = left.Patch.CompareTo(right.Patch);
        if (result != 0) return result;
        result = left.Kind.CompareTo(right.Kind);
        if (result != 0) return result;
        return left.Number.CompareTo(right.Number);
    }

    /// <summary>去掉 <c>v</c>／<c>V</c> 前缀。</summary>
    public static string Normalize(string tag)
    {
        var text = (tag ?? "").Trim();
        return text.Length > 0 && (text[0] is 'v' or 'V') ? text[1..] : text;
    }

    private static (int Major, int Minor, int Patch, int Kind, int Number) ParseVersion(string version)
    {
        var text = Normalize(version);
        var dash = text.IndexOf('-');
        var core = dash < 0 ? text : text[..dash];
        var suffix = dash < 0 ? "" : text[(dash + 1)..];

        var numbers = core.Split('.');
        var major = Int(numbers, 0);
        var minor = Int(numbers, 1);
        var patch = Int(numbers, 2);

        // alpha=0 < beta=1 < 正式=2 < hotfix=3（VERSIONING.md 的优先级表）。
        var kind = 2;
        var number = 0;
        if (suffix.StartsWith("alpha", StringComparison.OrdinalIgnoreCase)) { kind = 0; number = Tail(suffix); }
        else if (suffix.StartsWith("beta", StringComparison.OrdinalIgnoreCase)) { kind = 1; number = Tail(suffix); }
        else if (suffix.StartsWith("hotfix", StringComparison.OrdinalIgnoreCase)) { kind = 3; number = Tail(suffix); }

        return (major, minor, patch, kind, number);
    }

    private static int Int(string[] parts, int index) =>
        index < parts.Length && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    /// <summary>取后缀里的序号：<c>beta.4</c> → 4，<c>hotfix</c> → 0，<c>hotfix.2</c> → 2。</summary>
    private static int Tail(string suffix)
    {
        var dot = suffix.LastIndexOf('.');
        return dot >= 0 && int.TryParse(suffix[(dot + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;
}

/// <summary>
/// 下载并校验一个更新文件。**不决定下什么**（那是 <see cref="ReleaseFeed"/> 的事），
/// 只负责「把字节拿下来、对得上哈希、落在指定位置」。
/// </summary>
public sealed class UpdateDownloader
{
    /// <summary>多久没有新字节就判定这条连接死了。</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>可覆盖，测试把它压到毫秒级。</summary>
    public TimeSpan StallTimeout { get; set; } = DefaultStallTimeout;

    private readonly HttpClient _http;

    /// <param name="http">注入点：测试给假的 handler，生产用默认客户端。</param>
    public UpdateDownloader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("OrC-KSD-Updater/1.0");
    }

    /// <summary>
    /// 下载到 <paramref name="destination"/>（先写 <c>.part</c>，校验通过才改名）。
    /// </summary>
    /// <param name="expectedSha256">发布页给的哈希；null＝跳过校验（没有 SHA256.txt 时才允许）。</param>
    /// <param name="progress">0–1，按字节数报。</param>
    /// <exception cref="InvalidDataException">哈希对不上，或长度与发布页不符。</exception>
    public async Task DownloadAsync(
        ReleaseAsset asset,
        string destination,
        string? expectedSha256,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var part = destination + ".part";
        try
        {
            using (var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? asset.Size;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long received = 0;
                int read;
                // 每次读取单独设一个「多久没有新字节就算死」的闸门。
                // 为什么必须有：`HttpClient.Timeout` 在 ResponseHeadersRead 模式下只管到**响应头**，
                // 正文阶段没有超时——网络一停，ReadAsync 会一直挂着。实测卡在 6.5 MB 整整七分钟
                // 一个新字节都没来，游戏既不算失败也不往下走，玩家看到的就是「更新永远没动静」。
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    stall.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (read <= 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total > 0) progress?.Report(Math.Min(1d, received / (double)total));
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // 校验在**改名之前**：半截文件留在 .part 上，下次重下即可；写进正式名字就等于
            // 把一个坏包摆在那里，下次启动会当成「已下载」直接拿去更新。
            if (asset.Size > 0 && new FileInfo(part).Length != asset.Size)
                throw new InvalidDataException($"下载长度不符：期望 {asset.Size} 字节，实际 {new FileInfo(part).Length} 字节。");

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = Hash(part);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"下载校验失败：期望 {expectedSha256}，实际 {actual}。");
            }

            File.Move(part, destination, overwrite: true);
            progress?.Report(1d);
        }
        finally
        {
            if (File.Exists(part))
            {
                try { File.Delete(part); } catch (IOException) { /* 下次覆盖即可 */ }
            }
        }
    }

    /// <summary>文件的 SHA256（大写十六进制，与发布页 <c>SHA256.txt</c> 同形）。</summary>
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>
/// 拉起更新器时要传的参数。**单独抽出来是因为它错不起**：`--` 的位置决定
/// <c>OS.GetCmdlineUserArgs()</c> 能不能看到 <c>--apply</c>；少一个分隔符，更新器会正常启动、
/// 显示界面、然后什么都不做——玩家看到的是「更新完还是旧版本」。
/// </summary>
public static class UpdateLauncher
{
    /// <summary>更新器里识别「无人值守应用」的第一个参数。</summary>
    public const string ApplyVerb = "--apply";

    /// <summary>
    /// <c>[--headless, --, --apply, root, patch, (--relaunch)]</c>。
    /// <para>
    /// 顺序不能改：Godot 自己吃掉 <c>--headless</c> 之类的引擎参数，<c>--</c> 之后才是应用参数。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Arguments(string gameRoot, string patch, bool relaunch = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(patch);

        var arguments = new List<string> { "--headless", "--", ApplyVerb, gameRoot, patch };
        if (relaunch) arguments.Add("--relaunch");
        return arguments;
    }
}
