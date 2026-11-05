using System.Net;
using System.Text;
using Kards.Ui.OrcBridge.Updates;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 启动自动更新里**不需要网络**的那一半：从发布页 JSON 挑出该下的文件、比对版本、
/// 校验下载。这一半错了的后果是「玩家被提示更新、下到错的东西」或者「永远收不到更新」，
/// 两种都比崩掉更难发现。
/// </summary>
public class ReleaseFeedTests
{
    private const string MyBuild = "3CB83D2AE7F9A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F6071829";

    [Fact]
    public void 发布列表能解析出资产与发行说明()
    {
        var releases = ReleaseFeed.Parse("""
        [
          {
            "tag_name": "v2.0.0-beta.4",
            "body": "这版修了什么",
            "draft": false,
            "prerelease": true,
            "assets": [
              { "name": "OrC-KSD-2.0.0-beta.4-win-x64.zip", "size": 123, "browser_download_url": "https://example.test/full.zip" },
              { "name": "SHA256.txt", "size": 10, "browser_download_url": "https://example.test/SHA256.txt" }
            ]
          }
        ]
        """);

        var release = Assert.Single(releases);
        Assert.Equal("v2.0.0-beta.4", release.Tag);
        Assert.Equal("2.0.0-beta.4", release.Version);          // tag 的 v 前缀去掉
        Assert.Equal("这版修了什么", release.Notes);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal(123, release.Assets[0].Size);
    }

    [Fact]
    public void 版本比较按发行说明的优先级走()
    {
        // 三段数字 → alpha < beta < 正式 < hotfix → 序号。beta.10 必须大于 beta.4（不是字符串序）。
        Assert.True(ReleaseFeed.Compare("2.0.0-beta.4", "2.0.0-beta.3") > 0);
        Assert.True(ReleaseFeed.Compare("2.0.0-beta.10", "2.0.0-beta.4") > 0);
        Assert.True(ReleaseFeed.Compare("2.0.0", "2.0.0-beta.9") > 0);
        Assert.True(ReleaseFeed.Compare("2.0.0-hotfix", "2.0.0") > 0);
        Assert.True(ReleaseFeed.Compare("2.0.0-hotfix.2", "2.0.0-hotfix") > 0);
        Assert.True(ReleaseFeed.Compare("2.0.0-alpha.1", "2.0.0-beta.1") < 0);
        Assert.Equal(0, ReleaseFeed.Compare("v2.0.0-beta.3", "2.0.0-beta.3"));
    }

    [Fact]
    public void 优先挑buildId对得上的增量补丁()
    {
        var releases = ReleaseFeed.Parse("""
        [
          {
            "tag_name": "v2.0.0-beta.4",
            "body": "",
            "draft": false,
            "assets": [
              { "name": "OrC-KSD-2.0.0-beta.4-win-x64.zip", "size": 1220000000, "browser_download_url": "https://example.test/full.zip" },
              { "name": "OrC-KSD-2.0.0-beta.4-from-AAAAAAAAAAAA-to-FFFFFFFFFFFF.orcupdate", "size": 9000000, "browser_download_url": "https://example.test/other.orcupdate" },
              { "name": "OrC-KSD-2.0.0-beta.4-from-3CB83D2AE7F9-to-FFFFFFFFFFFF.orcupdate", "size": 7000000, "browser_download_url": "https://example.test/mine.orcupdate" }
            ]
          }
        ]
        """);

        var candidate = ReleaseFeed.Select(releases, "2.0.0-beta.3", MyBuild);

        Assert.NotNull(candidate);
        Assert.True(candidate!.IsPatch);
        Assert.Equal("https://example.test/mine.orcupdate", candidate.Asset.Url);
        Assert.Equal("2.0.0-beta.4", candidate.Version);
    }

    [Fact]
    public void 没有对得上的补丁就退回完整包()
    {
        // 玩家跨了好几个版本（或本地文件被改过 → buildId 不同），补丁逐字节对不上，只能下完整包。
        var releases = ReleaseFeed.Parse("""
        [
          {
            "tag_name": "v2.0.0-beta.4",
            "body": "",
            "draft": false,
            "assets": [
              { "name": "OrC-KSD-2.0.0-beta.4-from-AAAAAAAAAAAA-to-FFFFFFFFFFFF.orcupdate", "size": 9000000, "browser_download_url": "https://example.test/other.orcupdate" },
              { "name": "OrC-KSD-2.0.0-beta.4-win-x64.zip", "size": 1220000000, "browser_download_url": "https://example.test/full.zip" }
            ]
          }
        ]
        """);

        var candidate = ReleaseFeed.Select(releases, "2.0.0-beta.1", MyBuild);

        Assert.NotNull(candidate);
        Assert.False(candidate!.IsPatch);
        Assert.Equal("https://example.test/full.zip", candidate.Asset.Url);
    }

