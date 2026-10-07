using Orc.Core;
using Orc.Game;
using Orc.Game.Players;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// Engine-facing host. Deliberately free of any UI framework so it can be driven from a Godot node's
/// frame loop and tested in isolation. Three rules come from the engine's own contract:
///   1. the immediate-update handler must be registered before <c>Initialize</c>, or the opening
///      segment (card.load / deck.shuffled / turn chain) is missed;
///   2. the segment queue is unbounded and the engine never waits, so the frame loop must keep pumping;
///   3. every segment is followed by a fresh full read, so the UI's state can never drift from the engine.
/// </summary>
public sealed class OrcMatchHost
{
    private readonly Match _match;
    private int _viewerIndex;
    private readonly string _matchId;
    private Player _viewer = null!;
    private OrcCardReader _cards = null!;
    private OrcMatchReader _reader = null!;
    private OrcUpdateTranslator _translator = null!;
    private readonly OrcActionReader _actions = new();
    private IDisposable? _immediate;
    private UiMatchView? _view;
    private bool _detached;
    private bool _observer;
    private readonly Queue<(UiPresentationResolution Resolution, UiBattleActions Actions,
        IReadOnlyList<UiOrderImpact> Impacts, string[] Errors)> _captured = new();
    public bool HistoricalStatesVerified { get; private set; } = true;

    /// <param name="viewerIndex">0 = first player. Resolved after initialization; players are not readable while preparing.</param>
    public OrcMatchHost(Match match, string matchId, int viewerIndex = 0, Func<string, string>? artLookup = null)
    {
        _match = match;
        _matchId = matchId;
        _viewerIndex = viewerIndex;
        _artLookup = artLookup;
    }

    private readonly Func<string, string>? _artLookup;

    /// <summary>
    /// Readers need the card library, which only exists once the match leaves <c>Preparing</c>.
    /// Building them here also means a later definition-format change is confined to the reader.
    /// </summary>
    private void EnsureReaders()
    {
        if (_reader is not null) return;
        _cards = new OrcCardReader(_match.CardLibrary, _artLookup);
        _reader = new OrcMatchReader(_cards);
        _translator = new OrcUpdateTranslator(_cards);
    }

    /// <summary>Last read board. Null until <see cref="InitializeAsync"/> completes.</summary>
    public UiMatchView View => _view ?? throw new InvalidOperationException("Match host has not been initialized.");

    /// <summary>The player this host renders for.</summary>
    public Player Viewer => _viewer;

    /// <summary>The running match; for adapter-side drivers (tests, demo harnesses), not for rules.</summary>
    public Match Match => _match;

    /// <summary>uid -> engine card for the last read; commands arrive as uids.</summary>
    public IReadOnlyDictionary<string, Orc.Cards.Card> Index => _reader?.LastIndex ?? new Dictionary<string, Orc.Cards.Card>();

    /// <summary>Availability for the current board, read from the engine's query surfaces.</summary>
    public UiBattleActions Actions => _actions.Read(_match, _viewer, Index);

    /// <summary>Non-blocking feedback lane: fire and forget flags only, never animation work.</summary>
    public event Action<string>? ImmediateUpdate;

    /// <summary>One resolved action: steps to animate plus the authoritative board after it.</summary>
    public event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;

    /// <summary>Diagnostic combat projection. UI playback uses the complete PresentationReady sequence.</summary>
    public event Action<IReadOnlyList<UiOrderImpact>, UiMatchView, UiBattleActions>? CombatReady;

    /// <summary>Error entries ride along in segments; surfaced here for a UI toast.</summary>
    public event Action<string>? ErrorRaised;

    /// <summary>Must run before <see cref="InitializeAsync"/>.</summary>
    public void Attach()
    {
        _detached = false;
        _immediate ??= _match.Engine.OnImmediateUpdate((updateType, _) => ImmediateUpdate?.Invoke(updateType));
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Attach();
        await _match.Initialize(ct);
        _viewer = _match.Players[_viewerIndex];
        EnsureReaders();
        _view = _reader.Read(_match, _viewer, _matchId);
        CaptureCompletedActions();
    }

