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
    /// <summary>本机构建清单里的版本号（对话框里给玩家对照"从哪升到哪"）。</summary>
    private string? _localVersion;

    /// <summary>
    /// 是否有真人坐在屏幕前。<c>_updateRootOverride</c> 只有探针会设，探针要的是确定性结论，
    /// 不是有人点按钮——所以探针走静默下载，真人走弹框。
    /// </summary>
    private bool Interactive => _updateRootOverride is null;

    /// <summary>
    /// 弹「发现新版本」。三个出口：立即更新 / 稍后再说 / 跳过此版本。
    /// <para>
    /// 「立即更新」＝下载 + 校验 + 拉起更新器（<c>--relaunch</c>）+ 退出。替身仍在更新器里：
    /// 它等本进程结束才动文件，逐文件校验、失败回滚都在那边，这里绝不自己覆盖。
    /// </para>
    /// </summary>
    private void PromptUpdate(UpdateCandidate candidate)
    {
        var dialog = new ConfirmationDialog
        {
            Title = "发现新版本 " + candidate.Version,
            DialogText = $"当前版本 {_localVersion ?? "未知"}，可以更新到 {candidate.Version}。\n\n"
                + $"方式：增量补丁（{HumanSize(candidate.Asset.Size)}），只下改动的部分。\n"
                + "点「立即更新」会下载并校验，然后关闭游戏；装好后自动重新打开。\n"
                + "卡组、DIY 卡与导入的卡图都会保留。",
            OkButtonText = "立即更新",
        };
        dialog.AddCancelButton("稍后再说");
        var skip = dialog.AddButton("跳过此版本");
        skip.Pressed += () =>
        {
            RememberSkip(candidate.Version);
            LogUpdate("玩家跳过版本：" + candidate.Version);
            dialog.QueueFree();
            SetUpdateStatus("已跳过 " + candidate.Version, "下次启动不再提示该版本；可在 user://update.cfg 里清掉 [update] skip。");
        };
        dialog.Confirmed += () =>
        {
            dialog.QueueFree();
            _ = InstallAsync(candidate);
        };
        dialog.Canceled += () =>
        {
            dialog.QueueFree();
            SetUpdateStatus("有新版 " + candidate.Version, "下次启动会再问一次。", () => CheckForUpdatesOnStartup(true));
        };
        AddChild(dialog);
        dialog.PopupCentered();
        SetUpdateStatus("有新版 " + candidate.Version, "等待确认。", () => CheckForUpdatesOnStartup(true));
    }

    /// <summary>玩家点了「立即更新」：下载（带进度）→ 交给更新器 → 退出。</summary>
    private async Task InstallAsync(UpdateCandidate candidate)
    {
        var (dialog, bar, label) = BuildProgressDialog(candidate);
        try
        {
            using var http = _updateHttpFactory?.Invoke() ?? NewHttpClient();
            await DownloadAsync(http, candidate, bar, label).ConfigureAwait(true);
            if (_readyPatch is null) throw new InvalidDataException("下载完成但未取得补丁路径。");

            label.Text = "下载完成，正在启动更新程序…";
            bar.Value = 1;
            // 先交接再退出：更新器会等本进程结束才动文件，所以这里拉起就立刻返回。
            if (!HandOffUpdate(_readyPatch, relaunch: true))
                throw new InvalidDataException("游戏目录里没有可用的更新程序，请到发布页手动下载。");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            dialog.QueueFree();
            GetTree().Quit();
        }
        catch (Exception error)
        {
            if (GodotObject.IsInstanceValid(dialog)) dialog.QueueFree();
            LogUpdate("下载或安装失败：" + error);
            SetUpdateStatus("更新失败 · 点击重试", error.Message + "；详情见用户数据目录 updates/update.log。");
            var fail = new AcceptDialog
            {
                Title = "更新没有成功",
                DialogText = error.Message + "\n\n" + (error.Message.Contains("更新程序") ? "" : "可以点页脚的「更新失败 · 点击重试」，或到发布页手动下载。"),
                OkButtonText = "知道了",
            };
            AddChild(fail);
            fail.PopupCentered();
        }
    }

    /// <summary>「正在更新」对话框：一条进度条 + 一行会自己变的说明。</summary>
    private (AcceptDialog Dialog, ProgressBar Bar, Label Label) BuildProgressDialog(UpdateCandidate candidate)
    {
        var dialog = new AcceptDialog
        {
            Title = "正在更新到 " + candidate.Version,
            Exclusive = true,
            Unresizable = true,
            DialogCloseOnEscape = false,
        };
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        var label = new Label { Text = $"正在下载 {candidate.Version} …（{HumanSize(candidate.Asset.Size)}）", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        var bar = new ProgressBar { MinValue = 0, MaxValue = 1, Value = 0, CustomMinimumSize = new Vector2(420, 20), ShowPercentage = false };
        box.AddChild(label);
        box.AddChild(bar);
        box.AddChild(new Label { Text = "下载完成后游戏会关闭，由更新程序装好并自动重开。" });
        dialog.AddChild(box);
        dialog.GetOkButton().Visible = false; // 这个框不给「确定」：中途点掉会留下半截状态
        AddChild(dialog);
        dialog.PopupCentered();
        return (dialog, bar, label);
    }


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
            _localVersion = version;
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

            if (!manual && IsSkipped(candidate.Version))
            {
                SetUpdateStatus("已跳过 " + candidate.Version, "在 user://update.cfg 删掉 [update] skip 可重新提示。");
                LogUpdate("玩家曾跳过该版本：" + candidate.Version);
                return;
            }

            LogUpdate($"发现 {candidate.Version} asset={candidate.Asset.Name} bytes={candidate.Asset.Size}");
            // 探针（_updateRootOverride 非空）走「静默下载」：它要的是确定性结论，不是有人点按钮。
            if (Interactive)
            {
                PromptUpdate(candidate);
                return;
            }
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
    /// <param name="progressBar">非空＝进度画在对话框里（玩家点了「立即更新」）；空＝画在页脚按钮上。</param>
    private async Task DownloadAsync(NetHttpClient http, UpdateCandidate candidate, ProgressBar? progressBar = null, Label? progressLabel = null)
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
                if (progressBar is not null)
                {
                    if (!GodotObject.IsInstanceValid(progressBar)) return;
                    progressBar.Value = value;
                    if (progressLabel is not null && GodotObject.IsInstanceValid(progressLabel))
                        progressLabel.Text = $"正在下载 {candidate.Version} … {(int)(value * 100)}%（{HumanSize(candidate.Asset.Size)}）";
                    return;
                }
                var text = $"下载新版 {(int)(value * 100)}%";
                if (_updateStatusButton?.Text != text) SetUpdateStatus(text, candidate.Version + "；完成后退出游戏应用。");
            }))
            .ConfigureAwait(true);

        _readyPatch = target;
        GD.Print($"[update] 已下载并校验 {candidate.Version}：{target}（退出时应用）");
    }

    /// <summary>字节数转成人话（对话框里给玩家看的大小）。</summary>
    private static string HumanSize(long bytes) => bytes switch
    {
        <= 0 => "大小未知",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };


    /// <summary>
    /// 退出时把补丁交给更新器。更新器会**等本进程结束**再动文件，所以这里拉起它就立刻返回。
    /// <para>
    /// 不带 <c>--relaunch</c>：玩家是自己要退出的，不该被硬拉回来。下次启动就是新版。
    /// </para>
    /// </summary>
    private void ApplyPendingUpdateOnExit() => HandOffUpdate(_readyPatch, relaunch: false);

    /// <summary>
    /// 把补丁交给更新器。<paramref name="relaunch"/>＝装完是否自动重开：
    /// 玩家点了「立即更新」要重开（他本来就在玩游戏）；玩家自己退出的不重开（别硬拉回来）。
    /// </summary>
    /// <returns>拉起来了＝true；目录里没有更新器＝false（调用方负责告诉玩家）。</returns>
    private bool HandOffUpdate(string? patch, bool relaunch)
    {
        if (patch is null || _updateHandedOff) return false;
        if (!File.Exists(patch)) return false;

        var root = _updateRootOverride ?? OS.GetExecutablePath().GetBaseDir();
        var updater = Path.Combine(root, UpdaterExeName);
        if (!File.Exists(updater))
        {
            // 没有更新器＝这个安装目录没法自动替换。别在这里纠缠，交给调用方说明。
            GD.PushWarning("游戏目录里没有「" + UpdaterExeName + "」，本次更新未应用。");
            LogUpdate("缺少更新器：" + updater);
            return false;
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
            foreach (var argument in UpdateLauncher.Arguments(root, patch, relaunch))
                start.ArgumentList.Add(argument);
            Process.Start(start);
            LogUpdate(relaunch ? "已交给更新器，装完自动重开。" : "已交给更新器，退出后应用。");
            return true;
        }
        catch (Exception error)
        {
            _updateHandedOff = false;
            LogUpdate("拉起更新器失败：" + error);
            throw new InvalidDataException("启动更新程序失败：" + error.Message, error);
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

    /// <summary>玩家点过「跳过此版本」的版本号（存在 <c>user://update.cfg</c> 的 <c>[update] skip</c>）。</summary>
    private static string SkippedVersion()
    {
        var config = new ConfigFile();
        return config.Load("user://update.cfg") == Error.Ok ? config.GetValue("update", "skip", "").AsString() : "";
    }

    /// <summary>未点过「立即更新」且已跳过的版本，启动时就别再烦人。</summary>
    private static bool IsSkipped(string version) => SkippedVersion() == version;

    /// <summary>记住跳过。只记一个版本：新版本照常提示（否则一次误点就把自动更新永久关掉了）。</summary>
    private static void RememberSkip(string version)
    {
        var config = new ConfigFile();
        config.Load("user://update.cfg"); // 读失败也继续：当成空配置写回去
        config.SetValue("update", "skip", version);
        var error = config.Save("user://update.cfg");
        if (error != Error.Ok) GD.PushWarning("跳过版本没能写入 update.cfg：" + error);
    }
}
