using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using Kards.Ui.OrcBridge.Updates;
using HttpClient = System.Net.Http.HttpClient;

namespace Kards.Ui;

public partial class Main
{
    private sealed class UpdateProbeHttp(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }

    private async void VerifyAutoUpdate()
    {
        try
        {
            var root = ProjectSettings.GlobalizePath("res://artifacts/update-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            _updateRootOverride = root;
            const string build = "123456789ABC123456789ABC123456789ABC123456789ABC123456789ABC12345678";
            const string version = "2.0.0-beta.12";
            File.WriteAllText(Path.Combine(root, BuildManifestName), JsonSerializer.Serialize(new { Version = "2.0.0-beta.9", BuildId = build }));
            File.WriteAllText(Path.Combine(root, UpdaterExeName), "verification placeholder; never executed");
            var bytes = Encoding.UTF8.GetBytes("verified update pipeline " + Guid.NewGuid());
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var name = $"OrC-KSD-{version}-from-{build[..12]}-to-ABCDEF123456.orcupdate";
            var checksum = hash + "  " + name;
            string Manifest(bool patch) => JsonSerializer.Serialize(new { tag = "v" + version, assets = new object[] {
                new { name = patch ? name : $"OrC-KSD-{version}-win-x64.zip", size = bytes.Length, url = "https://updates.test/payload" },
                new { name = "SHA256.txt", size = checksum.Length, url = "https://updates.test/checksums" } } });
            var requests = new List<string>();
            void Feed(bool patch, bool corrupt = false) => _updateHttpFactory = () => new HttpClient(new UpdateProbeHttp(request =>
            {
                var url = request.RequestUri!.ToString(); requests.Add(url);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = url == ReleaseFeed.ManifestUrl
                    ? new StringContent(Manifest(patch)) : url.EndsWith("checksums", StringComparison.Ordinal)
                        ? new StringContent(corrupt ? new string('0', 64) + "  " + name : checksum) : new ByteArrayContent(bytes) };
            }));
            Feed(false); await CheckForUpdatesAsync(true);
            Require(_updateStatusButton!.Text.Contains("查看下载") && !requests.Any(r => r.EndsWith("payload")), "无匹配补丁时没有显示完整包入口，或擅自下载完整包");
            _updateHttpFactory = () => new HttpClient(new UpdateProbeHttp(_ => throw new HttpRequestException("模拟断网")));
            await CheckForUpdatesAsync(true);
            Require(_updateStatusButton.Text.Contains("点击重试"), "断网后没有明确重试入口");
            Feed(true, true); await CheckForUpdatesAsync(true);
            Require(_readyPatch is null && _updateStatusButton.Text.Contains("失败"), "损坏补丁被当作已就绪");
            Feed(true); await CheckForUpdatesAsync(true);
            await MenuFrameAsync();
            Require(_readyPatch is { } path && File.ReadAllBytes(path).SequenceEqual(bytes), "补丁没有下载并校验完成");
            Require(_updateStatusButton.Text.Contains("退出后更新"), "下载完成没有提示退出更新");
            _readyPatch = null; // This is a UI/download probe, never execute a fake patch.
            _updateRootOverride = null; _updateHttpFactory = null;
            GD.Print("AUTO_UPDATE_VERIFY_OK beta9 full-fallback-visible no-silent-full-download network-retry hash-reject download-ready exit-notice");
            GetTree().Quit();
        }
        catch (Exception error) { _readyPatch = null; GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
