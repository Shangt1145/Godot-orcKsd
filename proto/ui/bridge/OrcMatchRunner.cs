using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Orc.Game.Targeting;

namespace Kards.Ui;

/// <summary>
/// Godot-side wiring for a real engine match. Rules stay in the engine; this class only
///   - builds the match and hands the UI's targeter bridge to it,
///   - pumps the segment queue from the frame loop,
///   - translates UI commands into engine entry points and reports the engine's own refusal reasons.
/// It never decides legality, damage or outcomes.
/// </summary>
public sealed class OrcMatchRunner
{
    private const string InfantryId = "orc-demo-infantry";

    private OrcMatchHost? _host;
    private Match? _match;
    private string[]? _pendingSelection;
    private int? _pendingSlot;

    public bool IsRunning => _host is not null;
    public string MatchId { get; } = "orc-live";

    public event Action<UiMatchView, UiBattleActions>? ProjectionReady;
    public event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;
    public event Action<string>? ErrorRaised;

    /// <summary>Engine refusal / status text for the player. The wording is the engine's, not ours.</summary>
    public event Action<string>? HintRequested;

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
        var bridge = new OrcTargeterBridge(CollectInteractable, Present);
        _match = new Match(deckA, deckB, definitions, seed: 20261005, firstPlayerIndex: 0, targeterBridge: bridge);
        _host = new OrcMatchHost(_match, MatchId);
        _host.ImmediateUpdate += _ => { };
        _host.PresentationReady += (resolution, actions) => PresentationReady?.Invoke(resolution, actions);
        _host.ErrorRaised += message => ErrorRaised?.Invoke(message);

        await _host.InitializeAsync(ct);
        Publish();