    [Fact]
    public void 跨版本时挑兼容合集而不是放弃更新()
    {
        // 正式发行不上传 1.2 GB 全量包，所以「没有对得上的单补丁」不等于「更新不了」：
        // 兼容合集里装了好几个源构建的变体，更新器自己挑。
        var releases = ReleaseFeed.Parse("""
        [
          {
            "tag_name": "v2.0.0-beta.4",
            "body": "",
            "draft": false,
            "assets": [
              { "name": "OrC-KSD-2.0.0-beta.4-compatible-to-FFFFFFFFFFFF.orcupdate", "size": 41000000, "browser_download_url": "https://example.test/compatible.orcupdate" },
              { "name": "OrC-KSD-2.0.0-beta.4-from-3CB83D2AE7F9-to-FFFFFFFFFFFF.orcupdate", "size": 7000000, "browser_download_url": "https://example.test/mine.orcupdate" }
            ]
          }
        ]
        """);

        // 本机 buildId 对得上单补丁 → 优先单补丁（更小）。
        var exact = ReleaseFeed.Select(releases, "2.0.0-beta.3", MyBuild);
        Assert.Equal("https://example.test/mine.orcupdate", exact!.Asset.Url);

        // 对不上（跨了两个版本）→ 退到兼容合集，而不是「没有更新」。
        var fallback = ReleaseFeed.Select(releases, "2.0.0-beta.1", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        Assert.NotNull(fallback);
        Assert.True(fallback!.IsPatch);
        Assert.Equal("https://example.test/compatible.orcupdate", fallback.Asset.Url);
    }

    [Fact]
    public void 草稿与不比本机新的版本都不算更新()
    {
        var releases = ReleaseFeed.Parse("""
        [
          { "tag_name": "v2.0.0-beta.9", "draft": true,  "assets": [ { "name": "OrC-KSD-2.0.0-beta.9-win-x64.zip", "size": 1, "browser_download_url": "https://example.test/draft.zip" } ] },
          { "tag_name": "v2.0.0-beta.3", "draft": false, "assets": [ { "name": "OrC-KSD-2.0.0-beta.3-win-x64.zip", "size": 1, "browser_download_url": "https://example.test/old.zip" } ] }
        ]
        """);

        // 草稿比本机新也不能推；同版本、旧版本更不算。结论是「没有更新」而不是「挑一个凑合」。
        Assert.Null(ReleaseFeed.Select(releases, "2.0.0-beta.3", MyBuild));
    }

    [Fact]
    public void 最新版没有可用文件时不会退回旧版()
    {
        // 最新版只传了 SHA256.txt（漏传了包）——这时候应当**什么都不下**。
        // 退回上一个版本会让人「更新到更旧的版本」，比不更新更糟。
        var releases = ReleaseFeed.Parse("""
        [
          { "tag_name": "v2.0.0-beta.4", "draft": false, "assets": [ { "name": "SHA256.txt", "size": 10, "browser_download_url": "https://example.test/s.txt" } ] },
          { "tag_name": "v2.0.0-beta.3", "draft": false, "assets": [ { "name": "OrC-KSD-2.0.0-beta.3-win-x64.zip", "size": 1, "browser_download_url": "https://example.test/old.zip" } ] }
        ]
        """);

        Assert.Null(ReleaseFeed.Select(releases, "2.0.0-beta.2", MyBuild));
    }

    [Fact]
    public void 校验和文件按文件名取哈希()
    {
        const string text = """
        0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF  full/OrC-KSD-2.0.0-beta.4-win-x64.zip
        FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210  patches/OrC-KSD-2.0.0-beta.4-from-3CB83D2AE7F9-to-FFFFFFFFFFFF.orcupdate

        """;

        Assert.Equal(
            "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210",
            ReleaseFeed.HashFor(text, "OrC-KSD-2.0.0-beta.4-from-3CB83D2AE7F9-to-FFFFFFFFFFFF.orcupdate"));
        // 名字只对上一半不算：`beta.4-win-x64.zip` 不能匹配到 `x-beta.4-win-x64.zip`。
        Assert.Null(ReleaseFeed.HashFor(text, "beta.4-win-x64.zip"));
    }

    [Fact]
    public void 下载校验通过才落到正式文件名()
    {
        var payload = Encoding.UTF8.GetBytes("patch-bytes");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        var root = TempRoot();
        try
        {
            var downloader = new UpdateDownloader(Stub(payload));
            var target = Path.Combine(root, "update.orcupdate");
            downloader.DownloadAsync(new ReleaseAsset("x.orcupdate", payload.Length, "https://example.test/x"), target, expected)
                .GetAwaiter().GetResult();

            Assert.True(File.Exists(target));
            Assert.Equal(payload, File.ReadAllBytes(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void 校验失败不留下正式文件()
    {
        // 坏包留在正式名字上，下次启动会当成「已下载」直接拿去更新——这是最难查的一类事故。
        var root = TempRoot();
        try
        {
            var downloader = new UpdateDownloader(Stub(Encoding.UTF8.GetBytes("tampered")));
            var target = Path.Combine(root, "update.orcupdate");

            var error = Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
                new ReleaseAsset("x.orcupdate", 8, "https://example.test/x"), target,
                "0000000000000000000000000000000000000000000000000000000000000000")).GetAwaiter().GetResult();

            Assert.Contains("校验失败", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));      // 半截文件也清掉
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void 更新器参数的分隔符在应用参数之前()
    {
        // Godot 吃掉 `--headless`，`--` 之后才是应用参数。少一个 `--`，更新器会正常启动、
        // 显示界面、然后什么都不做——玩家看到的是「更新完还是旧版本」。
        var arguments = UpdateLauncher.Arguments(@"D://Games//OrC", @"C://Users//a//update.orcupdate");

        Assert.Equal(["--headless", "--", "--apply", @"D://Games//OrC", @"C://Users//a//update.orcupdate", "--relaunch"], arguments);
        Assert.True(Array.IndexOf(arguments.ToArray(), "--") < Array.IndexOf(arguments.ToArray(), UpdateLauncher.ApplyVerb));
        // 不自动重启时不带 --relaunch（手动更新场景）。
        Assert.DoesNotContain("--relaunch", UpdateLauncher.Arguments("r", "p", relaunch: false));
    }

    private static HttpClient Stub(byte[] payload)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        return new HttpClient(handler);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { /* 临时目录清不掉不影响断言 */ }
    }
}
