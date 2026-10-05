using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>Battle stage one: projected original-style board and interruptible combat presentation.</summary>
public partial class BattleScreen : Control
{
    private static readonly Vector2 BoardSize = new(1280, 720);
    private static readonly Rect2 SelfArea = new(260, 439, 760, 165), FrontArea = new(260, 267, 760, 164);
    private TextureCache _textures = null!;
    private AnimClock _clock = null!;
    private BattleDemoAdapter _demo = null!;
    private UiMatchView _state = null!;
    private UiBattleActions _actions = new();
    private Control _canvas = null!, _cardLayer = null!, _detail = null!;
    private KreditsDisplay _selfResource = null!, _enemyResource = null!;
    private Label _hint = null!;
    private Control _history = null!, _result = null!;
    private BattleCombat _combat = null!;
    private SfxPlayer _sfx = null!;
    private UiCombatResolution? _pendingCombat;
    private UiBattleActions? _pendingActions;
    private Label _deckCount = null!, _inspectStats = null!;
    private Button _endTurn = null!;
    private PopupMenu _menu = null!;
    private BattleAim _aim = null!;
    private BattlePaper _paper = null!;
    private readonly Dictionary<string, BattleCard> _cards = new();
    private readonly List<Tween> _tweens = new();
    private readonly List<BattleCard> _ghosts = new();
    private readonly Queue<string> _entries = new();
    private BattleCard? _hovered, _pressed;
    private string? _selected;
    private Vector2 _pressAt, _grabOffset;
    private bool _dragging, _busy, _usingDemo = true;
    private int _generation;
    public event Action<UiCommand>? CommandRequested;

    public event Action<string>? NavigationRequested;

    /// <summary>Raised by the gear menu: run the screen from a real engine match instead of the demo fixture.</summary>
    public event Action? RealMatchRequested;

