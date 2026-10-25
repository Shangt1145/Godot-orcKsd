using Kards.Ui.Core;
using Godot;

namespace Kards.Ui;

/// <summary>
/// 简易控制台：一个可随时呼出的浮层，显示 <see cref="GameLog"/> 的最近条目并接受几条调试命令。
/// 桌面按 F1 切换，手机点右下角“控制台”按钮。挂在 CanvasLayer 上，不参与屏幕切换。
/// </summary>
public partial class GameConsole : CanvasLayer
{
    private const int VisibleLines = 400;

    private readonly List<GameLogEntry> _recent = [];
    private PanelContainer _panel = null!;
    private RichTextLabel _log = null!;
    private LineEdit _input = null!;
    private Button _toggle = null!;
    private bool _open;

    /// <summary>切屏回调（由 Main 注入），供 <c>screen &lt;id&gt;</c> 命令使用。</summary>
    public Action<string>? ShowScreenRequested { get; set; }

    /// <summary>状态摘要回调（由 Main 注入），供 <c>state</c> 命令使用。</summary>
    public Func<string>? DescribeState { get; set; }

    public static GameConsole? Current { get; private set; }

    public override void _Ready()
    {
        Current = this;
        Layer = 128; // 压在其它 CanvasLayer 之上，但仍受屏幕内容遮挡控制。
        Build();
        GameLog.Written += OnWritten;
        foreach (var entry in GameLog.Snapshot()) _recent.Add(entry);
        Trim();
        Redraw();
        GameLog.Info("console", "控制台就绪：F1 或右下角按钮；输入 help 查看命令。");
    }

    public override void _ExitTree()
    {
        GameLog.Written -= OnWritten;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private void Build()
    {
        // 触发按钮固定右下角，任何时候都能点（手机上没有 F1）。
        _toggle = new Button
        {
            Text = "控制台",
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "显示/隐藏日志控制台（F1）",
        };
        _toggle.AddThemeFontSizeOverride("font_size", 13);
        _toggle.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _toggle.OffsetLeft = -104;
        _toggle.OffsetTop = -44;
        _toggle.OffsetRight = -12;
        _toggle.OffsetBottom = -12;
        _toggle.Pressed += Toggle;
        AddChild(_toggle);

        _panel = new PanelContainer { Visible = false };
        _panel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _panel.OffsetTop = -340;
        _panel.OffsetBottom = -52;
        var background = new StyleBoxFlat { BgColor = new Color(0.05f, 0.07f, 0.08f, 0.94f) };
        background.SetContentMarginAll(8);
        background.SetBorderWidthAll(1);
        background.BorderColor = new Color(0.62f, 0.52f, 0.32f, 0.85f);
        _panel.AddThemeStyleboxOverride("panel", background);
        AddChild(_panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        _panel.AddChild(column);

        _log = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            FitContent = false,
        };
        _log.AddThemeFontSizeOverride("normal_font_size", 12);
        _log.AddThemeColorOverride("default_color", new Color(0.86f, 0.87f, 0.84f));
        column.AddChild(_log);

        _input = new LineEdit
        {
            PlaceholderText = "help / clear / screen <id> / state / log <debug|info|warn|error>",
            ClearButtonEnabled = true,
        };
        _input.AddThemeFontSizeOverride("font_size", 12);
        _input.TextSubmitted += Run;
        column.AddChild(_input);
    }

    private void Toggle()
    {
        _open = !_open;
        _panel.Visible = _open;
        if (_open)
        {
            Redraw();
            if (!OS.HasFeature("mobile")) _input.GrabFocus();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode is not (Key.F1 or Key.Quoteleft)) return;
        Toggle();
        GetViewport().SetInputAsHandled();
    }

    private void OnWritten(GameLogEntry entry)
        // 效果回调在协议线程，控件只能在主线程写。
        => Callable.From(() =>
        {
            _recent.Add(entry);
            Trim();
            if (_open) Redraw();
        }).CallDeferred();

    private void Trim()
    {
        var excess = _recent.Count - VisibleLines;
        if (excess > 0) _recent.RemoveRange(0, excess);
    }

    private static string Color(GameLogLevel level) => level switch
    {
        GameLogLevel.Error => "e0736b",
        GameLogLevel.Warn => "d9a441",
        GameLogLevel.Info => "a8b3ab",
        _ => "6f7a74",
    };

    private void Redraw()
    {
        _log.Clear();
        foreach (var entry in _recent)
            _log.AppendText($"[color=#{Color(entry.Level)}]{entry}[/color]\n");
    }

    private void Append(string text) => GameLog.Info("console", text);

    private void Run(string command)
    {
        _input.Clear();
        var line = command.Trim();
        if (line.Length == 0) return;
        Append("> " + line);
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                Append("命令：help | clear | screen <id> | state | log <级别> | hide");
                break;
            case "clear":
                GameLog.Clear();
                _recent.Clear();
                Redraw();
                break;
            case "hide":
                if (_open) Toggle();
                break;
            case "state":
                Append(DescribeState?.Invoke() ?? "（无状态回调）");
                break;
            case "screen":
                if (parts.Length < 2) { Append("用法：screen <id>"); break; }
                if (ShowScreenRequested is { } show) { show(parts[1]); Append("切屏 → " + parts[1]); }
                else Append("（无切屏回调）");
                break;
            case "log":
                Append(parts.Length < 2
                    ? "用法：log <debug|info|warn|error>"
                    : "日志级别过滤暂未实现；当前显示最近 " + VisibleLines + " 条。");
                break;
            default:
                Append("未知命令：" + parts[0] + "（help 查看）");
                break;
        }
    }
}
