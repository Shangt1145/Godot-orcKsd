using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Orc.Game.Ui;
using Orc.UI;
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
///
/// 迁移 S8：整盘与单卡视图改由**引擎侧投影提供者**（`.ui` 插件）产出——前端不再自造卡读面。
/// 投影按**每帧拉取**；投影抛错＝**运行期隔离**（保留上一帧 + 记录错误），不炸对局。
/// </summary>
public sealed class OrcMatchHost
{
    private readonly Match _match;
    private int _viewerIndex;
    private readonly string _matchId;
    private Player _viewer = null!;
    private OrcUpdateTranslator? _translator;
    private readonly OrcActionReader _actions = new();
    private Func<Player, IReadOnlyList<UiOrderTargeting>>? _orderTargets;
    internal void SetOrderTargetReader(Func<Player, IReadOnlyList<UiOrderTargeting>> reader) => _orderTargets = reader;
    private IDisposable? _immediate;
    private UiMatchView? _view;
    private bool _detached;
    private bool _observer;
    private long _lastSequence;
    private readonly Func<string, string>? _artLookup;
    private IReadOnlyDictionary<string, UiCardDefinition> _sourceDefinitions =
        new Dictionary<string, UiCardDefinition>(StringComparer.Ordinal);
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

    /// <summary>
    /// 前端自持的**丰富卡定义**（美术/文本/flavor）——作为投影的 `definitionLookup` 注入；
    /// 引擎不参与渲染，故这类界面数据始终由前端提供。可在初始化后设置（provider 每次读取时按需构造）。
    /// </summary>
    internal void SetSourceDefinitions(IReadOnlyList<UiCardDefinition> definitions)
    {
        _sourceDefinitions = definitions.ToDictionary(c => c.CardId, StringComparer.Ordinal);
        Refresh();
    }

    private UiCardDefinition? DefinitionLookup(string cardId)
        => _sourceDefinitions.TryGetValue(cardId, out var definition) ? definition : null;

    /// <summary>取投影提供者（由 `.ui` 插件在装配期注册；未装配＝null）。</summary>
    private IUiProjectionProvider? Provider()
        => _match.UiProjections.CreateProvider(_artLookup, _matchId, DefinitionLookup);

    /// <summary>单卡投影委托（供表现层"段→演出"取不在盘面上的卡视图）。</summary>
    private UiCardView? ReadCardFor(Card card, Player viewer, string zone, int slotIndex)
        => Provider()?.ReadCard(_match, card, viewer, zone, slotIndex);

    private void EnsureTranslator() => _translator ??= new OrcUpdateTranslator(ReadCardFor);

    /// <summary>Last read board. Null until <see cref="InitializeAsync"/> completes.</summary>
    public UiMatchView View => _view ?? throw new InvalidOperationException("Match host has not been initialized.");

    /// <summary>The player this host renders for.</summary>
    public Player Viewer => _viewer;

    /// <summary>The running match; for adapter-side drivers (tests, demo harnesses), not for rules.</summary>
    public Match Match => _match;

    /// <summary>uid -> engine card for the last read; commands arrive as uids.</summary>
    public IReadOnlyDictionary<string, Card> Index => BuildIndex();

    /// <summary>
    /// uid → 引擎卡（本次读取的盘面 + 本人手牌 + 双方总部）。命令以 uid 到达，故需要这张反查表；
    /// 它是一次快照：上次读取后被销毁的卡自然缺席。
    /// </summary>
    private Dictionary<string, Card> BuildIndex()
    {
        var index = new Dictionary<string, Card>(StringComparer.Ordinal);
        if (_viewer is null)
        {
            return index;
        }

        foreach (var card in _viewer.Hand)
        {
            if (card is CardBase typed)
            {
                index[OrcRefs.KeyOf(typed)] = typed;
            }
        }

        var battlefield = _match.Battlefield;
        for (var i = 0; i < battlefield.FrontLine.Count; i++)
        {
            if (battlefield.FrontLine[i].Occupant is Card front)
            {
                index[OrcRefs.KeyOf(front)] = front;
            }
        }

        foreach (var player in _match.Players)
        {
            var line = battlefield.GetSupportLine(player.Index);
            for (var i = 0; i < line.Count; i++)
            {
                if (line[i].Occupant is Card support)
                {
                    index[OrcRefs.KeyOf(support)] = support;
                }
            }

            index[OrcRefs.KeyOf(player.Hq)] = player.Hq;
        }

        return index;
    }

