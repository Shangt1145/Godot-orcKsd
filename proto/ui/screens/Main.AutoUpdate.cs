using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Godot;
using Kards.Ui.OrcBridge.Updates;
// Godot 也有 HttpClient（HTTP 客户端节点），这里要的是 .NET 的那个。
using NetHttpClient = System.Net.Http.HttpClient;

namespace Kards.Ui;

/// <summary>
/// 启动时自动更新：查发布页 → 后台静默下载补丁 → **退出时**交给更新器应用。
/// <para>
/// 全自动，不问玩家。三条刻意的边界：
/// </para>
/// <list type="number">
/// <item><b>只下载，不当场替换</b>：正在跑的进程锁着自己的 dll 与 pck，替换必然失败。
/// 下载与校验在这里做，替换与回滚交给「更新游戏.exe」（那边有日志、逐文件校验和回滚）。</item>
/// <item><b>退出时才应用</b>：启动即替换意味着玩家刚点开游戏就被关掉重开。放在退出时，
/// 这一局不受影响，下次打开就是新版。</item>
/// <item><b>检查失败绝不拦启动</b>：没网、被墙、接口限流都是常态，一律只写日志。</item>
/// </list>
/// <para>
/// 关掉的办法：命令行加 <c>--no-update</c>，或在 <c>user://update.cfg</c> 写 <c>[update] auto=false</c>。
/// </para>
/// </summary>
public partial class Main
{
    /// <summary>构建清单：发版脚本写在游戏目录里，记录版本与构建 id（补丁按它匹配）。</summary>
    private const string BuildManifestName = ".orcksd-build.json";

    /// <summary>更新器在游戏目录里的名字（<c>Installer.InstallUpdater</c> 装的就是这个）。</summary>
    private const string UpdaterExeName = "更新游戏.exe";

    private bool _updateCheckStarted;
    /// <summary>已下载并校验通过的补丁路径；退出时交给更新器。</summary>
    private string? _readyPatch;
    /// <summary>已经拉起过更新器，退出钩子不要再拉第二次。</summary>
    private bool _updateHandedOff;

    /// <summary>启动后调用一次；自己吞掉所有异常。</summary>
    private async void CheckForUpdatesOnStartup()
    {
        if (_updateCheckStarted) return;
        _updateCheckStarted = true;

        try
        {
            if (OS.HasFeature("editor")) return;
            // Android 走整包 APK（系统安装器覆盖安装），没有「更新游戏.exe」也没有 .orcupdate 的意义。
            if (OS.HasFeature("android")) return;
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                // 探针与截图要的是确定性：网络一抖，同一份脚本两种结论。
                if (arg.StartsWith("--verify", StringComparison.Ordinal)) return;
                if (arg.StartsWith("--capture", StringComparison.Ordinal)) return;
                if (arg == "--no-update") return;
            }
            if (!AutoUpdateEnabled()) return;

            var root = OS.GetExecutablePath().GetBaseDir();
            var manifest = Path.Combine(root, BuildManifestName);
            if (!File.Exists(manifest)) return;   // 便携拷贝／源码运行：没有构建清单就不管

            var (version, buildId) = ReadBuildManifest(manifest);
            if (version is null || buildId is null) return;

            using var http = NewHttpClient();
            var json = await http.GetStringAsync(ReleaseFeed.ListUrl).ConfigureAwait(true);
            var candidate = ReleaseFeed.Select(ReleaseFeed.Parse(json), version, buildId);
            if (candidate is null) return;
            if (!candidate.IsPatch)
            {
                // 只有完整包时不自动更：1.2 GB 的下载不该在玩家不知情时发生。
                GD.Print($"[update] {version} → {candidate.Version} 只有完整包，跳过自动更新");
                return;
            }

            GD.Print($"[update] {version} → {candidate.Version} 可用（{candidate.Asset.Size} 字节），后台下载");
            await DownloadAsync(http, candidate).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            // 这里绝不能抛到启动流程：更新是锦上添花，不是开游戏的必要条件。
            GD.PushWarning("自动更新检查失败：" + error.Message);
        }
    }

    /// <summary>下载到 <c>user://updates</c> 并校验；成功才把路径交给退出钩子。</summary>
    private async Task DownloadAsync(NetHttpClient http, UpdateCandidate candidate)
    {
        var checksums = candidate.ChecksumsUrl is null
            ? null
            : await http.GetStringAsync(candidate.ChecksumsUrl).ConfigureAwait(true);
        var expected = checksums is null ? null : ReleaseFeed.HashFor(checksums, candidate.Asset.Name);

        // 落在 user:// 而不是游戏目录：那里可能没有写权限（装到 Program Files 时就是这样）。
        var target = Path.Combine(
            ProjectSettings.GlobalizePath("user://updates"),
            candidate.Version,
            candidate.Asset.Name);

        await new UpdateDownloader(http)
            .DownloadAsync(candidate.Asset, target, expected)
            .ConfigureAwait(true);

        if (expected is null) GD.PushWarning("发布页没有 SHA256.txt，这次下载未做哈希校验。");
        _readyPatch = target;
        GD.Print($"[update] 已下载并校验 {candidate.Version}：{target}（退出时应用）");
    }

    /// <summary>
    /// 退出时把补丁交给更新器。更新器会**等本进程结束**再动文件，所以这里拉起它就立刻返回。
    /// <para>
    /// 不带 <c>--relaunch</c>：玩家是自己要退出的，不该被硬拉回来。下次启动就是新版。
    /// </para>
    /// </summary>
    private void ApplyPendingUpdateOnExit()
    {
        if (_updateHandedOff || _readyPatch is not { } patch) return;
        if (!File.Exists(patch)) return;

        var root = OS.GetExecutablePath().GetBaseDir();
        var updater = Path.Combine(root, UpdaterExeName);
        if (!File.Exists(updater))
        {
            // 没有更新器＝这个安装目录没法自动替换。下次启动会重新下载，别在这里纠缠。
            GD.PushWarning("游戏目录里没有「" + UpdaterExeName + "」，本次更新未应用。");
            return;
        }

        _updateHandedOff = true;
        try
        {
            var start = new ProcessStartInfo(updater)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
            };
            // 参数顺序由 UpdateLauncher 统一决定（`--` 的位置错了，更新器会启动但什么都不做）。
            foreach (var argument in UpdateLauncher.Arguments(root, patch, relaunch: false))
                start.ArgumentList.Add(argument);
            Process.Start(start);
            GD.Print("[update] 已交给更新器，退出后应用");
        }
        catch (Exception error)
        {
            GD.PushWarning("拉起更新器失败：" + error.Message);
        }
    }

    private static NetHttpClient NewHttpClient()
    {
        var http = new NetHttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OrC-KSD-Launcher/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>读构建清单里的版本与构建 id（清单由发版脚本写，字段是 PascalCase）。</summary>
    private static (string? Version, string? BuildId) ReadBuildManifest(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var version = root.TryGetProperty("Version", out var v) ? v.GetString() : null;
        var buildId = root.TryGetProperty("BuildId", out var b) ? b.GetString() : null;
        return (version, buildId);
    }

    /// <summary>默认开启；<c>user://update.cfg</c> 里 <c>[update] auto=false</c> 可关掉。</summary>
    private static bool AutoUpdateEnabled()
    {
        var config = new ConfigFile();
        if (config.Load("user://update.cfg") != Error.Ok) return true;
        return config.GetValue("update", "auto", true).AsBool();
    }
}
