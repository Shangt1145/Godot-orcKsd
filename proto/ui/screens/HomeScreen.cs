using Godot;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class HomeScreen : VBoxContainer
{
    private DeckLibrary _library = null!;
    private DeckConstructionService _service = null!;
    private TextureCache _textures = null!;
    private VBoxContainer _deckDetail = null!;
    private Button _decks = null!, _collection = null!, _workshop = null!;
    internal Button PlayButton = null!;
    internal MenuHero Hero = null!;
    public event Action? PlayRequested;
    public event Action? StartRequested;
    public event Action<string>? NavigationRequested;
    public void Initialize(DeckLibrary library, DeckConstructionService service, TextureCache textures)
    {
        _library = library; _service = service; _textures = textures;
        AddThemeConstantOverride("separation", 20);
        var title = new HBoxContainer(); AddChild(title);
        title.AddChild(MenuStyles.Label("指挥部", 32, MenuStyles.MenuInk));
        title.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        title.AddChild(MenuStyles.Label("本地对战  /  卡组构筑  /  自定义卡牌", 13, MenuStyles.Dim));
        var hero = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 440), ClipContents = true }; AddChild(hero);
        Hero = new MenuHero(); Hero.Initialize(textures); hero.AddChild(Hero); Hero.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var caption = new VBoxContainer(); hero.AddChild(caption);
        caption.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterLeft); caption.OffsetLeft = 40; caption.OffsetRight = 620; caption.OffsetTop = -175; caption.OffsetBottom = 175;
        caption.AddThemeConstantOverride("separation", 15);
        caption.AddChild(MenuStyles.Label("下一场战斗，由你部署", 36, MenuStyles.Ink));
        caption.AddChild(MenuStyles.Label("选择你的部队，进入战场。", 17, MenuStyles.MenuDim));
        caption.AddChild(new Control { CustomMinimumSize = new(0, 8) });
        _deckDetail = new VBoxContainer(); _deckDetail.AddThemeConstantOverride("separation", 5); caption.AddChild(_deckDetail);
        caption.AddChild(new Control { CustomMinimumSize = new(0, 8) });
        PlayButton = MenuStyles.Button("开始对战  →", Play); MenuStyles.Primary(PlayButton);
        PlayButton.CustomMinimumSize = new(252, 58); PlayButton.SizeFlagsHorizontal = SizeFlags.ShrinkBegin; caption.AddChild(PlayButton);
        var choose = MenuStyles.Button("更换当前卡组", () => NavigationRequested?.Invoke("decklib"));
        choose.SizeFlagsHorizontal = SizeFlags.ShrinkBegin; caption.AddChild(choose);
        var links = new HBoxContainer(); links.AddThemeConstantOverride("separation", 18); AddChild(links);
        _decks = QuickLink(links, "我的卡组", "decklib");
        _collection = QuickLink(links, "卡牌收藏", "collection");
        _workshop = QuickLink(links, "DIY 工坊", "editor");
        Refresh();
    }
    private Button QuickLink(HBoxContainer row, string title, string route)
    {
        var button = MenuStyles.Button(title, () => NavigationRequested?.Invoke(route));
        button.CustomMinimumSize = new(0, 100); button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        button.Alignment = HorizontalAlignment.Left; button.AddThemeFontSizeOverride("font_size", 18); row.AddChild(button); return button;
    }
    private bool ReadyToPlay => _library.ActiveDeck is { } deck && _library.LoadError is null && _service.Validate(deck).Count == 0;
    private void Play() { if (ReadyToPlay) StartRequested?.Invoke(); else PlayRequested?.Invoke(); }
    public void Refresh()
    {
        MenuStyles.Clear(_deckDetail);
        var deck = _library.ActiveDeck;
        Hero.SetCards(_textures, (deck?.CardIds ?? []).Distinct().Select(id => _service.Cards.FirstOrDefault(c => c.CardId == id)?.ArtPath ?? ""));
        _deckDetail.AddChild(MenuStyles.Label("当前对战卡组", 12, MenuStyles.Gold));
        var name = MenuStyles.Label(deck?.Name ?? "尚未选择卡组", 24, MenuStyles.Ink);
        name.ClipText = true; name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; name.TooltipText = name.Text; _deckDetail.AddChild(name);
        var summary = deck is null ? "创建或导入一副卡组后即可开始。" : $"{deck.CardIds.Length + 1} 张（含总部） · {(deck.Options.UnrestrictedNations ? "混编" : deck.Options.MainNation)}";
        var errors = deck is null ? null : _service.Validate(deck);
        var status = _library.LoadError ?? (errors is { Count: > 0 } ? errors[0] : ReadyToPlay ? "准备就绪" : "请选择卡组");
        var detail = MenuStyles.Label(summary + "  /  " + status, 14, MenuStyles.Dim); detail.AutowrapMode = TextServer.AutowrapMode.WordSmart; _deckDetail.AddChild(detail);
        PlayButton.Text = ReadyToPlay ? "开始对战  →" : "选择卡组  →";
        _decks.Text = $"我的卡组  →\n{_library.Decks.Count} 副已保存卡组";
        _collection.Text = $"卡牌收藏  →\n{_service.PlayableCards.Count} 张可用卡牌";
        _workshop.Text = "DIY 工坊  →\n制作卡牌、词条与卡包";
    }
}
