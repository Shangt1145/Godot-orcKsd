using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    /// <summary>Capture the real presentation path, including handoff, dust and whole-board shake.</summary>
    public async Task CaptureSlamFilmAsync(string dir)
    {
        var jpeg = OS.GetCmdlineUserArgs().Contains("--capture-slam-jpeg");
        if (jpeg) dir += "-preview";
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(dir + "/.gdignore", "");
        var speed = _clock.Current; var reduced = _clock.ReducedMotion;
        var guiDisabled = GetViewport().GuiDisableInput;
        GetViewport().GuiDisableInput = true;
        _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
        var frame = 0;
        async Task Tick()
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var image = GetViewport().GetTexture().GetImage();
            if (jpeg) image.SaveJpg($"{dir}/slam_{frame:D4}.jpg", .95f);
            else image.SavePng($"{dir}/slam_{frame:D4}.png");
            frame++;
        }
        try
        {
            foreach (var defense in new[] { 3, 6, 10 })
            {
                ResetDemo();
                var side = defense == 6 ? "enemy" : "self";
                var before = _state with { MatchId = $"deployment-film-{defense}" };
                var unit = before.SelfHand[0] with { Uid = "film-deploy", OwnerSide = side, Zone = "hand",
                    EffectiveDefense = defense, Health = defense };
                before = side == "self" ? before with { SelfHand = [unit], SelfHandCount = 1 }
                    : before with { SelfHand = [], SelfHandCount = 0, EnemyHandCount = 1 };
                var placed = unit with { Zone = "support", SlotIndex = 3 };
                var after = side == "self" ? before with { SelfHand = [], SelfHandCount = 0, SelfLine = before.SelfLine.Append(placed).ToArray() }
                    : before with { EnemyHandCount = 0, EnemyLine = before.EnemyLine.Append(placed).ToArray() };
                ApplyProjection(after, new());
                var landingCenter = _cards[unit.Uid].RestPosition + _cards[unit.Uid].Size / 2;
                ApplyProjection(before, new());
                var style = BattleSequence.SlamStyle(defense);
                var caption = new BattleTierCaption { Size = Size,
                    TierLabel = defense == 3 ? "轻" : defense == 6 ? "中" : "重", DefenseValue = defense,
                    Detail = $"蓄力 {style.AnticipationSeconds:0.00}s · 收牌 {style.CollapseSeconds:0.00}s · 拍桌 {style.Shake:0.#}" };
                AddChild(caption);
                if (side == "self")
                {
                    var heldSize = new Vector2(144, 202);
                    _deploymentOrigins[unit.Uid] = new(landingCenter - heldSize / 2 - new Vector2(0, 12),
                        heldSize, Vector2.One, 0);
                    RestorePendingDeploymentPoses();
                }
                for (var i = 0; i < 24; i++) await Tick();
                var startFrame = frame;
                var playback = PresentSequenceAsync(new(before.MatchId,
                    [new UiDeploymentPresentation(placed, after)], after), new());
                while (!playback.IsCompleted) await Tick();
                await playback;
                for (var i = 0; i < 36; i++) await Tick();
                caption.QueueFree();
                GD.Print($"[slam-film] defense={defense} side={side} start={startFrame} end={frame} real-deployment-path");
            }
            GD.Print($"SLAM_FILM_OK frames={frame} dir={dir}");
        }
        finally
        {
            ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced);
            GetViewport().GuiDisableInput = guiDisabled;
        }
        GetTree().Quit();
    }
}

public partial class BattleTierCaption : Control
{
    public string TierLabel { get; set; } = "";
    public int DefenseValue { get; set; }
    public string Detail { get; set; } = "";
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; QueueRedraw(); }
    public override void _Draw()
    {
        var box = new Rect2(24, 22, 396, 92);
        DrawRect(box, new Color(.05f, .06f, .08f, .72f));
        DrawRect(box, new Color(.85f, .66f, .30f, .9f), false, 2);
        DrawString(ThemeDB.FallbackFont, new(42, 56), $"部署拍桌 · {TierLabel} · 防御 {DefenseValue}",
            HorizontalAlignment.Left, -1, 26, new(.95f, .91f, .84f));
        DrawString(ThemeDB.FallbackFont, new(42, 92), Detail,
            HorizontalAlignment.Left, -1, 15, new(.72f, .70f, .64f));
    }
}
