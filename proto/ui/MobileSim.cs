using Godot;

namespace Kards.Ui;

/// <summary>
/// 桌面上的"手机模拟"：让 Windows 构建走手机的分辨率与触屏输入路径。
/// <para>
/// 目的只有一个——**别为了看一眼布局就传 1 GB APK 到手机**。开启后：
/// ① 窗口按手机比例（默认 20:9）开；② 触屏长按逻辑生效，鼠标按下即当作触摸按下。
/// </para>
/// <para>
/// 开关：<c>-- --mobile-sim</c>，可选 <c>--mobile-size=1200x540</c>。
/// 真机（<c>OS.HasFeature("mobile")</c>）自动为 true，不需要这个开关。
/// </para>
/// </summary>
public static class MobileSim
{
    private static Vector2I _size = new(1200, 540);

    /// <summary>是否按手机处理（真机或 --mobile-sim）。</summary>
    public static bool Enabled { get; private set; }

    /// <summary>当前模拟分辨率（真机上是真实窗口尺寸）。</summary>
    public static Vector2I Size => _size;

    public static void Initialize()
    {
        var arguments = OS.GetCmdlineUserArgs();
        var device = OS.HasFeature("mobile");
        Enabled = device || arguments.Any(a => a == "--mobile-sim");
        if (!Enabled) return;

        foreach (var argument in arguments)
        {
            if (!argument.StartsWith("--mobile-size=", StringComparison.Ordinal)) continue;
            var parts = argument["--mobile-size=".Length..].Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height)
                && width > 320 && height > 240)
            {
                _size = new(width, height);
            }
        }

        if (device) return;
        // 只在桌面模拟时改窗口；真机窗口由系统给。
        DisplayServer.WindowSetSize(_size);
        GD.Print($"[mobile-sim] {_size.X}x{_size.Y} touch-only={Enabled}（关闭：去掉 --mobile-sim）");
    }
}
