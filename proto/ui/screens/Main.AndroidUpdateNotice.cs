using Godot;
using Kards.Ui.OrcBridge.Updates;

namespace Kards.Ui;

public partial class Main
{
    // 仅在页脚提示；网络返回时玩家可能已进入对局或编辑器，不弹窗打断操作。
    private async Task CheckAndroidUpdateNoticeAsync()
    {
        var current = ProjectSettings.GetSetting("application/config/version", "").AsString();
        if (string.IsNullOrWhiteSpace(current)) return;
        using var http = NewHttpClient();
        var release = ReleaseFeed.SelectAndroidNotice(await LoadReleasesAsync(http), current);
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;
        if (release is null) { SetUpdateStatus("已是最新 " + current, "点击重新检查更新。"); return; }
        SetUpdateStatus($"有新版 {release.Version} · 查看发布页", "手动下载 APK 覆盖安装以保留卡组和工坊；不自动下载或安装。", () => OpenRelease(release.Version));
        LogUpdate("Android 发现新版 " + release.Version + "，仅提示。");
    }
}
