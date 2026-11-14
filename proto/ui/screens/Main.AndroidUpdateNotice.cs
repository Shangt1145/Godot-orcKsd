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
        if (release is null || !GodotObject.IsInstanceValid(this) || !IsInsideTree()
            || !GodotObject.IsInstanceValid(_speedLabel)) return;

        var notice = new Button
        {
            Text = $"有新版 {release.Version} · 查看发布页",
            TooltipText = "在发布页查看更新说明，手动下载 APK 覆盖安装以保留卡组和工坊。",
            FocusMode = Control.FocusModeEnum.None,
        };
        notice.Pressed += () =>
        {
            var result = OS.ShellOpen($"https://github.com/{ReleaseFeed.Repository}/releases/tag/{Uri.EscapeDataString(release.Tag)}");
            if (result != Error.Ok) GD.PushWarning("无法打开更新发布页：" + result);
        };
        _speedLabel.GetParent().AddChild(notice);
    }
}
