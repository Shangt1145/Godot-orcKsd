using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Kards.Ui.OrcBridge.OrcText;

namespace Kards.Ui;

public partial class DiyEditorScreen : VBoxContainer
{
    private DiyCardLibrary _library = null!;
    private Func<IReadOnlyList<UiCardDefinition>> _catalog = null!;
    private Func<string, int> _usage = null!;
    private Func<string, string?> _storedError = null!;
    private OrcImportLibrary? _orcImports;
    private Func<IReadOnlyList<OrcImportOutcome>>? _importedOutcomes;
    private string? _selectedImported;
    private readonly List<string> _importedListed = [];
    private DiyCardDraft? _blocked;
    private TextureCache _textures = null!;
    private DiyCardDraft _draft = new();
    private bool _loading, _dirty;
    private Action? _afterDiscard;
    private readonly Dictionary<string, CheckBox> _keywords = new();
    private readonly List<string> _listed = [];
    private VBoxContainer _preview = null!;
    private Label _previewTitle = null!;
    private Label _notice = null!, _identity = null!, _effectsSummary = null!;
    internal ItemList Cards = null!;
    internal LineEdit CardName = null!, Search = null!;
    internal OptionButton CardType = null!, Nation = null!, Rarity = null!, UnitType = null!;
    internal SpinBox Cost = null!, OpCost = null!, Attack = null!, Defense = null!, Armor = null!;
    // ── 自导入卡图 ──
    internal SpinBox ArtZoom = null!;
    internal Button ImportArtButton = null!, RemoveArtButton = null!;
    private FileDialog _artDialog = null!;
    private Label _artStatus = null!;
    private string _importedArt = "";
    internal TextEdit Flavor = null!, EffectTextInput = null!;
    internal Label TextNotice = null!;
    private Godot.Timer _textTimer = null!;
    // 效果编辑在独立屏幕（DiyEffectEditorScreen）；本屏只留摘要＋入口按钮（可读性硬要求）。
    internal Button OpenEffectsButton = null!;
    internal Button ParseReplaceButton = null!, ParseAppendButton = null!;
    internal Button SaveButton = null!, NewButton = null!, CopyButton = null!, DeleteButton = null!, ValidateButton = null!, TrialButton = null!, ConstructButton = null!, ExportButton = null!, ImportButton = null!;
    internal Button AlignButton = null!;
    private CardFaceTuner _tuner = null!;
    internal ConfirmationDialog DeleteDialog = null!, DiscardDialog = null!;
    internal ConfirmationDialog? ActiveImport;
    internal TextEdit? ImportText;
    internal AcceptDialog? ActiveExport;
    internal ScrollContainer EditorScroll = null!;
    private TabContainer _sections = null!;
    private readonly List<ScrollContainer> _sectionScrolls = [];
    internal PanelContainer PreviewPanel = null!;
    internal string LastExportCode = "";
    public event Action? Saved;
    public event Action<string>? ConstructRequested;
    public event Action<DiyCardDraft>? TrialRequested;
    /// <summary>OrC 批量导入落盘后通知（Main 重编译快照并刷新构筑池）。</summary>
    public event Action? ImportedChanged;
    /// <summary>试打一张 OrC 导入卡（与 DIY 试打同一临时卡组流程）。</summary>
    public event Action<OrcImportOutcome>? ImportedTrialRequested;
    /// <summary>打开独立效果编辑器（Main 负责切屏）。</summary>
    public event Action? EffectsEditorRequested;
    public DiyCardDraft Draft => Read();
    /// <summary>草稿真源（效果编辑器以它为工作底稿；Read() 只合并本屏控件面）。</summary>
    internal DiyCardDraft CurrentDraft => _draft.Snapshot();
    internal bool IsDirty => _dirty;
    internal string Notice => _notice.Text;
    public void Initialize(DiyCardLibrary library, Func<IReadOnlyList<UiCardDefinition>> catalog,
        TextureCache textures, Func<string, int> usage, Func<string, string?> storedError,
        OrcImportLibrary? orcImports = null, Func<IReadOnlyList<OrcImportOutcome>>? importedOutcomes = null)
    {
        _library = library; _catalog = catalog; _textures = textures; _usage = usage; _storedError = storedError;
        _orcImports = orcImports; _importedOutcomes = importedOutcomes;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 10);
        var heading = new HBoxContainer(); AddChild(heading);
        var title = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; heading.AddChild(title);
        title.AddChild(MenuStyles.Label("卡牌编辑", 28, MenuStyles.Ink));
        title.AddChild(MenuStyles.Label("编辑 → 校验 → 保存 → 构筑 / 试打", 13, MenuStyles.Dim));
        NewButton = MenuStyles.Button("新建", () => Guard(() => Load(new(){PackId=_scopePack}))); heading.AddChild(NewButton);
        CopyButton = MenuStyles.Button("复制", Copy); heading.AddChild(CopyButton);
        SaveButton = MenuStyles.Button("保存", () => Save()); heading.AddChild(SaveButton);
        MenuStyles.Primary(SaveButton);
        ImportButton = MenuStyles.Button("导入", () => Guard(Import)); heading.AddChild(ImportButton);
        ExportButton = MenuStyles.Button("导出", Export); heading.AddChild(ExportButton);
        if (_orcImports is not null)
        {
            var orcButton = MenuStyles.Button("OrC 批量导入", OrcImport); heading.AddChild(orcButton);
            orcButton.TooltipText = "粘贴官方语料 JSON（行数组或 {\"cards\":[…]}），自动解析为引擎卡；解析不了的留诊断待核对。";
        }
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; AddChild(body);
        var listPanel = Panel(205); body.AddChild(listPanel);
        var list = new VBoxContainer(); listPanel.AddChild(list);
        Search = new LineEdit { PlaceholderText = "搜索包内卡牌" }; list.AddChild(Search);
        Search.TextChanged += _ => RefreshList();
        Cards = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(170, 100) }; list.AddChild(Cards);
        Cards.ItemSelected += index =>
        {
            if (index < 0) return;
            var i = (int)index;
            if (i < _listed.Count)
            {
                var card = _library.Cards.FirstOrDefault(c => c.Id == _listed[i])
                    ?? BuiltInWorkshopCatalog.Edit(_catalog().Single(c=>c.CardId==_listed[i]),_scopePack,RuntimeGameData.Directory("nations"));
                Guard(() => Load(card));
            }
            else
            {
                // OrC 导入区：只读展示（九 op 编辑器表达不了引擎 21 op 面——诚实边界，不伪装可编辑）。
                i -= _listed.Count + 1;
                if (i < 0 || i >= _importedListed.Count) return;
                _selectedImported = _importedListed[i];
                ShowImportedNotice(_selectedImported);
            }
        };
        DeleteButton = MenuStyles.Button("删除", () =>
        {
            if (_selectedImported is not null)
            {
                DeleteDialog.DialogText = "删除这张 OrC 导入卡？引用它的卡组需要重新编辑。";
                DeleteDialog.PopupCentered(); return;
            }
            if(_draft.SourceCardId.Length>0)
            {DeleteDialog.Title="恢复原版卡牌";DeleteDialog.DialogText="撤销此卡的本地修改并恢复原版？原卡组引用仍然有效。";DeleteDialog.PopupCentered();return;}
            DeleteDialog.Title="删除 DIY 卡牌";
            var uses = _usage(_draft.RuntimeId);
            DeleteDialog.DialogText = uses == 0 ? "删除这张已保存的 DIY 卡牌？"
                : $"有 {uses} 副卡组引用这张卡。删除后这些卡组需要重新编辑。仍然删除？";
            DeleteDialog.PopupCentered();
        }); list.AddChild(DeleteButton);
        var editPanel = Panel(450); editPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill; body.AddChild(editPanel);
        _sections = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; editPanel.AddChild(_sections);
        _sections.AddThemeStyleboxOverride("panel",MenuStyles.Box(MenuStyles.Panel,MenuStyles.Line,2,6));
        VBoxContainer EditorPage(string name)
        {
            var page = new ScrollContainer { Name = name, SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            _sections.AddChild(page); _sectionScrolls.Add(page);
            var content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; content.AddThemeConstantOverride("separation", 16); page.AddChild(content); return content;
        }
        var form = EditorPage("属性"); var rulesForm = EditorPage("规则与词条");
        var artForm = EditorPage("卡图"); var flavorForm = EditorPage("风味");
        EditorScroll = _sectionScrolls[0]; _sections.TabChanged += i => EditorScroll = _sectionScrolls[(int)i];
        var basic = Section(form, "基础信息");
        _identity = MenuStyles.Label("", 11, MenuStyles.Dim); _identity.Visible = false; basic.AddChild(_identity);
        CardName = new LineEdit { MaxLength = 40, PlaceholderText = "卡牌名称" }; Field(basic, "名称", CardName);
        CardName.TextChanged += _ => Changed();
        var nationRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; basic.AddChild(nationRow);
        Nation = Option(DiyCardCompiler.Nations); Field(nationRow, "国家", Nation);
        BuildNationFields(nationRow);
        var types = new GridContainer { Columns = 3 }; basic.AddChild(types);
        CardType = Option([new("unit", "单位"), new("order", "指令"), new("counter", "反制")]); Field(types, "卡牌类别", CardType);

        Rarity = Option(DiyCardCompiler.Rarities); Field(types, "稀有度", Rarity);
        UnitType = Option(DiyCardCompiler.UnitTypes); Field(types, "兵种", UnitType);
        Cost = Number(0, 99); Field(types, "部署 / 打出费用", Cost);
        OpCost = Number(0, 99); Field(types, "行动费用", OpCost);
        var stats = new GridContainer { Columns = 3 }; basic.AddChild(stats);
        Attack = Number(0, 99); Field(stats, "攻击", Attack);
        Defense = Number(1, 99); Field(stats, "防御", Defense);
        Armor = Number(0, 3); Field(stats, "重甲参值", Armor);
        foreach (var o in new[] { CardType, Nation, Rarity, UnitType }) o.ItemSelected += _ => Changed();
        foreach (var n in new[] { Cost, OpCost, Attack, Defense, Armor }) n.ValueChanged += _ => Changed();
        var keywordsSection = Section(rulesForm, "卡牌自带词条");
        var keywordGrid = new GridContainer { Columns = 3 }; _keywordGrid=keywordGrid;keywordsSection.AddChild(keywordGrid);
        foreach (var keyword in DiyCardCompiler.Keywords)
        {
            var box = new CheckBox { Text = keyword.Label, TooltipText = keyword.Label }; keywordGrid.AddChild(box);
            _keywords[keyword.Id] = box; box.Toggled += _ => Changed();
        }
        // 自导入卡图：本机图片 → 卡库旁 art/ 目录（PNG），缩放 50–300% 只作用于导入图。
        var artRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        artRow.AddThemeConstantOverride("separation", 8); var artSection = Section(artForm, "卡图与裁切"); artSection.AddChild(artRow);
        ImportArtButton = MenuStyles.Button("导入卡图…", () => _artDialog.Popup()); artRow.AddChild(ImportArtButton);
        RemoveArtButton = MenuStyles.Button("移除卡图", () => { _importedArt = ""; _draft = _draft with { ArtCardId = "",ArtSourceFile="",ArtRegion=new(0,0,1,1) };_cropApply.Disabled=true; Changed(); RefreshPreview(); });
        artRow.AddChild(RemoveArtButton);
        _artStatus = MenuStyles.Label("", 12, MenuStyles.Dim);
        _artStatus.SizeFlagsHorizontal = SizeFlags.ExpandFill; _artStatus.ClipText = true;
        artRow.AddChild(_artStatus);
        ArtZoom = Number(50, 300); Field(artSection, "插画缩放 %", ArtZoom);
        ArtZoom.ValueChanged += v => { if (!_loading && _importedArt.Length > 0) { DiyArtStore.UpdateZoom(_draft.RuntimeId, (int)v); Changed(); RefreshPreview(); } };
        _artDialog = new FileDialog
        {
            Title = "选择卡图图片", Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true, Size = new(720, 480)
        };
        _artDialog.Filters = ["*.png ; PNG 图片", "*.jpg ; JPEG 图片", "*.jpeg ; JPEG 图片", "*.webp ; WebP 图片"];
        _artDialog.FileSelected += OnArtImported; AddChild(_artDialog);
        BuildArtAdaptation(artSection);

        var textSec = Section(rulesForm, "卡牌规则文本");
        var hintText = MenuStyles.Label("写一句效果（例：部署：随机对 1 个敌方单位造成 2 点伤害。），解析后落到下方效果结构；文本同时作为卡面规则文字。解析失败时不改动现有结构。", 12, MenuStyles.Dim);
        hintText.AutowrapMode = TextServer.AutowrapMode.WordSmart; textSec.AddChild(hintText);
        EffectTextInput = new TextEdit
        {
            CustomMinimumSize = new(0, 90), WrapMode = TextEdit.LineWrappingMode.Boundary,
            PlaceholderText = "每行一条效果；支持：部署 / 亡计 / 使用时 / 回合开始 / 攻击后 / 监听句……"
        };
        textSec.AddChild(EffectTextInput);
        var parseRow = new HBoxContainer(); parseRow.AddThemeConstantOverride("separation", 8); textSec.AddChild(parseRow);
        ParseReplaceButton = MenuStyles.Button("解析并替换", () => ParseApply(replace: true)); parseRow.AddChild(ParseReplaceButton);
        ParseAppendButton = MenuStyles.Button("解析并追加", () => ParseApply(replace: false)); parseRow.AddChild(ParseAppendButton);
        TextNotice = MenuStyles.Label("", 12, MenuStyles.Dim);
        TextNotice.AutowrapMode = TextServer.AutowrapMode.WordSmart; textSec.AddChild(TextNotice);
        _textTimer = new Godot.Timer { OneShot = true, WaitTime = 0.6 }; AddChild(_textTimer);
        _textTimer.Timeout += RefreshTextNotice;
        EffectTextInput.TextChanged += () => { if (!_loading) { Changed(); _textTimer.Start(); } };

        var inspSec = Section(rulesForm, "效果");
        _effectsSummary = MenuStyles.Label("", 13, MenuStyles.Dim);
        _effectsSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart; inspSec.AddChild(_effectsSummary);
        OpenEffectsButton = MenuStyles.Button("打开效果编辑器…", () => EffectsEditorRequested?.Invoke());
        OpenEffectsButton.TooltipText = "在独立屏幕中编辑效果结构（触发/动作/参数），最右侧实时显示原始 DSL。";
        inspSec.AddChild(OpenEffectsButton);
        inspSec.AddChild(MenuStyles.Label("规则文本解析出的效果也会落到效果编辑器；卡面文字仍由本屏规则文本承载。", 12, MenuStyles.Dim));
        rulesForm.MoveChild(inspSec.GetParent().GetParent(),0);
        rulesForm.MoveChild(keywordsSection.GetParent().GetParent(),rulesForm.GetChildCount()-1);

        var flavorSec = Section(flavorForm, "风味文字");
        Flavor = new TextEdit { CustomMinimumSize = new(0, 65), WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = "风味文字，不作为规则执行（最多 300 字）" };
        Field(flavorSec, "风味文字", Flavor); Flavor.TextChanged += Changed;
        var previewPanel = Panel(245); body.AddChild(previewPanel);
        PreviewPanel = previewPanel;
        var previewSide = new VBoxContainer(); previewPanel.AddChild(previewSide);
        _previewTitle = MenuStyles.Label("卡面预览", 17, MenuStyles.Gold); previewSide.AddChild(_previewTitle);
        var previewScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; previewSide.AddChild(previewScroll);
        _preview = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; previewScroll.AddChild(_preview);
        ValidateButton = MenuStyles.Button("校验", Validate); previewSide.AddChild(ValidateButton);
        ConstructButton = MenuStyles.Button("用于构筑", () => { if (Save() && DiyCardCompiler.Validate(_draft,_catalog()).Success) ConstructRequested?.Invoke(_draft.RuntimeId); }); previewSide.AddChild(ConstructButton);
        TrialButton = MenuStyles.Button("试打", () =>
        {
            if (_selectedImported is not null)
            {
                var outcome = (_importedOutcomes?.Invoke() ?? []).LastOrDefault(o => o.Id == _selectedImported && o.Complete);
                if (outcome is null) { _notice.Text = "这张导入卡还在待核对（未编译入池），不能试打。"; return; }
                ImportedTrialRequested?.Invoke(outcome); return;
            }
            if (Save() && DiyCardCompiler.Validate(_draft,_catalog()).Success) TrialRequested?.Invoke(_draft.Snapshot());
        }); previewSide.AddChild(TrialButton);
        TrialButton.TooltipText = "用临时 12 张卡牌的卡组随机发牌；按稀有度加入最多 2 张当前卡，不改动已保存卡组。";
        // 模板对齐采用独立画布，避免被本屏 VBox 当作普通表单行重新排布。
        AlignButton = MenuStyles.Button("微调卡图…", () =>
        {
            var previewCard = DiyCardCompiler.Preview(Read(), _catalog());
            _tuner.Open(previewCard, _textures.Get(previewCard.ArtPath));
            _tuner.Visible = true;
        });
        AlignButton.TooltipText = "拖动绿框调整模板的插画槽；保存后更新卡面预览。";
        previewSide.AddChild(AlignButton);
        _tuner = new CardFaceTuner { Visible = false };
        var tunerLayer = new CanvasLayer { Layer = 30 }; AddChild(tunerLayer);
        tunerLayer.AddChild(_tuner);
        _tuner.Saved += RefreshPreview;
        _notice = MenuStyles.Label("", 13, MenuStyles.Dim); _notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _notice.CustomMinimumSize = new(0, 35); AddChild(_notice);
        DeleteDialog = new ConfirmationDialog { Title = "删除 DIY 卡牌" }; AddChild(DeleteDialog);
        DeleteDialog.Confirmed += () => Run(() =>
        {
            if (_selectedImported is not null)
            {
                var id = _selectedImported; _selectedImported = null;
                _orcImports!.Remove(id); _notice.Text = "OrC 导入卡已删除。";
                RefreshList(); ImportedChanged?.Invoke(); return;
            }
            var originalId=_draft.SourceCardId;
            _library.Delete(_draft.Id); Saved?.Invoke();
            if(originalId.Length>0)
            {Load(BuiltInWorkshopCatalog.Edit(_catalog().Single(c=>c.CardId==originalId),_scopePack,RuntimeGameData.Directory("nations")));_notice.Text="已撤销本地修改，原版卡牌已恢复。";}
            else {Load(new());_notice.Text="卡牌已删除；引用它的卡组请重新编辑。";}
        });
        DiscardDialog = new ConfirmationDialog { Title = "未保存的卡牌", DialogText = "丢弃当前修改并继续？" }; AddChild(DiscardDialog);
        DiscardDialog.Confirmed += () => { var action = _afterDiscard; _afterDiscard = null; action?.Invoke(); };
        DiscardDialog.Canceled += () => { _afterDiscard = null; RefreshList(); };
        Load(_library.Cards.FirstOrDefault() ?? new());
        if (_library.LoadError is { } error) _notice.Text = error;
    }
    internal void EnsureEditingVisible(Control control)
    {
        for (var i = 0; i < _sectionScrolls.Count; i++)
            if (_sectionScrolls[i].IsAncestorOf(control))
            { _sections.CurrentTab = i; EditorScroll = _sectionScrolls[i]; EditorScroll.EnsureControlVisible(control); return; }
    }
    private static PanelContainer Panel(float width) => new() { CustomMinimumSize = new(width, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
    /// <summary>分区面板：独立边距＋节标题——可读性硬要求（不把所有控件挤在一起）。</summary>
    private static VBoxContainer Section(VBoxContainer parent, string title)
    {
        var panel = new PanelContainer(); parent.AddChild(panel);
        var style = MenuStyles.Box(new("253026"), new("3d4b3b"), 2, 4); style.BorderWidthLeft = 2;
        panel.AddThemeStyleboxOverride("panel", style);
        var margin = new MarginContainer();
        foreach (var (name, value) in new[] { ("margin_left", 12), ("margin_top", 8), ("margin_right", 12), ("margin_bottom", 12) })
            margin.AddThemeConstantOverride(name, value);
        panel.AddChild(margin);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);
        box.AddChild(MenuStyles.Label(title, 16, MenuStyles.Gold));
        return box;
    }
    internal static Control Field(Node parent, string label, Control control)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 4); parent.AddChild(box);
        box.AddChild(MenuStyles.Label(label, 12, MenuStyles.Dim)); box.AddChild(control);
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return box;
    }
    internal static OptionButton Option(IEnumerable<UiDiyChoice> choices)
    {
        var option = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(90, 34), FitToLongestItem = false, ClipText = true };
        foreach (var choice in choices) { option.AddItem(choice.Label); option.SetItemMetadata(option.ItemCount - 1, choice.Id); }
        return option;
    }
    internal static SpinBox Number(int min, int max) => new() { MinValue = min, MaxValue = max, Step = 1, CustomMinimumSize = new(65, 34) };
    internal static string Value(OptionButton option) => option.GetItemMetadata(option.Selected).AsString();
    internal static void Select(OptionButton option, string value)
    { for (var i = 0; i < option.ItemCount; i++) if (option.GetItemMetadata(i).AsString() == value) { option.Select(i); return; } }
    private void Guard(Action action)
    {
        if (!_dirty) { action(); return; }
        _afterDiscard = action; DiscardDialog.PopupCentered();
    }
    private void Copy()
    {
        if (_blocked is not null) { _notice.Text = _storedError(_blocked.Id) ?? "这张卡未通过校验，请删除或新建。"; return; }
        var current = Read();
        var id="diy/"+Guid.NewGuid().ToString("N");
        try
        {
            var art=current.ArtFile.Length>0?_library.SaveArt(id,_library.ReadArt(current.ArtFile)):"";
            var source=current.ArtSourceFile.Length>0?_library.SaveArtSource(id,_library.ReadArt(current.ArtSourceFile)):"";
            Load(current with { Id = id, SourceCardId="", ArtFile=art, ArtSourceFile=source, Name = (current.Name + " 副本")[..Math.Min(40, current.Name.Length + 3)] });
        }
        catch(Exception e){_notice.Text="复制卡图失败："+e.Message;return;}
        Changed(); _notice.Text = "已创建副本，保存后才会进入卡库。";
    }
    internal void Load(DiyCardDraft draft)
    {
        if(draft.PackId.Length==0&&_scopePack.Length>0)draft=draft with{PackId=_scopePack};
        _blocked = _storedError(draft.Id) is null || draft.OrcEffects.Any(e=>e.DesktopJson is not null||e.PrefabJson is not null) ? null : draft.Snapshot();
        _loading = true; _draft = draft.Snapshot();
        RefreshWorkshopVocabulary(draft.PackId);
        foreach(var entry in new[]{(CardType,draft.CardType),(Nation,draft.Nation),(Rarity,draft.Rarity),(UnitType,draft.UnitType)})
            if(!Enumerable.Range(0,entry.Item1.ItemCount).Any(i=>entry.Item1.GetItemMetadata(i).AsString()==entry.Item2))
            {entry.Item1.AddItem(entry.Item2+"（原始值）");entry.Item1.SetItemMetadata(entry.Item1.ItemCount-1,entry.Item2);}
        CardName.Text = draft.Name; Select(CardType, draft.CardType); Select(Nation, draft.Nation); Select(Rarity, draft.Rarity);
        Select(UnitType, draft.UnitType);
        _importedArt = draft.ArtFile; ArtZoom.Value = draft.ArtScale;
        Cost.Value = draft.Cost; OpCost.Value = draft.OpCost; Attack.Value = draft.Attack; Defense.Value = draft.Defense; Armor.Value = draft.Armor;
        foreach (var pair in _keywords) pair.Value.ButtonPressed = draft.Keywords.Contains(pair.Key);
        Flavor.Text = draft.Flavor; EffectTextInput.Text = draft.EffectText;
        if (!draft.UseStructuredEffects && draft.OrcEffects.Length == 0 && draft.EffectText.Trim().Length > 0)
        {
            // 旧版文本卡自动升级：完整解析则落到结构（效果编辑器可见可编辑）；不完整保持原样。
            var hydration = OrcInspector.CompileText(draft.Id.Replace('/', '.'), draft.EffectText.Trim(), draft.CardType, _catalog());
            if (hydration.Parse.Complete)
            {
                var hydrated = new List<OrcEffectDraft>();
                foreach (var effect in hydration.Parse.Effects)
                { try { hydrated.Add(OrcEffectEditing.ToDraft(effect)); } catch (InvalidDataException) { } }
                if (hydrated.Count > 0) _draft = _draft with { OrcEffects = hydrated.ToArray() };
            }
        }
        _loading = false; _dirty = false; SaveButton.Text = "保存"; _notice.Text = "";
        RefreshList(); RefreshPreview(); RefreshTextNotice(); RefreshEffectsSummary(); foreach (var page in _sectionScrolls) page.ScrollVertical = 0;
        if (_blocked is not null) _notice.Text = $"卡牌未通过校验，原声明保留，不能用于实战：{_storedError(draft.Id)}";
    }
    private DiyCardDraft Read() => _blocked?.Snapshot() ?? _draft with
    {
        Name = CardName.Text, CardType = Value(CardType), Nation = Value(Nation), Rarity = Value(Rarity), UnitType = Value(UnitType),
        Cost = (int)Cost.Value, OpCost = (int)OpCost.Value, Attack = (int)Attack.Value, Defense = (int)Defense.Value,
        Keywords = _draft.DesktopCardJson is not null ? _draft.Keywords : _keywords.Where(k => k.Value.ButtonPressed&&(Value(CardType)=="unit"||WorkshopAuthoring.Keyword(k.Key)?.ProgramJson is not null)).Select(k => k.Key).ToArray(),
        KeywordValues = _draft.KeywordValues.Where(p=>_keywords.TryGetValue(p.Key,out var box)&&box.ButtonPressed)
            .Concat(_keywordValues.Where(p=>_keywords[p.Key].ButtonPressed).Select(p=>new KeyValuePair<string,int>(p.Key,(int)p.Value.Value)))
            .GroupBy(p=>p.Key).ToDictionary(g=>g.Key,g=>g.Last().Value),
        Armor = (int)Armor.Value, ArtFile = _importedArt,
        ArtScale = _importedArt.Length > 0 ? (int)ArtZoom.Value : 100,
        Flavor = Flavor.Text, EffectText = EffectTextInput.Text,
        // 效果编辑在独立屏幕：结构/简易行真源留在 _draft（经 ApplyEffects 写回）。
        Effects = _draft.Effects, OrcEffects = _draft.OrcEffects
    };
    /// <summary>效果编辑器实时写回（Applied 回调）：并入草稿＋刷新预览与摘要。</summary>
    internal void ApplyEffects(OrcEffectDraft[] orcEffects, DiyEffectDraft[] legacyEffects)
    {
        if (_loading || _blocked is not null) return;
        _draft = _draft.Snapshot() with { OrcEffects = orcEffects, Effects = legacyEffects, UseStructuredEffects=true };
        Changed(); RefreshEffectsSummary();
    }
    internal void MarkInspectorDirty()=>Changed();
    internal void ApplyInspectorDraft(DiyCardDraft draft)
    {
        Load(draft.PackId.Length==0&&_scopePack.Length>0?draft with{PackId=_scopePack}:draft);Changed();
    }
    internal void AcceptInspectorDraft(DiyCardDraft draft)
    {
        if(draft.PackId.Length==0&&_scopePack.Length>0)draft=draft with{PackId=_scopePack};
        try{_library.Save(draft);Load(draft);Saved?.Invoke();_notice.Text="完整 Inspector 草稿已保存；原生编译通过后方可用于实战。";}
        catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException){_notice.Text="Inspector 草稿尚未落盘："+e.Message;throw;}
    }
    internal IReadOnlyList<DiyCardDraft> AuthoredCards=>_library.Cards.Where(c=>_scopePack.Length==0||c.PackId==_scopePack).ToArray();
    internal void DeleteDesktopDraft(string id){_library.Delete(id);Saved?.Invoke();RefreshList();}
    /// <summary>效果摘要（不把编辑控件堆在本屏——可读性硬要求）。</summary>
    private void RefreshEffectsSummary()
    {
        var parts = new List<string>();
        if (_draft.OrcEffects.Length > 0)
            parts.Add($"{_draft.OrcEffects.Length} 条结构效果（{string.Join("、", _draft.OrcEffects.Take(3)
                .Select(e => OrcEffectEditing.Templates.FirstOrDefault(t => t.Id == e.Template && t.Guard.ToString() == e.Guard)?.Label ?? e.Template))}{(_draft.OrcEffects.Length > 3 ? "…" : "")}）");
        if (_draft.Effects.Length > 0) parts.Add($"{_draft.Effects.Length} 条简易效果");
        _effectsSummary.Text = parts.Count > 0 ? "当前：" + string.Join("；", parts) : "还没有效果。打开效果编辑器添加，或用规则文本解析。";
    }
    private void Changed()
    {
        if (_loading) return;
        _dirty = true; SaveButton.Text = "保存 *"; _notice.Text = "有未保存的修改。"; RefreshPreview();
    }
    private void RefreshPreview()
    {
        var draft = Read(); var isUnit = draft.CardType == "unit";
        _previewTitle.Text = "卡面预览 · " + CardFace.MasterName(CardFace.MasterFor(draft.CardType));
        UnitType.Disabled = !isUnit; OpCost.Editable = isUnit; Attack.Editable = isUnit; Defense.Editable = isUnit;
        foreach (var keyword in _keywords) keyword.Value.Disabled = (!isUnit&&WorkshopAuthoring.Keyword(keyword.Key)?.ProgramJson is null) || draft.DesktopCardJson is not null;
        Armor.Editable = isUnit && _keywords["armor"].ButtonPressed && draft.DesktopCardJson is null;
        _identity.Text = draft.Id; _identity.ClipText = true; _identity.TooltipText = draft.Id;
        // 新卡图只能由本机文件导入；旧存档引用只读兼容，可移除或导入替换。
        var imported = _importedArt.Length > 0;
        ImportArtButton.Disabled = _blocked is not null;
        RemoveArtButton.Disabled = _blocked is not null || (!imported && draft.ArtCardId.Length == 0);
        ArtZoom.Editable = imported;
        _artStatus.Text = imported ? $"已导入 {draft.ArtFile} · {draft.ArtScale}%"
            : draft.ArtCardId.Length > 0 ? "旧版卡图（导入可替换）" : "未导入 · 请选择本机图片";
        OpenEffectsButton.Disabled = _blocked is not null;
        OpenEffectsButton.TooltipText = _blocked is not null ? "卡牌未通过校验，先处理无效字段。" : "";
        var textMode = EffectTextInput.Text.Trim().Length > 0;
        RefreshEffectsSummary();
        var card = DiyCardCompiler.Preview(Read(), _catalog());
        MenuStyles.Clear(_preview);
        // 卡面用真实模板（AstralKards）渲染：卡框/卡图都是素材层，文字按 PSD 坐标叠加，不再用代码画牌。
        // 外面套一个定高容器：直接放进 VBox 会被拉成面板宽，(比例失衡后) 下半截就滚出可视区了。
        var faceHolder = new CenterContainer { CustomMinimumSize = new(0, 158) };
        var face = new CardFace();
        face.FitTo(150);
        faceHolder.AddChild(face);
        _preview.AddChild(faceHolder);
        face.SetDefinition(card);
        face.SetIllustration(_textures.Get(card.ArtPath), card.ArtPath.StartsWith("diyart://", StringComparison.Ordinal)
            ? DiyArtStore.ZoomOf(card.CardId) : 100);
        var name = MenuStyles.Label(card.Name, 16); name.AutowrapMode = TextServer.AutowrapMode.WordSmart; _preview.AddChild(name);
        var text = MenuStyles.Label(card.Text.Length == 0 ? "无附加效果" : card.Text, 13);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart; _preview.AddChild(text);
        if (card.Flavor.Length > 0)
        {
            var flavor = MenuStyles.Label(card.Flavor, 12, MenuStyles.Dim);
            flavor.AutowrapMode = TextServer.AutowrapMode.WordSmart; _preview.AddChild(flavor);
        }
        DeleteButton.Text=_draft.SourceCardId.Length>0?"恢复原版":"删除";
        DeleteButton.Disabled = !_library.Cards.Any(c => c.Id == _draft.Id);
        var blocked = _blocked is not null;
        CardName.Editable = Flavor.Editable = !blocked; Cost.Editable = !blocked;
        EffectTextInput.Editable = !blocked;
        if (blocked)
        {
            foreach (var option in new[] { CardType, Nation, Rarity, UnitType }) option.Disabled = true;
            foreach (var spin in new[] { OpCost, Attack, Defense, Armor }) spin.Editable = false;
            foreach (var keyword in _keywords.Values) keyword.Disabled = true;
            ParseReplaceButton.Disabled = ParseAppendButton.Disabled = true;
            OpenEffectsButton.Disabled = true;
        }
        else
        {
            foreach (var option in new[] { CardType, Nation, Rarity }) option.Disabled = false;
            ParseReplaceButton.Disabled = ParseAppendButton.Disabled = false;
            OpenEffectsButton.Disabled = false;
        }
        SaveButton.Disabled = ConstructButton.Disabled = TrialButton.Disabled = ExportButton.Disabled = blocked;
    }
    private void RefreshList()
    {
        Cards.Clear(); _listed.Clear(); _importedListed.Clear();
        foreach (var card in _library.Cards.Where(c => (_scopePack.Length==0||c.PackId==_scopePack)&&$"{c.Name} {c.Id}".Contains(Search.Text, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name))
        {
            _listed.Add(card.Id); Cards.AddItem((_storedError(card.Id) is null ? "" : "⚠ ") + card.Name + " · " + MenuStyles.TypeName(card.CardType));
            Cards.SetItemTooltip(_listed.Count - 1, card.Id);
            if (card.Id == _draft.Id) Cards.Select(_listed.Count - 1);
        }
        if(_scopePack.Length>0 && _workshopLibrary?.Packs.FirstOrDefault(p=>p.Id==_scopePack)?.IsBuiltIn==true)
        {
            var edited=_library.Cards.Where(c=>c.PackId==_scopePack).Select(c=>c.RuntimeId).ToHashSet(StringComparer.Ordinal);
            foreach(var card in _catalog().Where(c=>DeckCardPacks.RawOf(c.CardId)==_scopePack&&!edited.Contains(c.CardId)
                && $"{c.Name} {c.CardId}".Contains(Search.Text,StringComparison.OrdinalIgnoreCase)).OrderBy(c=>c.Name))
            {
                _listed.Add(card.CardId);Cards.AddItem(card.Name+" · 原版");Cards.SetItemTooltip(_listed.Count-1,card.CardId);
                if(card.CardId==_draft.SourceCardId)Cards.Select(_listed.Count-1);
            }
        }
        if (_orcImports is null || _orcImports.Rows.Count == 0) return;
        if (_listed.Count > 0) Cards.AddItem("── OrC 导入 ──"); // 分节头（无 DIY 卡时导入区顶格）
        var outcomes = _importedOutcomes?.Invoke() ?? [];
        var byId = outcomes.Where(o => o.Complete || o.Diagnostics.Count > 0)
            .GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var row in _orcImports.Rows.Where(r => $"{r.Name} {r.Id}".Contains(Search.Text, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Name))
        {
            var outcome = byId.GetValueOrDefault(row.Id);
            var status = outcome is { Complete: true } ? "导入" : "待核对";
            _importedListed.Add(row.Id);
            Cards.AddItem(("▣ " + row.Name + (row.Type is "order" or "指令" ? " · 指令" : " · 单位") + " · " + status));
            Cards.SetItemTooltip(_importedListed.Count - 1 + _listed.Count + (_listed.Count > 0 ? 1 : 0), row.Id);
            if (row.Id == _selectedImported) Cards.Select(_listed.Count + (_listed.Count > 0 ? 1 : 0) + _importedListed.Count - 1);
        }
    }
    private void ShowImportedNotice(string id)
    {
        var outcome = (_importedOutcomes?.Invoke() ?? []).LastOrDefault(o => o.Id == id);
        if (outcome is null) { _notice.Text = "OrC 导入卡 " + id; return; }
        _notice.Text = outcome.Complete
            ? $"OrC 导入卡：{outcome.Name}（已编译入池，可在构筑中直接使用）"
            : $"OrC 导入卡待核对：{outcome.Name} —— {string.Join("；", outcome.Diagnostics)}";
    }
    internal bool Save()
    {
        if (_blocked is not null) { _notice.Text = _storedError(_blocked.Id) ?? "这张卡未通过校验。"; return false; }
        var draft = Read();
        var validation = DiyCardCompiler.Validate(draft, _catalog());
        if (!validation.Success && !draft.OrcEffects.Any(e=>e.DesktopJson is not null||e.PrefabJson is not null)) { _notice.Text = string.Join("；", validation.Errors); return false; }
        try
        {
            _library.Save(draft); _draft = draft.Snapshot() with { Name = draft.Name.Trim() };
            _dirty = false; SaveButton.Text = "保存"; Saved?.Invoke(); RefreshList(); RefreshPreview();
            _notice.Text = validation.Success ? "已校验并保存，可构筑或试打。" : "完整声明草稿已保存，暂不能用于实战："+string.Join("；",validation.Errors);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { _notice.Text = $"未保存：{e.Message}"; return false; }
    }
    private void Validate()
    {
        var result = DiyCardCompiler.Validate(Read(), _catalog());
        _notice.Text = result.Success ? "校验通过：卡牌定义、效果编译、实例化和原生数据往返均成功。尚未保存。"
            : string.Join("；", result.Errors);
    }
    /// <summary>导入本机图片为卡图：解码→转 PNG 存卡库旁 art/（≤4MB），预览即时生效。</summary>
    internal void OnArtImported(string path)
    {
        try
        {
            var image = Image.LoadFromFile(path);
            if (image is null) { _notice.Text = "无法解码该图片（支持 PNG/JPEG/WebP）。"; return; }
            if (image.GetWidth() is < 16 or > 4096 || image.GetHeight() is < 16 or > 4096)
            { _notice.Text = $"图片尺寸须在 16–4096 像素（当前 {image.GetWidth()}×{image.GetHeight()}）。"; return; }
            ImportAdaptedImage(image);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { _notice.Text = $"导入失败：{e.Message}"; }
    }

    /// <summary>规则文本实时解析状态（600ms 防抖，只读提示——落结构必须点解析按钮）。</summary>
    private void RefreshTextNotice()    {
        var text = EffectTextInput.Text.Trim();
        if (text.Length == 0) { TextNotice.Text = ""; return; }
        // 与「解析并替换」同一条后端链：这里只探测，不改结构。
        var probe = BackendTextAuthoring.Parse(_draft.Id.Replace('/', '.'), text, Value(CardType));
        if (probe.Success)
        {
            TextNotice.Text = $"引擎解析：{probe.Effects.Length} 条效果。点解析按钮保留完整结构。";
            TextNotice.AddThemeColorOverride("font_color", MenuStyles.Dim);
        }
        else
        {
            TextNotice.Text = string.Join("\n", probe.Problems.Take(3));
            TextNotice.AddThemeColorOverride("font_color", new Color("dca58b"));
        }
    }

    /// <summary>规则文本 → 结构（替换/追加）。解析只走引擎（CardTextEffectCompiler），失败时不动结构。</summary>
    private void ParseApply(bool replace)
    {
        var text = EffectTextInput.Text.Trim();
        if (text.Length == 0) { TextNotice.Text = "规则文本为空。"; return; }
        // 引擎是唯一真源：解析与编译都经 BackendTextAuthoring 调后端 CardTextEffectCompiler。
        var backend = BackendTextAuthoring.Parse(_draft.Id.Replace('/', '.'), text, Value(CardType));
        if (!backend.Success)
        {
            TextNotice.Text = "引擎不支持这段语法，结构未改动：\n" + string.Join("\n", backend.Problems.Take(4));
            TextNotice.AddThemeColorOverride("font_color", new Color("dca58b"));
            return;
        }
        var drafts = backend.Effects.ToList();
        // 词条名不在后端 op 语法里，仍从旧的解析面取；取不到不影响效果结构。
        IReadOnlyList<OrcKeywordDecl> textKeywords = [];
        try { textKeywords = OrcInspector.CompileText(_draft.Id.Replace('/', '.'), text, Value(CardType), _catalog()).Parse.Keywords; }
        catch (Exception) { /* 词条只是附加信息 */ }
        // 文本词条并入勾选：量化重甲落到重甲参值，其余参值保留在完整声明中。
        var notes = new List<string>();
        foreach (var keyword in textKeywords)
        {
            var token = DiyCardCompiler.KeywordToken(keyword.Id);
            if (_keywords.TryGetValue(token, out var box))
            {
                box.ButtonPressed = true;
                if (keyword.Quantified && token == "armor") Armor.Value = keyword.Value;
                else if (keyword.Quantified)
                    notes.Add($"词条「{keyword.Id}」的参值 {keyword.Value} 已保留，可在完整 Inspector 的 kwValues 中修改。");
            }
            else notes.Add($"词条「{keyword.Id}」保留在完整声明中，可在完整 Inspector 的 kwMap 中修改。");
        }
        if (!replace)
        {
            // 追加＝同触发合并 op（后端模型是「一条触发 + op 序列」，不再走 V8 的 merge 指令）。
            var merged = new List<OrcEffectDraft>(_draft.OrcEffects);
            foreach (var incoming in drafts)
            {
                var hit = merged.FindIndex(e => e.Template == incoming.Template && e.Hook == incoming.Hook && e.Guard == incoming.Guard);
                if (hit < 0) merged.Add(incoming);
                else merged[hit] = merged[hit] with { Ops = [.. merged[hit].Ops, .. incoming.Ops] };
            }
            drafts = merged;
        }
        if (replace) ApplyEffects([.. drafts], []);
        else ApplyEffects([.. drafts], _draft.Effects);
        // 后端不产出桌面卡机制字段（那是 V8 链的私有产物），这里只把文本词条并入卡牌词条。
        if (textKeywords.Count > 0)
            _draft = _draft with { Keywords = _draft.Keywords.Concat(textKeywords.Select(k => DiyCardCompiler.KeywordToken(k.Id))).Distinct(StringComparer.Ordinal).ToArray() };
        TextNotice.Text = $"已解析 {drafts.Count} 条效果并{(replace ? "替换" : "追加")}到效果结构（点「打开效果编辑器」查看）。" + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : "");
        TextNotice.AddThemeColorOverride("font_color", MenuStyles.Dim);
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { _notice.Text = e.Message; }
    }
    private void Export()
    {
        if (!Save()) return;
        var result = DiyCardCompiler.Validate(_draft, _catalog());
        LastExportCode = DiyCardLibrary.Export(_draft);
        var dialog = new AcceptDialog { Title = "导出 DIY 卡牌", MinSize = new(690, 390) }; AddChild(dialog); ActiveExport = dialog;
        var box = new VBoxContainer(); dialog.AddChild(box);
        box.AddChild(MenuStyles.Label("卡牌代码可重新导入；原生数据可用于引擎工具。", 13, MenuStyles.Dim));
        var tabs = new TabContainer { CustomMinimumSize = new(640, 270) }; box.AddChild(tabs);
        void Tab(string name, string text)
        {
            var page = new VBoxContainer { Name = name }; tabs.AddChild(page);
            page.AddChild(new TextEdit { Text = text, Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, SizeFlagsVertical = SizeFlags.ExpandFill });
            page.AddChild(MenuStyles.Button("复制到剪贴板", () => DisplayServer.ClipboardSet(text)));
        }
        Tab("卡牌代码", LastExportCode); Tab("引擎单卡 JSON", result.EngineCardJson??string.Join("\n",result.Errors));
        dialog.Confirmed += () => { ActiveExport = null; dialog.QueueFree(); };
        dialog.Canceled += () => { ActiveExport = null; dialog.QueueFree(); };
        dialog.PopupCentered();
    }
    private void OrcImport()
    {
        var dialog = new ConfirmationDialog { Title = "OrC 批量导入", MinSize = new(700, 460), DialogHideOnOk = false }; AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        box.AddChild(MenuStyles.Label("粘贴 CSV、单卡 JSON、卡牌数组或官方语料 {\"cards\":[…]}。原版 OrC 解析后经 OrC-KSD 编译与实例化；未通过准入的声明完整保留待核对。", 13, MenuStyles.Dim));
        var text = new TextEdit { CustomMinimumSize = new(640, 260), WrapMode = TextEdit.LineWrappingMode.Boundary }; box.AddChild(text);
        var error = MenuStyles.Label("", 13, new("dca58b")); error.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(error);
        dialog.Confirmed += () =>
        {
            try
            {
                var rows = OrcImportService.ParseRows(text.Text);
                var outcomes = OrcImportService.ImportBatch(rows, _catalog());
                _orcImports!.Merge(rows);
                var complete = outcomes.Count(o => o.Complete);
                var pending = outcomes.Count - complete;
                var samples = outcomes.Where(o => !o.Complete).Take(3)
                    .Select(o => $"{o.Name}：{o.Diagnostics.FirstOrDefault()}");
                error.AddThemeColorOverride("font_color", MenuStyles.Gold);
                error.Text = $"导入完成：成功 {complete} 张，待核对 {pending} 张。" +
                    (pending > 0 ? $"\n待核对待修订（原文保留）：\n{string.Join("\n", samples)}" : "");
                RefreshList(); ImportedChanged?.Invoke();
                if (pending == 0) { dialog.QueueFree(); _notice.Text = $"OrC 导入完成：成功 {complete} 张。"; }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Text.Json.JsonException or ArgumentException or FormatException)
            { error.Text = e.Message; }
        };
        dialog.Canceled += () => dialog.QueueFree();
        dialog.PopupCentered();
    }
    private void Import()
    {
        var dialog = new ConfirmationDialog { Title = "导入 DIY 卡牌", MinSize = new(650, 330), DialogHideOnOk = false }; AddChild(dialog); ActiveImport = dialog;
        var box = new VBoxContainer(); dialog.AddChild(box);
        box.AddChild(MenuStyles.Label("粘贴 ORCDIY1: 卡牌代码。校验后另存为新卡，不覆盖现有卡牌。", 13, MenuStyles.Dim));
        var text = new TextEdit { CustomMinimumSize = new(590, 170), WrapMode = TextEdit.LineWrappingMode.Boundary }; box.AddChild(text); ImportText = text;
        var error = MenuStyles.Label("", 13, new("dca58b")); error.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(error);
        dialog.Confirmed += () =>
        {
            try
            {
                var card = DiyCardLibrary.Import(text.Text) with{PackId=_scopePack};
                var validation = DiyCardCompiler.Validate(card, _catalog());
                if (!validation.Success) { error.Text = string.Join("；", validation.Errors); return; }
                _library.Save(card); Saved?.Invoke(); Load(card); _notice.Text = "已导入并保存为新卡。";
                ActiveImport = null; ImportText = null; dialog.QueueFree();
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { error.Text = e.Message; }
        };
        dialog.Canceled += () => { ActiveImport = null; ImportText = null; dialog.QueueFree(); };
        dialog.PopupCentered();
    }
}