        // Mulligan: the auto policy keeps the opening hand, then both sides confirm to enter play.
        foreach (var player in _match.Players)
            await _match.MulliganDone(player, ct);
        Publish();
    }

    /// <summary>Frame-loop entry point. Segments are unbounded, so this must run every frame.</summary>
    public void Pump() => _host?.Pump();

    public UiMatchView? CurrentView => _host is null ? null : _host.View;

    public UiBattleActions CurrentActions => _host?.Actions ?? new UiBattleActions();

    /// <summary>
    /// Commands are submitted to the engine and the result is consumed as-is. Nothing is validated here:
    /// a refused play shows the engine's own reason.
    /// </summary>
    public async Task SubmitAsync(UiCommand command, CancellationToken ct = default)
    {
        if (_match is null || _host is null) return;
        if (_match.Phase != MatchPhase.Play)
        {
            HintRequested?.Invoke("当前阶段不接受该操作");
            return;
        }
        if (!ReferenceEquals(_match.CurrentPlayer, _host.Viewer))
        {
            HintRequested?.Invoke("等待对手行动");
            return;
        }
        switch (command)
        {
            case PlayCard play:
                await PlayAsync(play.Uid, ct);
                break;
            case MoveUnit move when Resolve(move.Uid) is UnitCard unit:
                // The player chose a front-line slot; replay it when the engine asks where to go.
                _pendingSlot = move.SlotIndex;
                Report(await _match.CommandManager.BeginMoveAsync(unit, ct));
                _pendingSlot = null;
                break;
            case AttackUnit attack when Resolve(attack.AttackerUid) is UnitCard attacker:
                // The player already picked the target; the engine asks again, so the choice is replayed.
                _pendingSelection = [attack.DefenderUid];
                Report(await _match.CommandManager.BeginAttackAsync(attacker, ct));
                _pendingSelection = null;
                break;
            case EndTurn:
                await _match.EndTurn(ct);
                break;
            default:
                HintRequested?.Invoke("该操作尚未接入引擎");
                return;
        }
        // The board is not refreshed here: Pump picks up the segment and the presentation plays first.
    }

    private async Task PlayAsync(string uid, CancellationToken ct)
    {
        if (_match is null) return;
        switch (Resolve(uid))
        {
            case UnitCard unit:
                Report(await _match.PlayManager.BeginUnitPrePlayAsync(unit, ct));
                break;
            case CommandCard order:
                Report(await _match.PlayManager.BeginCommandPrePlayAsync(order, ct));
                break;
            default:
                HintRequested?.Invoke("这张牌当前不可打出");
                break;
        }
    }

    private void Report(PlayResult result)
    {
        if (result.Status == PlayResultStatus.Success) return;
        HintRequested?.Invoke(result.Status == PlayResultStatus.Cancelled ? "已取消" : Reason(result.FailureReason?.ToString()));
    }

    private void Report(CommandResult result)
    {
        if (result.Status == CommandResultStatus.Success) return;
        HintRequested?.Invoke(result.Status == CommandResultStatus.Cancelled ? "已取消" : Reason(result.FailureReason?.ToString()));
    }

    /// <summary>Engine reason codes surfaced as player-readable text; no rule is inferred from them.</summary>
    private static string Reason(string? reason) => reason switch
    {
        "PointShortage" => "指挥点不足",
        "PhaseBlocked" => "当前阶段不可执行",
        "GameEnded" => "对局已结束",
        "NoCandidates" => "没有可选目标",
        "Suppressed" => "该单位被压制",
        "UnitDead" => "该单位已阵亡",
        "NonOwnerTurn" => "不是你的回合",
        "OwnerInvalid" => "不是你的单位",
        "BridgeNotAssembled" => "目标选择未接入",
        null => "操作被拒绝",
        _ => "操作被拒绝：" + reason
    };

    private Card? Resolve(string uid) => _host?.Index.GetValueOrDefault(uid);

    private void Publish()
    {
        if (_host is null) return;
        _host.Refresh();
        ProjectionReady?.Invoke(_host.View, _host.Actions);
    }

    /// <summary>
    /// Answers the engine when it asks the player to choose. The UI's own intent (a clicked target) wins;
    /// mulligan keeps the hand; a pending single choice takes the first allowed candidate until the
    /// interactive panel exists.
    /// </summary>
    private void Present(TargetingRequestDescription description, ITargetingResponder responder)
    {
        var slot = description.Slots.Count > 0 ? description.Slots[0] : null;
        if (slot is null)
        {
            responder.Cancel(description.RequestId);
            return;
        }
        var allowed = (slot.AllowedReferences ?? description.AllowedTargets).Where(r => r.IsAlive).ToArray();

        if (_pendingSelection is { Length: > 0 })
        {
            var picked = allowed.Where(r => _pendingSelection.Contains(OrcRefs.KeyOf(r.Value))).ToArray();
            if (picked.Length > 0)
            {
                responder.Complete(description.RequestId, Map(slot.Name, picked));
                return;
            }
        }
        // A move: honour the front-line slot the player dropped on, instead of any allowed slot.
        if (_pendingSlot is { } index)
        {
            var chosen = allowed.FirstOrDefault(r => OrcRefs.EntityOf(r.Value) is Slot target && target.Index == index);
            if (chosen is not null)
            {
                responder.Complete(description.RequestId, Map(slot.Name, [chosen]));
                return;
            }
        }
        if (slot.Kind == TargetSlotKind.MulliganSelect && slot.Min == 0)
        {
            responder.Complete(description.RequestId, Map(slot.Name, Array.Empty<Ref<Entity>>()));
            return;
        }
        if (allowed.Length > 0 && slot.Min <= 1)
        {
            responder.Complete(description.RequestId, Map(slot.Name, allowed.Take(1).ToArray()));
            return;
        }
        responder.Cancel(description.RequestId);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<Ref<Entity>>> Map(string slot, IEnumerable<Ref<Entity>> selected)
        => new Dictionary<string, IReadOnlyList<Ref<Entity>>>(StringComparer.Ordinal) { [slot] = selected.ToArray() };

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
