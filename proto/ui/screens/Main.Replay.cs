using Godot;
using Kards.Ui.Core;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private MatchReplayRecorder? _replayRecorder;
    private ReplayMatchFeed? _replayFeed;
    private PanelContainer? _readOnlyBar;
    private Label? _replayTime;
    private HSlider? _replaySeek;
    private Button? _replayPlay;
    private bool _updatingReplay;
    private int _replayLoadVersion;
    private string ReplayDirectory => ProjectSettings.GlobalizePath(_verificationRun ? "res://artifacts/verification-replays" : "user://replays");
    private void AttachRecording(IMatchFeed feed)
    {
        if (feed is ReplayMatchFeed) return;
        try
        {
            var perspective = feed is RemoteMatchFeed { IsObserver: true } ? "omniscient-viewer" : "player-view";
            var path = Path.Combine(ReplayDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".orcreplay");
            var recorder = new MatchReplayRecorder(path, perspective, "OrC-KSD" + VersionSuffix());
            _replayRecorder = recorder;
            void Record(UiMatchView view, UiPresentationResolution? presentation = null, int? seat = null)
            {
                if (_feed != feed || _replayRecorder != recorder) return;
                try { recorder.Record(view, presentation, seat); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    recorder.Dispose(); _replayRecorder = null;
                    Callable.From(() => _battle.ShowHint("回放保存失败：" + e.Message)).CallDeferred();
                }
            }
            if (feed is RemoteMatchFeed { IsObserver: true } observer)
            {
                observer.ObserverProjectionReady += (seat, view) => Record(view, seat: seat);
                observer.PresentationReady += (resolution, _) => Record(resolution.After, resolution, observer.Seat);
            }
            else
            {
                feed.ProjectionReady += (view, _) => Record(view);
                feed.PresentationReady += (resolution, _) => Record(resolution.After, resolution);
            }
            if (feed.CurrentView is { } initial) Record(initial);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _battle.ShowHint("无法保存回放：" + e.Message); }
    }
    private void OpenReplayLibrary()
    {
        Directory.CreateDirectory(ReplayDirectory);
        var dialog = new AcceptDialog { Title = "作战记录", MinSize = new Vector2I(900, 580) };
        var scroll = new ScrollContainer { CustomMinimumSize = new(850, 480), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 14); scroll.AddChild(rows); dialog.AddChild(scroll); AddChild(dialog);
        var head = new HBoxContainer(); rows.AddChild(head);
        head.AddChild(UiStyles.Label("作战记录  /  回放", 28, UiStyles.Gold));
        head.AddChild(UiStyles.Button("导入回放…", () => {
            var picker = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Filters = ["*.orcreplay ; 作战回放"] };
            AddChild(picker);
            picker.FileSelected += path => { dialog.QueueFree(); picker.QueueFree(); OpenReplay(path); };
            picker.Canceled += picker.QueueFree; picker.PopupCenteredRatio(.75f);
        }));
        rows.AddChild(UiStyles.Label("对局会自动记录。玩家记录保留当时视野；观战记录可切换已记录的视角。", 14, UiStyles.MenuDim));
        var files = Directory.EnumerateFiles(ReplayDirectory, "*.orcreplay").OrderByDescending(File.GetLastWriteTimeUtc).Take(100).ToArray();
        if (files.Length == 0) rows.AddChild(UiStyles.Label("尚无作战记录。完成一次训练或联机对局后，即可在这里回看。", 18, UiStyles.MenuInk));
        foreach (var file in files)
        {
            var panel = new PanelContainer(); panel.AddThemeStyleboxOverride("panel", MenuMaterials.Panel(new Color("252d29"), 18)); rows.AddChild(panel);
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 20); panel.AddChild(row);
            var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddChild(text);
            try
            {
                var header = MatchReplayRecorder.ReadHeader(file);
                text.AddChild(UiStyles.Label(header.CreatedAt.ToLocalTime().ToString("MM月dd日  HH:mm") + "  ·  作战记录", 22, UiStyles.MenuInk));
                text.AddChild(UiStyles.Label((header.Perspective == "omniscient-viewer" ? "观战视野" : "玩家视野") +
                    $"  ·  {new FileInfo(file).Length / 1024:N0} KB", 14, UiStyles.MenuDim));
                row.AddChild(UiStyles.Button("回看  →", () => { dialog.QueueFree(); OpenReplay(file); }));
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
            { text.AddChild(UiStyles.Label(Path.GetFileName(file) + "\n无法读取：" + e.Message, 14, UiStyles.MenuDim)); }
        }
        dialog.Confirmed += dialog.QueueFree; dialog.CloseRequested += dialog.QueueFree; dialog.PopupCentered();
    }
    private async void OpenReplay(string path)
    {
        var loadVersion = ++_replayLoadVersion;
        try
        {
            var recording = await Task.Run(() => MatchReplayRecorder.Load(path));
            if (loadVersion != _replayLoadVersion) return;
            StopRealMatch();
            var feed = new ReplayMatchFeed(recording); _replayFeed = feed;
            BindFeed(feed); Show("battle");
            BuildReadOnlyBar(feed, null); feed.Seek(0);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            var dialog = new AcceptDialog { Title = "无法打开回放", DialogText = e.Message };
            AddChild(dialog); dialog.Confirmed += dialog.QueueFree; dialog.CloseRequested += dialog.QueueFree; dialog.PopupCentered();
        }
    }
    private void BuildReadOnlyBar(ReplayMatchFeed? replay, RemoteMatchFeed? observer)
    {
        _readOnlyBar?.QueueFree();
        var panel = new PanelContainer { ZIndex = 90 }; _readOnlyBar = panel; AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide); panel.OffsetLeft = 230; panel.OffsetRight = -180; panel.OffsetTop = 14;
        panel.AddThemeStyleboxOverride("panel", MenuMaterials.Panel(new Color("18221e"), 10));
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 12); panel.AddChild(row);
        row.AddChild(UiStyles.Label(replay is null ? "观战 · 只读" : "回放 · 只读", 18, UiStyles.Gold));
        if (replay is not null)
        {
            _replayPlay = UiStyles.Button("播放", () => { if (replay.Position >= replay.Recording.Duration) replay.Seek(0); replay.Playing = !replay.Playing; }); row.AddChild(_replayPlay);
            var speed = new OptionButton(); foreach (var rate in new[] { "0.5×", "1×", "2×", "4×" }) speed.AddItem(rate);
            speed.Selected = 1; speed.ItemSelected += index => replay.Speed = new[] { .5, 1, 2, 4 }[(int)index]; row.AddChild(speed);
            _replaySeek = new HSlider { MinValue = 0, MaxValue = Math.Max(.001, replay.Recording.Duration), Step = .05, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(140, 30) };
            _replaySeek.ValueChanged += value => { if (!_updatingReplay) { replay.Playing = false; replay.Seek(value); } }; row.AddChild(_replaySeek);
            _replayTime = UiStyles.Label("", 14, UiStyles.MenuInk); row.AddChild(_replayTime);
        }
        if (replay?.Recording.Header.Perspective == "omniscient-viewer")
        {
            var perspective = new OptionButton(); perspective.AddItem("玩家 A 视角"); perspective.AddItem("玩家 B 视角");
            perspective.ItemSelected += seat => replay.SetPerspective((int)seat); row.AddChild(perspective);
        }
        if (observer is not null)
        {
            row.AddChild(UiStyles.Button("玩家 A 视角", () => observer.SetObserverPerspective(0)));
            row.AddChild(UiStyles.Button("玩家 B 视角", () => observer.SetObserverPerspective(1)));
        }
        row.AddChild(UiStyles.Button("离开", () => { StopRealMatch(); if (observer is not null) _room.Close(); Show("setup"); }));
        foreach (var button in row.GetChildren().OfType<Button>()) MenuMaterials.Button(button);
    }
    private void UpdateReplayControls()
    {
        if (_readOnlyBar is not null) _readOnlyBar.Visible = _active == "battle";
        if (_replayFeed is not { } replay || _replaySeek is null) return;
        _updatingReplay = true; _replaySeek.Value = replay.Position; _updatingReplay = false;
        _replayPlay!.Text = replay.Playing ? "暂停" : "播放";
        _replayTime!.Text = $"{TimeSpan.FromSeconds(replay.Position):mm\\:ss} / {TimeSpan.FromSeconds(replay.Recording.Duration):mm\\:ss}";
    }
}
