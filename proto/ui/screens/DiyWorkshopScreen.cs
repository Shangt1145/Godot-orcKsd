using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>Pack authoring navigation; all executable behavior is compiled by the native bridge.</summary>
public partial class DiyWorkshopScreen : VBoxContainer
{
    private DiyWorkshopLibrary _store=null!;
    private DiyCardLibrary _cards=null!;
    private DiyEditorScreen _editor=null!;
    private DeckConstructionService _service=null!;
    private TextureCache _textures=null!;
    private Action _changed=null!;
    private Func<bool> _reduced=null!;
    private VBoxContainer _navigation=null!,_workspace=null!;
    private Label _notice=null!;
    private VBoxContainer _keywordPane=null!;
    private DiyPackDraft? _pack;
    private bool _animating;
    private int _navigationVersion;
    internal Button PackChoice=null!,ExpansionChoice=null!,CreateChoice=null!,ModifyChoice=null!,ConfirmPack=null!;
    internal OptionButton BaseChoice=null!,ModifyPick=null!;
    internal LineEdit PackName=null!,KeywordName=null!;
    internal Button SaveKeyword=null!;
    internal DiyPackDraft? CurrentPack=>_pack;
    public void Initialize(DiyWorkshopLibrary store,DiyCardLibrary cards,DiyEditorScreen editor,DeckConstructionService service,TextureCache textures,Action changed,Func<bool> reduced)
    {
        _store=store;_cards=cards;_editor=editor;_service=service;_textures=textures;_changed=changed;_reduced=reduced;
        SizeFlagsVertical=SizeFlags.ExpandFill;AddThemeConstantOverride("separation",12);
        var top=new HBoxContainer();AddChild(top);_nativeToolbar=top;top.AddChild(MenuStyles.Label("DIY 工坊",32,MenuStyles.Ink));top.AddChild(new Control{SizeFlagsHorizontal=SizeFlags.ExpandFill});
        top.AddChild(MenuStyles.Button("工坊首页",()=>_editor.GuardNavigation(ShowHome)));
        _notice=MenuStyles.Label("",13,MenuStyles.Dim);_notice.AutowrapMode=TextServer.AutowrapMode.WordSmart;AddChild(_notice);
        _navigation=new VBoxContainer{SizeFlagsVertical=SizeFlags.ExpandFill};AddChild(_navigation);
        _workspace=new VBoxContainer{SizeFlagsVertical=SizeFlags.ExpandFill};AddChild(_workspace);AddChild(_editor);ShowHome();
        VisibilityChanged+=()=>{if(!Visible&&_animating)ShowHome();};
    }
    internal void OpenPack(DiyPackDraft pack)
    {
        if (IsNativePackage(pack.Id)
            && !_cards.Cards.Any(c => c.PackId == pack.Id && c.SourceCardId.Length > 0)) { _editor.GuardNavigation(() => OpenNativePack(pack.Id[5..])); return; }
        OpenCompatiblePack(pack);
    }
    private void BuildKeywords()
    {
        MenuStyles.Clear(_keywordPane);
        var pack = _store.Packs.Single(p => p.Id == _pack!.Id); DiyKeywordDraft draft = new();
        var row = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; row.AddThemeConstantOverride("separation", 24); _keywordPane.AddChild(row);
        var sidebar = new VBoxContainer { CustomMinimumSize = new(220, 0) }; row.AddChild(sidebar);
        sidebar.AddChild(MenuStyles.Label("包内词条", 22, MenuStyles.Gold));
        var list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(210, 120) }; sidebar.AddChild(list);
        foreach (var keyword in pack.Keywords) list.AddItem(keyword.Name);
        var form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; form.AddThemeConstantOverride("separation", 14); row.AddChild(form);
        KeywordName = new LineEdit { MaxLength = 30 }; DiyEditorScreen.Field(form, "词条名称", KeywordName);
        var description = new TextEdit { CustomMinimumSize = new(0, 90), WrapMode = TextEdit.LineWrappingMode.Boundary }; DiyEditorScreen.Field(form, "给玩家看的说明", description);
        var battle = new CheckBox { Text = "在战场卡面上显示这个词条" }; form.AddChild(battle);
        var summary = MenuStyles.Label("", 15, MenuStyles.MenuInk); summary.AutowrapMode = TextServer.AutowrapMode.WordSmart; form.AddChild(summary);
        var status = MenuStyles.Label("", 13, MenuStyles.Dim); status.AutowrapMode = TextServer.AutowrapMode.WordSmart; form.AddChild(status);
        void Describe() => summary.Text = draft.ProgramJson is not null ? "已设置参数或主动技能。" : draft.Effects.Length > 0 ? $"已编排 {draft.Effects.Length} 条效果。" : draft.EffectText.Length > 0 ? draft.EffectText : "尚未编排效果。";
        void Load(DiyKeywordDraft value) { draft = value.Snapshot(); KeywordName.Text = draft.Name; description.Text = draft.Description; battle.ButtonPressed = draft.Battle; Describe(); status.Text = WorkshopAuthoring.Errors.GetValueOrDefault(draft.Id) ?? ""; }
        sidebar.AddChild(MenuStyles.Button("＋ 新建词条", () => Load(new()))); list.ItemSelected += i => Load(pack.Keywords[(int)i]);
        var edit = MenuStyles.Button("编辑规则…", () =>
        {
            void Edit() => OpenKeywordRules(draft, KeywordName.Text, (effects, text) => { draft = draft with { Effects = effects, EffectText = text, ProgramJson = null }; Describe(); status.Text = "规则已更新，保存词条后生效。"; }, _keywordPane.GetWindow());
            if (draft.ProgramJson is null) { Edit(); return; }
            var confirm = new ConfirmationDialog { Title = "重新编排词条", DialogText = "这会以触发效果替换当前的参数与主动技能。取消编辑会保留原有行为。", OkButtonText = "重新编排", CancelButtonText = "保留原有效果" }; _keywordPane.AddChild(confirm);
            confirm.Confirmed += () => { confirm.QueueFree(); Edit(); }; confirm.Canceled += confirm.QueueFree; confirm.PopupCentered();
        }); MenuStyles.Primary(edit); form.AddChild(edit);
        form.AddChild(MenuStyles.Button("参数与主动技能…", () =>
        {
            var dialog = new AcceptDialog { Title = "参数与主动技能", OkButtonText = "完成", MinSize = new(1000, 680) }; _keywordPane.AddChild(dialog);
            var editor = new KeywordProgramEditor { CustomMinimumSize = new(960, 590), Catalog = () => _service.Cards }; dialog.AddChild(editor);
            editor.Applied += json => { draft = draft with { ProgramJson = json }; Describe(); status.Text = "规则已更新，保存词条后生效。"; };
            editor.Finished += dialog.QueueFree; dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; editor.Open(draft.ProgramJson); dialog.PopupCentered();
        }));
        SaveKeyword = MenuStyles.Button("保存词条", () =>
        {
            var keyword = draft with { Name = KeywordName.Text.Trim(), Description = description.Text, Battle = battle.ButtonPressed };
            if (string.IsNullOrWhiteSpace(keyword.Name)) { status.Text = "请填写词条名称。"; return; }
            if (WorkshopAuthoring.ValidateKeyword(keyword) is { } error) { status.Text = "请修正效果：" + error; return; }
            NativeTry(() => { _store.Save(pack with { Keywords = pack.Keywords.Where(k => k.Id != keyword.Id).Append(keyword).ToArray() }); _changed(); _pack = _store.Packs.Single(p => p.Id == pack.Id); BuildKeywords(); _notice.Text = "词条已保存。"; });
        }); form.AddChild(SaveKeyword);
        if (pack.Keywords.Length > 0) { list.Select(0); Load(pack.Keywords[0]); } else Load(new());
    }

    public void RefreshData()
    {
        if (_compatiblePack is not null) return;
        if (_pack is not null) _pack = _store.Packs.FirstOrDefault(p => p.Id == _pack.Id);
    }
}
