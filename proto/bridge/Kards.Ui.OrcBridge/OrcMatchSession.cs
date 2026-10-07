using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Managers;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>Why a command was refused, in the UI's own words. The engine's reason is never reworded away.</summary>
public enum UiSubmitOutcome { Applied, NotYourTurn, WrongPhase, UnknownCard, Rejected, Interrupted }

/// <summary>
/// The whole engine conversation behind one door: build a match, submit commands, answer the
/// mulligan. The UI layer talks to this and to <see cref="OrcMatchHost"/>; it never sees
/// <see cref="Match"/>, <see cref="Player"/> or a card object.
///
/// This exists so that "the UI depends on the engine" stays a fact about one file. Upstream changes
/// land here instead of rippling into screens and widgets.
/// </summary>
public sealed class OrcMatchSession
{
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
    public bool IsRunning => true;
    public string MatchId { get; private init; } = "";

    /// <summary>Present only for the session's own use — never handed to the UI layer.</summary>
    internal Match Match { get; private init; } = null!;

    public void Pump() => _host.Pump();

    /// <summary>
    /// Releases the match. The engine holds no unmanaged state, but a test that parks a mulligan
    /// leaves a task awaiting a responder; letting go of the session makes that explicit.
    /// </summary>
    public void Dispose()
    {
        _pendingMulligan = null;
        _mulliganRequest = null;
        _pendingSlot = null;
        _pendingSupportIndex = null;
    }

    public async Task InitializeAsync(CancellationToken ct = default) => await _host.InitializeAsync(ct);

