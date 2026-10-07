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
    private OrcMatchHost? _secondSeat;

    public bool IsRunning => _session is not null;
    public string MatchId { get; } = "orc-live";

    /// <summary>The opening hand is answered through the panel rather than kept automatically.</summary>
    public bool InteractiveMulligan { get; init; }

    /// <summary>Card id to art path, supplied by the UI's own catalog. The engine has no art concept.</summary>
    public Func<string, string>? ArtLookup { get; init; }

    /// <summary>
    /// Deals from the shipped card catalog instead of the single probe card. A null pool falls back
    /// to the probe card, so the verify paths keep working without a prepared catalog.
    /// </summary>
    public IReadOnlyList<UiCardDefinition>? CatalogCards { get; init; }

    /// <summary>When set, the match is opened hotseat and this is the seat the screen starts on.</summary>
    public bool Hotseat { get; init; }

    /// <summary>
    /// Deck seed. A verification run needs a hand it can actually play, and the opening is random —
    /// with the shipped pool most hands hold nothing affordable on turn one. Fixing the seed makes
    /// the run repeatable; it is not a product behaviour.
    /// </summary>
    public int? DeckSeed { get; init; }

    /// <summary>Which seat is currently shown: 0 or 1. Only meaningful when <see cref="Hotseat"/>.</summary>
    public int ActiveSeat { get; private set; }

    /// <summary>True when it is the seat now on screen whose turn it is.</summary>
    public bool ActiveSeatOnTurn => _session is not null
        && Match.CurrentPlayer is not null
        && Match.CurrentPlayer.Index == ActiveSeat;

    private Orc.Game.Match Match => _session?.Host.Match
        ?? throw new InvalidOperationException("The match is not running.");

    /// <summary>Swaps the visible seat. The engine keeps one truth; only the viewpoint changes.</summary>
    public void SwitchSeat()
    {
        if (!Hotseat || _session is null) return;
        ActiveSeat = ActiveSeat == 0 ? 1 : 0;
        Publish();
    }

    public event Action<UiMatchView, UiBattleActions>? ProjectionReady;
    public event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;
    public event Action<IReadOnlyList<UiOrderImpact>, UiMatchView, UiBattleActions>? CombatReady;
    public event Action<string>? ErrorRaised;

    /// <summary>Raised when the engine parks on a request the UI must answer (the mulligan panel).</summary>
    public event Action? MulliganRequested;

    public event Action<string>? HintRequested;

    public async Task StartAsync(CancellationToken ct = default)
    {
        // A real catalog means a real deck. The probe card is the fallback so the verify paths
        // still run on a machine where the catalog has not been prepared.
        var pool = CatalogCards is { Count: > 0 } ? CardPoolCompiler.Compile(CatalogCards).Entries : null;
        OrcMatchSession session;
        if (pool is { Count: > 0 })
        {
            var deck = DeckBuilder.Build(pool, seed: DeckSeed ?? 20261005);
            session = await OrcMatchSession.CreateAsync(pool, deck, deck,
                seed: DeckSeed ?? 20261005, matchId: MatchId, artLookup: ArtLookup, ct: ct);
        }
        else
        {
            session = await OrcMatchSession.CreateProbeAsync(InfantryId, 20, seed: DeckSeed ?? 20261005,
                matchId: MatchId, artLookup: ArtLookup, ct: ct);
        }
        _session = session;
        session.Host.ImmediateUpdate += _ => { };
        session.Host.PresentationReady += (resolution, actions) => PresentationReady?.Invoke(resolution, actions);
        session.Host.CombatReady += (impacts, after, actions) => CombatReady?.Invoke(impacts, after, actions);
        session.Host.ErrorRaised += message => ErrorRaised?.Invoke(message);
        // The engine parks on the mulligan slot; that is the moment the panel must open.
        session.MulliganRequested += () => MulliganRequested?.Invoke();

        if (Hotseat)
        {
            // The other seat reads the same live match. It adopts rather than initialises, because
            // the match is already running — one engine, one truth, two viewpoints.
            _secondSeat = new OrcMatchHost(session.Host.Match, $"{MatchId}-seat1", viewerIndex: 1, ArtLookup);
            _secondSeat.ImmediateUpdate += _ => { };
            _secondSeat.AttachToLiveMatch();
        }

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
    /// <summary>
    /// Frame-loop entry point. Only the active seat drains segments: two hosts pumping the same
    /// match would each consume the other's updates.
    /// </summary>
    public void Pump()
    {
        if (_session is null) return;
        if (ActiveSeat == 0 || _secondSeat is null) _session.Pump();
        else _secondSeat.Pump();
    }

    /// <summary>Ends the match and detaches the host so no further segments are delivered.</summary>
    public void Stop()
    {
        _session?.Host.Detach();
        _session?.Dispose();
        _session = null;
    }

    /// <summary>The seat currently on screen. Null before the match starts.</summary>
    public UiMatchView? CurrentView => (ActiveSeat == 0 || _secondSeat is null) ? _session?.View : _secondSeat.View;

    /// <summary>Actions for the seat on screen, so a hotseat player is never offered the other's moves.</summary>
    public UiBattleActions CurrentActions => (ActiveSeat == 0 || _secondSeat is null)
        ? _session?.Actions ?? new UiBattleActions()
        : _secondSeat.Actions;

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
        if (ActiveSeat == 0 || _secondSeat is null)
        {
            ProjectionReady?.Invoke(_session.View, _session.Actions);
            return;
        }
        _secondSeat.Pump();
        ProjectionReady?.Invoke(_secondSeat.View, _secondSeat.Actions);
    }



    private static string Reason(string? reason) => reason switch
    {
        null or "" => "引擎拒绝了该操作",
        _ => reason,
    };
}
