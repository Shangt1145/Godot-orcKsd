using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Godot;
using Kards.Ui.OrcBridge.Updates;
// Godot 也有 HttpClient（HTTP 客户端节点），这里要的是 .NET 的那个。
using NetHttpClient = System.Net.Http.HttpClient;

namespace Kards.Ui;

/// <summary>
/// 启动时自动检查更新：查发布页 → 挑增量补丁（对不上就完整包）→ 下载校验 →
/// 交给「更新游戏.exe」应用并重启。
/// <para>
/// 三个刻意的边界：
/// </para>
/// <list type="number">
/// <item><b>开发环境不查</b>：编辑器里没有 <c>.orcksd-build.json</c>，也没有构建 id，
/// 查了也只会误报。探针（<c>--verify-*</c>／<c>--capture-*</c>）同样跳过——它们要的是确定性。</item>
/// <item><b>检查失败绝不拦启动</b>：没网、被墙、GitHub 抽风都是常态，一律只写日志。</item>
/// <item><b>自己不替换任何文件</b>：正在跑的进程锁着自己的 dll 与 pck，替换必然失败。
/// 下载完只负责拉起更新器再退出，替换与回滚交给它（那边有日志、校验和回滚）。</item>
/// </list>
/// </summary>
public partial class Main
{
    /// <summary>构建清单：发版脚本写在游戏目录里，记录版本与构建 id（补丁按它匹配）。</summary>
    private const string BuildManifestName = ".orcksd-build.json";

    /// <summary>更新器在游戏目录里的名字（<c>Installer.InstallUpdater</c> 装的就是这个）。</summary>
    private const string UpdaterExeName = "更新游戏.exe";

    private bool _updateCheckStarted;

