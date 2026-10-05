using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Players;

namespace Kards.Ui;

/// <summary>
/// Godot-side wiring for a real engine match. Everything about rules lives in the engine; this class only
///   - builds the match and hands the UI's targeter bridge to it,
///   - pumps the segment queue from the frame loop,
///   - forwards what it receives to the screen (projection / presentation / errors).
/// It never computes damage, legality or outcomes.
/// </summary>
public sealed class OrcMatchRunner
{
    private const string InfantryId = "orc-demo-infantry";

    private OrcMatchHost? _host;
    private Match? _match;

    public bool IsRunning => _host is not null;
    public string MatchId { get; } = "orc-live";

    public event Action<UiMatchView, UiBattleActions>? ProjectionReady;
    public event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;
    public event Action<string>? ErrorRaised;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var definitions = new[]
        {
            new CardDefinitionEntry(InfantryId, new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard))
        };
        // Each side owns its own CardList instance: deck entries are bound to card instances at load time.
        var deckA = new CardList(Enumerable.Repeat(InfantryId, 20));
        var deckB = new CardList(Enumerable.Repeat(InfantryId, 20));
        var bridge = new OrcTargeterBridge(CollectInteractable);
        _match = new Match(deckA, deckB, definitions, seed: 20261005, firstPlayerIndex: 0, targeterBridge: bridge);
        _host = new OrcMatchHost(_match, MatchId);
        _host.ImmediateUpdate += _ => { };
        _host.PresentationReady += (resolution, actions) => PresentationReady?.Invoke(resolution, actions);
        _host.ErrorRaised += message => ErrorRaised?.Invoke(message);

        await _host.InitializeAsync(ct);
        ProjectionReady?.Invoke(_host.View, new UiBattleActions());

        // Mulligan: the auto policy keeps the opening hand, then both sides confirm to enter play.
        foreach (var player in _match.Players)
            await _match.MulliganDone(player, ct);
        _host.Refresh();
        ProjectionReady?.Invoke(_host.View, new UiBattleActions());
    }

    /// <summary>Frame-loop entry point. Segments are unbounded, so this must run every frame.</summary>
    public void Pump()
    {
        _host?.Pump();
    }

    public UiMatchView? CurrentView => _host is null ? null : _host.View;

    private IReadOnlyList<object?> CollectInteractable()
    {
        if (_match is null || _match.State == MatchState.Preparing) return Array.Empty<object?>();
        var references = new List<object?>();
        foreach (var line in new[] { _match.Battlefield.PlayerASupportLine, _match.Battlefield.FrontLine, _match.Battlefield.PlayerBSupportLine })
            foreach (var slot in line)
                references.Add(slot.Ref);
        foreach (var player in _match.Players) references.Add(player.Hq.Ref);
        return references;
    }

    public void Stop()
    {
        _host?.Detach();
        _host = null;
        _match = null;
    }
}
