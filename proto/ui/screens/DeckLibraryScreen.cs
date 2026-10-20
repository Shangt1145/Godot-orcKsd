using Godot;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DeckLibraryScreen : VBoxContainer
{
    private DeckLibrary _library = null!;
    private DeckConstructionService _service = null!;
    internal GridContainer Tiles = null!;
    internal VBoxContainer Details = null!;
    internal Button ImportButton = null!, NewButton = null!;
    private TextureCache _textures = null!;
    private Label _title = null!;
    private string? _selectedId;
    private readonly Dictionary<string, Button> _actions = new();
    public bool PlaySelection { get; private set; }
    internal string? SelectedDeckId => _selectedId;
    private Label _status = null!;
    private ConfirmationDialog _delete = null!;
    private string? _deleteId;
    public event Action<ConstructedDeck>? EditRequested;
    public event Action? NewRequested;
    public event Action? StartRequested;
    public event Action? Changed;
    public void Initialize(DeckLibrary library, DeckConstructionService service, TextureCache textures)
    {
        _library = library; _service = service; _textures = textures; SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 16);
        var title = new HBoxContainer(); AddChild(title);
        _title = MenuStyles.Label("我的卡组", 30, MenuStyles.MenuInk); title.AddChild(_title);
        title.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        ImportButton = MenuStyles.Button("导入代码", ImportDialog); title.AddChild(ImportButton);
        _status = MenuStyles.Label("", 13, MenuStyles.MenuDim); AddChild(_status);
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddThemeConstantOverride("separation", 24); AddChild(body);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; body.AddChild(scroll);
        Tiles = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(Tiles);
        Tiles.AddThemeConstantOverride("h_separation", 18); Tiles.AddThemeConstantOverride("v_separation", 18);
        scroll.Resized += () => Tiles.Columns = Math.Max(1, (int)((scroll.Size.X + 18) / 214));
        var panel = new PanelContainer { CustomMinimumSize = new(336, 0) }; body.AddChild(panel);
        panel.AddThemeStyleboxOverride("panel", MenuStyles.Box(MenuStyles.MenuPanel, MenuStyles.MenuLine, 1, 20));
        var detailScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; panel.AddChild(detailScroll);
        Details = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; detailScroll.AddChild(Details);
        _delete = new ConfirmationDialog { Title = "删除卡组" }; AddChild(_delete);
        _delete.Confirmed += () => Execute(() => { if (_deleteId is not null) _library.Delete(_deleteId); _deleteId = null; });
        Refresh();
    }
    public void Refresh()
    {
        var decks = _library.Decks;
        if (!decks.Any(d => d.Id == _selectedId)) _selectedId = _library.ActiveId ?? decks.FirstOrDefault()?.Id;
        _title.Text = PlaySelection ? "选择对战卡组" : "我的卡组";
        _status.Text = _library.LoadError ?? $"{decks.Count} 副卡组  ·  点击卡组查看详情";
        MenuStyles.Clear(Tiles);
        foreach (var deck in decks.OrderByDescending(d => d.Id == _library.ActiveId).ThenBy(d => d.Name))
        {
            var errors = _service.Validate(deck); var active = deck.Id == _library.ActiveId;
            var tile = MenuStyles.Button("", () => SelectDeck(deck.Id)); tile.Name = "Deck_" + deck.Id; tile.SetMeta("deck_id", deck.Id);
            tile.CustomMinimumSize = new(196, 320); tile.SizeFlagsHorizontal = SizeFlags.ExpandFill; tile.TooltipText = deck.Name;
            var tileStyle = MenuStyles.Box(new("253027"), _selectedId == deck.Id ? MenuStyles.Gold : MenuStyles.Line, 2, 12);
            tileStyle.BorderWidthBottom = _selectedId == deck.Id ? 4 : 1;
            tile.AddThemeStyleboxOverride("normal", tileStyle); Tiles.AddChild(tile);
            var contents = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; tile.AddChild(contents);
            contents.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); contents.OffsetLeft = contents.OffsetTop = 12; contents.OffsetRight = contents.OffsetBottom = -12; contents.AddThemeConstantOverride("separation", 8);
            var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore }; contents.AddChild(center);
            var cover = new MenuDeckCover(); cover.Initialize(_textures, deck.Options.MainNation, 158); center.AddChild(cover);
            var name = MenuStyles.Label(deck.Name, 17, MenuStyles.MenuInk); name.ClipText = true; name.HorizontalAlignment = HorizontalAlignment.Center; name.MouseFilter = MouseFilterEnum.Ignore; contents.AddChild(name);
            var summary = MenuStyles.Label($"{deck.CardIds.Length + 1} 张  ·  " + (active ? "当前对战" : errors.Count == 0 ? "可对战" : "草稿"), 12, active ? MenuStyles.Gold : MenuStyles.MenuDim); summary.HorizontalAlignment = HorizontalAlignment.Center; summary.MouseFilter = MouseFilterEnum.Ignore; contents.AddChild(summary);
        }
        NewButton = MenuStyles.Button("＋\n新建卡组", () => NewRequested?.Invoke()); NewButton.Name = "NewDeckTile"; NewButton.CustomMinimumSize = new(196, 320); NewButton.AddThemeFontSizeOverride("font_size", 24); Tiles.AddChild(NewButton);
        RenderDetails();
    }
    public void SetPlaySelection(bool enabled) { PlaySelection = enabled; if (Tiles is not null) Refresh(); }
    internal void SelectDeck(string id) { if (!_library.Decks.Any(d => d.Id == id)) return; _selectedId = id; Refresh(); }
    internal Button ActionButton(string id) => _actions[id];
    internal Button TileButton(string id) => Tiles.GetChildren().OfType<Button>().Single(b => b.HasMeta("deck_id") && b.GetMeta("deck_id").AsString() == id);
    private void RenderDetails()
    {
        MenuStyles.Clear(Details); _actions.Clear();
        var deck = _library.Decks.FirstOrDefault(d => d.Id == _selectedId);
        if (deck is null)
        {
            var empty = MenuStyles.Label("创建第一副卡组，\n或者导入已有卡组代码。", 17, MenuStyles.MenuDim); empty.AutowrapMode = TextServer.AutowrapMode.WordSmart; empty.CustomMinimumSize = new(290, 0); Details.AddChild(empty); return;
        }
        var errors = _service.Validate(deck); var active = deck.Id == _library.ActiveId;
        Details.AddChild(MenuStyles.Label(active ? "当前对战卡组" : "卡组详情", 12, MenuStyles.Gold));
        var art = new ArchiveArtwork { CustomMinimumSize = new(0, 150) };
        art.Initialize(_textures, faces: deck.CardIds.Distinct().Select(id => _service.Cards.FirstOrDefault(c => c.CardId == id)?.ArtPath ?? "")); Details.AddChild(art);
        var name = MenuStyles.Label(deck.Name, 23, MenuStyles.MenuInk); name.AutowrapMode = TextServer.AutowrapMode.WordSmart; name.CustomMinimumSize = new(290, 0); Details.AddChild(name);
        var ruleText = deck.Options.UnrestrictedNations ? "混编 · 不限制主盟国" : $"{deck.Options.MainNation} / {deck.Options.AllyNation}";
        ruleText += deck.Options.UnrestrictedSize ? " · 不限张数" : $" · 目标 {deck.Options.TotalCards} 张";
        var rules = MenuStyles.Label($"{deck.CardIds.Length + 1} 张（含总部）\n" + ruleText, 13, MenuStyles.MenuDim); rules.AutowrapMode = TextServer.AutowrapMode.WordSmart; Details.AddChild(rules);
        var packs = MenuStyles.Label("卡包：" + string.Join("、", DeckCardPacks.Selected(deck)
            .Select(id => _service.Packs.FirstOrDefault(p => p.Id == id)?.Name ?? "已移除的卡包")), 13, MenuStyles.MenuDim);
        packs.AutowrapMode = TextServer.AutowrapMode.WordSmart; Details.AddChild(packs);
        var validity = MenuStyles.Label(errors.Count == 0 ? "准备就绪" : string.Join("\n", errors.Take(2)), 12, errors.Count == 0 ? new Color("bdcc9c") : new("dca58b")); validity.AutowrapMode = TextServer.AutowrapMode.WordSmart; validity.TooltipText = string.Join("\n", errors); Details.AddChild(validity);
        var ready = errors.Count == 0 && _library.LoadError is null;
        var start = AddAction(Details, "start", "开始对战", () => Execute(() => { _library.Activate(deck.Id); StartRequested?.Invoke(); })); start.Disabled = !ready; MenuStyles.Primary(start);
        AddAction(Details, "edit", "编辑卡组", () => EditRequested?.Invoke(deck));
        var use = AddAction(Details, "activate", active ? "已设为首页卡组" : "设为首页卡组", () => Execute(() => _library.Activate(deck.Id))); use.Disabled = active || !ready;
        var utilities = new HBoxContainer(); utilities.AddThemeConstantOverride("separation", 6); Details.AddChild(utilities);
        AddAction(utilities, "copy", "复制", () => Execute(() => { var copy = deck with { Id = Guid.NewGuid().ToString("N"), Name = deck.Name[..Math.Min(deck.Name.Length, 59)] + " 副本" }; _library.Save(copy); _selectedId = copy.Id; }));
        AddAction(utilities, "export", "导出代码", () => ExportDialog(deck));
        AddAction(utilities, "delete", "删除", () => { _deleteId = deck.Id; _delete.DialogText = $"删除“{deck.Name}”？"; _delete.PopupCentered(); });
    }
    private Button AddAction(Container parent, string id, string label, Action action)
    {
        var button = MenuStyles.Button(label, action); button.Name = "DeckAction_" + id; _actions[id] = button; parent.AddChild(button); return button;
    }
    private void Execute(Action action)
    {
        try { action(); Refresh(); Changed?.Invoke(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { _status.Text = e.Message; }
    }
    private void ExportDialog(ConstructedDeck deck)
    {
        var dialog = new AcceptDialog { Title = "OrC-KSD 卡组代码", MinSize = new(650, 240) }; AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        box.AddChild(MenuStyles.Label("包含卡牌、主盟国与张数选项。此代码用于本项目。", 14, MenuStyles.Dim));
        var code = DeckLibrary.Export(deck);
        var text = new TextEdit { Text = code, Editable = false, CustomMinimumSize = new(600, 140), WrapMode = TextEdit.LineWrappingMode.Boundary, SizeFlagsVertical = SizeFlags.ExpandFill }; box.AddChild(text);
        var copy = MenuStyles.Button("复制到剪贴板", () => DisplayServer.ClipboardSet(code)); box.AddChild(copy);
        dialog.Confirmed += () => dialog.QueueFree(); dialog.Canceled += () => dialog.QueueFree(); dialog.CloseRequested += () => dialog.QueueFree(); dialog.PopupCentered();
    }
    private void ImportDialog()
    {
        var dialog = new ConfirmationDialog { Title = "导入 OrC-KSD 卡组", MinSize = new(650, 300), DialogHideOnOk = false }; AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        box.AddChild(MenuStyles.Label("粘贴 ORK1: 开头的代码；导入后作为新草稿保存。", 14, MenuStyles.Dim));
        var text = new TextEdit { CustomMinimumSize = new(600, 150), WrapMode = TextEdit.LineWrappingMode.Boundary, SizeFlagsVertical = SizeFlags.ExpandFill }; box.AddChild(text);
        var error = MenuStyles.Label("", 13, new("dca58b")); error.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(error);
        // Keep the dialog open on invalid input so the pasted code can be corrected.
        dialog.Confirmed += () =>
        {
            try { var deck = DeckLibrary.Import(text.Text); _library.Save(deck); Refresh(); Changed?.Invoke(); dialog.QueueFree(); EditRequested?.Invoke(deck); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { error.Text = e.Message; }
        };
        dialog.CloseRequested += () => dialog.QueueFree(); dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
    }
}
