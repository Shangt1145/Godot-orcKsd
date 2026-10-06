using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>
/// Capture hook for the reworked deployment slam. Plays the three build tiers in turn — build is
/// defense, so the tier defense value is the only thing that changes — and writes one PNG per frame
/// for an external tool to assemble into a video.
/// </summary>
public partial class BattleScreen
{
    private static readonly (string Label, int Defense)[] SlamFilmTiers =
    [
        ("tier0-light-def1", 1),
        ("tier1-medium-def4", 4),
        ("tier2-heavy-def7", 7),
    ];

    /// <summary>Frames captured at 60fps per phase.</summary>
    private const int SlamFilmFps = 60;
    private const int SlamFilmPreFrames = 24;         // the card hanging in the air before the drop
    private const int SlamFilmPostFrames = 105;      // the weight settling afterwards (~1.75s)

    /// <summary>
    /// Captures the three-tier landing sequence. Exits the process when finished so the run script
    /// terminates without a manual quit.
    /// </summary>
    public async Task CaptureSlamFilmAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var frame = 0;
        // Sample the frame that was already drawn, then wait for the next one.
        async Task Tick()
        {
            GetViewport().GetTexture().GetImage().SavePng($"{dir}/slam_{frame:D4}.png");
            frame++;
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
        async Task Settle(int n) { for (var i = 0; i < n; i++) await Tick(); }

        for (var tier = 0; tier < SlamFilmTiers.Length; tier++)
        {
            var (label, defense) = SlamFilmTiers[tier];
            // ResetDemo rebuilds the card nodes, so the probe must be looked up again every round.
            ResetDemo();
            await Settle(2);
            var card = _cards.Values.FirstOrDefault(c =>
                c.View is { } v && !v.IsHq && v.Visibility == Visibility.Full)
                ?? throw new Exception("No field card available for the slam film.");
            card.Visible = true;
            card.Position = card.RestPosition;
            card.Scale = Vector2.One;
            card.Modulate = Colors.White;
            var style = BattleSequence.SlamStyle(defense);
            var landingFrames = Math.Max(1, (int)Math.Round(BattleSequence.SlamSeconds(defense) * SlamFilmFps));

            // Caption the tier so the clip reads without narration. It stays up for the whole round,
            // not just the landing, so viewers can read the numbers while watching the weight.
            var caption = new BattleTierCaption
            {
                Size = Size,
                TierLabel = tier switch { 0 => "轻", 1 => "中", _ => "重" },
                DefenseValue = defense,
                Detail = $"尘土 {style.Dust}px · 震屏 {style.Shake}px · 亮度 +{style.Flash:0.00}",
            };
            AddChild(caption);

            await Settle(SlamFilmPreFrames);

            // Drive the landing while sampling frames, so the dust and the flash are on record.
            var playback = PlaySlamFrames(card, defense, landingFrames + SlamFilmPostFrames, Tick);
            await playback;
            caption.QueueFree();

            GD.Print($"[slam-film] {label} frames={frame} landingFrames={landingFrames} squash={style.Squash} dust={style.Dust} shake={style.Shake}");
        }
        GD.Print($"SLAM_FILM_OK frames={frame} dir={dir}");
        GetTree().Quit();
    }

    /// <summary>
    /// Replays the landing as N discrete steps rather than letting the tween run on the frame clock:
    /// a headless capture can drop frames, and a dropped landing frame is the whole point of the film.
    /// </summary>
    private async Task PlaySlamFrames(BattleCard card, int defense, int frames, Func<Task> tick)
    {
        var style = BattleSequence.SlamStyle(defense);
        var rest = card.RestPosition;
        var dust = style.Dust > 0
            ? new SlamDust { Size = Size, Center = rest + card.Size * new Vector2(.5f, .92f),
                Diameter = style.Dust, Duration = .42 }
            : null;
        if (dust is not null) _sequence.AddChild(dust);

        // Landing frame: the whole weight arrives at once (measured), then it settles.
        card.Position = rest + new Vector2(0, style.Squash * 260f);
        card.Scale = new(1 + style.Squash, 1 - style.Squash);
        card.Modulate = new Color(1f + style.Flash, 1f + style.Flash, 1f + style.Flash, 1f);
        await tick();

        var settle = Math.Max(1, frames - 1);
        for (var i = 1; i <= settle; i++)
        {
            var t = (float)i / settle;
            var flash = style.Flash * (1f - Math.Min(1f, t / .55f));
            card.Position = rest;
            card.Scale = Vector2.One;
            card.Modulate = new Color(1f + flash, 1f + flash, 1f + flash, 1f);
            if (dust is { } d && GodotObject.IsInstanceValid(d)) d.QueueRedraw();
            await tick();
        }
        card.Position = rest; card.Scale = Vector2.One; card.Modulate = Colors.White;
        if (dust is not null && GodotObject.IsInstanceValid(dust) && dust.GetParent() != null)
        { dust.GetParent()!.RemoveChild(dust); dust.QueueFree(); }
    }
}

/// <summary>Tier caption for the slam film. Geometry only; the numbers come from the shared table.</summary>
public partial class BattleTierCaption : Control
{
    public string TierLabel { get; set; } = "";
    public int DefenseValue { get; set; }
    public string Detail { get; set; } = "";
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; QueueRedraw(); }
    public override void _Draw()
    {
        var box = new Rect2(24, 22, 396, 92);
        DrawRect(box, new Color(0.05f, 0.06f, 0.08f, .72f));
        DrawRect(box, new Color(.85f, .66f, .30f, .9f), false, 2f);
        DrawString(ThemeDB.FallbackFont, new Vector2(42, 56), $"部署拍桌 · 身材 {TierLabel} · 防御 {DefenseValue}",
            HorizontalAlignment.Left, -1, 26, new Color(.95f, .91f, .84f));
        DrawString(ThemeDB.FallbackFont, new Vector2(42, 92), Detail,
            HorizontalAlignment.Left, -1, 15, new Color(.72f, .70f, .64f));
    }
}