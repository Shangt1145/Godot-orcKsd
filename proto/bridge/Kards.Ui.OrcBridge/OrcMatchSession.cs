using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Managers;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui.OrcBridge;

/// <summary>Why a command was refused, in the UI's own words. The engine's reason is never reworded away.</summary>
public enum UiSubmitOutcome { Applied, NotYourTurn, WrongPhase, UnknownCard, Rejected, Interrupted, Busy }

/// <summary>
/// The whole engine conversation behind one door: build a match, submit commands, answer the
/// mulligan. The UI layer talks to this and to <see cref="OrcMatchHost"/>; it never sees
/// <see cref="Match"/>, <see cref="Player"/> or a card object.
///
/// This exists so that "the UI depends on the engine" stays a fact about one file. Upstream changes
/// land here instead of rippling into screens and widgets.
/// </summary>
public sealed class OrcMatchSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private bool _disposed;
    private bool _automaticChoice;
    private readonly OrcMatchHost _host;
    private readonly Func<Match?, IReadOnlyList<object?>> _interactables;
    private Task<MulliganResult>? _mulliganRequest;
    private (TargetingRequestDescription Description, ITargetingResponder Responder)? _pendingMulligan;
    private string[]? _pendingSelection;
    private int? _pendingSlot;

    private OrcMatchSession(OrcMatchHost host, Match match,
        Func<Match?, IReadOnlyList<object?>> interactables)
    {
        _host = host; Match = match;
        _interactables = interactables;
    }

    public OrcMatchHost Host => _host;
    public UiMatchView View => _host.View;
    public UiBattleActions Actions => _host.Actions;
    public bool IsRunning => !_disposed;
    public bool IsViewerOnTurn => !_disposed && Match.Phase == MatchPhase.Play
        && ReferenceEquals(Match.CurrentPlayer, _host.Viewer);
    public bool DriveOpponent { get; set; } = true;
    public bool IsSubmitting => _commands.CurrentCount == 0;
    public string? LastRejectionReason { get; private set; }
    public string MatchId { get; private init; } = "";
    public UiCardPoolReport? CardPoolReport { get; private set; }

    /// <summary>Present only for the session's own use — never handed to the UI layer.</summary>
    internal Match Match { get; private init; } = null!;

    public void Pump() { if (!_disposed) _host.Pump(); }
    public void SettleProjection() { if (!_disposed) _host.SettleProjection(); }
    public void SelectViewer(int index)
    {
        if (_disposed || IsSubmitting || MulliganPending) return;
        _host.SelectViewer(index);
        ClearIntent();
    }

    /// <summary>
    /// Releases the match. The engine holds no unmanaged state, but a test that parks a mulligan
    /// leaves a task awaiting a responder; letting go of the session makes that explicit.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_pendingMulligan is { } pending) pending.Responder.Cancel(pending.Description.RequestId);
        _pendingMulligan = null;
        _mulliganRequest = null;
        ClearIntent();
        _host.Detach();
    }

    public async Task InitializeAsync(CancellationToken ct = default) => await _host.InitializeAsync(ct);

    /// <summary>UI-facing probe factory; its signature contains no engine callback types.</summary>
    public static Task<OrcMatchSession> CreateDefaultProbeAsync(
        string cardId, int copies, int seed, string matchId,
        Func<string, string>? artLookup = null, CancellationToken ct = default) =>
        CreateProbeAsync(cardId, copies, seed, matchId, artLookup: artLookup, ct: ct);

    /// <summary>Catalogue compilation and deck construction stay behind the engine boundary.</summary>
    public static async Task<OrcMatchSession> CreateFromCatalogAsync(
        IReadOnlyList<UiCardDefinition> cards, int seed, string matchId, string fallbackCardId,
        Func<string, string>? artLookup = null, CancellationToken ct = default,
        IReadOnlyList<string>? openingHand = null, string? sourceDirectory = null)
    {
        if (sourceDirectory is null) throw new InvalidDataException("Catalog admission requires its source directory.");
        var (pool, report) = VerifiedCardPool.Compile(cards, sourceDirectory);
        if (pool.Count == 0) throw new InvalidDataException("No reviewed cards are available; probe fallback is not a catalog match.");
        if (openingHand is not null && openingHand.Any(id => !pool.Any(e => e.Id == id)))
            throw new InvalidDataException("The requested opening hand contains unreviewed cards.");
        var deck = DeckBuilder.BuildVerified(pool, seed, VerifiedCardPool.ValidationDeckSize);
        var session = await CreateAsync(pool, deck, deck, seed, matchId,
            artLookup: artLookup, ct: ct, openingHand: openingHand);
        session.CardPoolReport = report;
        return session;
    }

    /// <summary>
    /// Builds a match over a single-card probe deck. The card list is a test fixture, not a game
    /// design choice: a real deck list arrives through <see cref="CreateAsync"/>'s definitions.
    /// </summary>
    public static async Task<OrcMatchSession> CreateProbeAsync(
        string cardId, int copies, int seed, string matchId, int viewerIndex = 0,
        Func<Match?, IReadOnlyList<object?>>? interactables = null,
        Func<string, string>? artLookup = null, CancellationToken ct = default)
    {
        var definitions = new[]
        {
            new CardDefinitionEntry(cardId, new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard))
        };
        var probe = Enumerable.Repeat(cardId, copies).ToArray();
        return await CreateAsync(definitions, probe, probe, seed, matchId, viewerIndex,
            interactables, artLookup, ct);
    }

    /// <summary>
    /// Creates and initialises a match over a real deck. Every id in either deck must have a
    /// definition. Each side gets its own CardList because deck entries bind to card instances at
    /// load time — sharing one would bind both sides to the same card objects.
    /// </summary>
    /// <param name="artLookup">Art path resolver; the engine has no art concept.</param>
    /// <param name="openingHand">
    /// Optional opening-hand override. When supplied, the dealt hand is replaced with these card ids
    /// (in order) right after the match initialises, before the mulligan is presented. Used to pin
    /// the first cards a player sees — for example one unit per deployment-slam size tier. Ids the
    /// pool does not define are skipped, so a stale id degrades to a smaller hand rather than an
    /// unplayable match.
    /// </param>
    public static async Task<OrcMatchSession> CreateAsync(
        IReadOnlyList<CardDefinitionEntry> definitions,
        IReadOnlyList<string> deckA, IReadOnlyList<string> deckB,
        int seed, string matchId, int viewerIndex = 0,
        Func<Match?, IReadOnlyList<object?>>? interactables = null,
        Func<string, string>? artLookup = null, CancellationToken ct = default,
        IReadOnlyList<string>? openingHand = null)
    {
        if (deckA.Count == 0 || deckB.Count == 0)
            throw new ArgumentException("Both decks must have at least one card.", nameof(deckA));
        var known = definitions.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var missing = deckA.Concat(deckB).Where(id => !known.Contains(id)).Distinct().ToArray();
        if (missing.Length > 0)
            throw new ArgumentException($"The deck references undefined card(s): {string.Join(", ", missing)}", nameof(deckA));

        var listA = new Orc.Game.Collections.CardList();
        listA.AddRange(deckA);
        var listB = new Orc.Game.Collections.CardList();
        listB.AddRange(deckB);
        Match? match = null;
        Func<Match?, IReadOnlyList<object?>> collect = interactables ?? DefaultInteractables;
        OrcMatchSession? session = null;
        var bridge = new OrcTargeterBridge(() => collect(match), (d, r) =>
        {
            if (session is null) { OrcTargeterBridge.AutoRespond(d, r); return; }
            // A mulligan is the UI panel's to answer; anything else replays the player's choice.
            if (d.Slots.Count > 0 && d.Slots[0].Kind == TargetSlotKind.MulliganSelect)
            {
                session.NotePendingRequest(d, r);
                session.MulliganRequested?.Invoke();
                return;
            }
            session.AnswerParked(d, r);
        });
        match = new Match(listA, listB, definitions, seed: seed, firstPlayerIndex: viewerIndex, targeterBridge: bridge);
        var host = new OrcMatchHost(match, matchId, viewerIndex, artLookup);
        session = new OrcMatchSession(host, match, collect) { MatchId = matchId };
        try
        {
            await host.InitializeAsync(ct);
            if (openingHand is { Count: > 0 })
                await ApplyOpeningHandAsync(host, definitions, openingHand, ct);
        }
        catch { session.Dispose(); throw; }
        return session;
    }

    /// <summary>
    /// Replaces the viewer's dealt hand with the requested cards. This runs after initialise (the card
    /// library only exists then) and before the mulligan is presented, so the panel shows exactly
    /// these cards. Each card is created and loaded through the engine's own path, so its stats,
    /// modifiers and unit type are real. The replaced cards are dropped rather than returned to the
    /// deck: this is a pin, not a deal.
    /// </summary>
    private static async Task ApplyOpeningHandAsync(
        OrcMatchHost host, IReadOnlyList<CardDefinitionEntry> definitions, IReadOnlyList<string> cardIds, CancellationToken ct)
    {
        var known = definitions.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var viewer = host.Viewer;
        var library = host.Match.CardLibrary;

        while (viewer.Hand.Count > 0) viewer.Hand.RemoveAt(0);
        foreach (var id in cardIds)
        {
            if (!known.Contains(id)) continue;
            var card = library.Instantiate(id);
            await card.LoadAsync(viewer, ct);
            viewer.Hand.Add(card);
        }
        host.Refresh();
    }

    private static IReadOnlyList<object?> DefaultInteractables(Match? match)
    {
        if (match is null || match.State == MatchState.Preparing) return Array.Empty<object?>();
        var references = new List<object?>();
        var lines = new[]
        {
            match.Battlefield.PlayerASupportLine, match.Battlefield.FrontLine, match.Battlefield.PlayerBSupportLine
        };
        foreach (var line in lines)
            foreach (var slot in line) references.Add(slot.Ref);
        // Board units as well as their slots. The engine's coarse filter intersects this list with its
        // own candidates, and attack candidates are *unit* references — hand over slots only and the
        // enemy HQ is the sole survivor, so every unit the player aims at is silently replaced by it.
        foreach (var line in lines)
            foreach (var slot in line)
                if (slot.Occupant is UnitCard unit) references.Add(unit.Ref);
        foreach (var player in match.Players) references.Add(player.Hq.Ref);
        return references;
    }

    // ── Mulligan ────────────────────────────────────────────────────────────

    /// <summary>Answers every non-viewer side, then either parks the viewer's mulligan or keeps it.</summary>
    public async Task SettleMulliganAsync(bool interactive, CancellationToken ct = default)
    {
        if (_disposed) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var viewer = _host.Viewer;
        foreach (var player in Match.Players.Where(p => !ReferenceEquals(p, viewer)))
        {
            await Match.MulliganDone(player, linked.Token);
            _host.CaptureCompletedActions();
        }
        if (interactive)
        {
            // Not wrapped in Task.Run: the returned task parks on the mulligan slot by itself, and
            // hopping threads would make the UI's panel touch the scene tree off the main thread.
            // Do not await it here — it only completes once the UI answers.
            _mulliganRequest = RunMulliganAsync(viewer, ct);
            _ = _mulliganRequest.ContinueWith(t => { _ = t.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        else
        {
            await Match.MulliganDone(viewer, linked.Token);
            _host.CaptureCompletedActions();
        }
    }

    private async Task<MulliganResult> RunMulliganAsync(Player viewer, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try { return await Match.BeginMulliganAsync(viewer, linked.Token); }
        finally { if (!_disposed) _host.CaptureCompletedActions(); }
    }

    /// <summary>
    /// Answers a parked non-mulligan request on the player's behalf. The UI has already chosen (it
    /// clicked a target or dropped on a slot), so the choice is replayed here rather than leaving the
    /// engine to ask again. An unmatched player choice cancels the request without substitution.
    /// </summary>
    public void AnswerParked(TargetingRequestDescription description, ITargetingResponder responder)
    {
        if (_disposed) { responder.Cancel(description.RequestId); return; }
        if (description.Slots.Count == 0) { ClearIntent(); Refuse(responder, description); return; }
        var slot = description.Slots[0];
        var allowed = (slot.AllowedReferences ?? description.AllowedTargets).Where(r => r.IsAlive).ToArray();

        // 1) A target the player clicked: answer with exactly that reference.
        if (_pendingSelection is { Length: > 0 })
        {
            var chosen = allowed.Where(r => _pendingSelection.Contains(OrcRefs.KeyOf(r.Value))).ToArray();
            if (chosen.Length > 0) { Complete(description, responder, slot.Name, chosen); return; }
            LastRejectionReason = "SelectedTargetUnavailable";
            ClearIntent(); Refuse(responder, description); return;
        }

        // 2) A slot the player dropped on. The UI sends an *engine slot index*, while the candidate
        //    list is what the engine is willing to accept this action; answering with a different slot
        //    than the one dropped on is worse than refusing, because the unit would appear somewhere the
        //    player did not point at. When the engine does not offer the slot, the request is cancelled so
        //    the failure and its reason reach the player.
        var wanted = SlotAt(slot.Name);
        if (_pendingSupportIndex is not null || _pendingSlot is not null)
        {
            if (wanted is not null && allowed.Any(r => ReferenceEquals(r.Value, wanted.Value)))
            {
                Complete(description, responder, slot.Name, [wanted]);
                return;
            }
            LastRejectionReason = "SelectedSlotUnavailable";
            ClearIntent();
            Refuse(responder, description);
            return;
        }

        ClearIntent();
        if (_automaticChoice) OrcTargeterBridge.AutoRespond(description, responder);
        else { LastRejectionReason = "SelectionRequired"; Refuse(responder, description); }
    }

    /// <summary>
    /// Ends a request the player cannot answer without selecting a different target.
    /// </summary>
    private static void Refuse(ITargetingResponder responder, TargetingRequestDescription description)
    {
        responder.Cancel(description.RequestId);
    }

    /// <summary>
    /// Submits an answer and makes sure the request actually ends.
    ///
    /// The engine is parked inside <c>BeginInteraction</c> — a void call it makes on its own thread of
    /// control, awaiting a <c>Task</c> this callback is expected to complete. <c>Complete</c> returning
    /// <c>false</c> means the submission was rejected and **the request is still waiting**: the engine
    /// has already returned from the callback and will not ask again, so an unanswered request parks
    /// forever and every later command on that match hangs with it.
    ///
    /// A refused answer is cancelled using the same request id. The session lifetime also cancels
    /// engine work on exit; a player choice must never fall back to an automatic selection.
    /// </summary>
    private void Complete(TargetingRequestDescription description, ITargetingResponder responder,
        string slotName, IReadOnlyList<Ref<Entity>> selection)
    {
        ClearIntent();
        if (responder.Complete(description.RequestId, Wrap(slotName, selection))) return;
        LastRejectionReason = "SelectionRejected";
        responder.Cancel(description.RequestId);
    }

    /// <summary>
    /// The engine slot a drop maps to. Deployment asks on the anonymous slot ("default") with
    /// support-line candidates; movement also uses an anonymous slot with front-line candidates.
    /// </summary>
    private Ref<Entity>? SlotAt(string slotName)
    {
        // Which line a slot belongs to is decided by what the caller recorded, not by the slot's name.
        // A deployment and a unit drag both use the engine's default slot — `PlayManager` and
        // `CommandManager` each create `new SingleSelectSlot()` with no name — so the name cannot tell
        // them apart, while the intent can: a deployment recorded a support index, a drag recorded a
        // front-line one. A drag that pointed at an enemy recorded neither and answers by reference.
        if (_pendingSupportIndex is { } support)
        {
            var supportLine = Match.Battlefield.GetSupportLine(_host.Viewer.Index);
            return support >= 0 && support < supportLine.Count ? supportLine[support].Ref : null;
        }
        if (_pendingSlot is not { } front) return null;
        var line = Match.Battlefield.FrontLine;
        return front >= 0 && front < line.Count ? line[front].Ref : null;
    }

    /// <summary>
    /// Consumes the player's recorded intent. Left standing, a stale slot or selection would answer
    /// the *next* request — a deployment would replay the last move's position.
    /// </summary>
    private void ClearIntent()
    {
        _pendingSelection = null;
        _pendingSlot = null;
        _pendingSupportIndex = null;
    }

    private static Dictionary<string, IReadOnlyList<Ref<Entity>>> Wrap(string slot, IReadOnlyList<Ref<Entity>> selections) =>
        new(StringComparer.Ordinal) { [slot] = selections };

    /// <summary>Records the player's chosen target, replayed when the engine asks where to strike.</summary>
    public void NoteSelection(params string[] targetUids) => _pendingSelection = targetUids;

    /// <summary>Records the slot the player dropped on, replayed when the engine asks for a position.</summary>
    public void NoteSlot(int? slot) => _pendingSlot = slot;

    /// <summary>Records the support-line slot a play asked for.</summary>
    public void NoteSupportIndex(int? index) => _pendingSupportIndex = index;

    /// <summary>True while the engine is waiting for this session's mulligan answer.</summary>
    public bool MulliganPending => _mulliganRequest is { IsCompleted: false };

    /// <summary>Raised when the engine parks on the mulligan slot; the UI opens its panel.</summary>
    public event Action? MulliganRequested;

    /// <summary>Records a parked mulligan request so <see cref="CompleteMulliganAsync"/> can answer it.</summary>
    public void NotePendingRequest(TargetingRequestDescription description, ITargetingResponder responder)
    {
        if (_disposed) { responder.Cancel(description.RequestId); return; }
        if (description.Slots.Count > 0 && description.Slots[0].Kind == TargetSlotKind.MulliganSelect)
            _pendingMulligan = (description, responder);
    }

    /// <summary>
    /// Completes a parked mulligan. The answer goes back through the targeter that asked for it —
    /// the same responder path the engine parked on — so there is no parallel "mulligan API" to
    /// keep in sync. <see cref="OrcTargeterBridge.SelectReplace"/> owns the keep/replace policy.
    /// </summary>
    public async Task<UiSubmitOutcome> CompleteMulliganAsync(IReadOnlyList<string> keepUids, CancellationToken ct = default)
    {
        if (_disposed) return UiSubmitOutcome.Interrupted;
        if (_pendingMulligan is null) return UiSubmitOutcome.Rejected;
        var pending = _pendingMulligan.Value;
        var slot = pending.Description.Slots[0];
        var allowed = (slot.AllowedReferences ?? pending.Description.AllowedTargets).Where(r => r.IsAlive).ToArray();
        if (!pending.Responder.Complete(pending.Description.RequestId,
            new Dictionary<string, IReadOnlyList<Ref<Entity>>>(StringComparer.Ordinal)
            {
                [slot.Name] = OrcTargeterBridge.SelectReplace(allowed, keepUids),
            })) return UiSubmitOutcome.Rejected;
        _pendingMulligan = null;
        if (_mulliganRequest is not null)
        {
            // The engine reports why a mulligan failed; surfacing it turns a silent stall into a
            // readable reason instead of a phase that never advances.
            try { MulliganOutcome = await _mulliganRequest.WaitAsync(ct); }
            catch (OperationCanceledException) { return UiSubmitOutcome.Interrupted; }
            catch (Exception e) { MulliganError = e.Message; throw; }
        }
        _mulliganRequest = null;
        return UiSubmitOutcome.Applied;
    }

    /// <summary>The engine's own answer to the last mulligan, for diagnostics and tests.</summary>
    public MulliganResult? MulliganOutcome { get; private set; }

    /// <summary>Set when completing the mulligan threw; null in the normal path.</summary>
    public string? MulliganError { get; private set; }

    // ── Commands ────────────────────────────────────────────────────────────

    /// <summary>
    /// Submits one contract command. Nothing is validated here beyond phase and turn ownership: a
    /// refused play reports the engine's own status rather than a UI guess.
    /// </summary>
    public async Task<UiSubmitOutcome> SubmitAsync(UiCommand command, CancellationToken ct = default)
    {
        if (_disposed) return UiSubmitOutcome.Interrupted;
        if (command is ChooseMulligan keep) return await CompleteMulliganAsync(keep.KeepUids, ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try { if (!await _commands.WaitAsync(0, linked.Token)) return UiSubmitOutcome.Busy; }
        catch (OperationCanceledException) { return UiSubmitOutcome.Interrupted; }
        try
        {
            LastRejectionReason = null;
            ct = linked.Token;
            if (Match.Phase != MatchPhase.Play) return UiSubmitOutcome.WrongPhase;
            if (!IsViewerOnTurn) return UiSubmitOutcome.NotYourTurn;
            switch (command)
            {
                case PlayCard play:
                    return await PlayAsync(play.Uid, play.SupportIndex, ct);
                case MoveUnit move:
                {
                    if (Resolve(move.Uid) is not UnitCard mover) return UiSubmitOutcome.UnknownCard;
                    // The player chose a slot; replay it when the engine asks where to go.
                    _pendingSlot = move.SlotIndex;
                    return Report(await Match.CommandManager.BeginMoveAsync(mover, ct));
                }
                case AttackUnit attack:
                {
                    if (Resolve(attack.AttackerUid) is not UnitCard attacker) return UiSubmitOutcome.UnknownCard;
                    _pendingSelection = [attack.DefenderUid];
                    return Report(await Match.CommandManager.BeginAttackAsync(attacker, ct));
                }
                case CommandUnit gesture:
                {
                    if (Resolve(gesture.Uid) is not UnitCard actor) return UiSubmitOutcome.UnknownCard;
                    _pendingSelection = gesture.TargetUid is { } target ? [target] : null;
                    _pendingSlot = gesture.SlotIndex;
                    // The gesture, not the decision. This is the engine's single drag entry point: it merges
                    // the move and attack candidates and dispatches on what was pointed at — enemy unit or
                    // headquarters for an attack, empty slot for a move (CommandManager.BeginCommandAsync →
                    // DispatchSelectedAsync). The recorded intent answers its request; pointing at something
                    // illegal now fails the action with the engine's own reason, where before the UI decided
                    // the gesture was a move, found the slot occupied and cancelled in silence.
                    return Report(await Match.CommandManager.BeginCommandAsync(actor, ct));
                }
                case EndTurn:
                    // EndTurn reports nothing; the segments it produces are read on the next Pump.
                    await Match.EndTurn(ct);
                    _host.CaptureCompletedActions();
                    // The engine has no AI: passing the turn hands play to the opponent, which the
                    // driver plays through the same engine entry points and passes straight back.
                    if (DriveOpponent && Match.State == MatchState.InProgress && !ReferenceEquals(Match.CurrentPlayer, _host.Viewer))
                    {
                        _automaticChoice = true;
                        try { await OrcOpponentDriver.PlayTurnAsync(Match, Match.CurrentPlayer, ct: ct,
                            actionCompleted: _host.CaptureCompletedActions); }
                        finally { _automaticChoice = false; }
                    }
                    return UiSubmitOutcome.Applied;
                case ChooseTarget:
                    // Targeting is answered through the present callback, not by a separate command.
                    return UiSubmitOutcome.Rejected;
                default:
                    return UiSubmitOutcome.Rejected;
            }
        }
        catch (OperationCanceledException) { return UiSubmitOutcome.Interrupted; }
        finally
        {
            ClearIntent();
            if (!_disposed) _host.CaptureCompletedActions();
            _commands.Release();
        }
    }

    private Card? Resolve(string uid) =>
        _host.Index.TryGetValue(uid, out var card) && OrcRefs.IsAlive(card) ? card : null;

    private async Task<UiSubmitOutcome> PlayAsync(string uid, int? supportIndex, CancellationToken ct)
    {
        if (Resolve(uid) is not UnitCard unit) return UiSubmitOutcome.UnknownCard;
        // The position is the player's, expressed through the engine's own interaction: its candidate set
        // for a support deployment is every empty slot of the line (Orc.Game Managers/PlayManager.cs, the
        // ① 候选 step), and AnswerParked replays the chosen slot as the answer. A slot outside that set —
        // occupied or out of range — is refused by the coarse filter, so the engine still decides legality.
        // Command points are the engine's call too: they are enforced by the play chain's verification.
        _pendingSupportIndex = supportIndex;
        // Contract-only callers may omit a position. This explicit adapter policy is limited to
        // untargeted deployment; a named but illegal position is always refused.
        var automatic = _automaticChoice;
        _automaticChoice = supportIndex is null;
        try
        {
            return Report(await Match.PlayManager.BeginUnitPrePlayAsync(unit, ct));
        }
        finally { _automaticChoice = automatic; }
    }

    private int? _pendingSupportIndex;

    private UiSubmitOutcome Report(CommandResult result)
    {
        if (result.IsSuccess) return UiSubmitOutcome.Applied;
        LastRejectionReason ??= result.FailureReason?.ToString() ?? result.Status.ToString();
        return UiSubmitOutcome.Rejected;
    }

    private UiSubmitOutcome Report(PlayResult result)
    {
        if (result.IsSuccess) return UiSubmitOutcome.Applied;
        LastRejectionReason ??= result.FailureReason?.ToString() ?? result.Status.ToString();
        return UiSubmitOutcome.Rejected;
    }
}
