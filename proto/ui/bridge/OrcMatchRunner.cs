using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>
/// Drives a real match for the Godot layer. Every engine conversation goes through
/// <see cref="OrcMatchSession"/>: this type holds no engine types of its own, so an upstream change
/// lands in the bridge instead of rippling into the screens. It only adds what the bridge
/// deliberately leaves out — Godot diagnostics and the player-facing hint strings.
/// </summary>
public sealed class OrcMatchRunner
{
    private const string InfantryId = "orc-demo-infantry";

    private OrcMatchSession? _session;

    public bool IsRunning => _session is not null;
    public string MatchId { get; } = "orc-live";

    /// <summary>The opening hand is answered through the panel rather than kept automatically.</summary>
    public bool InteractiveMulligan { get; init; }

    /// <summary>Card id to art path, supplied by the UI's own catalog. The engine has no art concept.</summary>
    public Func<string, string>? ArtLookup { get; init; }

    public event Action<UiMatchView, UiBattleActions>? ProjectionReady;
    public event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;
    public event Action<IReadOnlyList<UiOrderImpact>, UiMatchView, UiBattleActions>? CombatReady;
    public event Action<string>? ErrorRaised;

    /// <summary>Raised when the engine parks on a request the UI must answer (the mulligan panel).</summary>
    public event Action? MulliganRequested;

    public event Action<string>? HintRequested;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var session = await OrcMatchSession.CreateProbeAsync(InfantryId, 20, seed: 20261005,
            matchId: MatchId, artLookup: ArtLookup, ct: ct);
        _session = session;
        session.Host.ImmediateUpdate += _ => { };
        session.Host.PresentationReady += (resolution, actions) => PresentationReady?.Invoke(resolution, actions);
        session.Host.CombatReady += (impacts, after, actions) => CombatReady?.Invoke(impacts, after, actions);
        session.Host.ErrorRaised += message => ErrorRaised?.Invoke(message);
        // The engine parks on the mulligan slot; that is the moment the panel must open.
        session.MulliganRequested += () => MulliganRequested?.Invoke();

        Publish();

        // The opponent side is answered by the driver; the viewer either answers through the panel
        // (the request parks until ChooseMulligan arrives) or keeps everything.
        await session.SettleMulliganAsync(InteractiveMulligan, ct);
        // Only the answered path needs a pump: draining segments while the mulligan request is
        // still parked would consume state that task is waiting on. The frame loop covers the rest.
        if (!InteractiveMulligan) session.Pump();
        Publish();
    }

    /// <summary>Frame-loop entry point. Segments are unbounded, so this must run every frame.</summary>
    public void Pump() => _session?.Pump();

    /// <summary>Ends the match and detaches the host so no further segments are delivered.</summary>
    public void Stop()
    {
        _session?.Host.Detach();
        _session?.Dispose();
        _session = null;
    }

    public UiMatchView? CurrentView => _session?.View;

    public UiBattleActions CurrentActions => _session?.Actions ?? new UiBattleActions();

    /// <summary>
    /// Submits one contract command. The session owns the engine rules; this only reports refusals
    /// in the player's language and keeps the board out of the way until Pump delivers the segment.
    /// </summary>
    public async Task SubmitAsync(UiCommand command, CancellationToken ct = default)
    {
        if (_session is null) return;
        GD.Print($"[engine] submit {command.GetType().Name}");
        if (command is ChooseMulligan keep)
        {
            await CompleteMulliganAsync(keep.KeepUids, ct);
            return;
        }
        if (command is MoveUnit move) _session.NoteSlot(move.SlotIndex);
        if (command is AttackUnit attack) _session.NoteSelection(attack.DefenderUid);

        UiSubmitOutcome outcome;
        try
        {
            outcome = await _session.SubmitAsync(command, ct);
        }
        catch (Exception e)
        {
            // Engine entry points mostly report through result objects; the few that throw (phase
            // gates) must not die silently in a fire-and-forget task.
            GD.PushError($"[engine] {command.GetType().Name}: {e.Message}");
            HintRequested?.Invoke(Reason(e.Message.Contains(' ') ? null : e.Message));
            return;
        }

        switch (outcome)
        {
            case UiSubmitOutcome.NotYourTurn:
                HintRequested?.Invoke("等待对手行动");
                break;
            case UiSubmitOutcome.WrongPhase:
                HintRequested?.Invoke("当前阶段不接受该操作");
                break;
            case UiSubmitOutcome.UnknownCard:
                HintRequested?.Invoke("找不到该单位");
                break;
            case UiSubmitOutcome.Rejected:
                HintRequested?.Invoke("引擎拒绝了该操作");
                break;
        }
        // The board is not refreshed here: Pump picks up the segment and the presentation plays first.
    }

    /// <summary>
    /// Submits the panel's keep list. The engine replaces everything not kept (silent draw),
    /// confirms the side, and enters play once both sides have confirmed.
    /// </summary>
    private async Task CompleteMulliganAsync(IReadOnlyList<string> keepUids, CancellationToken ct)
    {
        if (_session is null) { HintRequested?.Invoke("当前没有等待的换牌"); return; }
        // No MulliganRequested here: that event means "a request arrived", and re-raising it after
        // answering would pop the panel a second time.
        var outcome = await _session.CompleteMulliganAsync(keepUids, ct);
        if (outcome != UiSubmitOutcome.Applied) HintRequested?.Invoke("当前没有等待的换牌");
        // Answering produces a segment; the refreshed view (and the play phase) arrives on Pump.
        _session.Pump();
        Publish();
    }

    private void Publish()
    {
        if (_session is null) return;
        ProjectionReady?.Invoke(_session.View, _session.Actions);
    }



    private static string Reason(string? reason) => reason switch
    {
        null or "" => "引擎拒绝了该操作",
        _ => reason,
    };
}
