using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class CollectionScreen : VBoxContainer
{
    private CardCatalog _catalog = null!;
    private TextureCache _textures = null!;
    private readonly List<OptionButton> _filters = [];
    private LineEdit _search = null!;
    private GridContainer _grid = null!;
    private VBoxContainer _detail = null!;
    private Label _count = null!, _pageLabel = null!;
    private Button _prev = null!, _next = null!;
    private UiCardDefinition? _selected;
    private int _page;
    private const int PageSize = 18;
    public void Initialize(CardCatalog catalog, TextureCache textures)
    {
        _catalog = catalog;
        _textures = textures;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        var heading = new HBoxContainer();
        AddChild(heading);
        var title = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        heading.AddChild(title);
        title.AddChild(UiStyles.Label("卡牌图鉴", 30));
        title.AddChild(UiStyles.Label("浏览卡池、词条与卡面原文", 14, UiStyles.Dim));
        _count = UiStyles.Label("", 16, UiStyles.Gold);
        heading.AddChild(_count);
        var searchRow = new HBoxContainer();
        AddChild(searchRow);
        _search = new LineEdit { PlaceholderText = "搜索卡名、编号或卡面文本…", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(250, 42) };
        searchRow.AddChild(_search);
        _search.TextChanged += _ => { _page = 0; Render(); };
        searchRow.AddChild(UiStyles.Button("重置筛选", () => { _search.Text = ""; foreach (var f in _filters) f.Select(0); _page = 0; Render(); }));
        var filters = new HBoxContainer();
        AddChild(filters);
        AddFilter(filters, "全部国别", catalog.Cards.Select(c => c.Set), x => x);
        AddFilter(filters, "全部卡类", catalog.Cards.Select(c => c.CardType), UiStyles.TypeName);
        AddFilter(filters, "全部兵种", catalog.Cards.Select(c => c.UnitType), x => x);
        AddFilter(filters, "全部稀有度", catalog.Cards.Select(c => c.Rarity), UiStyles.RarityName);
        AddFilter(filters, "全部词条", catalog.Cards.SelectMany(c => c.Keywords), x => CardControl.KeywordLabel(x, new Dictionary<string, int>()));
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(body);
        var gridSide = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddChild(gridSide);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        gridSide.AddChild(scroll);
        _grid = new GridContainer { Columns = 6, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_grid);
        var pager = new HBoxContainer();
        gridSide.AddChild(pager);
        _prev = UiStyles.Button("← 上一页", () => { _page--; Render(); });
        pager.AddChild(_prev);
        _pageLabel = UiStyles.Label("");
        _pageLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _pageLabel.HorizontalAlignment = HorizontalAlignment.Center;
        pager.AddChild(_pageLabel);
        _next = UiStyles.Button("下一页 →", () => { _page++; Render(); });
        pager.AddChild(_next);
        var detailPanel = new PanelContainer { CustomMinimumSize = new Vector2(318, 0) };
        body.AddChild(detailPanel);
        var detailScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        detailPanel.AddChild(detailScroll);
        _detail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        detailScroll.AddChild(_detail);
        scroll.Resized += () => { _grid.Columns = Math.Max(2, (int)(scroll.Size.X / 153)); };
        Render();
        ShowDetail(catalog.Cards.FirstOrDefault());
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
        o.ItemSelected += _ => { _page = 0; Render(); };
        row.AddChild(o);
        _filters.Add(o);
    }
    private void Render()
    {
        if (_grid is null)
            return;
        var vals = _filters.Select(f => f.GetItemMetadata(f.Selected).AsString()).ToArray();
        var list = _catalog.Filter(_search.Text.Trim(), vals[0], vals[1], vals[2], vals[3], vals[4]).ToArray();
        var pages = Math.Max(1, (list.Length + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);
        _count.Text = $"{list.Length} / {_catalog.Cards.Count} 张";
        _pageLabel.Text = $"{_page + 1} / {pages}";
        _prev.Disabled = _page == 0;
        _next.Disabled = _page == pages - 1;
        UiStyles.Clear(_grid);
        foreach (var def in list.Skip(_page * PageSize).Take(PageSize))
        {
            var item = new VBoxContainer();
            item.AddThemeConstantOverride("separation", 5);
            _grid.AddChild(item);
            var card = new CardControl();
            card.Bind(def, _textures, 137);
            card.SetState(_selected?.CardId == def.CardId, false);
            card.Activated += _ => ShowDetail(def);
            card.LongPressed += _ => ShowDetail(def);
            item.AddChild(card);
            var name = UiStyles.Label(def.Name, 13);
            name.CustomMinimumSize = new Vector2(137, 0);
            name.ClipText = true;
            name.TooltipText = def.Name;
            item.AddChild(name);
            item.AddChild(UiStyles.Label($"{def.Set} · {UiStyles.TypeName(def.CardType)}", 11, UiStyles.Dim));
        }
        if (list.Length == 0)
            _grid.AddChild(UiStyles.Label("没有符合条件的卡牌", 18, UiStyles.Dim));
    }
    public void ShowDetail(UiCardDefinition? card)
    {
        _selected = card;
        UiStyles.Clear(_detail);
        if (card is null)
            return;
        foreach (var item in _grid.GetChildren())
            foreach (var child in item.GetChildren())
                if (child is CardControl c)
                    c.SetState(c.Definition.CardId == card.CardId, false);
        _detail.AddChild(UiStyles.Label(card.Name, 22, UiStyles.Gold));
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