    /// <summary>Shows text supplied from outside (for example an engine refusal reason).</summary>
    public void ShowHint(string text)
    {
        _hint.Text = text;
        _hint.Visible = true;
    }
    public void Initialize(CardCatalog catalog, TextureCache textures, AnimClock clock, SfxPlayer sfx)
    {
        _textures = textures; _clock = clock; _sfx = sfx; _demo = new(catalog.Cards);
        ClipContents = true;
        _canvas = new Control { Size = BoardSize, MouseFilter = MouseFilterEnum.Stop };
        AddChild(_canvas);
        var background = new TextureRect { Size = BoardSize, MouseFilter = MouseFilterEnum.Ignore,
            Texture = textures.Get("res://proto/assets/battle/tabletop-v2.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale };
        _canvas.AddChild(background);
        _paper = new BattlePaper { Size = BoardSize, MouseFilter = MouseFilterEnum.Ignore };
        _canvas.AddChild(_paper);
        _enemyResource = new KreditsDisplay { Position = new(40, 0), Size = new(250, 108) }; _canvas.AddChild(_enemyResource);
        _selfResource = new KreditsDisplay { Position = new(40, 614), Size = new(250, 106), Bottom = true }; _canvas.AddChild(_selfResource);
        var menu = new BattleMenuButton { Position = new(1210, 10), Size = new(45, 45), Flat = true, TooltipText = "菜单" };
        menu.AddThemeFontSizeOverride("font_size", 32); menu.AddThemeColorOverride("font_color", new("e5e1d3"));
        _canvas.AddChild(menu);
        var popup = menu.GetPopup();
        _menu = popup;
        popup.AddItem("图鉴", 0); popup.AddItem("设置", 1); popup.AddSeparator(); popup.AddItem("重新布阵", 2);
        popup.AddItem("步兵攻击演示", 3); popup.AddItem("坦克攻击演示", 4); popup.AddItem("火炮攻击演示", 5);
        popup.AddItem("战斗机攻击演示", 6); popup.AddItem("轰炸机攻击演示", 7); popup.AddItem("总部摧毁演示", 8);
        popup.AddItem("飞机摧毁演示", 9);
        popup.AddItem("太空战机攻击演示", 10); popup.AddItem("太空战舰攻击演示", 11);
        popup.AddSeparator();
        popup.AddItem("抽牌演示", 12); popup.AddItem("连续抽牌演示", 13); popup.AddItem("对手抽牌演示", 14);
        popup.AddItem("指令揭示演示", 15); popup.AddItem("多目标指令演示", 16); popup.AddItem("对手指令演示", 17);
        popup.AddSeparator(); popup.AddItem("反制武装演示", 18); popup.AddItem("取消武装演示", 19);
        popup.AddItem("反制触发演示", 20); popup.AddItem("治疗演示", 21); popup.AddItem("强化演示", 22);
        popup.AddItem("压制与解除演示", 23); popup.AddItem("费用变化演示", 24);
        popup.AddSeparator(); popup.AddItem("真实对局（接入引擎）", 25);
        popup.IdPressed += id =>
        {
            if (id == 25) RealMatchRequested?.Invoke();
            else if (id == 0) NavigationRequested?.Invoke("collection");
            else if (id == 1) NavigationRequested?.Invoke("settings");
            else if (id == 2) ResetDemo();
            else if (id == 9) StartScenario("tank", false, "fighter");
            else if (id is 10 or 11) StartScenario(id == 10 ? "spacefighter" : "landcruiser");
            else if (id is >= 12 and <= 17) _ = StartPresentationScenarioAsync(new[] { "draw", "draw-two", "enemy-draw", "order", "multi-order", "enemy-order" }[(int)id - 12]);
            else if (id is >= 18 and <= 24) _ = StartPresentationScenarioAsync(new[] { "counter-arm", "counter-disarm", "counter-trigger", "status-heal", "status-buff", "status-suppressed", "status-cost" }[(int)id - 18]);
            else StartScenario(new[] { "infantry", "tank", "artillery", "fighter", "bomber", "artillery" }[(int)id - 3], id == 8);
        };
        _history = new Control { Position = new(31, 226), Size = new(70, 260), MouseFilter = MouseFilterEnum.Ignore }; _canvas.AddChild(_history);
        var historyToggle = new BattleHistoryButton { Position = new(30, 119), Size = new(36, 30), Flat = true, TooltipText = "行动记录", ToggleMode = true, ButtonPressed = true };
        historyToggle.Toggled += visible => _history.Visible = visible; _canvas.AddChild(historyToggle);
        _endTurn = new Button { Position = new(1120, 445), Size = new(135, 36), Text = "结束回合" };
        foreach (var state in new[] { "normal", "disabled", "hover", "pressed", "focus" })
        {
            var box = UiStyles.Box(new Color(0, 0, 0, state == "hover" ? .23f : .08f), new("d5cfb7"), 0, 4);
            box.BorderWidthLeft = box.BorderWidthTop = box.BorderWidthRight = box.BorderWidthBottom = 2;
            _endTurn.AddThemeStyleboxOverride(state, box);
        }
        _endTurn.AddThemeColorOverride("font_color", new("e9e3cc"));
        _endTurn.AddThemeColorOverride("font_disabled_color", new("c7c1ad"));
        _endTurn.Pressed += EndTurnClicked; _canvas.AddChild(_endTurn);
        for (var i = 0; i < 4; i++)
        {
            var deck = new BattleCard { Position = new(1150 + i * 2, 624 - i * 2), Size = new(110, 154), Rotation = .70f, MouseFilter = MouseFilterEnum.Ignore };
            deck.PivotOffset = deck.Size / 2; deck.Bind(null, BattleCardMode.Hidden, textures); _canvas.AddChild(deck);
        }
        _deckCount = Text("", new(1235, 651), new(38, 28), 17, "e9e4d0"); _canvas.AddChild(_deckCount);
        _hint = Text("", new(430, 594), new(420, 26), 14, "ece5ce");
        _hint.HorizontalAlignment = HorizontalAlignment.Center; _hint.Visible = false; _canvas.AddChild(_hint);
        _cardLayer = new Control { Size = BoardSize, MouseFilter = MouseFilterEnum.Ignore }; _canvas.AddChild(_cardLayer);
        _aim = new BattleAim { Size = BoardSize, MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 180 }; _canvas.AddChild(_aim);
        _combat = new BattleCombat { Size = BoardSize, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 200 };
        _canvas.AddChild(_combat); _combat.Initialize(clock, textures, sfx);
        InitializePresentation();
        _detail = new Control { Position = new(1027, 103), Size = new(215, 275), MouseFilter = MouseFilterEnum.Ignore, ZIndex = 160, Visible = false }; _canvas.AddChild(_detail);
        _inspectStats = Text("", new(1027, 362), new(215, 23), 12, "e2dbc1"); _inspectStats.ZIndex = 161; _inspectStats.Visible = false; _canvas.AddChild(_inspectStats);
        _result = new Control { Position = new(445, 271), Size = new(390, 145), ZIndex = 250, Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _canvas.AddChild(_result);
        _canvas.GuiInput += BoardInput;
        Resized += FitBoard;
        VisibilityChanged += () => { if (!IsVisibleInTree()) CancelSelection(); };
        _clock.Changed += PreferencesChanged;
        ResetDemo(); FitBoard();
    }
    private static Label Text(string text, Vector2 at, Vector2 size, int fontSize, string color)
    {
        var label = UiStyles.Label(text, fontSize, new(color)); label.Position = at; label.Size = size;
        label.MouseFilter = MouseFilterEnum.Ignore; return label;
    }
    private void FitBoard()
    {
        if (_canvas is null || Size.X <= 0 || Size.Y <= 0) return;
        var scale = Math.Min(Size.X / BoardSize.X, Size.Y / BoardSize.Y);
        _canvas.Scale = Vector2.One * scale; _canvas.Position = (Size - BoardSize * scale) / 2;
    }
    public void ResetDemo()
    {
        ClearPresentation();
        _pendingCombat = null; _pendingActions = null; _combat.Interrupt();
        _usingDemo = true; _demo.Reset(); _entries.Clear(); UiStyles.Clear(_history);
        Render(_demo.State, _demo.Actions, false);
        _hint.Visible = false;
    }
    public void ApplyProjection(UiMatchView state, UiBattleActions actions)
    {
        ClearPresentation();
        _pendingCombat = null; _pendingActions = null; _combat.Interrupt();
        _usingDemo = false;
        Render(UiSnapshots.Freeze(state), UiSnapshots.Freeze(actions), _state?.MatchId == state.MatchId);
    }
    private void FinishMotion()
    {
        foreach (var tween in _tweens) if (GodotObject.IsInstanceValid(tween)) tween.Kill();
        _tweens.Clear();
        foreach (var ghost in _ghosts) if (GodotObject.IsInstanceValid(ghost) && !ghost.IsQueuedForDeletion()) { ghost.GetParent()?.RemoveChild(ghost); ghost.QueueFree(); }
        _ghosts.Clear(); _busy = false;
        foreach (var c in _cards.Values) { c.Position = c.RestPosition; c.Rotation = c.RestRotation; c.Scale = Vector2.One; c.Visible = true; }
    }
    private void Render(UiMatchView state, UiBattleActions actions, bool animate)
    {
        _generation++;
        var previous = _cards.ToDictionary(p => p.Key, p => (RestPosition: p.Value.Position, p.Value.Size, RestRotation: p.Value.Rotation, p.Value.Mode, p.Value.View));
        FinishMotion();
        _selected = null; _pressed = null; _hovered = null; _dragging = false; _aim.Visible = false;
        _detail.Visible = _inspectStats.Visible = false;
        _hint.Visible = false;
        UiStyles.Clear(_cardLayer); _cards.Clear();
        _state = state; _actions = actions;
        _selfResource.Bind(state.SelfKredits, state.SelfMaxKredits, state.SelfPlayerName);
        _enemyResource.Bind(state.EnemyKredits, state.EnemyMaxKredits, state.EnemyPlayerName);
        _deckCount.Text = state.SelfDeckCount.ToString();
        _endTurn.Text = state.Phase == "over" ? "对局结束" : state.ActivePlayerSide == "self" ? "结束回合" : "对手回合";
        _endTurn.Disabled = !actions.CanEndTurn;
        _result.Visible = state.Phase == "over";
        foreach (var id in Enumerable.Range(2, 23)) _menu.SetItemDisabled(_menu.GetItemIndex(id), !_usingDemo);
        if (_result.Visible)
        {
            UiStyles.Clear(_result);
            var paper = new Panel { Size = new(390, 160), MouseFilter = MouseFilterEnum.Ignore };
            paper.AddThemeStyleboxOverride("panel", UiStyles.Box(new Color(.12f, .13f, .11f, .94f), new("827c64"), 1, 8));
            _result.AddChild(paper);
            var victory = Text(state.ResultTitle ?? "对局结束", new(0, 10), new(390, 75), 52, "e8dec1");
            victory.HorizontalAlignment = HorizontalAlignment.Center; _result.AddChild(victory);
            var restart = new Button { Text = _usingDemo ? "再来一局" : "返回图鉴", Position = new(115, 104), Size = new(160, 38) };
            foreach (var style in new[] { "normal", "hover", "pressed", "focus" }) restart.AddThemeStyleboxOverride(style, _endTurn.GetThemeStylebox(style));
            restart.AddThemeColorOverride("font_color", new("e9e3cc"));
            restart.Pressed += () => { if (_usingDemo) ResetDemo(); else NavigationRequested?.Invoke("collection"); }; _result.AddChild(restart);
        }
        _paper.FrontOwnedBySelf = state.SelfLine.Any(c => c.Zone == "frontline");
        _paper.FrontOccupied = state.SelfLine.Concat(state.EnemyLine).Any(c => c.Zone == "frontline"); _paper.QueueRedraw();
        AddRow(state.EnemyHq, state.EnemyLine.Where(c => c.Zone != "frontline"), 82);
        AddRow(null, state.EnemyLine.Concat(state.SelfLine).Where(c => c.Zone == "frontline"), 266);
        AddRow(state.SelfHq, state.SelfLine.Where(c => c.Zone != "frontline"), 442);
        var hand = state.SelfHand.OrderBy(c => c.SlotIndex).ToArray();
        var spacing = Math.Min(98, 590f / Math.Max(1, hand.Length - 1));
        for (var i = 0; i < hand.Length; i++)
        {
            var relative = i - (hand.Length - 1) / 2f;
            var c = AddCard(hand[i], BattleCardMode.Hand, new(144, 202), new(640 - 72 + relative * spacing, 628 + MathF.Abs(relative) * 7), relative * .042f);
            c.ZIndex = 20 + i;
        }
        // Opponent identities are never synthesized from definitions, only anonymous backs from the public count.
        var enemyCount = Math.Clamp(state.EnemyHandCount ?? 0, 0, 9);
        for (var i = 0; i < enemyCount; i++)
        {
            var rel = i - (enemyCount - 1) / 2f;
            var back = new BattleCard { Size = new(110, 154), Position = new(585 + rel * 51, -90 + MathF.Abs(rel) * 4), Rotation = -rel * .045f, MouseFilter = MouseFilterEnum.Ignore };
            back.PivotOffset = back.Size / 2; back.Bind(null, BattleCardMode.Hidden, _textures); _cardLayer.AddChild(back);
        }
        if (animate && !_clock.ReducedMotion)
        {
            var generation = _generation;
            foreach (var (uid, card) in _cards)
            {
                if (!previous.TryGetValue(uid, out var old)) continue;
                if (old.Mode == BattleCardMode.Hand && card.Mode == BattleCardMode.Field)
                {
                    var ghost = new BattleCard { Position = old.RestPosition, Size = old.Size, Rotation = old.RestRotation, ZIndex = 130, MouseFilter = MouseFilterEnum.Ignore };
                    ghost.Bind(card.View, BattleCardMode.Hand, _textures); ghost.PivotOffset = ghost.Size / 2;
                    _cardLayer.AddChild(ghost); _ghosts.Add(ghost); card.Visible = false; _busy = true;
                    var t = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
                    var time = .42 * _clock.Scale;
                    t.TweenProperty(ghost, "position", card.RestPosition + (card.Size - ghost.Size) / 2, time);
                    t.TweenProperty(ghost, "scale", card.Size / ghost.Size, time);
                    t.TweenProperty(ghost, "rotation", 0f, time); _tweens.Add(t);
                    t.Finished += () => { if (generation != _generation) return; card.Visible = true; ghost.QueueFree(); _ghosts.Remove(ghost); _busy = false; };
                }
                else if (card.RestPosition.DistanceTo(old.RestPosition) > 1)
                {
                    card.Position = old.RestPosition; card.Rotation = old.RestRotation;
                    var t = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                    t.TweenProperty(card, "position", card.RestPosition, .32 * _clock.Scale);
                    t.TweenProperty(card, "rotation", card.RestRotation, .32 * _clock.Scale); _tweens.Add(t);
                }
            }
        }
        UpdateSelection();
    }
    private void AddRow(UiCardView? hq, IEnumerable<UiCardView> units, float y)
    {
        var row = units.OrderBy(c => c.SlotIndex).ToList();
        if (hq is not null) row.Insert(hq.SlotIndex < 0 ? row.Count / 2 : Math.Clamp(hq.SlotIndex, 0, row.Count), hq);
        var width = row.Count * 112 + Math.Max(0, row.Count - 1) * 17;
        for (var i = 0; i < row.Count; i++)
            AddCard(row[i], row[i].IsHq ? BattleCardMode.Hq : BattleCardMode.Field, new(112, 150), new(640 - width / 2f + i * 129, y), 0);
    }
    private BattleCard AddCard(UiCardView view, BattleCardMode mode, Vector2 size, Vector2 at, float rotation)
    {
        var card = new BattleCard { Size = size, Position = at, RestPosition = at, RestRotation = rotation, Rotation = rotation };
        card.PivotOffset = size / 2; card.Bind(view, mode, _textures);
        card.Pressed += CardPressed; card.Hovered += CardHovered;
        _cardLayer.AddChild(card); _cards[view.Uid] = card; return card;
    }
    public void SelectCard(string uid)
    {
        if (_busy || !_cards.TryGetValue(uid, out var card) || card.View?.Visibility != Visibility.Full) return;
        if (card.View.OwnerSide != "self" || card.View.IsHq) return;
        if (card.Mode == BattleCardMode.Hand && !_actions.PlayableUids.Contains(uid))
        { CancelSelection(); ShowDetail(card); Hint(card.View.BlockedReasons.FirstOrDefault() ?? "这张牌当前不可部署"); return; }
        if (card.Mode == BattleCardMode.Field && !_actions.Moves.Any(m => m.Uid == uid) && !_actions.AttackPreviews.Any(p => p.AttackerUid == uid))
        { CancelSelection(); ShowDetail(card); Hint("此单位当前没有可用行动"); return; }
        _selected = uid; _detail.Visible = _inspectStats.Visible = false;
        Hint(card.Mode == BattleCardMode.Hand ? "拖动卡牌到我方支援线部署；右键取消" : "拖动单位到前线移动，或拖到敌方卡牌发动攻击；右键取消");
        UpdateSelection();
    }
    private void CardPressed(BattleCard card, Vector2 viewportPosition)
    {
        if (_busy) return;
        // Actions are drag-only. A click never deploys, moves or attacks; it only selects and previews.
        if (card.View?.OwnerSide == "enemy")
        {
            ShowDetail(card);
            if (_selected is not null && !_actions.AttackPreviews.Any(p => p.AttackerUid == _selected && p.DefenderUid == card.Uid))
                Hint("该目标当前不可选");
            return;
        }
        if (card.View?.IsHq == true) return;
        var position = card.Position; var rotation = card.Rotation;
        FinishMotion(); SelectCard(card.Uid);
        if (_selected != card.Uid) return;
        if (card.Mode == BattleCardMode.Hand) { card.Position = position; card.Rotation = rotation; card.ZIndex = 100; }
        _pressed = card; _pressAt = _canvas.GetGlobalTransformWithCanvas().AffineInverse() * viewportPosition;
        _grabOffset = _pressAt - card.Position; _dragging = false;
    }
    private void CardHovered(BattleCard card, bool on)
    {
        if (_busy) return;
        if (on)
        {
            _hovered = card;
            if (!_busy && !_dragging && card.Mode == BattleCardMode.Hand)
            {
                card.Position = card.RestPosition + new Vector2(0, -90); card.Rotation = 0; card.ZIndex = 100;
            }
            if (_selected is null && !_dragging) ShowDetail(card);
            if (_selected is not null) PreviewTarget(card.Uid);
        }
        else
        {
            if (_hovered == card) _hovered = null;
            if (!_dragging && card.Mode == BattleCardMode.Hand && _pressed != card)
            { card.Position = card.RestPosition; card.Rotation = card.RestRotation; card.ZIndex = 20 + (card.View?.SlotIndex ?? 0); }
            if (_selected is null) _detail.Visible = _inspectStats.Visible = false;
            if (_aim.Preview?.DefenderUid == card.Uid) { _aim.Preview = null; card.Targeted = false; card.QueueRedraw(); }
        }
    }
    private void ShowDetail(BattleCard card)
    {
        if (card.View?.Visibility != Visibility.Full) return;
        UiStyles.Clear(_detail);
        var inspect = new BattleCard { Size = new(180, 252), MouseFilter = MouseFilterEnum.Ignore };
        inspect.Bind(card.View, card.View.IsHq ? BattleCardMode.Hq : BattleCardMode.Inspect, _textures);
        _detail.AddChild(inspect); _detail.Visible = true;
        _inspectStats.Text = card.View.IsHq ? $"总部防御  {card.View.Health}" : $"攻 {card.View.EffectiveAttack}  防 {card.View.Health ?? card.View.EffectiveDefense}  操作 {card.View.EffectiveOpCost}";
        _inspectStats.Visible = true;
    }
    public void PreviewTarget(string uid)
    {
        if (_selected is null || !_cards.TryGetValue(_selected, out var source) || !_cards.TryGetValue(uid, out var target)) return;
        var preview = _actions.AttackPreviews.FirstOrDefault(p => p.AttackerUid == _selected && p.DefenderUid == uid);
        if (preview is null || target.View?.Visibility != Visibility.Full) return;
        foreach (var c in _cards.Values) { c.Targeted = c == target; c.QueueRedraw(); }
        _aim.From = source.Position + source.Size / 2; _aim.To = target.Position + target.Size / 2;
        _aim.Preview = preview; _aim.Moving = false; _aim.Visible = true; _aim.QueueRedraw();
    }
    private void UpdateSelection()
    {
        foreach (var c in _cards.Values)
        {
            c.Selected = c.Uid == _selected; c.Targeted = false;
            c.Available = _actions.PlayableUids.Contains(c.Uid) || _actions.Moves.Any(m => m.Uid == c.Uid) || _actions.AttackPreviews.Any(p => p.AttackerUid == c.Uid);
            c.QueueRedraw();
        }
        _paper.DeployHint = _selected is not null && _actions.PlayableUids.Contains(_selected);
        _paper.MoveHint = _selected is not null && _actions.Moves.Any(m => m.Uid == _selected && m.ToZone == "frontline"); _paper.QueueRedraw();
    }
    public void CancelSelection()
    {
        if (_canvas is null) return;
        if (_pendingPresentation is { } presentation)
        {
            var actions = _pendingPresentationActions ?? new(); ClearPresentation(); _combat.Interrupt();
            Render(presentation.After, actions, false);
        }
        else _sequence?.Interrupt();
        if (_pendingCombat is { } committed)
        {
            var actions = _pendingActions ?? new(); _pendingCombat = null; _pendingActions = null;
            _combat.Interrupt(); Render(committed.After, actions, false);
        }
        else _combat.Interrupt();
        FinishMotion(); _pressed = null; _hovered = null; _dragging = false; _selected = null;
        _aim.Visible = false; _aim.Preview = null; _detail.Visible = _inspectStats.Visible = false;
        UpdateSelection(); _hint.Visible = false;
    }
    private void BoardInput(InputEvent e)
    {
        // Clicking the board never commits an action; dragging is the only way to deploy, move or attack.
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } || _pressed is not null || _busy) return;
        if (_selected is not null) CancelSelection();
    }
    private void TryDrop(Vector2 at)
    {
        if (_selected is null) return;
        var target = _cards.Values.FirstOrDefault(c => c.View?.OwnerSide == "enemy" && new Rect2(c.Position, c.Size).HasPoint(at)
            && _actions.AttackPreviews.Any(p => p.AttackerUid == _selected && p.DefenderUid == c.Uid));
        if (target is not null && _actions.AttacksEnabled) Submit(new AttackUnit(_selected, target.Uid));
        else if (SelfArea.HasPoint(at) && _actions.PlayableUids.Contains(_selected))
        {
            var index = _cards.Values.Count(c => c.View?.OwnerSide == "self" && (c.View.IsHq || c.View.Zone == "support") && c.Mode != BattleCardMode.Hand
                && c.RestPosition.X + c.Size.X / 2 < at.X);
            Submit(new PlayCard(_selected, index));
        }
        else if (FrontArea.HasPoint(at) && _actions.Moves.Any(m => m.Uid == _selected && m.ToZone == "frontline"))
        {
            // The front line is a shared row: the drop point picks the slot, so a unit can enter to the
            // left or the right of whatever already stands there instead of always landing on one side.
            var slot = _cards.Values.Count(c => c.View?.Zone == "frontline" && c.Mode != BattleCardMode.Hand
                && c.RestPosition.X + c.Size.X / 2 < at.X);
            Submit(new MoveUnit(_selected, "frontline", slot));
        }
        else { CancelSelection(); Hint("已取消，卡牌返回原位"); }
    }
    public override void _Input(InputEvent e)
    {
        if (!IsVisibleInTree() || _canvas is null) return;
        if (e is InputEventKey { Keycode: Key.Escape, Pressed: true } || e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
        { CancelSelection(); return; }
        if (e is InputEventMouseMotion motion)
        {
            var at = _canvas.GetGlobalTransformWithCanvas().AffineInverse() * motion.Position;
            if (_pressed is not null && !_busy)
            {
                if (at.DistanceTo(_pressAt) > 8) _dragging = true;
                _detail.Visible = _inspectStats.Visible = false;
                // Only a hand card travels with the cursor. A board unit stays on its slot and the
                // gesture draws an arrow instead: that is how the original separates moving a unit
                // from placing a card.
                if (_dragging && _pressed.Mode == BattleCardMode.Hand)
                {
                    _pressed.Position = at - _grabOffset; _pressed.Rotation = 0; _pressed.ZIndex = 140;
                }
            }
            if (_selected is not null && _cards.TryGetValue(_selected, out var selected) && selected.Mode == BattleCardMode.Field)
            {
                _aim.From = selected.Position + selected.Size / 2;
                if (_hovered is not null && _actions.AttackPreviews.Any(p => p.AttackerUid == _selected && p.DefenderUid == _hovered.Uid)) PreviewTarget(_hovered.Uid);
                else
                {
                    _aim.Preview = null; _aim.To = at; _aim.Visible = true;
                    _aim.Moving = FrontArea.HasPoint(at) && _actions.Moves.Any(m => m.Uid == _selected); _aim.QueueRedraw();
                }
            }
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release && _pressed is not null)
        {
            var wasDragging = _dragging;
            _pressed.Position = _pressed.RestPosition; _pressed.Rotation = _pressed.RestRotation;
            _pressed.ZIndex = _pressed.Mode == BattleCardMode.Hand ? 20 + (_pressed.View?.SlotIndex ?? 0) : 0;
            _pressed = null; _dragging = false;
            if (wasDragging) TryDrop(_canvas.GetGlobalTransformWithCanvas().AffineInverse() * release.Position);
        }
    }
    private void Submit(UiCommand command)
    {
        if (_busy) return;
        if (!_usingDemo) { CommandRequested?.Invoke(command); CancelSelection(); Hint("等待对局状态更新"); return; }
        if (command is AttackUnit attack)
        {
            if (_demo.TryResolveAttack(attack, out var resolution, out _) && resolution is not null)
                _ = PresentCombatAsync(resolution, _demo.Actions);
            return;
        }
        var oldCard = command switch { PlayCard p => _cards.GetValueOrDefault(p.Uid)?.View, MoveUnit m => _cards.GetValueOrDefault(m.Uid)?.View, _ => null };
        if (!_demo.TrySubmit(command, out var message)) { CancelSelection(); Hint("当前无法执行该行动"); return; }
        Render(_demo.State, _demo.Actions, true); Hint(message);
        if (oldCard is not null)
        {
            _sfx.Play(command is PlayCard ? "deploy" : "move", oldCard); Record(oldCard, message);
        }
    }
    private async void EndTurnClicked()
    {
        if (!_actions.CanEndTurn || _busy) return;
        Submit(new EndTurn());
        if (!_usingDemo) return;
        var generation = _generation;
        await ToSignal(GetTree().CreateTimer(1.1), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree() || generation != _generation || !_usingDemo) return;
        _demo.BeginNextTurn(); Render(_demo.State, _demo.Actions, false); Hint("我方回合，资源已刷新");
    }
    private void Hint(string text)
    {
        // Routine targeting/deployment cues are conveyed on the cards and board, not permanent tutorial text.
        if (_hint is null || !(text.StartsWith("当前无法") || text.Contains("不可") || text.StartsWith("等待"))) return;
        _hint.Text = text; _hint.Visible = true;
    }
    private void Record(UiCardView card, string message)
    {
        if (_history.GetChildCount() >= 3) { var oldest = _history.GetChild(0); _history.RemoveChild(oldest); oldest.QueueFree(); }
        foreach (var child in _history.GetChildren().OfType<Control>()) child.Position += new Vector2(0, 76);
        var thumbnail = new BattleCard { Size = new(48, 67), TooltipText = message };
        thumbnail.Bind(card, BattleCardMode.Inspect, _textures); _history.AddChild(thumbnail);
    }
    public void StartScenario(string kind, bool hq = false, string? targetType = null)
    {
        ResetDemo(); _demo.ResetCombatScenario(kind, hq, targetType); Render(_demo.State, _demo.Actions, false);
        var attack = _actions.AttackPreviews.First(p => p.AttackerUid == "self-front" && p.DefenderUid == (hq ? "enemy-hq" : "enemy-0"));
        SelectCard(attack.AttackerUid); PreviewTarget(attack.DefenderUid);
    }
    public async Task PresentCombatAsync(UiCombatResolution supplied, UiBattleActions afterActions)
    {
        if (supplied.MatchId != _state.MatchId || supplied.After.MatchId != supplied.MatchId) return;
        // The presentation accepts resolved state from its adapter; it never calculates combat damage.
        CancelSelection();
        var resolution = UiSnapshots.Freeze(supplied); var actions = UiSnapshots.Freeze(afterActions);
        if (!_cards.TryGetValue(resolution.Attacker.Uid, out var attacker) || !_cards.TryGetValue(resolution.Defender.Uid, out var defender)
            || attacker.View?.Visibility != Visibility.Full || defender.View?.Visibility != Visibility.Full
            || resolution.Attacker.Visibility != Visibility.Full || resolution.Defender.Visibility != Visibility.Full)
        { Render(resolution.After, actions, false); return; }
        _pendingCombat = resolution; _pendingActions = actions; _busy = true; _endTurn.Disabled = true;
        _selfResource.Bind(resolution.After.SelfKredits, resolution.After.SelfMaxKredits, resolution.After.SelfPlayerName);
        _enemyResource.Bind(resolution.After.EnemyKredits, resolution.After.EnemyMaxKredits, resolution.After.EnemyPlayerName);
        var generation = _generation;
        Record(resolution.Attacker, $"{resolution.Attacker.Definition.Name} → {resolution.Defender.Definition.Name}");
        try
        {
            await _combat.PlayAsync(resolution, attacker, defender);
            if (generation != _generation || _pendingCombat != resolution) return;
            _pendingCombat = null; _pendingActions = null;
            Render(resolution.After, actions, true);
        }
        catch (OperationCanceledException) { /* The interrupt path already settled the authoritative after-state. */ }
        catch (Exception e)
        {
            GD.PushError(e.ToString());
            if (generation == _generation) CancelSelection();
        }
    }
    private void PreferencesChanged() { CancelSelection(); }
    public override void _ExitTree() { if (_clock is not null) _clock.Changed -= PreferencesChanged; ClearPresentation(); _pendingCombat = null; _combat.Interrupt(); FinishMotion(); }

    public async Task VerifyAsync()
    {
        // Slam is graded by defense and stretched for aircraft (presentation estimates, not original timings).
        if (BattleSequence.SlamSeconds(1, "infantry") >= BattleSequence.SlamSeconds(7, "infantry")
            || BattleSequence.SlamSeconds(1, "infantry") >= BattleSequence.SlamSeconds(4, "infantry"))
            throw new Exception("Deployment slam is not graded by defense.");
        if (BattleSequence.SlamSeconds(4, "fighter") <= BattleSequence.SlamSeconds(4, "infantry"))
            throw new Exception("Air deployment slam is not longer.");
        ResetDemo();
        var uid = _actions.PlayableUids.First(); var oldHandCount = _state.SelfHand.Count;
        SelectCard(uid); TryDrop(new(640, 555));
        await ToSignal(GetTree().CreateTimer(.55), SceneTreeTimer.SignalName.Timeout);
        if (_state.SelfHand.Count != oldHandCount - 1 || !_cards.TryGetValue(uid, out var deployed) || deployed.Mode != BattleCardMode.Field || _ghosts.Count != 0)
            throw new Exception("Battle deployment/identity/transition verification failed.");
        var move = _actions.Moves.First(); SelectCard(move.Uid); TryDrop(new(640, 350));
        await ToSignal(GetTree().CreateTimer(.4), SceneTreeTimer.SignalName.Timeout);
        if (_state.SelfLine.First(c => c.Uid == move.Uid).Zone != "frontline") throw new Exception("Battle move failed.");
        // Entering the front line must respect the side the card was dropped on, not always the right.
        ResetDemo();
        var entering = _actions.Moves.First().Uid;
        var anchor = _cards.Values.First(c => c.View?.Zone == "frontline");
        SelectCard(entering); TryDrop(new(anchor.RestPosition.X - 30, 350));
        await ToSignal(GetTree().CreateTimer(.4), SceneTreeTimer.SignalName.Timeout);
        var entered = _state.SelfLine.First(c => c.Uid == entering);
        var anchored = _state.SelfLine.First(c => c.Uid == anchor.Uid);
        if (entered.Zone != "frontline" || entered.SlotIndex > anchored.SlotIndex)
            throw new Exception("Front-line entry ignored the side it was dropped on.");
        ResetDemo();
        var preview = _actions.AttackPreviews.First(); SelectCard(preview.AttackerUid); PreviewTarget(preview.DefenderUid);
        if (!_aim.Visible || _aim.Preview != preview) throw new Exception("Battle preview failed.");
        CancelSelection();
        if (_aim.Visible || _selected is not null || _cards.Values.Any(c => c.Position.DistanceTo(c.RestPosition) > .1f)) throw new Exception("Battle cancel failed.");
        var hidden = _state.EnemyLine[0] with { Visibility = Visibility.Hidden };
        ApplyProjection(_state with { MatchId = "hidden-probe", EnemyLine = [hidden] }, new());
        if (_cards[hidden.Uid].Mode != BattleCardMode.Hidden || _cards[hidden.Uid].TooltipText.Length != 0) throw new Exception("Battle hidden card leaked.");
        var hiddenHq = _state.EnemyHq! with { Visibility = Visibility.Hidden, Definition = _state.EnemyHq!.Definition with { Name = "hidden-hq-sentinel" } };
        ApplyProjection(_state with { EnemyHq = hiddenHq }, new());
        if (_cards[hiddenHq.Uid].Mode != BattleCardMode.Hidden) throw new Exception("Hidden HQ identity leaked into HUD.");
        ResetDemo();
        foreach (var left in new[] { true, false })
        {
            var placingUid = _actions.PlayableUids.First();
            await DragFixtureAsync(placingUid, new(left ? 315 : 965, 549));
            var placed = _cards[placingUid]; var headquarters = _cards["self-hq"];
            if (left ? placed.RestPosition.X >= headquarters.RestPosition.X : placed.RestPosition.X <= headquarters.RestPosition.X)
                throw new Exception("Support deployment did not preserve the chosen HQ side.");
            ResetDemo();
        }
        // Exercise the real Godot GUI press/drag/release path, not only the public selection helpers.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var draggingUid = _actions.PlayableUids.First();
        await DragFixtureAsync(draggingUid, new(885, 549));
        if (_state.SelfHand.Any(c => c.Uid == draggingUid)) throw new Exception("Battle GUI drag deployment failed.");
        ResetDemo();
        draggingUid = _actions.PlayableUids.First();
        await DragFixtureAsync(draggingUid, new(1050, 500));
        if (!_state.SelfHand.Any(c => c.Uid == draggingUid) || _selected is not null || _cards[draggingUid].Position != _cards[draggingUid].RestPosition)
            throw new Exception("Battle invalid drop did not restore the hand.");
        var movingUid = _actions.Moves.First().Uid;
        // A click must never commit an action: dragging is the only gesture that moves a unit.
        SelectCard(movingUid);
        await ClickFixtureAsync(new(890, 350));
        await ToSignal(GetTree().CreateTimer(.4), SceneTreeTimer.SignalName.Timeout);
        if (_state.SelfLine.First(c => c.Uid == movingUid).Zone != "support") throw new Exception("A click committed a move.");
        await DragFixtureAsync(movingUid, new(890, 350));
        if (_state.SelfLine.First(c => c.Uid == movingUid).Zone != "frontline") throw new Exception("Battle GUI drag move failed.");
        // A board unit keeps its slot and draws an arrow; a hand card is the one that travels.
        ResetDemo();
        var arrowUid = _actions.Moves.First().Uid;
        await DragBeginFixtureAsync(arrowUid, new(880, 340));
        if (_cards[arrowUid].Position != _cards[arrowUid].RestPosition) throw new Exception("Dragging a board unit moved the card instead of drawing an arrow.");
        if (!_aim.Visible) throw new Exception("Dragging a board unit did not draw an arrow.");
        await DragEndFixtureAsync(new(880, 340));
        if (_state.SelfLine.First(c => c.Uid == arrowUid).Zone != "frontline") throw new Exception("Releasing the arrow did not move the unit.");
        ResetDemo();
        var handUid = _actions.PlayableUids.First();
        await DragBeginFixtureAsync(handUid, new(700, 520));
        if (_cards[handUid].Position == _cards[handUid].RestPosition) throw new Exception("Dragging a hand card did not move the card.");
        await DragEndFixtureAsync(new(700, 520));
        ResetDemo();
        SelectCard(_actions.PlayableUids.First());
        GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, Pressed = true }, true);
        if (_selected is not null) throw new Exception("Battle escape cancellation failed.");
        var interruptUid = _actions.PlayableUids.First(); SelectCard(interruptUid); TryDrop(new(640, 555)); CancelSelection();
        if (_ghosts.Count != 0 || !_cards[interruptUid].Visible || _cards[interruptUid].Position != _cards[interruptUid].RestPosition)
            throw new Exception("Battle transition interruption failed.");
        ResetDemo(); GD.Print("BATTLE_STAGE1_VERIFY_OK deploy move preview hidden gui-drag invalid-drop gui-click escape interrupt");
    }
    private async Task ClickFixtureAsync(Vector2 local)
    {
        var point = _canvas.GetGlobalTransformWithCanvas() * local;
        GetViewport().PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    private async Task DragFixtureAsync(string uid, Vector2 to)
    {
        var card = _cards[uid];
        var start = _canvas.GetGlobalTransformWithCanvas() * (card.RestPosition + new Vector2(card.Size.X / 2, 24));
        var finish = _canvas.GetGlobalTransformWithCanvas() * to;
        GetViewport().PushInput(new InputEventMouseMotion { Position = start, GlobalPosition = start }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().PushInput(new InputEventMouseButton { Position = start, GlobalPosition = start, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().PushInput(new InputEventMouseMotion { Position = finish, GlobalPosition = finish, Relative = finish - start, ButtonMask = MouseButtonMask.Left }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().PushInput(new InputEventMouseButton { Position = finish, GlobalPosition = finish, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await ToSignal(GetTree().CreateTimer(.55), SceneTreeTimer.SignalName.Timeout);
    }
    private async Task DragBeginFixtureAsync(string uid, Vector2 to)
    {
        var card = _cards[uid];
        var start = _canvas.GetGlobalTransformWithCanvas() * (card.RestPosition + new Vector2(card.Size.X / 2, 24));
        var finish = _canvas.GetGlobalTransformWithCanvas() * to;
        GetViewport().PushInput(new InputEventMouseMotion { Position = start, GlobalPosition = start }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().PushInput(new InputEventMouseButton { Position = start, GlobalPosition = start, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().PushInput(new InputEventMouseMotion { Position = finish, GlobalPosition = finish, Relative = finish - start, ButtonMask = MouseButtonMask.Left }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    private async Task DragEndFixtureAsync(Vector2 to)
    {
        var point = _canvas.GetGlobalTransformWithCanvas() * to;
        GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await ToSignal(GetTree().CreateTimer(.5), SceneTreeTimer.SignalName.Timeout);
    }
    public void CapturePreview()
    {
        ResetDemo();
        var p = _actions.AttackPreviews.FirstOrDefault(p => p.DefenderDies) ?? _actions.AttackPreviews.First();
        SelectCard(p.AttackerUid); PreviewTarget(p.DefenderUid);
    }
    public void CaptureHandInspect()
    {
        ResetDemo(); CardHovered(_cards[_state.SelfHand[2].Uid], true);
    }
    public async Task VerifyCombatAsync()
    {
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
        foreach (var type in new[] { "infantry", "tank", "artillery", "fighter", "bomber" })
        {
            StartScenario(type);
            var before = _state; var phases = new List<string>();
            void OnPhase(string phase) => phases.Add(phase);
            _combat.PhaseChanged += OnPhase;
            if (!_demo.TryResolveAttack(new("self-front", "enemy-0"), out var resolution, out _)) throw new Exception("Missing combat fixture.");
            await PresentCombatAsync(resolution!, _demo.Actions);
            _combat.PhaseChanged -= OnPhase;
            if (!phases.Contains("death") || !phases.Contains("settled") || (resolution!.DamageToAttacker > 0 && !phases.Contains("counter")))
                throw new Exception($"Weapon choreography failed: {type}");
            if (_cards.ContainsKey("enemy-0") || _state.SelfKredits != resolution.After.SelfKredits || _cards["self-front"].View?.Health != resolution.AttackerAfter?.Health)
                throw new Exception($"Resolved combat projection failed: {type}");
            if (before.EnemyLine.First(c => c.Uid == "enemy-0").Health != 3 || _combat.LiveEffectCount != 0 || _busy)
                throw new Exception("Combat mutated old state or retained effects.");
        }
        ResetDemo();
        var survives = _actions.AttackPreviews.First(p => !p.DefenderDies && !p.AttackerDies);
        _demo.TryResolveAttack(new(survives.AttackerUid, survives.DefenderUid), out var surviving, out _);
        await PresentCombatAsync(surviving!, _demo.Actions);
        if (_cards[survives.DefenderUid].View?.Health != surviving!.DefenderAfter?.Health
            || _cards[survives.AttackerUid].View?.Health != surviving.AttackerAfter?.Health) throw new Exception("Surviving damage badges did not consume supplied health.");
        ResetDemo();
        var mutual = _actions.AttackPreviews.First(p => p.DefenderDies && p.AttackerDies);
        _demo.TryResolveAttack(new(mutual.AttackerUid, mutual.DefenderUid), out var bothDead, out _);
        await PresentCombatAsync(bothDead!, _demo.Actions);
        if (_cards.ContainsKey(mutual.AttackerUid) || _cards.ContainsKey(mutual.DefenderUid)) throw new Exception("Mutual death projection failed.");
        StartScenario("tank", false, "fighter");
        _demo.TryResolveAttack(new("self-front", "enemy-0"), out var aircraft, out _);
        await PresentCombatAsync(aircraft!, _demo.Actions);
        if (_cards.ContainsKey("enemy-0") || _combat.LiveEffectCount != 0) throw new Exception("Aircraft destruction retained actors/effects.");
        StartScenario("artillery", true);
        _demo.TryResolveAttack(new("self-front", "enemy-hq"), out var hq, out _);
        await PresentCombatAsync(hq!, _demo.Actions);
        if (_state.Phase != "over" || !_result.Visible || _cards.ContainsKey("enemy-hq") || !_endTurn.Disabled) throw new Exception("HQ presentation failed.");
        StartScenario("tank"); _demo.TryResolveAttack(new("self-front", "enemy-0"), out var interrupted, out _);
        var inFlight = PresentCombatAsync(interrupted!, _demo.Actions);
        await ToSignal(GetTree().CreateTimer(.21), SceneTreeTimer.SignalName.Timeout);
        CancelSelection(); await inFlight;
        if (!MatchesProjection(interrupted!.After) || _combat.LiveEffectCount != 0 || _busy || _cards.ContainsKey("enemy-0")) throw new Exception("Interrupted combat did not settle supplied after-state.");
        StartScenario("bomber"); _demo.TryResolveAttack(new("self-front", "enemy-0"), out var stale, out _);
        inFlight = PresentCombatAsync(stale!, _demo.Actions); ResetDemo(); await inFlight;
        if (!_cards.ContainsKey("enemy-0") || _state.SelfKredits != 8 || _combat.LiveEffectCount != 0) throw new Exception("Stale combat mutated reset board.");
        // Production projection: one drag emits a request; it cannot spend Kredits, hurt or delete a card locally.
        StartScenario("tank");
        var projected = _state; ApplyProjection(projected, _actions);
        var sent = new List<UiCommand>(); void OnCommand(UiCommand command) => sent.Add(command);
        CommandRequested += OnCommand;
        await DragFixtureAsync("self-front", _cards["enemy-0"].RestPosition + _cards["enemy-0"].Size / 2);
        CommandRequested -= OnCommand;
        if (sent.Count != 1 || sent[0] is not AttackUnit || _state.SelfKredits != projected.SelfKredits || !_cards.ContainsKey("enemy-0")
            || _state.EnemyLine[0].Health != projected.EnemyLine[0].Health || _combat.IsPlaying)
            throw new Exception("Production UI executed a game rule.");
        ApplyProjection(projected with { MatchId = "unknown-kredits", EnemyKredits = null, EnemyMaxKredits = null }, new());
        if (_enemyResource.Available is not null || _enemyResource.Slots is not null) throw new Exception("Unknown resource was fabricated.");
        ApplyProjection(projected with { MatchId = "unknown-result", Phase = "over", EnemyHq = null }, new());
        if (_result.GetChildren().OfType<Label>().Single().Text != "对局结束") throw new Exception("UI inferred victory without a supplied result.");
        _clock.SetSpeed(AnimClock.Speed.Faster); _clock.SetReducedMotion(true);
        StartScenario("fighter"); _demo.TryResolveAttack(new("self-front", "enemy-0"), out var reduced, out _);
        await PresentCombatAsync(reduced!, _demo.Actions);
        if (!MatchesProjection(reduced!.After) || _combat.LiveEffectCount != 0) throw new Exception("Reduced-motion combat failed.");
        _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); ResetDemo();
        GD.Print("BATTLE_STAGE2_VERIFY_OK weapons=5 counter damage death mutual-death hq interrupt reset external-request unknown-kredits supplied-result reduced-motion effects=0");
    }
    private bool MatchesProjection(UiMatchView expected) => _state.MatchId == expected.MatchId && _state.Phase == expected.Phase
        && _state.SelfKredits == expected.SelfKredits
        && _state.SelfLine.Select(c => (c.Uid, c.Health, c.Zone)).SequenceEqual(expected.SelfLine.Select(c => (c.Uid, c.Health, c.Zone)))
        && _state.EnemyLine.Select(c => (c.Uid, c.Health, c.Zone)).SequenceEqual(expected.EnemyLine.Select(c => (c.Uid, c.Health, c.Zone)))
        && _state.EnemyHq?.Health == expected.EnemyHq?.Health;
    public async Task CaptureCombatFramesAsync(string dir)
    {
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
        foreach (var type in new[] { "infantry", "tank", "artillery", "fighter", "bomber", "spacefighter", "landcruiser", "air-target", "hq" })
        {
            StartScenario(type == "hq" ? "artillery" : type == "air-target" ? "tank" : type, type == "hq", type == "air-target" ? "fighter" : null);
            _demo.TryResolveAttack(new("self-front", type == "hq" ? "enemy-hq" : "enemy-0"), out var resolution, out _);
            var captures = new List<Task>();
            async Task Capture(string phase)
            {
                await ToSignal(GetTree().CreateTimer(phase == "counter" ? .05 : phase == "hq-destroy" ? .26 : .14), SceneTreeTimer.SignalName.Timeout);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng($"{dir}/battle-stage2-{type}-{phase}.png");
            }
            void OnPhase(string phase)
            {
                if (phase is "death" or "hq-destroy" or "counter") captures.Add(Capture(phase));
            }
            _combat.PhaseChanged += OnPhase;
            await PresentCombatAsync(resolution!, _demo.Actions);
            _combat.PhaseChanged -= OnPhase; await Task.WhenAll(captures);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (type == "hq") GetViewport().GetTexture().GetImage().SavePng(dir + "/battle-stage2-hq-settled.png");
        }
        _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); ResetDemo();
    }
}

public partial class BattlePaper : Control
{
    public bool DeployHint { get; set; }
    public bool MoveHint { get; set; }
    public bool FrontOwnedBySelf { get; set; }
    public bool FrontOccupied { get; set; }
    public override void _Draw()
    {
        var seam = new Color("171b16");
        if (FrontOccupied)
        {
            var y = FrontOwnedBySelf ? 426 : 248;
            DrawLine(new(260, y), new(1020, y), seam, 3);
            for (var x = 274; x < 1014; x += 35)
                DrawColoredPolygon([new(x - 10, y), new(x + 10, y), new(x, y + (FrontOwnedBySelf ? 10 : -10))], seam);
        }
        if (DeployHint) Highlight(new(260, 442, 760, 150), "在这里部署");
        if (MoveHint) Highlight(new(260, 266, 760, 150), "进入前线");

    }
    private void Highlight(Rect2 rect, string caption)
    {
        DrawRect(rect, new Color(.76f, .67f, .38f, .06f));
        DrawRect(rect, new Color(.76f, .67f, .38f, .65f), false, 1.5f);
        DrawString(GetThemeDefaultFont(), rect.Position + new Vector2(16, 24), caption, HorizontalAlignment.Left, -1, 13, new Color("ddce97"));
    }
}