    /// <summary>启动后调用一次；自己吞掉所有异常。</summary>
    private async void CheckForUpdatesOnStartup()
    {
        if (_updateCheckStarted) return;
        _updateCheckStarted = true;

        try
        {
            if (OS.HasFeature("editor")) return;
            // Android 走整包 APK（系统安装器覆盖安装），没有「更新游戏.exe」也没有 .orcupdate 的意义。
            // 这里显式挡掉，而不是靠「没有构建清单」顺带跳过——将来若把清单打进 APK，
            // 那段「没有更新器」的错误提示就会跑到手机上，而它对着手机毫无意义。
            if (OS.HasFeature("android")) return;
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                // 探针与截图要的是确定性：网络一抖，同一份脚本两种结论。
                if (arg.StartsWith("--verify", StringComparison.Ordinal)) return;
                if (arg.StartsWith("--capture", StringComparison.Ordinal)) return;
                if (arg == "--no-update") return;
            }

            var root = OS.GetExecutablePath().GetBaseDir();
            var manifest = Path.Combine(root, BuildManifestName);
            if (!File.Exists(manifest)) return;   // 便携拷贝／源码运行：没有构建清单就不管

            var (version, buildId) = ReadBuildManifest(manifest);
            if (version is null || buildId is null) return;

            using var http = NewHttpClient();
            var json = await http.GetStringAsync(ReleaseFeed.ListUrl).ConfigureAwait(true);
            var candidate = ReleaseFeed.Select(ReleaseFeed.Parse(json), version, buildId);
            if (candidate is null) return;
            if (string.Equals(ReadSkippedVersion(), candidate.Version, StringComparison.Ordinal)) return;

            PromptUpdate(root, candidate);
        }
        catch (Exception error)
        {
            // 这里绝不能抛到启动流程：更新是锦上添花，不是开游戏的必要条件。
            GD.PushWarning("自动更新检查失败：" + error.Message);
        }
    }

    private void PromptUpdate(string root, UpdateCandidate candidate)
    {
        var size = candidate.Asset.Size > 0 ? FormatSize(candidate.Asset.Size) : "大小未知";
        var kind = candidate.IsPatch ? "增量补丁" : "完整包";
        var notes = candidate.Notes.Trim();
        if (notes.Length > 360) notes = notes[..360] + "…";

        var dialog = new ConfirmationDialog
        {
            Title = "发现新版本 " + candidate.Version,
            DialogText = "这版可以用" + kind + "更新（" + size + "），"
                + (candidate.IsPatch ? "只下改动的部分。" : "补丁对不上本机构建，只能下完整包。")
                + "\n\n更新时游戏会关闭，装完自动重新打开。卡组、DIY 卡与导入的卡图都会保留。"
                + (notes.Length > 0 ? "\n\n" + notes : ""),
            OkButtonText = "现在更新",
            CancelButtonText = "稍后",
            MinSize = new Vector2I(620, 320),
        };
        dialog.AddButton("跳过这个版本", true, "skip");
        dialog.CustomAction += async key =>
        {
            if (key != "skip") return;
            WriteSkippedVersion(candidate.Version);
            dialog.QueueFree();
        };
        dialog.Confirmed += () => _ = ApplyUpdateAsync(root, candidate, dialog);
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>下载 → 校验 → 拉起更新器 → 退出。失败只改对话框文案，不退出游戏。</summary>
    private async Task ApplyUpdateAsync(string root, UpdateCandidate candidate, ConfirmationDialog prompt)
    {
        var updater = Path.Combine(root, UpdaterExeName);
        if (!File.Exists(updater))
        {
            prompt.DialogText = "游戏目录里没有「" + UpdaterExeName + "」，无法自动更新。\n"
                + "请到发布页下载完整包覆盖安装。";
            return;
        }

        prompt.Hide();

        var status = new Label { Text = "正在连接…", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        var bar = new ProgressBar { MaxValue = 100, ShowPercentage = true };
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        column.AddChild(status);
        column.AddChild(bar);

        var progress = new AcceptDialog
        {
            Title = "正在下载 " + candidate.Version,
            DialogHideOnOk = false,
            MinSize = new Vector2I(560, 170),
        };
        progress.AddChild(column);
        progress.GetOkButton().Visible = false;      // 下载中不给「确定」：点了也没意义
        AddChild(progress);
        progress.PopupCentered();

        try
        {
            using var http = NewHttpClient();
            var checksums = candidate.ChecksumsUrl is null
                ? null
                : await http.GetStringAsync(candidate.ChecksumsUrl).ConfigureAwait(true);
            var expected = checksums is null ? null : ReleaseFeed.HashFor(checksums, candidate.Asset.Name);

            // 落在 user:// 而不是游戏目录：那里可能没有写权限（装到 Program Files 时就是这样）。
            var target = Path.Combine(
                ProjectSettings.GlobalizePath("user://updates"),
                candidate.Version,
                candidate.Asset.Name);

            var reporter = new Progress<double>(fraction => Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(bar)) return;
                bar.Value = Math.Round(fraction * 100, 1);
                status.Text = "已下载 " + Math.Round(fraction * 100) + "%（" + FormatSize((long)(candidate.Asset.Size * fraction)) + "）";
            }).CallDeferred());

            await new UpdateDownloader(http)
                .DownloadAsync(candidate.Asset, target, expected, reporter)
                .ConfigureAwait(true);

            if (expected is null) GD.PushWarning("发布页没有 SHA256.txt，这次下载未做哈希校验。");

            status.Text = "下载完成，正在启动更新器…";
            LaunchUpdater(updater, root, target);
        }
        catch (Exception error)
        {
            GD.PushWarning("自动更新失败：" + error);
            progress.QueueFree();
            prompt.Show();
            prompt.DialogText = "更新没成功：" + error.Message + "\n\n可以稍后再试，或到发布页手动下载完整包。";
        }
    }

    /// <summary>
    /// 拉起更新器后立刻退出：更新器会**等本进程结束**再动文件（它自己轮询 KardsUi 进程），
    /// 装完按 <c>--relaunch</c> 把游戏重新打开。
    /// </summary>
    private void LaunchUpdater(string updater, string root, string patch)
    {
        var start = new ProcessStartInfo(updater)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        // 参数顺序由 UpdateLauncher 统一决定（`--` 的位置错了，更新器会启动但什么都不做）。
        foreach (var argument in UpdateLauncher.Arguments(root, patch)) start.ArgumentList.Add(argument);

        Process.Start(start);
        GetTree().Quit();
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

    private static string? ReadSkippedVersion()
    {
        var config = new ConfigFile();
        return config.Load("user://update.cfg") == Error.Ok ? config.GetValue("update", "skip", "").AsString() : null;
    }

    private static void WriteSkippedVersion(string version)
    {
        var config = new ConfigFile();
        config.Load("user://update.cfg");
        config.SetValue("update", "skip", version);
        config.Save("user://update.cfg");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => Math.Round(bytes / (double)(1L << 30), 2) + " GB",
        >= 1L << 20 => Math.Round(bytes / (double)(1L << 20), 1) + " MB",
        >= 1L << 10 => Math.Round(bytes / (double)(1L << 10)) + " KB",
        _ => bytes + " B",
    };
}