    /// <summary>Availability for the current board, read from the engine's query surfaces.</summary>
    public UiBattleActions Actions
    {
        get
        {
            var actions = _actions.Read(_match, _viewer, Index);
            return actions.CanEndTurn && _orderTargets is not null
                ? actions with { OrderTargets = _orderTargets(_viewer) } : actions;
        }
    }

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
        EnsureTranslator();
        _view = ProjectRequired();
        CaptureCompletedActions();
    }

    /// <summary>
    /// Adopts a match that is already running, resolving this host's viewpoint from it. This is the
    /// observer path: another viewpoint over the same live game. <see cref="InitializeAsync"/> cannot be
    /// used because it would initialise the match a second time; a seat only needs its own viewer and view.
    /// </summary>
    public void AttachToLiveMatch()
    {
        if (_match.State == MatchState.Preparing)
            throw new InvalidOperationException("The match is still preparing; initialise it first.");
        _viewer = _match.Players[_viewerIndex];
        _observer = true;
        EnsureTranslator();
        _view = ProjectRequired();
    }

    public void SelectViewer(int index)
    {
        if (_detached) return;
        _viewerIndex = index;
        _viewer = _match.Players[index];
        _captured.Clear(); // a viewpoint switch settles the board, not the previous seat's animation
        Refresh();
    }

    /// <summary>卡牌选择面板要展示的定义视图（不依赖盘面实例）。</summary>
    internal UiCardView? ReadChoiceDefinition(string id)
    {
        if (!_match.CardLibrary.Contains(id)) return null;
        var definition = StaticCardProjection.Project(
            id, _match.CardLibrary.Get(id), _artLookup, DefinitionLookup);
        return new UiCardView
        {
            Uid = "definition:" + id,
            Definition = definition,
            EffectiveCost = definition.Cost,
            Visibility = Visibility.Full,
        };
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
        EnsureTranslator();
        var segments = _match.Engine.TakeSegments();
        if (segments.Count > 1) HistoricalStatesVerified = false;
        foreach (var segment in segments)
        {
            _lastSequence = Math.Max(_lastSequence + 1, segment.Sequence);
            var updates = segment.Entries
                .Where(e => e.Kind == LogEntryKind.Update)
                .Select(e => (Update: e.Message, Payload: e.Data))
                .ToArray();
            var errors = segment.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToArray();
            // 先冻结 before 再拉取 after：Refresh() 有副作用（写 _view），若先取 after，
            // before 会被覆盖成事后状态——段差异恒为零，部署/扣费在演出链上彻底不可见。
            var before = _view;
            var after = UiSnapshots.Freeze(ProjectRequired());
            var translation = _translator!.Translate(updates, _viewer, before, after);
            _view = after;
            var resolution = UiSnapshots.Freeze(new UiPresentationResolution(_matchId, translation.Steps, after)
                { Sequence = _lastSequence, Before = before });
            _captured.Enqueue((resolution, UiSnapshots.Freeze(Actions), translation.Impacts, errors));
        }
        // Phase changes and mulligan replacements can be silent in the current engine.
        // Keep the live projection fresh without changing already frozen action snapshots.
        if (segments.Count == 0)
        {
            var before = _view;
            var after = UiSnapshots.Freeze(Refresh());
            // Concede intentionally emits no engine update. Deliver its authoritative phase
            // change once through the same presentation lane, without fabricating a game event.
            var silent = _translator!.Translate([], _viewer, before, after);
            // 资产化引擎的部分规则效果（如部署扣费）不产生引擎 update 段，只体现为视图差异。
            // 这类差异若不入队演出，PresentationPlayer 的状态基线会停在付款前，UI 资源显示失真。
            var material = before.SelfKredits != after.SelfKredits || before.EnemyKredits != after.EnemyKredits
                || before.SelfLine.Count != after.SelfLine.Count || before.EnemyLine.Count != after.EnemyLine.Count;
            if (silent.Steps.Count > 0 || material
                || before.Phase != "over" && after.Phase == "over" && after.ResultTitle is not null)
                _captured.Enqueue((UiSnapshots.Freeze(new UiPresentationResolution(_matchId, silent.Steps, after)
                    { Before = before, Sequence = ++_lastSequence }), UiSnapshots.Freeze(Actions), [], []));
        }
    }

    /// <summary>
    /// Re-reads the board without animation. Mulligan's replacement draws are silent (no card.drawn /
    /// card.hand.add), so the opening hand must be re-read explicitly after a swap.
    /// 投影抛错＝运行期隔离：保留上一帧并上报错误。
    /// </summary>
    public UiMatchView Refresh()
    {
        var projected = Project();
        return _view = projected ?? _view
            ?? throw new InvalidOperationException(
                "投影不可用：本对局未装配 UI 投影提供者（`.ui` 插件），且尚无上一帧可保留。");
    }

    private UiMatchView ProjectRequired() => Refresh();

    private UiMatchView? Project()
    {
        var provider = Provider();
        if (provider is null)
        {
            ErrorRaised?.Invoke("本对局未装配 UI 投影提供者（`.ui` 插件）——整盘视图不可用。");
            return null;
        }

        try
        {
            return provider.Project(_match, _viewer);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorRaised?.Invoke($"投影失败（保留上一帧）：{e.Message}");
            return null;
        }
    }

    public void Detach()
    {
        _detached = true;
        _captured.Clear();
        _immediate?.Dispose();
        _immediate = null;
    }
}
