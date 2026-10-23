using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    private DiyPackDraft? _compatiblePack;
    internal bool UsesUnifiedWorkspace => _compatiblePack is not null || _native?.HasSession == true;

    private void OpenCompatiblePack(DiyPackDraft pack) => _editor.GuardNavigation(() => GuardNative(() => NativeTry(() =>
    {
        _native?.Suspend(); NativeWorkspace(); _compatiblePack = pack.Snapshot(); _pack = pack;
        var heading = new HBoxContainer(); _workspace.AddChild(heading);
        var title = MenuStyles.Label(pack.Name, 23, MenuStyles.Gold); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; heading.AddChild(title);
        heading.AddChild(MenuStyles.Button("‹ 我的卡包", () => _editor.GuardNavigation(ShowNativePackages)));
        heading.AddChild(MenuStyles.Button("卡包信息…", () => EditCompatiblePackInfo(pack)));
        if (_store.Packs.Any(p => p.Id == pack.Id) && !pack.IsExpansion)
            heading.AddChild(MenuStyles.Button("词条与效果", () =>
            {
                var dialog = new AcceptDialog { Title = "词条与效果", OkButtonText = "完成", MinSize = new(1000, 680) }; AddChild(dialog);
                var scroll = new ScrollContainer { CustomMinimumSize = new(960, 590), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; dialog.AddChild(scroll);
                _keywordPane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(_keywordPane); BuildKeywords();
                dialog.Confirmed += () => dialog.QueueFree(); dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
            }));
        var tools = new HBoxContainer(); _workspace.AddChild(tools);
        var search = new LineEdit { PlaceholderText = "搜索卡牌", SizeFlagsHorizontal = SizeFlags.ExpandFill }; tools.AddChild(search);
        tools.AddChild(MenuStyles.Button("＋ 新建卡牌", () => OpenCompatibleCard(pack, new DiyCardDraft { PackId = pack.Id })));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; _workspace.AddChild(scroll);
        var grid = new GridContainer { Columns = 6, SizeFlagsHorizontal = SizeFlags.ExpandFill }; grid.AddThemeConstantOverride("h_separation", 22); grid.AddThemeConstantOverride("v_separation", 18); scroll.AddChild(grid);
        var saved = _cards.Cards.Where(c => c.PackId == pack.Id).ToArray();
        var cards = _service.Cards.Where(c => DeckCardPacks.RawOf(c.CardId) == pack.Id)
            .Concat(saved.Select(c => DiyCardCompiler.Preview(c, _service.Cards))).GroupBy(c => c.CardId).Select(g => g.Last()).ToArray();
        var pager = new HBoxContainer(); _workspace.AddChild(pager); var count = MenuStyles.Label("", 14); var page = 0;
        void Render()
        {
            MenuStyles.Clear(grid); var selected = cards.Where(c => c.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            var pages = Math.Max(1, (selected.Length + 23) / 24); page = Math.Clamp(page, 0, pages - 1); count.Text = $"{selected.Length} 张卡牌 · {page + 1} / {pages}";
            foreach (var card in selected.Skip(page * 24).Take(24))
            {
                var face = new CardControl { UseEditableFace = true }; grid.AddChild(face); face.Bind(card, _textures, 154);
                face.Activated += _ => NativeTry(() => OpenCompatibleCard(pack, saved.FirstOrDefault(c => c.RuntimeId == card.CardId)
                    ?? BuiltInWorkshopCatalog.Edit(card, pack.Id, RuntimeGameData.Directory("nations"))));
            }
            if (selected.Length == 0) grid.AddChild(MenuStyles.Label("没有匹配的卡牌。可清空搜索或新建卡牌。", 16));
        }
        pager.AddChild(MenuStyles.Button("上一页", () => { page--; Render(); })); pager.AddChild(count); pager.AddChild(MenuStyles.Button("下一页", () => { page++; Render(); }));
        search.TextChanged += _ => { page = 0; Render(); }; scroll.Resized += () => grid.Columns = Math.Max(1, (int)(scroll.Size.X / 176)); Render();
        _notice.Text = "点击卡牌进入制作。现有卡牌、词条与修改会保留，保存后更新构筑与新对局。";
    })));

    /// <summary>
    /// 验证夹具入口：等价于在卡牌一览里点中一张卡——把 <c>_editor</c> 挂到工作区并 Show。
    /// <c>OpenPack</c> 只显示卡包列表，编辑器要再进一层才可见；夹具直接点不可见控件只会落空。
    /// </summary>
    internal void OpenCompatibleCardForVerification(DiyPackDraft pack, DiyCardDraft draft) => OpenCompatibleCard(pack, draft);

    private void OpenCompatibleCard(DiyPackDraft pack, DiyCardDraft draft) => _editor.GuardNavigation(() => NativeTry(() =>
    {
        NativeWorkspace(); _compatiblePack = pack; _pack = pack;
        var row = new HBoxContainer(); _workspace.AddChild(row);
        row.AddChild(MenuStyles.Button("‹ 卡牌一览", () => OpenCompatiblePack(pack))); row.AddChild(MenuStyles.Label(pack.Name, 23, MenuStyles.Gold));
        _editor.GetParent().RemoveChild(_editor); _workspace.AddChild(_editor); _editor.Show(); _editor.SetPackScope(pack); _editor.SetWorkshopFocus(true); _editor.Load(draft.Snapshot());
        _notice.Text = "修改后保存；卡牌的原有编号、效果和自定义词条会保留。";
    }));
}
