using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class CollectionScreen : VBoxContainer
{
    private CardCatalog _catalog = null!;
    private Func<string, bool> _canPlay = _ => true;
    private IReadOnlyList<UiCardDefinition> Cards => _catalog.Cards.Where(c => _canPlay(c.CardId)
        && !c.IsToken && ConstructionRules.CopyLimit(c.Rarity) > 0).ToArray();
    private TextureCache _textures = null!;
    private readonly List<OptionButton> _filters = [];
    private LineEdit _search = null!;
    private GridContainer _grid = null!;
    private VBoxContainer _detail = null!;
    private Label _count = null!;
    private ScrollContainer _scroll = null!;
    private Control _scrollContent = null!;
    private HBoxContainer _filterRow = null!;
    private Button _filterToggle = null!;
    private UiCardDefinition? _selected;
    private UiCardDefinition[] _results = [];
    private int _firstRow = -1, _lastRow = -1;
    private const int RowStride = 256, RowGap = 16, Overscan = 1;
    private VBoxContainer? _packPage;
    private Control? _cardBody;
    private Button? _backButton;
    private Label? _heading;
    private string? _activePack; // null = pack list page; "builtin:taikong" = source pool; "pack/<id>" = a pack
    private OptionButton? _expansionFilter; // pack-scoped source picker: base pack vs its expansions
    private string? _expansionFamilyId; // which family the source picker currently lists
    internal ScrollContainer CardScroll => _scroll;
    internal LineEdit Search => _search;
    internal IReadOnlyList<UiCardDefinition> Results => _results;
    internal IEnumerable<CardControl> VisibleCards => _grid.GetChildren().OfType<VBoxContainer>()
        .SelectMany(item => item.GetChildren().OfType<CenterContainer>()).SelectMany(center => center.GetChildren().OfType<CardControl>());
    internal string? SelectedCardId => _selected?.CardId;
    public void RefreshCatalog()
    {
        var values = new IEnumerable<string>[] { Cards.Select(c => c.Set), Cards.Select(c => c.CardType),
            Cards.Select(c => c.UnitType), Cards.Select(c => c.Rarity), Cards.SelectMany(c => c.Keywords) };
        for (var i = 0; i < _filters.Count; i++)
        {
            var filter = _filters[i]; var selected = filter.GetItemMetadata(filter.Selected).AsString();
            var title = filter.GetItemText(0); filter.Clear(); filter.AddItem(title); filter.SetItemMetadata(0, "");
            foreach (var value in values[i].Where(x => x.Length > 0).Distinct().OrderBy(x => x))
            {
                filter.AddItem(i switch { 1 => UiStyles.TypeName(value), 3 => UiStyles.RarityName(value),
                    4 => CardControl.KeywordLabel(value, new Dictionary<string, int>()), _ => value });
                filter.SetItemMetadata(filter.ItemCount - 1, value);
                if (selected == value) filter.Select(filter.ItemCount - 1);
            }
        }
        RenderPackPage();
        Render();
        if (_activePack is null)
            ShowDetail(Cards.FirstOrDefault(c => c.CardId == _selected?.CardId) ?? Cards.FirstOrDefault());
    }
    public void Initialize(CardCatalog catalog, TextureCache textures, Func<string, bool>? canPlay = null)
    {
        _catalog = catalog;
        _canPlay = canPlay ?? (_ => true);
        _textures = textures;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        var heading = new HBoxContainer();
        AddChild(heading);
        _backButton = UiStyles.Button("← 卡包", () => OpenPack(null));
        _backButton.Visible = false;
        heading.AddChild(_backButton);
        _heading = UiStyles.Label("收藏", 30, UiStyles.MenuInk);
        heading.AddChild(_heading);
        _search = new LineEdit { PlaceholderText = "搜索卡名、编号或卡面文本…", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(250, 42) };
        heading.AddChild(_search);
        _search.TextChanged += _ => Render();
        _filterToggle = UiStyles.Button("筛选", () => { _filterRow.Visible = !_filterRow.Visible; _filterToggle.SetPressedNoSignal(_filterRow.Visible); }); _filterToggle.ToggleMode = true; heading.AddChild(_filterToggle);
        heading.AddChild(UiStyles.Button("重置", () => { foreach (var f in _filters) f.Select(0); _expansionFilter!.Select(0); _search.Text = ""; Render(); }));
        _count = UiStyles.Label("", 14, UiStyles.Gold); heading.AddChild(_count);
        var filters = new HBoxContainer();
        _filterRow = filters; filters.Visible = false;
        AddChild(filters);
        AddFilter(filters, "全部国别", catalog.Cards.Select(c => c.Set), x => x);
        AddFilter(filters, "全部卡类", catalog.Cards.Select(c => c.CardType), UiStyles.TypeName);
        AddFilter(filters, "全部兵种", catalog.Cards.Select(c => c.UnitType), x => x);
        AddFilter(filters, "全部稀有度", catalog.Cards.Select(c => c.Rarity), UiStyles.RarityName);
        AddFilter(filters, "全部词条", catalog.Cards.SelectMany(c => c.Keywords), x => CardControl.KeywordLabel(x, new Dictionary<string, int>()));
        // Pack-scoped source picker: expansions fold into their base pack, so they surface here —
        // never as front-page tiles (2026-10-13 用户拍板：扩展包只是卡池内筛选，不单独设入口).
        _expansionFilter = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(150, 38) };
        _expansionFilter.AddItem("全部来源");
        _expansionFilter.SetItemMetadata(0, "");
        _expansionFilter.ItemSelected += _ => Render();
        _expansionFilter.Visible = false;
        filters.AddChild(_expansionFilter);
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(body);
        _cardBody = body;
        _packPage = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _packPage.AddThemeConstantOverride("separation", 12);
        AddChild(_packPage);
        var gridSide = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddChild(gridSide);
        _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.ShowAlways };
        gridSide.AddChild(_scroll);
        // The scroll extent represents every result; only nearby rows hold card actors/textures.
        _scrollContent = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Pass };
        _scroll.AddChild(_scrollContent);
        _grid = new GridContainer { Columns = 6, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 16); _grid.AddThemeConstantOverride("v_separation", RowGap);
        _scrollContent.AddChild(_grid); _grid.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        _scrollContent.Resized += () => _grid.Size = new(_scrollContent.Size.X, _grid.Size.Y);
        var detailPanel = new PanelContainer { CustomMinimumSize = new Vector2(318, 0) };
        body.AddChild(detailPanel);
        var detailScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        detailPanel.AddChild(detailScroll);
        _detail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        detailScroll.AddChild(_detail);
        _scroll.Resized += ResizeGrid;
        RefreshCatalog();
    }

    /// <summary>A pack as shown on the front page: display name, card id prefix selector, count.</summary>
    private sealed record PackTile(string Name, string? Filter, int Count);

    /// <summary>A base pack plus the expansions folded into it — expansions are a filter, never a tile.</summary>
    private sealed record PackFamily(string Id, string Name, IReadOnlyList<string> ExpansionIds);

    private static IReadOnlyList<PackFamily> PackFamilies()
    {
        var packs = OrcEngineHost.PackDescriptors;
        return [.. packs.Where(p => p.BasePackId is null)
            .Select(p => new PackFamily(p.Id, p.Name,
                [.. packs.Where(e => e.BasePackId == p.Id).Select(e => e.Id)]))
            .OrderBy(f => f.Name, StringComparer.Ordinal)];
    }

    /// <summary>Card count for one family: the base pack's cards plus every expansion's.</summary>
    private int ScopedCount(CardCatalog catalog, PackFamily family) =>
        Cards.Count(c => c.CardId.StartsWith("pack/" + family.Id + "/", StringComparison.Ordinal)
            || family.ExpansionIds.Any(e => c.CardId.StartsWith("pack/" + e + "/", StringComparison.Ordinal)));

    private IReadOnlyList<PackTile> PackTiles(CardCatalog catalog)
    {
        var cards = Cards;
        var source = cards.Count(c => DeckCardPacks.Of(c.CardId) == "source");
        var tiles = new List<PackTile>();
        if (source > 0) tiles.Add(new("太空战争", "builtin:taikong", source));
        // Only base packs get tiles; their expansions are already part of the pack's pool and
        // surface as the source picker inside the pack page.
        tiles.AddRange(PackFamilies()
            .Select(f => new PackTile(f.Name, "pack/" + f.Id, ScopedCount(catalog, f)))
            .Where(t => t.Count > 0));
        foreach (var (id, name) in new[] { ("diy", "我的 DIY 卡牌"), ("orc", "已导入卡牌") })
        {
            var count = cards.Count(c => DeckCardPacks.Of(c.CardId) == id);
            if (count > 0) tiles.Add(new(name, id, count));
        }
        return tiles;
    }

    internal void OpenPack(string? pack)
    {
        _activePack = pack;
        Render();
    }

    /// <summary>Verification surface for the pack front page: name, selector, count per tile.</summary>
    internal IReadOnlyList<(string Name, string? Filter, int Count)> PackFrontTiles() =>
        PackTiles(_catalog).Select(t => (t.Name, t.Filter, t.Count)).ToArray();

    private void RenderPackPage()
    {
        if (_packPage is null) return;
        UiStyles.Clear(_packPage);
        _packPage.AddChild(UiStyles.Label("选择一个卡包查看它的卡池", 15, UiStyles.Dim));
        var grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12); grid.AddThemeConstantOverride("v_separation", 12);
        _packPage.AddChild(grid);
        foreach (var tile in PackTiles(_catalog))
        {
            var button = new Button { CustomMinimumSize = new Vector2(220, 96), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            box.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            box.AddThemeConstantOverride("separation", 6);
            var name = UiStyles.Label(tile.Name, 20, UiStyles.MenuInk);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            box.AddChild(name);
            var count = UiStyles.Label($"{tile.Count} 张卡", 13, UiStyles.Dim);
            count.HorizontalAlignment = HorizontalAlignment.Center;
            box.AddChild(count);
            button.AddChild(box);
            var captured = tile;
            button.Pressed += () => OpenPack(captured.Filter);
            grid.AddChild(button);
        }
        if (PackTiles(_catalog).Count == 0)
            _packPage.AddChild(UiStyles.Label("没有可浏览的卡包", 18, UiStyles.Dim));
    }
    private void AddFilter(HBoxContainer row, string all, IEnumerable<string> values, Func<string, string> label)
    {
        var o = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(150, 38) };
        o.AddItem(all);
        o.SetItemMetadata(0, "");
        foreach (var v in values.Where(x => x.Length > 0).Distinct().OrderBy(x => x))
        {
            o.AddItem(label(v));
            o.SetItemMetadata(o.ItemCount - 1, v);
        }
        o.ItemSelected += _ => Render();
        row.AddChild(o);
        _filters.Add(o);
    }
    private void Render()
    {
        if (_grid is null)
            return;
        var inPack = _activePack is not null;
        _backButton!.Visible = inPack;
        _packPage!.Visible = !inPack;
        _cardBody!.Visible = inPack;
        if (!inPack)
        {
            _heading!.Text = "收藏";
            _count.Text = $"{PackTiles(_catalog).Count} 个卡包";
            if (_expansionFilter is not null) _expansionFilter.Visible = false;
            return;
        }
        var family = PackFamilies().FirstOrDefault(f => "pack/" + f.Id == _activePack);
        var packName = _activePack switch
        {
            "builtin:taikong" => "太空战争",
            "diy" => "我的 DIY 卡牌",
            "orc" => "已导入卡牌",
            _ when family is not null => family.Name,
            _ => _catalog.Cards.FirstOrDefault(c => c.CardId.StartsWith(_activePack + "/", StringComparison.Ordinal))?.Set
                is { } set && set.StartsWith("pack:", StringComparison.Ordinal) ? set["pack:".Length..] : _activePack!,
        };
        _heading!.Text = $"收藏 · {packName}";
        // The source picker lists this family's base pool and expansions; rebuild only on family change
        // so the user's selection survives re-renders.
        if (_expansionFilter is not null && family?.Id != _expansionFamilyId)
        {
            _expansionFamilyId = family?.Id;
            _expansionFilter.Clear();
            _expansionFilter.AddItem("全部来源");
            _expansionFilter.SetItemMetadata(0, "");
            if (family is not null)
            {
                _expansionFilter.AddItem(family.Name);
                _expansionFilter.SetItemMetadata(_expansionFilter.ItemCount - 1, "pack/" + family.Id);
                foreach (var expId in family.ExpansionIds)
                {
                    _expansionFilter.AddItem(OrcEngineHost.PackDescriptors.First(p => p.Id == expId).Name);
                    _expansionFilter.SetItemMetadata(_expansionFilter.ItemCount - 1, "pack/" + expId);
                }
            }
        }
        if (_expansionFilter is not null)
            _expansionFilter.Visible = family is { ExpansionIds.Count: > 0 };
        var vals = _filters.Select(f => f.GetItemMetadata(f.Selected).AsString()).ToArray();
        IEnumerable<UiCardDefinition> query = _catalog.Filter(_search.Text.Trim(), vals[0], vals[1], vals[2], vals[3], vals[4])
            .Where(c => _canPlay(c.CardId) && !c.IsToken && ConstructionRules.CopyLimit(c.Rarity) > 0);
        query = _activePack switch
        {
            "builtin:taikong" => query.Where(c => DeckCardPacks.Of(c.CardId) == "source"),
            _ when family is not null => query.Where(c => c.CardId.StartsWith("pack/" + family.Id + "/", StringComparison.Ordinal)
                || family.ExpansionIds.Any(e => c.CardId.StartsWith("pack/" + e + "/", StringComparison.Ordinal))),
            var pack => query.Where(c => c.CardId.StartsWith(pack + "/", StringComparison.Ordinal))
        };
        if (_expansionFilter is { Visible: true } picker)
        {
            var source = picker.GetItemMetadata(picker.Selected).AsString();
            if (source.Length > 0)
                query = query.Where(c => c.CardId.StartsWith(source + "/", StringComparison.Ordinal));
        }
        _results = query.ToArray();
        ShowDetail(_results.FirstOrDefault(c => c.CardId == _selected?.CardId) ?? _results.FirstOrDefault());
        _count.Text = $"{_results.Length} 张";
        var filterCount = vals.Count(v => v.Length > 0); _filterToggle.Text = filterCount == 0 ? "筛选" : $"筛选 · {filterCount}";
        _scroll.ScrollVertical = 0;
        SetScrollExtent(); UpdateWindow(true);
    }
    private void SetScrollExtent() => _scrollContent.CustomMinimumSize = new(137, Math.Max(1, (_results.Length + _grid.Columns - 1) / _grid.Columns) * RowStride);
    private void ResizeGrid()
    {
        var columns = Math.Max(1, (int)((_scroll.Size.X - 20 + 16) / 153));
        if (_grid.Columns != columns)
        {
            var firstCard = _scroll.ScrollVertical / RowStride * _grid.Columns;
            _grid.Columns = columns; SetScrollExtent();
            _scroll.ScrollVertical = firstCard / columns * RowStride;
        }
        UpdateWindow(true);
    }
    public override void _Process(double delta)
    {
        if (IsVisibleInTree() && _grid is not null) UpdateWindow();
    }
    private void UpdateWindow(bool force = false)
    {
        var totalRows = (_results.Length + _grid.Columns - 1) / _grid.Columns;
        var first = Math.Max(0, _scroll.ScrollVertical / RowStride - Overscan);
        var last = Math.Min(totalRows, (int)Math.Ceiling((_scroll.ScrollVertical + _scroll.Size.Y) / RowStride) + Overscan);
        if (!force && first == _firstRow && last == _lastRow) return;
        _firstRow = first; _lastRow = last;
        UiStyles.Clear(_grid);
        _grid.Position = new(0, first * RowStride);
        _grid.Size = new(_scrollContent.Size.X, Math.Max(1, last - first) * RowStride - RowGap);
        foreach (var def in _results.Skip(first * _grid.Columns).Take(Math.Max(0, last - first) * _grid.Columns))
        {
            var item = new VBoxContainer { CustomMinimumSize = new(137, RowStride - RowGap), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            item.AddThemeConstantOverride("separation", 5);
            _grid.AddChild(item);
            var center = new CenterContainer(); item.AddChild(center);
            var card = new CardControl();
            card.Bind(def, _textures, 137);
            card.SetState(_selected?.CardId == def.CardId, false);
            card.Activated += _ => ShowDetail(def);
            card.LongPressed += _ => ShowDetail(def);
            center.AddChild(card);
            var name = UiStyles.Label(def.Name, 13);
            name.CustomMinimumSize = new Vector2(137, 0);
            name.ClipText = true;
            name.TooltipText = def.Name;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            item.AddChild(name);
            var meta = UiStyles.Label($"{def.Set} · {UiStyles.TypeName(def.CardType)}", 11, UiStyles.Dim);
            meta.ClipText = true; meta.HorizontalAlignment = HorizontalAlignment.Center; item.AddChild(meta);
        }
        if (_results.Length == 0)
            _grid.AddChild(UiStyles.Label("没有符合条件的卡牌", 18, UiStyles.Dim));
    }
    public void ShowDetail(UiCardDefinition? card)
    {
        _selected = card;
        UiStyles.Clear(_detail);
        if (card is null)
            return;
        foreach (var visible in VisibleCards) visible.SetState(visible.Definition.CardId == card.CardId, false);
        var name = UiStyles.Label(card.Name, 22, UiStyles.Gold); name.AutowrapMode = TextServer.AutowrapMode.WordSmart; name.CustomMinimumSize = new(280, 0); _detail.AddChild(name);
        if (card.NameEn.Length > 0)
            _detail.AddChild(UiStyles.Label(card.NameEn, 14, UiStyles.Dim));
        var full = new CardControl();
        full.Bind(card, _textures, 280);
        _detail.AddChild(full);
        _detail.AddChild(UiStyles.Label($"{card.Set}  /  {UiStyles.TypeName(card.CardType)}  /  {UiStyles.RarityName(card.Rarity)}", 13, UiStyles.Dim));
        var statLine = $"费用 {Stat(card.Cost)}   攻击 {Stat(card.BaseAttack)}   防御 {Stat(card.BaseDefense)}   操作 {Stat(card.BaseOpCost)}";
        _detail.AddChild(UiStyles.Label("基础数值", 12, UiStyles.Dim));
        var stats = UiStyles.Label(statLine, 13);
        stats.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _detail.AddChild(stats);
        if (card.HasVariableStats)
            _detail.AddChild(UiStyles.Label("含可变数值，以卡面说明为准", 12, UiStyles.Gold));
        if (card.Keywords.Count > 0)
        {
            var kw = UiStyles.Label(string.Join("  ", card.Keywords.Select(k => CardControl.KeywordLabel(k, card.KeywordValues))), 14, UiStyles.Gold);
            kw.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _detail.AddChild(kw);
        }
        var text = UiStyles.Label(card.Text, 15);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _detail.AddChild(text);
        if (card.Flavor.Length > 0)
        {
            var flavor = UiStyles.Label(card.Flavor, 12, UiStyles.Dim);
            flavor.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _detail.AddChild(flavor);
        }
        var id = UiStyles.Label(card.CardId, 11, UiStyles.Dim);
        id.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
        _detail.AddChild(id);
    }
    private static string Stat(int? value) => value?.ToString() ?? "—";
}
