using Godot;
using Kards.Ui.Core;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DeckEditorScreen : VBoxContainer
{
    private DeckConstructionService _service = null!;
    private DeckLibrary _library = null!;
    private TextureCache _textures = null!;
    private ConstructedDeck _draft = new();
    private bool _loading;
    private bool _dirty;
    internal LineEdit DeckName = null!, Search = null!;
    internal OptionButton MainNation = null!, AllyNation = null!;
    internal CheckBox FreeNations = null!, FreeSize = null!, AvailableOnly = null!;
    internal SpinBox TotalCards = null!;
    internal Button SaveButton = null!, StartButton = null!;
    internal GridContainer Grid = null!;
    internal VBoxContainer Rows = null!;
    private Label _count = null!, _status = null!, _pageLabel = null!, _summary = null!, _notice = null!;
    private HBoxContainer _curve = null!;
    private readonly List<OptionButton> _filters = [];
    private Button _prev = null!, _next = null!;
    private int _page;
    private ConfirmationDialog _discard = null!, _clear = null!;
    private Action? _afterDiscard;
    public event Action? Saved;
    public event Action? StartRequested;
    public ConstructedDeck Draft => _draft.Snapshot();
    public void RefreshCatalog() => Render();
    public void Initialize(DeckConstructionService service, DeckLibrary library, TextureCache textures)
    {
        _service = service; _library = library; _textures = textures;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 10);
        var heading = new HBoxContainer(); AddChild(heading);
        heading.AddChild(UiStyles.Label("卡组构筑", 26, UiStyles.Gold));
        DeckName = new LineEdit { PlaceholderText = "卡组名称", MaxLength = 64, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(200, 38) };
        heading.AddChild(DeckName);
        DeckName.TextChanged += text => { if (_loading) return; _draft = _draft with { Name = text }; Changed(false); };
        heading.AddChild(UiStyles.Button("新建", () => Open(new ConstructedDeck())));
        SaveButton = UiStyles.Button("保存", () => SaveDraft()); heading.AddChild(SaveButton);
        StartButton = UiStyles.Button("使用并对战", Start); heading.AddChild(StartButton);
        UiStyles.Primary(StartButton);
        var options = new HBoxContainer(); AddChild(options);
        options.AddChild(UiStyles.Label("主国 / 总部", 13));
        MainNation = NationOption(ConstructionRules.MainNations, false); options.AddChild(MainNation);
        options.AddChild(UiStyles.Label("盟国", 13));
        AllyNation = NationOption(ConstructionRules.MainNations, false); AllyNation.TooltipText = "可以选择盟国而不加入盟国卡牌"; options.AddChild(AllyNation);
        FreeNations = new CheckBox { Text = "不限制主盟国" }; options.AddChild(FreeNations);
        options.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        options.AddChild(UiStyles.Label("总张数（含总部）", 13));
        TotalCards = new SpinBox { MinValue = 6, MaxValue = 512, Step = 1, Value = 40, CustomMinimumSize = new(80, 38) }; options.AddChild(TotalCards);
        FreeSize = new CheckBox { Text = "不限张数" }; options.AddChild(FreeSize);
        options.AddChild(UiStyles.Button("标准规则", () => SetOptions(_draft.Options with { UnrestrictedNations = false, TotalCards = 40, UnrestrictedSize = false })));
        MainNation.ItemSelected += _ => ReadOptions(); AllyNation.ItemSelected += _ => ReadOptions();
        FreeNations.Toggled += _ => ReadOptions(); FreeSize.Toggled += _ => ReadOptions(); TotalCards.ValueChanged += _ => ReadOptions();
        _notice = UiStyles.Label("点击卡牌加入；右侧 − 移除；长按查看完整卡面。总部单独计数。", 13, UiStyles.Dim); AddChild(_notice);
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; AddChild(body);
        body.AddThemeConstantOverride("separation", 18);
        var pool = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; body.AddChild(pool);
        var searchRow = new HBoxContainer(); pool.AddChild(searchRow);
        Search = new LineEdit { PlaceholderText = "搜索卡名、编号、卡面文本", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(180, 38) }; searchRow.AddChild(Search);
        AvailableOnly = new CheckBox { Text = "仅实战可用", ButtonPressed = true }; searchRow.AddChild(AvailableOnly);
        Search.TextChanged += _ => { _page = 0; RenderPool(); }; AvailableOnly.Toggled += _ => { _page = 0; RenderPool(); };
        var filters = new HBoxContainer(); pool.AddChild(filters);
        Filter(filters, "全部国家", service.Cards.Select(c => c.Set), s => s);
        Filter(filters, "全部卡类", service.Cards.Select(c => c.CardType), UiStyles.TypeName);
        Filter(filters, "全部稀有度", service.Cards.Select(c => c.Rarity), UiStyles.RarityName);
        Filter(filters, "全部费用", service.Cards.Select(c => c.Cost?.ToString() ?? ""), s => s + " 费");
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; pool.AddChild(scroll);
        Grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(Grid);
        // Calculate from the parent's available width. Reading the grid's own minimum width
        // would create a feedback loop in which adding a column keeps expanding the layout.
        body.Resized += () => Grid.Columns = Math.Max(1, (int)((body.Size.X - 340 - 18 + 16) / 156));
        var pager = new HBoxContainer(); pool.AddChild(pager);
        _prev = UiStyles.Button("上一页", () => { _page--; RenderPool(); }); pager.AddChild(_prev);
        _pageLabel = UiStyles.Label(""); _pageLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill; _pageLabel.HorizontalAlignment = HorizontalAlignment.Center; pager.AddChild(_pageLabel);
        _next = UiStyles.Button("下一页", () => { _page++; RenderPool(); }); pager.AddChild(_next);
        var deckPanel = new PanelContainer { CustomMinimumSize = new(340, 0) }; body.AddChild(deckPanel);
        deckPanel.AddThemeStyleboxOverride("panel", UiStyles.Box(UiStyles.MenuPanel, UiStyles.MenuLine, 1, 12));
        var deck = new VBoxContainer(); deckPanel.AddChild(deck);
        var top = new HBoxContainer(); deck.AddChild(top);
        _count = UiStyles.Label("", 20, UiStyles.Gold); _count.SizeFlagsHorizontal = SizeFlags.ExpandFill; top.AddChild(_count);
        top.AddChild(UiStyles.Button("清空", () => _clear.PopupCentered()));
        _summary = UiStyles.Label("", 12, UiStyles.Dim); _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart; deck.AddChild(_summary);
        _curve = new HBoxContainer { CustomMinimumSize = new(0, 62) }; deck.AddChild(_curve);
        var deckScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; deck.AddChild(deckScroll);
        Rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; deckScroll.AddChild(Rows);
        _status = UiStyles.Label("", 12); _status.AutowrapMode = TextServer.AutowrapMode.WordSmart; _status.CustomMinimumSize = new(310, 55); deck.AddChild(_status);
        _discard = new ConfirmationDialog { Title = "未保存的修改", DialogText = "当前卡组有未保存的修改。丢弃修改并打开另一副卡组？" }; AddChild(_discard);
        _discard.Confirmed += () => { var next = _afterDiscard; _afterDiscard = null; next?.Invoke(); };
        _discard.Canceled += () => _afterDiscard = null;
        _clear = new ConfirmationDialog { Title = "清空卡组", DialogText = "移除当前卡组中的全部卡牌？保存后才会覆盖已有卡组。" }; AddChild(_clear);
        _clear.Confirmed += () => { _draft = _draft with { CardIds = [] }; Changed(); };
        Load(_library.ActiveDeck ?? new ConstructedDeck());
    }
    private static OptionButton NationOption(IEnumerable<string> nations, bool empty)
    {
        var o = new OptionButton { CustomMinimumSize = new(85, 38) };
        if (empty) { o.AddItem("无盟国"); o.SetItemMetadata(0, ""); }
        foreach (var nation in nations) { o.AddItem(nation); o.SetItemMetadata(o.ItemCount - 1, nation); }
        return o;
    }
    private void Filter(HBoxContainer row, string all, IEnumerable<string> values, Func<string, string> label)
    {
        var o = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        o.AddItem(all); o.SetItemMetadata(0, "");
        foreach (var v in values.Where(v => v.Length > 0).Distinct().OrderBy(v => v)) { o.AddItem(label(v)); o.SetItemMetadata(o.ItemCount - 1, v); }
        o.ItemSelected += _ => { _page = 0; RenderPool(); }; row.AddChild(o); _filters.Add(o);
    }
    private static void Select(OptionButton option, string value)
    {
        for (var i = 0; i < option.ItemCount; i++) if (option.GetItemMetadata(i).AsString() == value) { option.Select(i); return; }
        option.Select(0);
    }
    public void Open(ConstructedDeck deck)
    {
        if (_dirty) { _afterDiscard = () => Load(deck); _discard.PopupCentered(); }
        else Load(deck);
    }
    internal void Load(ConstructedDeck deck)
    {
        _draft = deck.Snapshot(); _loading = true; DeckName.Text = deck.Name;
        Select(MainNation, deck.Options.MainNation); Select(AllyNation, deck.Options.AllyNation);
        FreeNations.ButtonPressed = deck.Options.UnrestrictedNations; FreeSize.ButtonPressed = deck.Options.UnrestrictedSize; TotalCards.Value = deck.Options.TotalCards;
        _loading = false; _dirty = false; _page = 0; _notice.Text = "点击卡牌加入；右侧 − 移除；长按查看完整卡面。总部单独计数。"; Render();
    }
    private void SetOptions(DeckOptions options)
    {
        _loading = true; Select(MainNation, options.MainNation); Select(AllyNation, options.AllyNation);
        FreeNations.ButtonPressed = options.UnrestrictedNations; FreeSize.ButtonPressed = options.UnrestrictedSize; TotalCards.Value = options.TotalCards;
        _loading = false; ReadOptions();
    }
    private void ReadOptions()
    {
        if (_loading) return;
        if (AllyNation.GetItemMetadata(AllyNation.Selected).AsString() == MainNation.GetItemMetadata(MainNation.Selected).AsString())
            AllyNation.Select(MainNation.Selected == 0 ? 1 : 0);
        _draft = _draft with { Options = new DeckOptions { MainNation = MainNation.GetItemMetadata(MainNation.Selected).AsString(), AllyNation = AllyNation.GetItemMetadata(AllyNation.Selected).AsString(), UnrestrictedNations = FreeNations.ButtonPressed, UnrestrictedSize = FreeSize.ButtonPressed, TotalCards = (int)TotalCards.Value } };
        Changed();
    }
    internal bool AddCard(UiCardDefinition card)
    {
        var reason = ConstructionRules.CannotAdd(_draft, card);
        if (reason is null && !_draft.Options.UnrestrictedNations && card.Set == _draft.Options.AllyNation
            && _draft.CardIds.Count(id => _service.Cards.Any(c => c.CardId == id && c.Set == card.Set)) >= 12) reason = "盟国卡牌最多 12 张";
        if (reason is not null) { _notice.Text = reason; return false; }
        _draft = _draft with { CardIds = _draft.CardIds.Append(card.CardId).ToArray() };
        Changed(); return true;
    }
    internal void RemoveCard(string id)
    {
        var list = _draft.CardIds.ToList(); if (!list.Remove(id)) return;
        _draft = _draft with { CardIds = list.ToArray() }; Changed();
    }
    private void Changed(bool pool = true) { _dirty = true; SaveButton.Text = "保存 *"; RenderDeck(); if (pool) RenderPool(); }
    private void Render() { TotalCards.Editable = !FreeSize.ButtonPressed; AllyNation.Disabled = FreeNations.ButtonPressed; SaveButton.Text = _dirty ? "保存 *" : "保存"; RenderDeck(); RenderPool(); }
    private void RenderPool()
    {
        if (Grid is null) return;
        var vals = _filters.Select(f => f.GetItemMetadata(f.Selected).AsString()).ToArray();
        var all = _service.Cards.Where(c => !c.IsToken && (!AvailableOnly.ButtonPressed || _service.PlayableIds.Contains(c.CardId))
            && (FreeNations.ButtonPressed || c.Set == _draft.Options.MainNation || c.Set == _draft.Options.AllyNation)
            && (vals[0].Length == 0 || c.Set == vals[0]) && (vals[1].Length == 0 || c.CardType == vals[1])
            && (vals[2].Length == 0 || c.Rarity == vals[2]) && (vals[3].Length == 0 || c.Cost?.ToString() == vals[3])
            && (Search.Text.Length == 0 || $"{c.Name} {c.CardId} {c.Text}".Contains(Search.Text, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Cost).ThenBy(c => c.Set).ThenBy(c => c.CardId).ToArray();
        const int pageSize = 12;
        var pages = Math.Max(1, (all.Length + pageSize - 1) / pageSize); _page = Math.Clamp(_page, 0, pages - 1);
        _pageLabel.Text = $"{_page + 1} / {pages}  ·  {all.Length} 种卡牌"; _prev.Disabled = _page == 0; _next.Disabled = _page == pages - 1;
        UiStyles.Clear(Grid);
        foreach (var c in all.Skip(_page * pageSize).Take(pageSize))
        {
            var item = new VBoxContainer { CustomMinimumSize = new(140, 0) }; Grid.AddChild(item);
            var card = new CardControl(); card.Bind(c, _textures, 137); item.AddChild(card);
            card.Activated += _ => AddCard(c); card.LongPressed += _ => Inspect(c);
            card.SetState(_draft.CardIds.Contains(c.CardId), false);
            var label = UiStyles.Label(c.Name, 12); label.ClipText = true; label.TooltipText = c.Name; item.AddChild(label);
            var copies = _draft.CardIds.Count(id => id == c.CardId);
            var limit = ConstructionRules.CopyLimit(c.Rarity);
            var b = UiStyles.Button($"{copies}/{limit}  ＋", () => AddCard(c));
            b.Name = "AddCard"; b.Disabled = ConstructionRules.CannotAdd(_draft, c) is not null;
            item.AddChild(b);
            var support = UiStyles.Label(_service.PlayableIds.Contains(c.CardId) ? $"{c.Set} · {UiStyles.RarityName(c.Rarity)}" : "未支持实战 · 可保存草稿", 11, UiStyles.Dim);
            support.ClipText = true; support.TooltipText = support.Text; item.AddChild(support);
        }
        if (all.Length == 0) Grid.AddChild(UiStyles.Label("没有符合筛选条件的卡牌", 16, UiStyles.Dim));
    }
    private void RenderDeck()
    {
        TotalCards.Editable = !FreeSize.ButtonPressed; AllyNation.Disabled = FreeNations.ButtonPressed;
        _count.Text = FreeSize.ButtonPressed ? $"{_draft.CardIds.Length + 1} 张（含总部）" : $"{_draft.CardIds.Length + 1} / {_draft.Options.TotalCards} 张（含总部）";
        var definitions = _service.Cards.ToDictionary(c => c.CardId);
        var chosen = _draft.CardIds.Where(definitions.ContainsKey).Select(id => definitions[id]).ToArray();
        _summary.Text = $"单位 {chosen.Count(c => c.CardType == "unit")} · 指令 {chosen.Count(c => c.CardType == "order")} · 反制 {chosen.Count(c => c.CardType == "counter")}\n"
            + string.Join(" · ", chosen.GroupBy(c => c.Set).Select(g => $"{g.Key} {g.Count()}"));
        UiStyles.Clear(_curve);
        for (var cost = 0; cost <= 7; cost++)
        {
            var n = chosen.Count(c => cost == 7 ? c.Cost >= 7 : c.Cost == cost);
            var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _curve.AddChild(column);
            var max = Math.Max(1, chosen.GroupBy(c => Math.Min(7, c.Cost ?? 0)).Select(g => g.Count()).DefaultIfEmpty(1).Max());
            var chart = new Control { CustomMinimumSize = new(25, 38), TooltipText = $"{cost}{(cost == 7 ? "+" : "")} 费：{n} 张" }; column.AddChild(chart);
            var height = 34f * n / max;
            chart.AddChild(new ColorRect { Position = new(5, 38 - height), Size = new(18, height), Color = UiStyles.Gold, MouseFilter = MouseFilterEnum.Ignore });
            var text = UiStyles.Label($"{cost}{(cost == 7 ? "+" : "")}", 11, UiStyles.Dim); text.HorizontalAlignment = HorizontalAlignment.Center; column.AddChild(text);
        }
        UiStyles.Clear(Rows);
        Rows.AddChild(UiStyles.Label($"总部 · {_draft.Options.MainNation} × 1", 13, UiStyles.Gold));
        foreach (var group in _draft.CardIds.GroupBy(id => id).OrderBy(g => definitions.GetValueOrDefault(g.Key)?.Cost ?? 999).ThenBy(g => g.Key))
        {
            var id = group.Key; var c = definitions.GetValueOrDefault(id);
            var row = new HBoxContainer { Name = "DeckRow" }; Rows.AddChild(row);
            var title = UiStyles.Button($"{c?.Cost?.ToString() ?? "?"}  {c?.Name ?? id}", () => { if (c is not null) Inspect(c); });
            title.SizeFlagsHorizontal = SizeFlags.ExpandFill; title.ClipText = true; title.TooltipText = c?.Text ?? id; title.Alignment = HorizontalAlignment.Left; row.AddChild(title);
            row.AddChild(UiStyles.Label("×" + group.Count(), 13, UiStyles.Gold));
            var minus = UiStyles.Button("−", () => RemoveCard(id)); minus.Name = "RemoveCard"; row.AddChild(minus);
            var plus = UiStyles.Button("+", () => { if (c is not null) AddCard(c); }); plus.Disabled = c is null || ConstructionRules.CannotAdd(_draft, c) is not null; row.AddChild(plus);
        }
        var errors = _service.Validate(_draft);
        StartButton.Disabled = errors.Count > 0 || _library.LoadError is not null;
        SaveButton.Disabled = string.IsNullOrWhiteSpace(_draft.Name) || _library.LoadError is not null;
        _status.Text = errors.Count == 0 ? "可用于对战" : string.Join("\n", errors.Take(2)) + (errors.Count > 2 ? $"\n另有 {errors.Count - 2} 项；悬停查看" : "");
        _status.TooltipText = string.Join("\n", errors); _status.Modulate = errors.Count == 0 ? UiStyles.Gold : new Color("dca58b");
        if (_library.LoadError is not null) _notice.Text = _library.LoadError;
    }
    internal bool SaveDraft()
    {
        try { _library.Save(_draft); _dirty = false; SaveButton.Text = "保存"; _notice.Text = "已保存"; Saved?.Invoke(); return true; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { _notice.Text = e.Message; return false; }
    }
    private void Start()
    {
        if (_service.Validate(_draft).Count > 0 || !SaveDraft()) return;
        try { _library.Activate(_draft.Id); Saved?.Invoke(); StartRequested?.Invoke(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { _notice.Text = e.Message; }
    }
    private void Inspect(UiCardDefinition card)
    {
        var dialog = new AcceptDialog { Title = card.Name, MinSize = new(560, 500), Exclusive = false }; AddChild(dialog);
        var body = new HBoxContainer(); dialog.AddChild(body);
        var face = new CardControl(); face.Bind(card, _textures, 240); body.AddChild(face);
        var text = UiStyles.Label($"{card.Set} · {UiStyles.TypeName(card.CardType)} · {UiStyles.RarityName(card.Rarity)}\n\n{card.Text}\n\n{card.Flavor}", 15);
        text.CustomMinimumSize = new(250, 0); text.AutowrapMode = TextServer.AutowrapMode.WordSmart; text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var scroll = new ScrollContainer { CustomMinimumSize = new(260, 336), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddChild(scroll); scroll.AddChild(text);
        dialog.CloseRequested += () => dialog.QueueFree(); dialog.Canceled += () => dialog.QueueFree(); dialog.Confirmed += () => dialog.QueueFree(); dialog.PopupCentered();
    }
}
