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
/// 自动检查并显示状态；匹配补丁在后台下载，完整包提供下载入口。三条边界：
/// </para>
/// <list type="number">
/// <item><b>只下载，不当场替换</b>：正在跑的进程锁着自己的 dll 与 pck，替换必然失败。
/// 下载与校验在这里做，替换与回滚交给「更新游戏.exe」（那边有日志、逐文件校验和回滚）。</item>
/// <item><b>退出时才应用</b>：启动即替换意味着玩家刚点开游戏就被关掉重开。放在退出时，
/// 这一局不受影响，下次打开就是新版。</item>
/// <item><b>检查失败绝不拦启动</b>：显示失败与重试入口，并记录日志。</item>
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

    private bool _updateChecking;
    private Button? _updateStatusButton;
    private Action? _updateStatusAction;
    private Func<NetHttpClient>? _updateHttpFactory;
    private string? _updateRootOverride;

    private void InitializeUpdateStatus(HBoxContainer row)
    {
        _updateStatusButton = new Button { Text = "检查更新", FocusMode = Control.FocusModeEnum.None };
        _updateStatusAction = () => CheckForUpdatesOnStartup(true);
        _updateStatusButton.Pressed += () => _updateStatusAction?.Invoke();
        row.AddChild(_updateStatusButton);
    }

    private void SetUpdateStatus(string text, string detail, Action? action = null)
    {
        if (_updateStatusButton is null || !GodotObject.IsInstanceValid(_updateStatusButton)) return;
        _updateStatusButton.Text = text;
        _updateStatusButton.TooltipText = detail;
        _updateStatusAction = action ?? (() => CheckForUpdatesOnStartup(true));
    }

    private static void LogUpdate(string message)
    {
        GD.Print("[update] " + message);
        try
        {
            var folder = ProjectSettings.GlobalizePath("user://updates"); Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "update.log"), DateTimeOffset.Now.ToString("O") + " " + message + System.Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void OpenRelease(string version) => OS.ShellOpen("https://github.com/" + ReleaseFeed.Repository + "/releases/tag/v" + Uri.EscapeDataString(version));

    private void ShowReadyUpdate() => SetUpdateStatus("新版已就绪 · 退出后更新", "继续游玩不受影响；正常退出后应用更新，下次启动使用新版。", () =>
    {
        var dialog = new ConfirmationDialog { Title = "新版已下载", DialogText = "退出游戏后将应用更新。请先保存编辑内容并结束当前对局。", OkButtonText = "退出并更新", CancelButtonText = "继续使用" };
        AddChild(dialog); dialog.Confirmed += () => { dialog.QueueFree(); GetTree().Quit(); }; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    });
    /// <summary>已下载并校验通过的补丁路径；退出时交给更新器。</summary>
    private string? _readyPatch;
    /// <summary>已经拉起过更新器，退出钩子不要再拉第二次。</summary>
    private bool _updateHandedOff;

    /// <summary>启动后调用一次；自己吞掉所有异常。</summary>
    private async void CheckForUpdatesOnStartup(bool manual = false) => await CheckForUpdatesAsync(manual);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateChecking) return;
        if (_readyPatch is not null) { ShowReadyUpdate(); return; }

        try
        {
            if (!manual && OS.HasFeature("editor")) return;
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                // 探针与截图要的是确定性：网络一抖，同一份脚本两种结论。
                if (!manual && arg.StartsWith("--verify", StringComparison.Ordinal)) return;
                if (!manual && arg.StartsWith("--capture", StringComparison.Ordinal)) return;
                if (!manual && arg == "--no-update") return;
            }
            if (!manual && !AutoUpdateEnabled()) { SetUpdateStatus("自动更新已关闭 · 手动检查", "点击检查本次更新，不改变自动更新设置。"); return; }
            _updateChecking = true;
            SetUpdateStatus("正在检查更新…", "正在连接发行清单；失败后可点击重试。");

            if (OS.HasFeature("android"))
            {
                await CheckAndroidUpdateNoticeAsync();
                return;
            }

            var root = _updateRootOverride ?? OS.GetExecutablePath().GetBaseDir();
            var manifest = Path.Combine(root, BuildManifestName);
            if (!File.Exists(manifest)) { SetUpdateStatus("开发运行 · 查看发布版", "此目录没有发行构建清单。", () => OS.ShellOpen("https://github.com/" + ReleaseFeed.Repository + "/releases")); return; }

            var (version, buildId) = ReadBuildManifest(manifest);
            if (version is null || buildId is null) throw new InvalidDataException("游戏构建清单不完整，请使用完整发行包。");
            LogUpdate($"检查 version={version} build={buildId} root={root}");

            using var http = _updateHttpFactory?.Invoke() ?? NewHttpClient();
            var candidate = ReleaseFeed.Select(await LoadReleasesAsync(http).ConfigureAwait(true), version, buildId);
            if (candidate is null) { SetUpdateStatus("已是最新 " + version, "点击可重新检查更新。"); LogUpdate("未发现适用的新版本。"); return; }
            if (!candidate.IsPatch || !File.Exists(Path.Combine(root, UpdaterExeName)))
            {
                var reason = candidate.IsPatch ? "游戏目录缺少更新器，请下载完整包。" : "没有适用于此构建的补丁，请下载完整包。";
                SetUpdateStatus("有新版 " + candidate.Version + " · 查看下载", reason, () => OpenRelease(candidate.Version));
                LogUpdate(reason + " target=" + candidate.Version);
                return;
            }

            LogUpdate($"发现 {candidate.Version} asset={candidate.Asset.Name} bytes={candidate.Asset.Size}");
            SetUpdateStatus("正在下载 " + candidate.Version + "…", "下载完成后会提示；退出游戏时应用。");
            await DownloadAsync(http, candidate).ConfigureAwait(true);
            ShowReadyUpdate();
        }
        catch (Exception error)
        {
            // 这里绝不能抛到启动流程：更新是锦上添花，不是开游戏的必要条件。
            SetUpdateStatus("更新失败 · 点击重试", error.Message + "；详情见用户数据目录 updates/update.log。");
            LogUpdate("检查或下载失败：" + error);
        }
        finally { _updateChecking = false; }
    }

    /// <summary>
    /// 先读静态清单，失败才退回 API。
    /// <para>
    /// 顺序不能反：API 按出口 IP 限 60 次/小时，被限流时是**静默失败**——玩家只会觉得
    /// 「怎么一直没更新」。清单走发布页 CDN，不受这个配额约束。
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<ReleaseInfo>> LoadReleasesAsync(NetHttpClient http)
    {
        try
        {
            var manifest = await http.GetStringAsync(ReleaseFeed.ManifestUrl).ConfigureAwait(true);
            return ReleaseFeed.ParseManifest(manifest);
        }
        catch (Exception error)
        {
            // 清单还没挂上（首次发版前）或网络挡了：退回 API，至少还有一次机会。
            GD.PushWarning("静态清单不可用，退回发布接口：" + error.Message);
            var json = await http.GetStringAsync(ReleaseFeed.ListUrl).ConfigureAwait(true);
            return ReleaseFeed.Parse(json);
        }
    }

    /// <summary>下载到 <c>user://updates</c> 并校验；成功才把路径交给退出钩子。</summary>
    private async Task DownloadAsync(NetHttpClient http, UpdateCandidate candidate)
    {
        var checksums = candidate.ChecksumsUrl is null
            ? null
            : await http.GetStringAsync(candidate.ChecksumsUrl).ConfigureAwait(true);
        var expected = checksums is null ? null : ReleaseFeed.HashFor(checksums, candidate.Asset.Name);
        if (expected is null) throw new InvalidDataException("发布清单缺少本补丁的 SHA256，已停止更新。");

        // 落在 user:// 而不是游戏目录：那里可能没有写权限（装到 Program Files 时就是这样）。
        var target = Path.Combine(
            ProjectSettings.GlobalizePath("user://updates"),
            candidate.Version,
            candidate.Asset.Name);

        await new UpdateDownloader(http)
            .DownloadAsync(candidate.Asset, target, expected, new Progress<double>(value =>
            {
                if (_readyPatch is not null) return;
                var text = $"下载新版 {(int)(value * 100)}%";
                if (_updateStatusButton?.Text != text) SetUpdateStatus(text, candidate.Version + "；完成后退出游戏应用。");
            }))
            .ConfigureAwait(true);

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

        var root = _updateRootOverride ?? OS.GetExecutablePath().GetBaseDir();
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
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            // 参数顺序由 UpdateLauncher 统一决定（`--` 的位置错了，更新器会启动但什么都不做）。
            foreach (var argument in UpdateLauncher.Arguments(root, patch, relaunch: false))
                start.ArgumentList.Add(argument);
            Process.Start(start);
            LogUpdate("已交给更新器，退出后应用。");
        }
        catch (Exception error)
        {
            LogUpdate("拉起更新器失败：" + error);
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