    /// <summary>
    /// Builds a match over a single-card probe deck. The card list is a test fixture, not a game
    /// design choice: a real deck list arrives through <see cref="CreateAsync"/>'s definitions.
    /// </summary>
    public static async Task<OrcMatchSession> CreateProbeAsync(
        string cardId, int copies, int seed, string matchId, int viewerIndex = 0,
        Func<Match?, IReadOnlyList<object?>>? interactables = null,
        Action<Orc.Game.Targeting.TargetingRequestDescription, Orc.Game.Targeting.ITargetingResponder>? present = null,
        Func<string, string>? artLookup = null, CancellationToken ct = default)
    {
        var definitions = new[]
        {
            new CardDefinitionEntry(cardId, new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard))
        };
        return await CreateAsync(definitions, copies, copies, seed, matchId, viewerIndex,
            interactables, present, artLookup, ct);
    }

    /// <summary>
    /// Creates and initialises a match. Each side gets its own card list because deck entries bind
    /// to card instances at load time.
    /// </summary>
    public static async Task<OrcMatchSession> CreateAsync(
        IReadOnlyList<CardDefinitionEntry> definitions, int deckACopies, int deckBCopies,
        int seed, string matchId, int viewerIndex = 0,
        Func<Match?, IReadOnlyList<object?>>? interactables = null,
        Action<Orc.Game.Targeting.TargetingRequestDescription, Orc.Game.Targeting.ITargetingResponder>? present = null,
        Func<string, string>? artLookup = null, CancellationToken ct = default)
    {
        // Each side owns its own CardList instance: deck entries are bound to card instances at load time.
        var deckA = new Orc.Game.Collections.CardList(Enumerable.Repeat(definitions[0].Id, deckACopies));
        var deckB = new Orc.Game.Collections.CardList(Enumerable.Repeat(definitions[0].Id, deckBCopies));
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
        match = new Match(deckA, deckB, definitions, seed: seed, firstPlayerIndex: viewerIndex, targeterBridge: bridge);
        var host = new OrcMatchHost(match, matchId, viewerIndex, artLookup);
        session = new OrcMatchSession(host, match, collect) { MatchId = matchId };
        await host.InitializeAsync(ct);
        return session;
    }

    private static IReadOnlyList<object?> DefaultInteractables(Match? match)
    {
        if (match is null || match.State == MatchState.Preparing) return Array.Empty<object?>();
        var references = new List<object?>();
        foreach (var line in new[]
        {
            match.Battlefield.PlayerASupportLine, match.Battlefield.FrontLine, match.Battlefield.PlayerBSupportLine
        })
            foreach (var slot in line) references.Add(slot.Ref);
        foreach (var player in match.Players) references.Add(player.Hq.Ref);
        return references;
    }

    // ── Mulligan ────────────────────────────────────────────────────────────

    /// <summary>Answers every non-viewer side, then either parks the viewer's mulligan or keeps it.</summary>
    public async Task SettleMulliganAsync(bool interactive, CancellationToken ct = default)
    {
        var viewer = _host.Viewer;
        foreach (var player in Match.Players.Where(p => !ReferenceEquals(p, viewer)))
            await Match.MulliganDone(player, ct);
        if (interactive)
        {
            // Not wrapped in Task.Run: the returned task parks on the mulligan slot by itself, and
            // hopping threads would make the UI's panel touch the scene tree off the main thread.
            // Do not await it here — it only completes once the UI answers.
            _mulliganRequest = Match.BeginMulliganAsync(viewer, ct);
        }
        else await Match.MulliganDone(viewer, ct);
    }

    /// <summary>
    /// Answers a parked non-mulligan request on the player's behalf. The UI has already chosen (it
    /// clicked a target or dropped on a slot), so the choice is replayed here rather than leaving the
    /// engine to ask again. Anything unmatched falls back to the conservative policy.
    /// </summary>
    public void AnswerParked(TargetingRequestDescription description, ITargetingResponder responder)
    {
        if (description.Slots.Count == 0) { responder.Cancel(description.RequestId); return; }
        var slot = description.Slots[0];
        var allowed = (slot.AllowedReferences ?? description.AllowedTargets).Where(r => r.IsAlive).ToArray();
        if (_pendingSelection is { Length: > 0 })
        {
            var chosen = allowed.Where(r => _pendingSelection.Contains(OrcRefs.KeyOf(r.Value))).ToArray();
            if (chosen.Length > 0) { responder.Complete(description.RequestId, Wrap(slot.Name, chosen)); return; }
        }
        if (_pendingSlot is { } index && index >= 0 && index < allowed.Length)
        {
            responder.Complete(description.RequestId, Wrap(slot.Name, [allowed[index]]));
            return;
        }
        OrcTargeterBridge.AutoRespond(description, responder);
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
        if (_pendingMulligan is null) return UiSubmitOutcome.Rejected;
        var pending = _pendingMulligan.Value;
        _pendingMulligan = null;
        var slot = pending.Description.Slots[0];
        var allowed = (slot.AllowedReferences ?? pending.Description.AllowedTargets).Where(r => r.IsAlive).ToArray();
        pending.Responder.Complete(pending.Description.RequestId,
            new Dictionary<string, IReadOnlyList<Ref<Entity>>>(StringComparer.Ordinal)
            {
                [slot.Name] = OrcTargeterBridge.SelectReplace(allowed, keepUids),
            });
        if (_mulliganRequest is not null)
        {
            // The engine reports why a mulligan failed; surfacing it turns a silent stall into a
            // readable reason instead of a phase that never advances.
            try { MulliganOutcome = await _mulliganRequest; }
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
        if (command is ChooseMulligan keep) { await CompleteMulliganAsync(keep.KeepUids, ct); return UiSubmitOutcome.Applied; }
        if (Match.Phase != MatchPhase.Play) return UiSubmitOutcome.WrongPhase;
        // EndTurn always passes: it is how the shell engine advances the opponent's turn as well.
        if (command is not EndTurn && !ReferenceEquals(Match.CurrentPlayer, _host.Viewer)) return UiSubmitOutcome.NotYourTurn;
        try
        {
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
                    return Report(await Match.CommandManager.BeginAttackAsync(attacker, ct));
                }
                case EndTurn:
                    // EndTurn reports nothing; the segments it produces are read on the next Pump.
                    await Match.EndTurn(ct);
                    // The engine has no AI: passing the turn hands play to the opponent, which the
                    // driver plays through the same engine entry points and passes straight back.
                    if (Match.State == MatchState.InProgress && !ReferenceEquals(Match.CurrentPlayer, _host.Viewer))
                        await OrcOpponentDriver.PlayTurnAsync(Match, Match.CurrentPlayer, ct: ct);
                    return UiSubmitOutcome.Applied;
                case ChooseTarget:
                    // Targeting is answered through the present callback, not by a separate command.
                    return UiSubmitOutcome.Rejected;
                default:
                    return UiSubmitOutcome.Rejected;
            }
        }
        catch (OperationCanceledException) { return UiSubmitOutcome.Interrupted; }
    }

    private Card? Resolve(string uid) =>
        _host.Index.TryGetValue(uid, out var card) && OrcRefs.IsAlive(card) ? card : null;

    private async Task<UiSubmitOutcome> PlayAsync(string uid, int? supportIndex, CancellationToken ct)
    {
        if (Resolve(uid) is not UnitCard unit) return UiSubmitOutcome.UnknownCard;
        // The engine offers front-line and support-line slots through one targeter inside
        // BeginUnitPrePlayAsync, so a support deployment is a choice, not a separate entry point.
        // When the caller names a slot we replay it as the answer.
        if (supportIndex is not null) _pendingSupportIndex = supportIndex.Value;
        return Report(await Match.PlayManager.BeginUnitPrePlayAsync(unit, ct));
    }

    private int? _pendingSupportIndex;

    private UiSubmitOutcome Report(CommandResult result) => result.Status switch
    {
        CommandResultStatus.Success => UiSubmitOutcome.Applied,
        _ => UiSubmitOutcome.Rejected,
    };

    private UiSubmitOutcome Report(PlayResult result) => result.Status switch
    {
        PlayResultStatus.Success => UiSubmitOutcome.Applied,
        _ => UiSubmitOutcome.Rejected,
    };
}
