using Kards.Ui.Core;
using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>
/// 跨平台调试面板：本地对局操作、状态检查及可筛选日志。
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
    private GameLogLevel _minimumLevel;
    private LineEdit _logSearch = null!;
    private double _refreshElapsed;
    public Func<MatchDebugSnapshot?>? ReadDebugState { get; set; }
    public Func<UiMatchView?>? ReadProjection { get; set; }
    public Func<MatchDebugCommand,Task<MatchDebugResult>>? ExecuteDebug { get; set; }
    public bool IsOpen => _open;

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
        GameLog.Info("console", "调试面板就绪：F1 或右下角按钮；本地对局可启用图形化调试。");
    }

    public override void _ExitTree()
    {
        GameLog.Written -= OnWritten;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private void Build()
    {
        var consoleTheme=UiStyles.CreateTheme();consoleTheme.DefaultFontSize=MobileSim.Enabled?24:16;
        // 触发按钮固定右下角，任何时候都能点（手机上没有 F1）。
        _toggle = new Button
        {
            Text = "控制台",
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "对局调试与日志（F1）",
        };
        _toggle.AddThemeFontSizeOverride("font_size", MobileSim.Enabled?24:13);
        _toggle.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _toggle.OffsetLeft = MobileSim.Enabled?-172:-104;
        _toggle.OffsetTop = MobileSim.Enabled?-78:-44;
        _toggle.OffsetRight = -12;
        _toggle.OffsetBottom = -12;
        _toggle.Pressed += Toggle;
        AddChild(_toggle);

        _panel = new PanelContainer { Visible = false,Theme=consoleTheme };
        _panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _panel.AnchorLeft = .03f; _panel.AnchorRight = .97f;
        _panel.AnchorTop = .27f; _panel.AnchorBottom = .92f;
        var background = new StyleBoxFlat { BgColor = new Color(0.05f, 0.07f, 0.08f, 0.94f) };
        background.SetContentMarginAll(8);
        background.SetBorderWidthAll(1);
        background.BorderColor = new Color(0.62f, 0.52f, 0.32f, 0.85f);
        _panel.AddThemeStyleboxOverride("panel", background);
        AddChild(_panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        _panel.AddChild(column);

        var header=new HBoxContainer();column.AddChild(header);
        header.AddChild(new Label{Text="游戏调试台",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill});
        AddButton(header,"刷新",RefreshDebug);
        AddButton(header,"复制诊断快照",CopySnapshot);
        AddButton(header,"关闭",Toggle);
        var tabs=new TabContainer{SizeFlagsVertical=Control.SizeFlags.ExpandFill};column.AddChild(tabs);
        BuildDebug(tabs);
        var logs=new VBoxContainer{Name="日志"};tabs.AddChild(logs);
        var filters=new HBoxContainer();logs.AddChild(filters);
        var level=new OptionButton();foreach(var name in new[]{"全部级别","信息及以上","警告及以上","仅错误"})level.AddItem(name);
        level.ItemSelected+=index=>{_minimumLevel=(GameLogLevel)index;Redraw();};filters.AddChild(level);
        _logSearch=new LineEdit{PlaceholderText="搜索类别或内容",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,ClearButtonEnabled=true};
        _logSearch.TextChanged+=_=>Redraw();filters.AddChild(_logSearch);
        AddButton(filters,"清空",()=>{GameLog.Clear();_recent.Clear();Redraw();});
        AddButton(filters,"复制日志",()=>DisplayServer.ClipboardSet(string.Join('\n',FilteredLogs())));

        _log = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            FitContent = false,
        };
        _log.AddThemeFontSizeOverride("normal_font_size", MobileSim.Enabled?22:14);
        _log.AddThemeColorOverride("default_color", new Color(0.86f, 0.87f, 0.84f));
        logs.AddChild(_log);

        _input = new LineEdit
        {
            PlaceholderText = "help / clear / screen <id> / state / log <debug|info|warn|error>",
            ClearButtonEnabled = true,
        };
        _input.AddThemeFontSizeOverride("font_size", MobileSim.Enabled?24:14);
        _input.TextSubmitted += Run;
        logs.AddChild(_input);
    }

    private void Toggle()
    {
        _open = !_open;
        _panel.Visible = _open;
        if (_open)
        {
            Redraw();
            RefreshDebug();
        }
    }

    public override void _Process(double delta)
    {
        if(!_open)return;
        _refreshElapsed+=delta;if(_refreshElapsed<.5)return;_refreshElapsed=0;RefreshDebug();
    }
    public bool CapturesInput(InputEvent input)
    {
        var at=input switch{InputEventMouse m=>(Vector2?)m.Position,InputEventScreenTouch t=>t.Position,InputEventScreenDrag d=>d.Position,_=>null};
        return at is { } point?(_toggle.GetGlobalRect().HasPoint(point)||_open&&_panel.GetGlobalRect().HasPoint(point))
            : _open&&input is InputEventKey;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if(key.Keycode==Key.Escape&&_open){Toggle();GetViewport().SetInputAsHandled();return;}
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
        foreach (var entry in FilteredLogs())
            _log.AppendText($"[color=#{Color(entry.Level)}]{entry.ToString().Replace("[","[lb]")}[/color]\n");
    }
    private IEnumerable<GameLogEntry> FilteredLogs()=>_recent.Where(e=>e.Level>=_minimumLevel&&(_logSearch.Text.Length==0||e.ToString().Contains(_logSearch.Text,StringComparison.OrdinalIgnoreCase)));

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
                if(parts.Length==2&&Enum.TryParse<GameLogLevel>(parts[1],true,out var minimum)){_minimumLevel=minimum;Redraw();}
                else Append("用法：log <debug|info|warn|error>");
                break;
            default:
                Append("未知命令：" + parts[0] + "（help 查看）");
                break;
        }
    }
}
