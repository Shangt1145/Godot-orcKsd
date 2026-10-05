using Orc.Core;
using Orc.Game;
using Orc.Game.Players;
using Kards.Ui.Contracts;

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
    private readonly int _viewerIndex;
    private readonly string _matchId;
    private Player _viewer = null!;
    private OrcCardReader _cards = null!;
    private OrcMatchReader _reader = null!;
    private OrcUpdateTranslator _translator = null!;
    private IDisposable? _immediate;
    private UiMatchView? _view;

    /// <param name="viewerIndex">0 = first player. Resolved after initialization; players are not readable while preparing.</param>
    public OrcMatchHost(Match match, string matchId, int viewerIndex = 0)
    {
        _match = match;
        _matchId = matchId;
        _viewerIndex = viewerIndex;
    }

    /// <summary>
    /// Readers need the card library, which only exists once the match leaves <c>Preparing</c>.
    /// Building them here also means a later definition-format change is confined to the reader.
    /// </summary>
    private void EnsureReaders()
    {
        if (_reader is not null) return;
        _cards = new OrcCardReader(_match.CardLibrary);
        _reader = new OrcMatchReader(_cards);
        _translator = new OrcUpdateTranslator(_cards);
    }

    /// <summary>Last read board. Null until <see cref="InitializeAsync"/> completes.</summary>
    public UiMatchView View => _view ?? throw new InvalidOperationException("Match host has not been initialized.");

    /// <summary>Non-blocking feedback lane: fire and forget flags only, never animation work.</summary>
    public event Action<string>? ImmediateUpdate;

    /// <summary>One resolved action: steps to animate plus the authoritative board after it.</summary>
    public event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;

    /// <summary>Error entries ride along in segments; surfaced here for a UI toast.</summary>
    public event Action<string>? ErrorRaised;

    /// <summary>Must run before <see cref="InitializeAsync"/>.</summary>
    public void Attach()
    {
        _immediate ??= _match.Engine.OnImmediateUpdate((updateType, _) => ImmediateUpdate?.Invoke(updateType));
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Attach();
        await _match.Initialize(ct);
        _viewer = _match.Players[_viewerIndex];
        EnsureReaders();
        _view = _reader.Read(_match, _viewer, _matchId);
    }

    /// <summary>Frame-loop entry point: drains segments, then re-reads the board.</summary>
    public void Pump()
    {
        if (_view is null || _viewer is null) return;
        EnsureReaders();
        foreach (var segment in _match.Engine.TakeSegments())
        {
            var updates = segment.Entries
                .Where(e => e.Kind == LogEntryKind.Update)
                .Select(e => (Update: e.Message, Payload: e.Data))
                .ToArray();
            foreach (var entry in segment.Entries.Where(e => e.Level == LogLevel.Error))
                ErrorRaised?.Invoke(entry.Message);

            var after = _reader.Read(_match, _viewer, _matchId);
            var steps = _translator.Translate(updates, _viewer, _view ?? after, after);
            var previous = _view;
            _view = after;
            if (steps.Count == 0) continue;
            PresentationReady?.Invoke(
                new UiPresentationResolution(_matchId, steps, after),
                new UiBattleActions());
            _ = previous;
        }
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
        _immediate?.Dispose();
        _immediate = null;
    }
}