    /// <summary>
    /// Adopts a match that is already running, resolving this host's viewpoint from it. This is the
    /// hotseat path: a second seat over the same live game. <see cref="InitializeAsync"/> cannot be
    /// used because it would initialise the match a second time; a seat only needs its own viewer,
    /// readers and view.
    /// </summary>
    public void AttachToLiveMatch()
    {
        if (_match.State == MatchState.Preparing)
            throw new InvalidOperationException("The match is still preparing; initialise it first.");
        _viewer = _match.Players[_viewerIndex];
        _observer = true;
        EnsureReaders();
        _view = _reader.Read(_match, _viewer, _matchId);
    }

    public void SelectViewer(int index)
    {
        if (_detached) return;
        _viewerIndex = index;
        _viewer = _match.Players[index];
        _captured.Clear(); // a viewpoint switch settles the board, not the previous seat's animation
        Refresh();
    }

    /// <summary>A full projection settles past presentations, including a silent phase change.</summary>
    public void SettleProjection()
    {
        if (_detached) return;
        CaptureCompletedActions();
        foreach (var item in _captured)
            foreach (var error in item.Errors) ErrorRaised?.Invoke(error);
        _captured.Clear();
        Refresh();
    }

    /// <summary>Frame-loop entry point: drains segments, then re-reads the board.</summary>
    public void Pump()
    {
        if (_detached) return;
        if (_observer) { Refresh(); return; }
        CaptureCompletedActions();
        while (!_detached && _captured.TryDequeue(out var item))
        {
            foreach (var error in item.Errors) ErrorRaised?.Invoke(error);
            // Kept for adapter diagnostics. The UI consumes PresentationReady only, including hits.
            if (item.Impacts.Count > 0) CombatReady?.Invoke(item.Impacts, item.Resolution.After, item.Actions);
            PresentationReady?.Invoke(item.Resolution, item.Actions);
        }
    }

    /// <summary>
    /// Freeze at the end of each adapter-driven engine action, before another action can mutate it.
    /// There is no engine segment-completed callback in this version. Multiple segments collected
    /// after external calls cannot recover their individual historical boards; expose that limitation.
    /// This is the sole engine-queue consumer. Delivery to the scene happens later in Pump.
    /// </summary>
    public void CaptureCompletedActions()
    {
        if (_detached || _observer || _view is null || _viewer is null) return;
        EnsureReaders();
        var segments = _match.Engine.TakeSegments();
        if (segments.Count > 1) HistoricalStatesVerified = false;
        foreach (var segment in segments)
        {
            var updates = segment.Entries
                .Where(e => e.Kind == LogEntryKind.Update)
                .Select(e => (Update: e.Message, Payload: e.Data))
                .ToArray();
            var errors = segment.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToArray();
            var after = UiSnapshots.Freeze(_reader.Read(_match, _viewer, _matchId));
            var translation = _translator.Translate(updates, _viewer, _view ?? after, after);
            _view = after;
            var resolution = UiSnapshots.Freeze(new UiPresentationResolution(_matchId, translation.Steps, after)
                { Sequence = segment.Sequence });
            _captured.Enqueue((resolution, UiSnapshots.Freeze(Actions), translation.Impacts, errors));
        }
        // Phase changes and mulligan replacements can be silent in the current engine.
        // Keep the live projection fresh without changing already frozen action snapshots.
        if (segments.Count == 0) Refresh();
    }

    /// <summary>
    /// Re-reads the board without animation. Mulligan's replacement draws are silent (no card.drawn /
    /// card.hand.add), so the opening hand must be re-read explicitly after a swap.
    /// </summary>
    public UiMatchView Refresh()
    {
        EnsureReaders();
        return _view = _reader.Read(_match, _viewer, _matchId);
    }

    public void Detach()
    {
        _detached = true;
        _captured.Clear();
        _immediate?.Dispose();
        _immediate = null;
    }
}
