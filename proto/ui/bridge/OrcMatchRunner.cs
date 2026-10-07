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
    private readonly CancellationTokenSource _lifetime = new();
    private bool _stopped;

    public bool IsRunning => _session is not null;
    public bool IsSubmitting => _session?.IsSubmitting == true;
    public bool TargetPending => _session?.TargetPending == true;
    public string MatchId { get; } = $"orc-{Guid.NewGuid():N}";

    /// <summary>The opening hand is answered through the panel rather than kept automatically.</summary>
    public bool InteractiveMulligan { get; init; }

    /// <summary>Card id to art path, supplied by the UI's own catalog. The engine has no art concept.</summary>
    public Func<string, string>? ArtLookup { get; init; }

    /// <summary>
    /// Deals from the shipped card catalog instead of the single probe card. A null pool falls back
    /// to the probe card, so the verify paths keep working without a prepared catalog.
    /// </summary>
    public IReadOnlyList<UiCardDefinition>? CatalogCards { get; init; }
    public string? CatalogSourceDirectory { get; init; }
    public UiCardPoolReport? CardPoolReport => _session?.CardPoolReport;

    /// <summary>When set, the match is opened hotseat and this is the seat the screen starts on.</summary>
    public bool Hotseat { get; init; }

    /// <summary>
    /// Deck seed. A verification run needs a hand it can actually play, and the opening is random —
    /// with the shipped pool most hands hold nothing affordable on turn one. Fixing the seed makes
    /// the run repeatable; it is not a product behaviour.
    /// </summary>
    public int? DeckSeed { get; init; }

    /// <summary>
    /// Pins the cards the player opens with, replacing whatever was dealt. A demonstration aid: it
    /// lets the deployment slam be observed at every size tier in a real match. Null deals normally.
    /// </summary>
    public IReadOnlyList<string>? OpeningHand { get; init; }

    /// <summary>Which seat is currently shown: 0 or 1. Only meaningful when <see cref="Hotseat"/>.</summary>
    public int ActiveSeat { get; private set; }

    /// <summary>True when it is the seat now on screen whose turn it is.</summary>
    public bool ActiveSeatOnTurn => _session?.IsViewerOnTurn == true;

    /// <summary>Swaps the visible seat. The engine keeps one truth; only the viewpoint changes.</summary>
    public void SwitchSeat()
    {
        if (!Hotseat || _session is null) return;
        if (_session.MulliganPending) { HintRequested?.Invoke("请先完成换牌"); return; }
        if (_session.IsSubmitting) { HintRequested?.Invoke("上一操作尚未完成"); return; }
        ActiveSeat = ActiveSeat == 0 ? 1 : 0;
        _session.SelectViewer(ActiveSeat);
        Publish();
    }

    public event Action<UiMatchView, UiBattleActions>? ProjectionReady;
    public event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;
    public event Action<IReadOnlyList<UiOrderImpact>, UiMatchView, UiBattleActions>? CombatReady;
    public event Action<string>? ErrorRaised;

    /// <summary>Raised when the engine parks on a request the UI must answer (the mulligan panel).</summary>
    public event Action? MulliganRequested;
    public event Action<UiTargetRequest, IUiTargetResponder>? TargetRequested;
    public event Action? TargetClosed;

    public event Action<string>? HintRequested;

    public async Task StartAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        // A real catalog means a real deck. The probe card is the fallback so the verify paths
        // still run on a machine where the catalog has not been prepared.
        OrcMatchSession session;
        if (CatalogCards is { Count: > 0 })
        {
            session = await OrcMatchSession.CreateFromCatalogAsync(CatalogCards,
                seed: DeckSeed ?? Random.Shared.Next(), matchId: MatchId, fallbackCardId: InfantryId, artLookup: ArtLookup, ct: ct,
                openingHand: OpeningHand, sourceDirectory: CatalogSourceDirectory);
        }
        else
        {
            session = await OrcMatchSession.CreateDefaultProbeAsync(InfantryId, 20, seed: DeckSeed ?? Random.Shared.Next(),
                matchId: MatchId, artLookup: ArtLookup, ct: ct);
        }
        if (_stopped) { session.Dispose(); return; }
        _session = session;
        session.DriveOpponent = !Hotseat;
        session.Host.ImmediateUpdate += _ => { };
        session.Host.PresentationReady += (resolution, actions) => PresentationReady?.Invoke(resolution, actions);
        session.Host.CombatReady += (impacts, after, actions) => CombatReady?.Invoke(impacts, after, actions);
        session.Host.ErrorRaised += message => ErrorRaised?.Invoke(message);
        // The engine parks on the mulligan slot; that is the moment the panel must open.
        session.MulliganRequested += () => MulliganRequested?.Invoke();
        session.TargetRequested += (request, responder) => TargetRequested?.Invoke(request, responder);
        session.TargetClosed += () => TargetClosed?.Invoke();

        Publish();

        // The opponent side is answered by the driver; the viewer either answers through the panel
        // (the request parks until ChooseMulligan arrives) or keeps everything.
        await session.SettleMulliganAsync(InteractiveMulligan, ct);
        // Publish the settled baseline; initialization snapshots must not replay over the new phase.
        if (_stopped) return;
        Publish();
    }

    /// <summary>Frame-loop entry point. One host drains the queue for either viewpoint.</summary>
    public void Pump()
    {
        if (_session is null) return;
        _session.Pump();
    }

    /// <summary>Ends the match and detaches the host so no further segments are delivered.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _lifetime.Cancel();
        _session?.Dispose();
        _session = null;
    }

    /// <summary>The seat currently on screen. Null before the match starts.</summary>
    public UiMatchView? CurrentView => _session?.View;

    /// <summary>Actions for the seat on screen, so a hotseat player is never offered the other's moves.</summary>
    public UiBattleActions CurrentActions => _session?.TargetPending == true ? new() : _session?.Actions ?? new();

    public event Action<UiCommand>? CommandRefused;

    /// <summary>
    /// Submits one contract command. The session owns the engine rules; this only reports refusals
    /// in the player's language and keeps the board out of the way until Pump delivers the segment.
    /// </summary>
    public async Task SubmitAsync(UiCommand command, CancellationToken ct = default)
    {
        if (_session is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        GD.Print($"[engine] submit {command.GetType().Name}");
        if (command is ChooseMulligan keep)
        {
            await CompleteMulliganAsync(keep.KeepUids, ct);
            return;
        }
        var session = _session;
        UiSubmitOutcome outcome;
        try
        {
            outcome = await session.SubmitAsync(command, ct);
        }
        catch (Exception e)
        {
            if (_stopped || session != _session) return;
            // Engine entry points mostly report through result objects; the few that throw (phase
            // gates) must not die silently in a fire-and-forget task.
            GD.PushError($"[engine] {command.GetType().Name}: {e.Message}");
            CommandRefused?.Invoke(command);
            HintRequested?.Invoke(Reason(e.Message.Contains(' ') ? null : e.Message));
            return;
        }

        if (_stopped || session != _session) return;
        if (outcome is not UiSubmitOutcome.Applied)
            CommandRefused?.Invoke(command); // refusal does not interrupt another action's animation

        switch (outcome)
        {
            case UiSubmitOutcome.NotYourTurn:
                HintRequested?.Invoke("等待对手行动");
                break;
            case UiSubmitOutcome.WrongPhase:
                HintRequested?.Invoke("当前阶段不接受该操作");
                break;
            case UiSubmitOutcome.UnknownCard:
                HintRequested?.Invoke("找不到该卡牌");
                break;
            case UiSubmitOutcome.Rejected:
                HintRequested?.Invoke(Reason(session.LastRejectionReason));
                break;
            case UiSubmitOutcome.Busy:
                HintRequested?.Invoke("上一操作尚未完成");
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
        var session = _session;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var outcome = await session.CompleteMulliganAsync(keepUids, linked.Token);
        if (_stopped || _session != session) return;
        if (outcome != UiSubmitOutcome.Applied) HintRequested?.Invoke("当前没有等待的换牌");
        // Answering produces a segment; the refreshed view (and the play phase) arrives on Pump.
        if (!_stopped) Publish();
    }

    private void Publish()
    {
        if (_session is null) return;
        _session.SettleProjection();
        ProjectionReady?.Invoke(_session.View, _session.Actions);
    }



    private static string Reason(string? reason) => reason switch
    {
        null or "" => "引擎拒绝了该操作",
        "SelectedTargetUnavailable" => "所选目标已失效或不是合法目标",
        "SelectedSlotUnavailable" => "所选位置不可用",
        "SelectionRequired" => "请指定目标或位置",
        "SelectionRejected" => "引擎拒绝了这次选择",
        "NoAvailableCandidates" => "没有符合这张指令条件的目标",
        "PlayerCancelled" or "Cancelled" => "已取消出牌",
        "PrePlayPointShortage" or "CounterPointShortage" => "指挥点不足",
        "PrePlayNoAvailableSlots" => "没有可部署的位置",
        "PlayVerificationRejected" => "引擎未通过打出复验",
        _ => $"引擎拒绝了该操作：{reason}",
    };
}
