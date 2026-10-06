using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>
/// Capture hook for the P11 presentations: the attack trajectory (a unit fires its own weapon at
/// the enemy HQ) and the end-of-match band. Both are pure visuals, so a headless test can never
/// confirm them — the frames have to be rendered and looked at.
/// </summary>
public partial class BattleScreen
{
    private const int FilmPreFrames = 6;
    private const int FilmSettleFrames = 26;
    private const int FilmHoldFrames = 30;

    /// <summary>
    /// Records the assault trajectory and the result banner as numbered PNGs. Choreography that
    /// awaits render signals internally cannot be photographed from the caller's own awaits, so
    /// frames are written by a per-frame observer attached to the process loop.
    /// </summary>
    public async Task CaptureP11FilmAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        // A headless render does not advance the engine's frame clock, so a timer-based wait inside
        // the choreography never returns. Step mode counts rendered frames instead.
        _combat.StepFrames = 1;
        _sequence.StepFrames = 1;
        var recorder = new P11FilmRecorder(this, dir);
        AddChild(recorder);
        var report = new List<string>();

        async Task Settle(int n)
        {
            for (var i = 0; i < n; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }

        // 1) Assault: an artillery unit fires a shell at the enemy HQ, then the hit lands.
        ResetDemo();
        await Settle(FilmPreFrames);
        StartScenario("artillery");
        await Settle(FilmPreFrames);
        var attacker = _cards.Values.First(c => c.View is { IsHq: false, Visibility: Visibility.Full }
            && c.View.Zone == "frontline" && c.View.OwnerSide == "self");
        var defender = _cards.Values.First(c => c.View is { IsHq: true, Visibility: Visibility.Full }
            && c.View.OwnerSide == "enemy");
        // The drag-aim overlay would cross the whole board and hide the very shot under review.
        _aim.Visible = false;
        CancelSelection();
        var assaultStart = recorder.Count;
        try
        {
            await _combat.PresentAssaultAsync(attacker,
            [
                (new UiOrderImpact(defender.View!, defender.View!, 2, attacker.View!), defender),
            ]);
        }
        catch (Exception e) { GD.Print("[p11-film] assault threw " + e.GetType().Name + ": " + e.Message); }
        var assaultFrames = recorder.Count - assaultStart;
        await Settle(FilmSettleFrames);
        report.Add($"assault frames={assaultFrames} attacker={attacker.View!.CardId} target={defender.View!.CardId}");

        // 2) Result band: a supplied finished match, so the banner and the panel both appear.
        ResetDemo();
        await Settle(FilmPreFrames);
        var resultStart = recorder.Count;
        try
        {
            await _sequence.ResultBannerAsync(true, "总部被摧毁");
        }
        catch (Exception e) { GD.Print("[p11-film] result threw " + e.GetType().Name + ": " + e.Message); }
        var resultFrames = recorder.Count - resultStart;
        ApplyProjection(_state with
        {
            Phase = "over",
            ResultTitle = "胜利",
            ResultReason = "HqZero",
            FinalTurn = 9,
        }, _actions);
        await Settle(FilmHoldFrames);
        report.Add($"result frames={resultFrames}");

        GD.Print($"P11_FILM_OK frames={recorder.Count} assault={assaultFrames} result={resultFrames} dir={dir}");
        foreach (var line in report) GD.Print("[p11-film] " + line);
        GetTree().Quit();
    }
}

/// <summary>
/// Writes one PNG per rendered frame. Attaching to the process loop is the only way to capture a
/// choreography that awaits render signals internally, and FramePostDraw is the only moment the
/// framebuffer holds a complete frame.
/// </summary>
public partial class P11FilmRecorder : Node
{
    private readonly Control _owner;
    private readonly string _dir;

    /// <summary>Frames written so far; the file index comes from here so nothing is overwritten.</summary>
    public int Count { get; private set; }

    public P11FilmRecorder(Control owner, string dir)
    {
        _owner = owner; _dir = dir;
        // FramePostDraw is the only moment the framebuffer holds a complete frame, and connecting
        // to it is what lets this observer catch a choreography that awaits render signals itself.
        RenderingServer.Singleton.Connect(RenderingServer.SignalName.FramePostDraw, Callable.From(Snap));
    }

    private void Snap()
    {
        _owner.GetViewport().GetTexture().GetImage().SavePng($"{_dir}/p11_{Count:D4}.png");
        Count++;
    }
}