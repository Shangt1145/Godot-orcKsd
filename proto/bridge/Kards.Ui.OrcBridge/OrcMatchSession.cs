using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Managers;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Orc.Game.Board;
using Orc.Game.Judicators;
using Orc.Packages;
using Orc.UI;
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
public sealed partial class OrcMatchSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private bool _disposed;
    private bool _automaticChoice;
    /// <summary>Upper bound for one driven opponent turn, before play is forcibly handed back.</summary>
    // 对手回合的总预算必须覆盖最坏情况：每一步都用满 ActionBudget（12 × 4s）。
    // 之前写死 20 秒，长回合会被 WaitAsync 截断，任务被丢弃、回合留在对手侧——玩家再也点不动。
    private static readonly TimeSpan OpponentTurnBudget =
        OrcOpponentDriver.ActionBudget * OrcOpponentDriver.MaxActions + TimeSpan.FromSeconds(10);
    private readonly OrcMatchHost _host;
    private Task<MulliganResult>? _mulliganRequest;
    private ISelectorInstance? _pendingMulligan;
    private string[]? _pendingSelection;
    private int? _pendingSlot;
    private SourceEffectAssembly? _effects;
    private DesktopTargetContext? _desktopTargets;
    private bool _interactiveCommand;
    private CancellationToken _activeCommandToken;
    private string? _pendingGapId;
    private UiCommand? _admittedCommand;
    public Func<UiActionInteractionState>? InteractionStateReader { get; set; }

    private OrcMatchSession(OrcMatchHost host, Match match)
    {
        _host = host; Match = match;
        match.InteractionStateReader = ReadInteractionState;
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

    /// <summary>本局的 targeter 桥（AI 动作链的挂起点；对手驱动 OrcOpponentDriver 使用）。</summary>
    public OrcTargeterBridge TargeterBridge { get; private set; } = null!;
    public string MatchId { get; private init; } = "";
    public UiCardPoolReport? CardPoolReport { get; private set; }

    /// <summary>Present only for the session's own use — never handed to the UI layer.</summary>
    internal Match Match { get; private init; } = null!;

    public void Pump() { if (!_disposed) _host.Pump(); }

    public void PollEvents()
    {
        if (!_disposed && !IsSubmitting && !MulliganPending) _host.PollEvents();
    }

    /// <summary>Starts an idle opponent turn, including the opening turn when the viewer goes second.</summary>
    public async Task DriveOpponentIfNeededAsync(CancellationToken ct = default)
    {
        if (_disposed || !DriveOpponent || MulliganPending) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try { if (!await _commands.WaitAsync(0, linked.Token)) return; }
        catch (OperationCanceledException) { return; }
        try { await DriveOpponentTurnAsync(linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            if (!_disposed) _host.CaptureCompletedActions();
            _commands.Release();
        }
    }

    private async Task DriveOpponentTurnAsync(CancellationToken ct)
    {
        if (!DriveOpponent || Match.State != MatchState.InProgress || Match.Phase != MatchPhase.Play
            || ReferenceEquals(Match.CurrentPlayer, _host.Viewer)) return;
        _host.PollEvents();
        _automaticChoice = true;
        _activeCommandToken = ct;
        try
        {
            await OrcOpponentDriver.PlayTurnAsync(_host, TargeterBridge, Match.CurrentPlayer, ct: ct,
                actionCompleted: _host.PollEvents).WaitAsync(OpponentTurnBudget, ct);
        }
        catch (TimeoutException)
        {
            LastRejectionReason = "OpponentStalled:" + PendingSelectorName;
            CancelTargetChoice();
        }
        finally { _automaticChoice = false; }
        await ForceOpponentPassAsync(ct);
        if (!_disposed) _host.PollEvents();
    }

    /// <summary>
    /// 对手回合的兜底收尾：驱动跑完（或被超时截断）之后如果回合仍停在对手侧，强制交回玩家。
    /// 驱动内部的 EndTurn 有它自己的 try/catch，失败只会写日志，外层无从得知——
    /// 不做这一步，一次收尾失败就会把整局卡死在对面的回合里。
    /// </summary>
    private async Task ForceOpponentPassAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (Match.State != MatchState.InProgress) return;
            if (ReferenceEquals(Match.CurrentPlayer, _host.Viewer)) return;
            try
            {
                await Match.EndTurn(ct).WaitAsync(OrcOpponentDriver.ActionBudget, ct);
                _host.CaptureCompletedActions();
            }
            catch (Exception e)
            {
                LastRejectionReason = "OpponentPassFailed:" + e.GetType().Name;
            }
        }
    }
    public void SettleProjection() { if (!_disposed) _host.SettleProjection(); }
    public void SelectViewer(int index)
    {
        if (_disposed || IsSubmitting || MulliganPending || TargetPending || index is < 0 or > 1) return;
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
        CancelTargetChoice();
        _effects?.Dispose();
        _desktopTargets?.Dispose();
        if (_pendingMulligan is { } pending) pending.Submit(new CancelEvent());
        _pendingMulligan = null;
        MulliganRequestId = null;
        _mulliganRequest = null;
        ClearIntent();
        _host.Detach();
    }

    public async Task InitializeAsync(CancellationToken ct = default) => await _host.InitializeAsync(ct);

    /// <summary>UI-facing probe factory; its signature contains no engine callback types.</summary>
    public static Task<OrcMatchSession> CreateDefaultProbeAsync(
        string cardId, int copies, int seed, string matchId,
        Func<string, string>? artLookup = null, CancellationToken ct = default,
        int? firstPlayerIndex = null) =>
        CreateProbeAsync(cardId, copies, seed, matchId, artLookup: artLookup, ct: ct, firstPlayerIndex: firstPlayerIndex);

    /// <summary>Catalogue compilation and deck construction stay behind the engine boundary.</summary>
    public static async Task<OrcMatchSession> CreateFromCatalogAsync(
        IReadOnlyList<UiCardDefinition> cards, int seed, string matchId, string fallbackCardId,
        Func<string, string>? artLookup = null, CancellationToken ct = default,
        IReadOnlyList<string>? openingHand = null, string? sourceDirectory = null, ConstructedDeck? constructedDeck = null,
        IReadOnlyList<DiyCardDraft>? customCards = null, PackSet? packs = null,
        IReadOnlyList<OrcImportOutcome>? imported = null, int? firstPlayerIndex = null)
    {
        if (sourceDirectory is null) throw new InvalidDataException("Catalog admission requires its source directory.");
        // 目录编译会构造引擎 CardDefinition（词条注册面 fail-fast），必须先暖机——幂等，应用启动本就先于建局。
        OrcEngineHost.WarmUp();
        var sourceCards = cards.Where(c => !c.CardId.StartsWith("diy/", StringComparison.Ordinal)
            && !c.CardId.StartsWith("pack/", StringComparison.Ordinal)
            && !c.CardId.StartsWith("orc/", StringComparison.Ordinal)).ToArray();
        var (reviewed, report, effects, convertedEffects) = VerifiedCardPool.CompileInternal(sourceCards, sourceDirectory);
        var custom = (customCards ?? []).Select(c => DiyCardCompiler.Compile(c.Snapshot(), cards)).ToArray();
        if (custom.Select(c => c.Entry.Id).Distinct(StringComparer.Ordinal).Count() != custom.Length)
            throw new InvalidDataException("DIY 卡牌编号重复");
        if (cards.Any(c => c.CardId.StartsWith("diy/", StringComparison.Ordinal) && !custom.Any(d => d.Entry.Id == c.CardId)))
            throw new InvalidDataException("缺少 DIY 卡牌的可执行定义");
        var importedCards = (imported ?? []).Where(o => o.Complete && o.Entry is not null).ToArray();
        if (importedCards.Select(o => o.Entry!.Id).Distinct(StringComparer.Ordinal).Count() != importedCards.Length)
            throw new InvalidDataException("OrC 导入卡牌编号重复");
        if (importedCards.Any(o => custom.Any(c => c.Entry.Id == o.Entry!.Id)))
            throw new InvalidDataException("OrC 导入卡牌编号与 DIY 卡牌冲突");
        var importedEffects = importedCards.ToDictionary(o => o.Entry!.Id,
            o => (IReadOnlyList<EffectSnapshot>)(o.Snapshots ?? []), StringComparer.Ordinal);
        var packSet = packs ?? PackSet.FromSnapshot(OrcEngineHost.Snapshot());
        var packEntries = packSet.Definitions;
        var packIdSet = packEntries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (packIdSet.Count != packEntries.Count)
            throw new InvalidDataException("卡包卡牌编号重复");
        var replacements=custom.Select(c=>c.Entry.Id).ToHashSet(StringComparer.Ordinal);
        var pool = reviewed.Where(c=>!replacements.Contains(c.Id)).Concat(custom.Select(c => c.Entry)).Concat(packEntries.Where(c=>!replacements.Contains(c.Id)))
            .Concat(importedCards.Select(o => o.Entry!)).ToArray();
        var completeCards = sourceCards.Where(c=>!replacements.Contains(c.CardId)).Concat(custom.Select(c => c.Card))
            .Concat(PackCardDefinitions(packSet, artLookup).Where(c=>!replacements.Contains(c.CardId)))
            .Concat(importedCards.Select(o => o.Card!)).ToArray();
        if (reviewed.Count == 0) throw new InvalidDataException("No reviewed cards are available; probe fallback is not a catalog match.");
        if (openingHand is not null && openingHand.Any(id => !pool.Any(e => e.Id == id)))
            throw new InvalidDataException("The requested opening hand contains unreviewed cards.");
        constructedDeck = constructedDeck?.Snapshot();
        if (constructedDeck is not null)
        {
            var availablePacks = pool.Select(e => DeckCardPacks.Of(e.Id)).ToHashSet(StringComparer.Ordinal);
            var problems = ConstructionRules.Validate(constructedDeck, completeCards, pool.Select(e => e.Id).ToHashSet())
                .Concat(DeckCardPacks.Selected(constructedDeck).Where(p => !availablePacks.Contains(p))
                    .Select(p => "所选卡包已不可用：" + p)).ToArray();
            if (problems.Length > 0) throw new InvalidDataException(string.Join("；", problems));
            if (openingHand is not null) throw new InvalidDataException("构筑卡组不能替换起手牌");
        }
        IReadOnlyList<string> deck = constructedDeck?.CardIds ?? DeckBuilder.BuildVerified(reviewed, seed);
        var opponent = constructedDeck is null ? null
            : new DeckConstructionService(sourceCards, sourceDirectory).Starter(deck.Count, unchecked(seed + 1));
        var opponentDeck = opponent?.CardIds ?? DeckBuilder.BuildVerified(reviewed, unchecked(seed + 1));
        var session = await CreateNativeAsync(pool, deck, opponentDeck, seed, matchId,
            artLookup: artLookup, ct: ct, openingHand: openingHand, deckConfiguration: constructedDeck?.Options,
            // 自动对手只沿用主/盟国；牌组由 opponentDeck 提供，不传递编辑器其余选项。
            opponentDeckConfiguration: opponent is null ? null
                : new DeckOptions { MainNation = opponent.Options.MainNation, AllyNation = opponent.Options.AllyNation },
            customCards: custom, packs: packSet,
            convertedEffects: convertedEffects.Where(p=>!replacements.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value), importedEffects: importedEffects, firstPlayerIndex: firstPlayerIndex);
        try
        {
            session._effects = new SourceEffectAssembly(session.Match, effects.Where(p=>!replacements.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value), reason => session.LastRejectionReason ??= reason);
            var desktopPlans=new Dictionary<string,string[]>(StringComparer.Ordinal);
            foreach(var content in packSet.Contents)
                foreach(var plan in PackPresentationData.Plans(content)) desktopPlans.Add(plan.Key,plan.Value);
            foreach(var card in custom)desktopPlans[card.Entry.Id]=(card.Draft.OrcEffects.Length>0
                ?card.Draft.OrcEffects.Where(e=>e.DesktopJson is not null).Select(e=>e.DesktopJson!).ToArray()
                :!card.Draft.UseStructuredEffects&&card.Draft.EffectText.Length>0?OrcInspector.CompileText(card.Draft.Id,card.Draft.EffectText,card.Draft.CardType).Parse.Effects.Select(e=>e.DesktopJson!).ToArray():[]).Concat(WorkshopAuthoring.KeywordGraphs(card.Draft)).ToArray();
            foreach(var card in importedCards.Where(c=>c.DesktopEffectsJson is not null))
            {using var graph=System.Text.Json.JsonDocument.Parse(card.DesktopEffectsJson!);desktopPlans[card.Entry!.Id]=graph.RootElement.EnumerateArray().Select(e=>e.GetRawText()).ToArray();}
            session._desktopTargets=new DesktopTargetContext(session.Match,desktopPlans);
            session._host.SetOrderTargetReader(p=>session._effects.ReadOrderTargets(p)
                .Concat(session._desktopTargets.ReadOrderTargets(p)).Concat(KeywordOrderTargeting.Read(p))
                .DistinctBy(t=>t.Uid).ToArray());
            session._host.SetSourceDefinitions(completeCards);
            session.CardPoolReport = report;
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    /// <summary>
    /// Builds a match over a single-card probe deck. The card list is a test fixture, not a game
    /// design choice: a real deck list arrives through <see cref="CreateAsync"/>'s definitions.
    /// </summary>
    public static async Task<OrcMatchSession> CreateProbeAsync(
        string cardId, int copies, int seed, string matchId, int viewerIndex = 0,
        Func<string, string>? artLookup = null, CancellationToken ct = default,
        int? firstPlayerIndex = null)
    {
        var definitions = new[]
        {
            new CardDefinitionEntry(cardId, new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard))
        };
        var probe = Enumerable.Repeat(cardId, copies).ToArray();
        return await CreateAsync(definitions, probe, probe, seed, matchId, viewerIndex,
            artLookup, ct, firstPlayerIndex: firstPlayerIndex);
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
    public static Task<OrcMatchSession> CreateAsync(
        IReadOnlyList<CardDefinitionEntry> definitions,
        IReadOnlyList<string> deckA, IReadOnlyList<string> deckB,
        int seed, string matchId, int viewerIndex = 0,
        Func<string, string>? artLookup = null, CancellationToken ct = default,
        IReadOnlyList<string>? openingHand = null, DeckOptions? deckConfiguration = null, DeckOptions? opponentDeckConfiguration = null,
        int? firstPlayerIndex = null)
        => CreateNativeAsync(definitions, deckA, deckB, seed, matchId, viewerIndex, artLookup, ct,
            openingHand, deckConfiguration, opponentDeckConfiguration, firstPlayerIndex: firstPlayerIndex);

    private static async Task<OrcMatchSession> CreateNativeAsync(
        IReadOnlyList<CardDefinitionEntry> definitions,
        IReadOnlyList<string> deckA, IReadOnlyList<string> deckB,
        int seed, string matchId, int viewerIndex = 0,
        Func<string, string>? artLookup = null, CancellationToken ct = default,
        IReadOnlyList<string>? openingHand = null, DeckOptions? deckConfiguration = null, DeckOptions? opponentDeckConfiguration = null,
        IReadOnlyList<CompiledDiyCard>? customCards = null, PackSet? packs = null,
        IReadOnlyDictionary<string, EffectSnapshot>? convertedEffects = null,
        IReadOnlyDictionary<string, IReadOnlyList<EffectSnapshot>>? importedEffects = null,
        int? firstPlayerIndex = null)
    {
        // 引擎 S2：动作实现注册表必须先有默认集，否则 CommandManager 构造直接抛
        // 「未注册动作实现」。Register 幂等，放在建局入口最稳。
        Orc.Foundation.Actions.FoundationActions.Register();
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
        OrcMatchSession? session = null;
        var bridge = new OrcTargeterBridge(selector =>
        {
            if (session is null) { OrcTargeterBridge.AutoRespond(selector); return; }
            if (selector.SelectorName == SelectorNames.Mulligan)
            {
                session.NotePendingRequest(selector);
                session.MulliganRequested?.Invoke();
            }
            else session.AnswerParked(selector);
        });
        // 引擎侧阵营已数据化为 string 常量类（真源＝资产服务）。
        string FactionOf(string nation) => nation switch { "av76" => Faction.Soviet, "deran" => Faction.Germany,
            "USG" => Faction.USA, "UN" => Faction.Britain, _ => Faction.TryNormalize(nation, out var faction) ? faction : throw new InvalidDataException("未知构筑国家") };
        PlayerDeckConfiguration? Config(DeckOptions? options) => options is null ? null
            : new PlayerDeckConfiguration(FactionOf(options.MainNation), FactionOf(options.AllyNation));
        var packSet = packs ?? PackSet.FromSnapshot(OrcEngineHost.Snapshot());
        var registry = new CardEffectRegistry();
        foreach (var card in customCards ?? [])
            if (card.Effects.Count > 0)
                registry.DeclarePrefab(card.Entry.Id, card.Effects.Select(s => s.Root.Id).ToArray());
        // 转换卡与 DIY 同一条装配路：先声明（注册先于卡加载——引擎管道序），
        // Match 构造后把快照登记进 Prefabs。效果组件本身只有磁盘装载路径消费。
        foreach (var cardId in (convertedEffects ?? new Dictionary<string, EffectSnapshot>()).Keys)
            registry.DeclarePrefab(cardId, [convertedEffects![cardId].Root.Id]);
        // OrC 导入卡同管道序：多效果声明为多预制体 id，Match 构造后逐个登记。
        foreach (var cardId in (importedEffects ?? new Dictionary<string, IReadOnlyList<EffectSnapshot>>()).Keys)
            registry.DeclarePrefab(cardId, importedEffects![cardId].Select(s => s.Root.Id).ToArray());
        // 卡包按一个整体装配：声明进 registry、内联预制体与效果库进对局的 PrefabManager；
        // 注册先于卡加载（引擎管道序），且 InitializeAsync 会把每个词条绑定对本局预制体核验。
        // 先手完全随机：由对局种子派生（与洗切同一受控随机体系，界面不参与、玩家不可指定）。
        // 种子默认来自 Random.Shared.Next()，所以每局不同；显式给种子时仍可整体复现。
        // 诊断路径可显式传 firstPlayerIndex 固定座位——只有验证夹具用它，玩家入口永远走随机。
        var openingPlayer = firstPlayerIndex ?? new MatchRandomService(seed).Next(2);
        var match = new Match(listA, listB, definitions, seed: seed, firstPlayerIndex: openingPlayer, targeterBridge: bridge, effectRegistry: registry,
            deckConfigForPlayerA: Config(deckConfiguration), deckConfigForPlayerB: Config(opponentDeckConfiguration),
            packageActivator: new SnapshotPackageActivator(packSet.Snapshot));
        match.Engine.ScriptEvaluator = NativeScriptHost.Evaluator;
        foreach (var card in customCards ?? [])
            foreach (var snapshot in card.Effects) match.Engine.Prefabs.RegisterPrefab(snapshot);
        if (convertedEffects is not null)
            foreach (var snapshot in convertedEffects.Values) match.Engine.Prefabs.RegisterPrefab(snapshot);
        if (importedEffects is not null)
            foreach (var snapshots in importedEffects.Values)
                foreach (var snapshot in snapshots) match.Engine.Prefabs.RegisterPrefab(snapshot);
        // Load immutable native prefabs through the native assembler, then bind only the
        // declarations not replaced by a local edit. Otherwise old and edited effects run twice.
        CardPackAssembler.Attach(new CardEffectRegistry(), match.Engine, packSet.Contents);
        var replacedIds=(customCards??[]).Select(c=>c.Entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var declaration in packSet.Contents.SelectMany(c=>c.Declarations).Where(d=>!replacedIds.Contains(d.CardId)))
            registry.DeclarePrefab(declaration.CardId,declaration.PrefabIds);
        // 引擎级运行期卡（随快照冻结）：声明与预制体同样先于卡加载登记。
        foreach (var entry in packSet.Snapshot.RuntimeCards)
        {
            foreach (var prefab in entry.Prefabs) match.Engine.Prefabs.RegisterPrefab(prefab);
            if (entry.PrefabIds.Count > 0) registry.DeclarePrefab(entry.Id, entry.PrefabIds);
        }
        var host = new OrcMatchHost(match, matchId, viewerIndex, artLookup);
        session = new OrcMatchSession(host, match) { MatchId = matchId };
        session.TargeterBridge = bridge; // AI 动作链的挂起点（对手驱动 OrcOpponentDriver 使用）。
        try
        {
            await host.InitializeAsync(ct);
            var packExpected = packSet.Contents.SelectMany(c => c.Declarations).Where(d=>!replacedIds.Contains(d.CardId))
                .ToLookup(d => d.CardId, d => d.PrefabIds, StringComparer.Ordinal);
            foreach (var player in match.Players)
                foreach (var card in player.Hand.OfType<CardBase>()
                    .Concat(Enumerable.Range(0, player.Deck.Count).Select(i => player.Deck.PeekInstance(i))))
                {
                    if (!match.CardLibrary.TryGetRegisteredId(card.Definition, out var id)) continue;
                    var expected = customCards?.FirstOrDefault(c => c.Entry.Id == id);
                    foreach (var snapshot in expected?.Effects ?? [])
                    {
                        var loaded = card.Effects.Where(e => e.Name == snapshot.Root.Id).ToArray();
                        if (loaded.Length != 1 || !loaded[0].IsMounted)
                            throw new InvalidDataException($"DIY 卡牌 {id} 的效果 {snapshot.Root.Id} 未完整装载，拒绝开始对局");
                    }
                    if (importedEffects is not null && importedEffects.TryGetValue(id, out var wanted))
                        foreach (var snapshot in wanted)
                        {
                            var loadedOrc = card.Effects.Where(e => e.Name == snapshot.Root.Id).ToArray();
                            if (loadedOrc.Length != 1 || !loadedOrc[0].IsMounted)
                                throw new InvalidDataException($"OrC 导入卡牌 {id} 的效果 {snapshot.Root.Id} 未完整装载，拒绝开始对局");
                        }
                    foreach (var prefabIds in packExpected[id])
                        foreach (var prefabId in prefabIds.Distinct(StringComparer.Ordinal))
                        {
                            var packLoaded = card.Effects.Where(e => e.Name == prefabId).ToArray();
                            if (packLoaded.Length != 1 || !packLoaded[0].IsMounted)
                                throw new InvalidDataException($"卡包卡牌 {id} 的效果 {prefabId} 未完整装载，拒绝开始对局");
                        }
                }
            if (openingHand is { Count: > 0 })
                await ApplyOpeningHandAsync(host, definitions, openingHand, ct);
        }
        catch { session.Dispose(); throw; }
        return session;
    }

    /// <summary>
    /// 卡包卡 + 引擎级运行期卡的**静态 UI 定义**（图鉴/构筑/起手展示用）——由引擎侧 `.ui` 插件的静态投影产出，
    /// 前端不自造定义（迁移 S8）；卡面文本取自卡包 `texts.json`。
    /// </summary>
    private static IReadOnlyList<UiCardDefinition> PackCardDefinitions(PackSet packSet, Func<string, string>? artLookup)
    {
        var result = new List<UiCardDefinition>();
        foreach (var content in packSet.Contents)
        {
            var prefix = content.Prefix;
            var texts = PackPresentationData.Texts(content);
            foreach (var entry in content.Definitions)
            {
                var rawId = entry.Id.StartsWith(prefix, StringComparison.Ordinal)
                    ? entry.Id[prefix.Length..]
                    : entry.Id;
                result.Add(StaticCardProjection.Project(
                    entry.Id, entry.Definition, artLookup,
                    text: texts.TryGetValue(rawId, out var text) ? text : null) with { Flavor = PackPresentationData.Flavor(content.RootDirectory, rawId) });
            }
        }

        foreach (var entry in packSet.Snapshot.RuntimeCards)
        {
            result.Add(StaticCardProjection.Project(entry.Id, entry.Definition, artLookup));
        }

        return result;
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
            BeginViewerMulligan(ct);
        }
        else
        {
            await Match.MulliganDone(viewer, linked.Token);
            _host.CaptureCompletedActions();
        }
    }

    public string? MulliganRequestId { get; private set; }
    private long _mulliganDelivery;

    private bool BeginViewerMulligan(CancellationToken ct = default)
    {
        if (_disposed || MulliganPending || IsSubmitting || Match.Phase != MatchPhase.Mulligan
            || Match.MulliganManager.IsConfirmed(_host.Viewer)) return false;
        MulliganOutcome = null; MulliganError = null;
        _mulliganRequest = RunMulliganAsync(_host.Viewer, ct);
        _ = _mulliganRequest.ContinueWith(t => { _ = t.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return true;
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
    public void AnswerParked(ISelectorInstance selector)
    {
        if (_disposed || _activeCommandToken.IsCancellationRequested) { selector.Submit(new CancelEvent()); return; }
        var data = selector.Presentation;
        if (data.Identifiers is not null)
        {
            if (_automaticChoice) OrcTargeterBridge.AutoRespond(selector);
            else PresentTargetChoice(selector);
            return;
        }
        var allowed = data.Candidates.Where(r => r.IsAlive).ToArray();
        if (_pendingGapId is { } gapId)
        {
            var wanted = allowed.FirstOrDefault(r => r.Value is FormationGap && OrcRefs.KeyOf(r.Value) == gapId);
            ClearIntent();
            if (wanted is not null) selector.Submit(new PickEvent([wanted]));
            else { LastRejectionReason = "FormationGapUnavailable"; selector.Submit(new CancelEvent()); }
            return;
        }
        if (_pendingSelection is { Length: > 0 })
        {
            var chosen = _pendingSelection.Select(uid => allowed.FirstOrDefault(r => OrcRefs.KeyOf(r.Value) == uid)).ToArray();
            if (chosen.All(r => r is not null))
            {
                ClearIntent();
                selector.Submit(selector.Mode == SelectorInteractionMode.Drag && chosen.Length == 1
                    ? new DropEvent(chosen[0]) : new PickEvent(chosen!));
                return;
            }
            LastRejectionReason = "SelectedTargetUnavailable";
            ClearIntent(); selector.Submit(new CancelEvent()); return;
        }
        if (_pendingSupportIndex is not null || _pendingSlot is not null)
        {
            var wanted = SlotAt();
            ClearIntent();
            if (wanted is not null && allowed.Any(r => ReferenceEquals(r.Value, wanted.Value)))
                selector.Submit(selector.Mode == SelectorInteractionMode.Drag ? new DropEvent(wanted) : new PickEvent([wanted]));
            else { LastRejectionReason = "SelectedSlotUnavailable"; selector.Submit(new CancelEvent()); }
            return;
        }
        if (_automaticChoice) OrcTargeterBridge.AutoRespond(selector);
        else if (_interactiveCommand) PresentTargetChoice(selector);
        else { LastRejectionReason = "SelectionRequired"; selector.Submit(new CancelEvent()); }
    }

    private Ref<Entity>? SlotAt()
    {
        if (_pendingSupportIndex is { } support)
        {
            var line = Match.Battlefield.GetSupportLine(_host.Viewer.Index);
            return support >= 0 && support < line.Count ? line[support].Ref : null;
        }
        if (_pendingSlot is not { } front) return null;
        var frontline = Match.Battlefield.FrontLine;
        return front >= 0 && front < frontline.Count ? frontline[front].Ref : null;
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
        _pendingGapId = null;
    }

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
    public void NotePendingRequest(ISelectorInstance selector)
    {
        if (_disposed) { selector.Submit(new CancelEvent()); return; }
        if (selector.SelectorName == SelectorNames.Mulligan)
        {
            _pendingMulligan = selector;
            MulliganRequestId = $"{MatchId}:mulligan:{Interlocked.Increment(ref _mulliganDelivery)}";
        }
    }

    /// <summary>
    /// Completes a parked mulligan. The answer goes back through the targeter that asked for it —
    /// the same responder path the engine parked on — so there is no parallel "mulligan API" to
    /// keep in sync. <see cref="OrcTargeterBridge.SelectReplace"/> owns the keep/replace policy.
    /// </summary>
    public async Task<UiSubmitOutcome> CompleteMulliganAsync(IReadOnlyList<string> keepUids, CancellationToken ct = default,
        string? requestId = null)
    {
        if (_disposed) return UiSubmitOutcome.Interrupted;
        if (_pendingMulligan is null) return UiSubmitOutcome.Rejected;
        if (requestId is not null && requestId != MulliganRequestId) return UiSubmitOutcome.Rejected;
        var pending = _pendingMulligan;
        var allowed = pending.Presentation.Candidates.Where(r => r.IsAlive).ToArray();
        var keys = allowed.Select(r => OrcRefs.KeyOf(r.Value)).ToHashSet(StringComparer.Ordinal);
        if (keepUids.Distinct(StringComparer.Ordinal).Count() != keepUids.Count || keepUids.Any(id => !keys.Contains(id)))
            return UiSubmitOutcome.Rejected;
        if (!pending.Submit(new PickEvent(OrcTargeterBridge.SelectReplace(allowed, keepUids))))
            return UiSubmitOutcome.Rejected;
        if (ReferenceEquals(_pendingMulligan, pending)) { _pendingMulligan = null; MulliganRequestId = null; }
        if (_mulliganRequest is not null)
        {
            // The engine reports why a mulligan failed; surfacing it turns a silent stall into a
            // readable reason instead of a phase that never advances.
            try { MulliganOutcome = await _mulliganRequest.WaitAsync(ct); }
            catch (OperationCanceledException) { return UiSubmitOutcome.Interrupted; }
            catch (Exception e) { MulliganError = e.Message; throw; }
        }
        _mulliganRequest = null;
        if (MulliganOutcome is { IsSuccess: false } failed)
        {
            LastRejectionReason = failed.FailureReason?.ToString() ?? failed.Status.ToString();
            return UiSubmitOutcome.Rejected;
        }
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
    public IReadOnlyList<UiKeywordSkill> KeywordSkills(string uid)
    {
        if(Resolve(uid) is not UnitCard card||!ReferenceEquals(card.Owner,_host.Viewer))return [];
        return KeywordProgramRuntime.Components(card).SelectMany(c=>c.Definition.Skills.Select(s=>new UiKeywordSkill(c.Keyword,s.Id,s.Name,c.CanUse(s.Id),Math.Max(0,c.Number(s.Cost)),string.Join(" · ",c.State.Select(p=>$"{p.Key} {p.Value}"))))).ToArray();
    }
    public async Task<UiSubmitOutcome> SubmitAsync(UiCommand command, CancellationToken ct = default)
    {
        var outcome = await SubmitCoreAsync(command, ct);
        if (outcome != UiSubmitOutcome.Applied)
            Console.WriteLine($"[bridge] submit {command.GetType().Name} -> {outcome} reason={LastRejectionReason}");
        return outcome;
    }

    private async Task<UiSubmitOutcome> SubmitCoreAsync(UiCommand command, CancellationToken ct = default)
    {
        if (_disposed) return UiSubmitOutcome.Interrupted;
        if (command is ChooseMulligan keep) return await CompleteMulliganAsync(keep.KeepUids, ct, keep.RequestId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try { if (!await _commands.WaitAsync(0, linked.Token)) return UiSubmitOutcome.Busy; }
        catch (OperationCanceledException) { return UiSubmitOutcome.Interrupted; }
        try
        {
            LastRejectionReason = null;
            // Flush asynchronous arrivals before a new command can play or move the arriving card.
            _host.PollEvents();
            ct = linked.Token;
            _activeCommandToken = ct;
            using var cancelChoice = ct.Register(CancelTargetChoice);
            if (Match.Phase != MatchPhase.Play) return UiSubmitOutcome.WrongPhase;
            if (!IsViewerOnTurn) return UiSubmitOutcome.NotYourTurn;
            var inputActor = BattleInteractionStateMachine.Actor(command) is { } actorUid
                ? OrcRefs.EntityOf(Resolve(actorUid)) : _host.Viewer.Hq;
            var targetUid = command switch { AttackUnit attack => attack.DefenderUid,
                CommandUnit gesture => gesture.TargetUid, PlayCard play => play.TargetUid, _ => null };
            var inputTarget = targetUid is null ? null : OrcRefs.EntityOf(Resolve(targetUid));
            if (!Match.CanAcceptInput(_host.Viewer, inputActor, inputTarget))
            { LastRejectionReason = "InteractionBlocked"; return UiSubmitOutcome.Rejected; }
            _admittedCommand = command;
            switch (command)
            {
                case UseKeywordSkill skill:
                    if(Resolve(skill.Uid) is not UnitCard skillHost||!ReferenceEquals(skillHost.Owner,_host.Viewer))return UiSubmitOutcome.UnknownCard;
                    var component=KeywordProgramRuntime.Components(skillHost).FirstOrDefault(c=>c.Keyword==skill.KeywordId);
                    _choiceSourceUid=skill.Uid;
                    _interactiveCommand = true;
                    if(component is null||!await component.UseAsync(skill.SkillId,ct))return UiSubmitOutcome.Rejected;
                    _host.CaptureCompletedActions();return UiSubmitOutcome.Applied;
                case Concede:
                    var conceded = Match.Concede(_host.Viewer, ct);
                    if (conceded.IsAccepted) return UiSubmitOutcome.Applied;
                    LastRejectionReason = conceded.FailureReason?.ToString();
                    return UiSubmitOutcome.Rejected;
                case PlayCard play:
                    _choiceSourceUid = play.Uid;
                    _interactiveCommand = true;
                    _pendingSelection = play.TargetUid is { } selected ? [selected] : null;
                    _pendingGapId = play.GapId;
                    return await PlayAsync(play.Uid, play.SupportIndex, ct);
                case MoveUnit move:
                {
                    if (Resolve(move.Uid) is not UnitCard mover) return UiSubmitOutcome.UnknownCard;
                    // The player chose a slot; replay it when the engine asks where to go.
                    if (move.ToZone == "support") _pendingSupportIndex = move.SlotIndex;
                    else if (move.ToZone == "frontline") _pendingSlot = move.SlotIndex;
                    else return UiSubmitOutcome.Rejected;
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
                    await DriveOpponentTurnAsync(ct);
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
            CancelTargetChoice();
            ClearIntent();
            _interactiveCommand = false;
            _choiceSourceUid = null;
            _activeCommandToken = default;
            _admittedCommand = null;
            if (!_disposed) _host.CaptureCompletedActions();
            _commands.Release();
        }
    }

    private Card? Resolve(string uid) =>
        _host.Index.TryGetValue(uid, out var card) && OrcRefs.IsAlive(card) ? card : null;

    private async Task<UiSubmitOutcome> PlayAsync(string uid, int? supportIndex, CancellationToken ct)
    {
        if (Resolve(uid) is OrderCard order)
        {
            _interactiveCommand = true;
            return Report(await Match.PlayManager.BeginOrderPrePlayAsync(order, ct));
        }
        if (Resolve(uid) is CounterCard counter)
            return Report(await Match.PlayManager.UseCounterAsync(counter, ct));
        if (Resolve(uid) is not UnitCard unit) return UiSubmitOutcome.UnknownCard;
        // Compatibility for older contract fixtures explicitly naming storage slots. Product drag
        // submits GapId and goes through the canonical native gap selector; these paths never mix.
        if (supportIndex is { } legacy && _pendingGapId is null)
        {
            var line = Match.Battlefield.GetSupportLine(_host.Viewer);
            if (legacy < 0 || legacy >= line.Count || !line[legacy].IsEmpty)
            { LastRejectionReason = "SelectedSlotUnavailable"; return UiSubmitOutcome.Rejected; }
            return Report(await Match.PlayManager.PlayUnitAsync(unit, line[legacy], ct));
        }
        // Contract-only callers may omit a position. This explicit adapter policy is limited to
        // untargeted deployment; a named but illegal position is always refused.
        var automatic = _automaticChoice;
        _automaticChoice = _pendingGapId is null;
        try
        {
            return Report(await Match.PlayManager.BeginUnitPrePlayAsync(unit, ct));
        }
        finally { _automaticChoice = automatic; }
    }

    private int? _pendingSupportIndex;

    private ActionInteractionState? ReadInteractionState(Player player)
    {
        if (!ReferenceEquals(player, _host.Viewer) || InteractionStateReader is null) return null;
        var state = InteractionStateReader();
        var continuation = _admittedCommand is EndTurn ? player.Hq.Id
            : _admittedCommand is { } command && BattleInteractionStateMachine.Actor(command) is { } uid
                && Guid.TryParseExact(uid, "N", out var id) ? id : (Guid?)null;
        // A parked choice belongs to the already admitted command. It may complete that command,
        // while fresh commands still pass the admission check above and the session semaphore.
        var busy = state.PendingActorUids.Select(uid => Guid.TryParseExact(uid, "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty && id != continuation).ToHashSet();
        return new(state.AllowsActions, busy, continuation);
    }

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
