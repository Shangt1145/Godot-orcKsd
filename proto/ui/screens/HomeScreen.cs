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
    private Label _inventory = null!;
    internal Button PlayButton = null!;
    internal MenuHero Hero = null!;
    public event Action? PlayRequested;
    public event Action? StartRequested;

    public void Initialize(DeckLibrary library, DeckConstructionService service, TextureCache textures)
    {
        _library = library; _service = service; _textures = textures;
        AddThemeConstantOverride("separation", 20);
        var heading = new HBoxContainer(); AddChild(heading);
        heading.AddChild(UiStyles.Label("指挥部", 30, UiStyles.MenuInk));
        heading.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        heading.AddChild(UiStyles.Label("OrC-KSD  /  卡牌作战", 13, UiStyles.Gold));
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; AddChild(body);
        body.AddThemeConstantOverride("separation", 24);
        var hero = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 350), ClipContents = true }; body.AddChild(hero);
        Hero = new MenuHero(); Hero.Initialize(textures); hero.AddChild(Hero); Hero.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var caption = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; hero.AddChild(caption);
        caption.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide); caption.OffsetLeft = 38; caption.OffsetRight = -38; caption.OffsetTop = -184; caption.OffsetBottom = -32;
        caption.AddThemeConstantOverride("separation", 10);
        caption.AddChild(UiStyles.Label("准备出击", 44, UiStyles.MenuInk));
        caption.AddChild(UiStyles.Label("以你的卡组，部署下一场战斗。", 16, UiStyles.MenuInk));
        var action = new HBoxContainer(); caption.AddChild(action);
        PlayButton = UiStyles.Button("开始对战  →", Play); UiStyles.Primary(PlayButton); PlayButton.CustomMinimumSize = new(220, 52); action.AddChild(PlayButton);
        var side = new PanelContainer { CustomMinimumSize = new(328, 0) }; body.AddChild(side);
        side.AddThemeStyleboxOverride("panel", UiStyles.Box(UiStyles.MenuPanel, UiStyles.MenuLine, 1, 22));
        _deckDetail = new VBoxContainer(); _deckDetail.AddThemeConstantOverride("separation", 14); side.AddChild(_deckDetail);
        var inventory = new PanelContainer(); AddChild(inventory);
        inventory.AddThemeStyleboxOverride("panel", UiStyles.Box(new("222b20eb"), UiStyles.MenuLine, 1, 16));
        _inventory = UiStyles.Label("", 14, UiStyles.MenuDim); inventory.AddChild(_inventory);
        Refresh();
    }
    private bool ReadyToPlay => _library.ActiveDeck is { } deck && _library.LoadError is null && _service.Validate(deck).Count == 0;
    private void Play()
    {
        if (ReadyToPlay) StartRequested?.Invoke();
        else PlayRequested?.Invoke();
    }
    public void Refresh()
    {
        UiStyles.Clear(_deckDetail);
        _deckDetail.AddChild(UiStyles.Label("当前对战卡组", 13, UiStyles.Gold));
        var deck = _library.ActiveDeck;
        if (deck is not null)
        {
            var center = new CenterContainer(); _deckDetail.AddChild(center);
            var cover = new MenuDeckCover(); cover.Initialize(_textures, deck.Options.MainNation, 180); center.AddChild(cover);
            var name = UiStyles.Label(deck.Name, 24, UiStyles.MenuInk); name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            name.MaxLinesVisible = 2; name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; name.TooltipText = deck.Name;
            name.CustomMinimumSize = new(270, 0); _deckDetail.AddChild(name);
            _deckDetail.AddChild(UiStyles.Label($"{deck.CardIds.Length + 1} 张（含总部）  ·  " + (deck.Options.UnrestrictedNations ? "混编" : deck.Options.MainNation), 13, UiStyles.MenuDim));
            var errors = _service.Validate(deck);
            var state = UiStyles.Label(errors.Count == 0 ? "准备就绪" : errors[0], 13, errors.Count == 0 ? new Color("bdcc9c") : new("dca58b")); state.AutowrapMode = TextServer.AutowrapMode.WordSmart; _deckDetail.AddChild(state);
        }
        else
        {
            var text = UiStyles.Label(_library.LoadError ?? "尚未选择对战卡组。", 16, UiStyles.MenuDim); text.AutowrapMode = TextServer.AutowrapMode.WordSmart; text.CustomMinimumSize = new(270, 0); _deckDetail.AddChild(text);
        }
        _deckDetail.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        var hint = UiStyles.Label("在顶部“我的卡组”中更换、编辑或导入卡组。", 14, UiStyles.MenuDim); hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; _deckDetail.AddChild(hint);
        PlayButton.Text = ReadyToPlay ? "开始对战  →" : "选择卡组  →";
        _inventory.Text = $"已保存卡组  {_library.Decks.Count}       /       卡牌收藏  {_service.Cards.Count}       /       自定义卡牌  {_service.CustomCards.Count}";
    }
}
