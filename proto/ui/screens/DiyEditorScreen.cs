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
    internal ConfirmationDialog DeleteDialog = null!, DiscardDialog = null!;
    internal ConfirmationDialog? ActiveImport;
    internal TextEdit? ImportText;
    internal AcceptDialog? ActiveExport;
    internal ScrollContainer EditorScroll = null!;
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
        title.AddChild(UiStyles.Label("DIY 卡牌", 26, UiStyles.Gold));
        title.AddChild(UiStyles.Label("编辑 → 校验 → 保存 → 构筑 / 试打", 13, UiStyles.Dim));
        NewButton = UiStyles.Button("新建", () => Guard(() => Load(new(){PackId=_scopePack}))); heading.AddChild(NewButton);
        CopyButton = UiStyles.Button("复制", Copy); heading.AddChild(CopyButton);
        SaveButton = UiStyles.Button("保存", () => Save()); heading.AddChild(SaveButton);
        ImportButton = UiStyles.Button("导入", () => Guard(Import)); heading.AddChild(ImportButton);
        ExportButton = UiStyles.Button("导出", Export); heading.AddChild(ExportButton);
        if (_orcImports is not null)
        {
            var orcButton = UiStyles.Button("OrC 批量导入", OrcImport); heading.AddChild(orcButton);
            orcButton.TooltipText = "粘贴官方语料 JSON（行数组或 {\"cards\":[…]}），自动解析为引擎卡；解析不了的留诊断待核对。";
        }
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; AddChild(body);
        var listPanel = Panel(205); body.AddChild(listPanel);
        var list = new VBoxContainer(); listPanel.AddChild(list);
        Search = new LineEdit { PlaceholderText = "搜索本地 DIY 卡牌" }; list.AddChild(Search);
        Search.TextChanged += _ => RefreshList();
        Cards = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(170, 100) }; list.AddChild(Cards);
        Cards.ItemSelected += index =>
        {
            if (index < 0) return;
            var i = (int)index;
            if (i < _listed.Count)
            {
                var card = _library.Cards.First(c => c.Id == _listed[i]);
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
        DeleteButton = UiStyles.Button("删除", () =>
        {
            if (_selectedImported is not null)
            {
                DeleteDialog.DialogText = "删除这张 OrC 导入卡？引用它的卡组需要重新编辑。";
                DeleteDialog.PopupCentered(); return;
            }
            var uses = _usage(_draft.Id);
            DeleteDialog.DialogText = uses == 0 ? "删除这张已保存的 DIY 卡牌？"
                : $"有 {uses} 副卡组引用这张卡。删除后这些卡组需要重新编辑。仍然删除？";
            DeleteDialog.PopupCentered();
        }); list.AddChild(DeleteButton);
        var editPanel = Panel(450); editPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill; body.AddChild(editPanel);
        var editScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; editPanel.AddChild(editScroll);
        EditorScroll = editScroll;
        var form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; editScroll.AddChild(form);
        // ── 编辑区：分区面板布局（每节独立边距＋节标题），可读性优先 ──
        form.AddThemeConstantOverride("separation", 12);
        var basic = Section(form, "基础信息");
        _identity = UiStyles.Label("", 11, UiStyles.Dim); basic.AddChild(_identity);
        CardName = new LineEdit { MaxLength = 40, PlaceholderText = "卡牌名称" }; Field(basic, "名称", CardName);
        CardName.TextChanged += _ => Changed();
        var types = new GridContainer { Columns = 3 }; basic.AddChild(types);
        CardType = Option([new("unit", "单位"), new("order", "指令")]); Field(types, "卡牌类别", CardType);
        Nation = Option(DiyCardCompiler.Nations); Field(types, "国籍", Nation);
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
        BuildNationFields(basic);
        basic.AddChild(UiStyles.Label("单位词条", 14, UiStyles.Gold));
        var keywordGrid = new GridContainer { Columns = 4 }; _keywordGrid=keywordGrid;basic.AddChild(keywordGrid);
        foreach (var keyword in DiyCardCompiler.Keywords)
        {
            var box = new CheckBox { Text = keyword.Label, TooltipText = keyword.Label }; keywordGrid.AddChild(box);
            _keywords[keyword.Id] = box; box.Toggled += _ => Changed();
        }
        // 自导入卡图：本机图片 → 卡库旁 art/ 目录（PNG），缩放 50–300% 只作用于导入图。
        var artRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        artRow.AddThemeConstantOverride("separation", 8); basic.AddChild(artRow);
        ImportArtButton = UiStyles.Button("导入卡图…", () => _artDialog.Popup()); artRow.AddChild(ImportArtButton);
        RemoveArtButton = UiStyles.Button("移除卡图", () => { _importedArt = ""; _draft = _draft with { ArtCardId = "",ArtSourceFile="",ArtRegion=new(0,0,1,1) };_cropApply.Disabled=true; Changed(); RefreshPreview(); });
        artRow.AddChild(RemoveArtButton);
        _artStatus = UiStyles.Label("", 12, UiStyles.Dim);
        _artStatus.SizeFlagsHorizontal = SizeFlags.ExpandFill; _artStatus.ClipText = true;
        artRow.AddChild(_artStatus);
        ArtZoom = Number(50, 300); Field(basic, "导入图缩放 %（50＝缩小留边，100＝铺满，300＝放大裁切）", ArtZoom);
        ArtZoom.ValueChanged += v => { if (!_loading && _importedArt.Length > 0) { DiyArtStore.UpdateZoom(_draft.Id, (int)v); Changed(); RefreshPreview(); } };
        _artDialog = new FileDialog
        {
            Title = "选择卡图图片", Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true, Size = new(720, 480)
        };
        _artDialog.Filters = ["*.png ; PNG 图片", "*.jpg ; JPEG 图片", "*.jpeg ; JPEG 图片", "*.webp ; WebP 图片"];
        _artDialog.FileSelected += OnArtImported; AddChild(_artDialog);
        BuildArtAdaptation(basic);

        var textSec = Section(form, "规则文本 —— 自然语言解析入口");
        var hintText = UiStyles.Label("写一句效果（例：部署：随机对 1 个敌方单位造成 2 点伤害。），解析后落到下方效果结构；文本同时作为卡面规则文字。解析失败时不改动现有结构。", 12, UiStyles.Dim);
        hintText.AutowrapMode = TextServer.AutowrapMode.WordSmart; textSec.AddChild(hintText);
        EffectTextInput = new TextEdit
        {
            CustomMinimumSize = new(0, 90), WrapMode = TextEdit.LineWrappingMode.Boundary,
            PlaceholderText = "每行一条效果；支持：部署 / 亡计 / 使用时 / 回合开始 / 攻击后 / 监听句……"
        };
        textSec.AddChild(EffectTextInput);
        var parseRow = new HBoxContainer(); parseRow.AddThemeConstantOverride("separation", 8); textSec.AddChild(parseRow);
        ParseReplaceButton = UiStyles.Button("解析并替换", () => ParseApply(replace: true)); parseRow.AddChild(ParseReplaceButton);
        ParseAppendButton = UiStyles.Button("解析并追加", () => ParseApply(replace: false)); parseRow.AddChild(ParseAppendButton);
        TextNotice = UiStyles.Label("", 12, UiStyles.Dim);
        TextNotice.AutowrapMode = TextServer.AutowrapMode.WordSmart; textSec.AddChild(TextNotice);
        _textTimer = new Godot.Timer { OneShot = true, WaitTime = 0.6 }; AddChild(_textTimer);
        _textTimer.Timeout += RefreshTextNotice;
        EffectTextInput.TextChanged += () => { if (!_loading) { Changed(); _textTimer.Start(); } };

        var inspSec = Section(form, "效果");
        _effectsSummary = UiStyles.Label("", 13, UiStyles.Dim);
        _effectsSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart; inspSec.AddChild(_effectsSummary);
        OpenEffectsButton = UiStyles.Button("打开效果编辑器…", () => EffectsEditorRequested?.Invoke());
        OpenEffectsButton.TooltipText = "在独立屏幕中编辑效果结构（触发/动作/参数），最右侧实时显示原始 DSL。";
        inspSec.AddChild(OpenEffectsButton);
        inspSec.AddChild(UiStyles.Label("规则文本解析出的效果也会落到效果编辑器；卡面文字仍由本屏规则文本承载。", 12, UiStyles.Dim));

        var flavorSec = Section(form, "风味文字");
        Flavor = new TextEdit { CustomMinimumSize = new(0, 65), WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = "风味文字，不作为规则执行（最多 300 字）" };
        Field(flavorSec, "风味文字", Flavor); Flavor.TextChanged += Changed;
        var previewPanel = Panel(245); body.AddChild(previewPanel);
        PreviewPanel = previewPanel;
        var previewSide = new VBoxContainer(); previewPanel.AddChild(previewSide);
        previewSide.AddChild(UiStyles.Label("卡面预览", 17, UiStyles.Gold));
        var previewScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; previewSide.AddChild(previewScroll);
        _preview = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; previewScroll.AddChild(_preview);
        ValidateButton = UiStyles.Button("校验", Validate); previewSide.AddChild(ValidateButton);
        ConstructButton = UiStyles.Button("用于构筑", () => { if (Save()) ConstructRequested?.Invoke(_draft.Id); }); previewSide.AddChild(ConstructButton);
        TrialButton = UiStyles.Button("试打", () =>
        {
            if (_selectedImported is not null)
            {
                var outcome = (_importedOutcomes?.Invoke() ?? []).LastOrDefault(o => o.Id == _selectedImported && o.Complete);
                if (outcome is null) { _notice.Text = "这张导入卡还在待核对（未编译入池），不能试打。"; return; }
                ImportedTrialRequested?.Invoke(outcome); return;
            }
            if (Save()) TrialRequested?.Invoke(_draft.Snapshot());
        }); previewSide.AddChild(TrialButton);
        TrialButton.TooltipText = "用临时 12 张卡牌的卡组随机发牌；按稀有度加入最多 2 张当前卡，不改动已保存卡组。";
        _notice = UiStyles.Label("", 13, UiStyles.Dim); _notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
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
            _library.Delete(_draft.Id); Saved?.Invoke(); Load(new()); _notice.Text = "卡牌已删除；引用它的卡组请重新编辑。";
        });
        DiscardDialog = new ConfirmationDialog { Title = "未保存的卡牌", DialogText = "丢弃当前修改并继续？" }; AddChild(DiscardDialog);
        DiscardDialog.Confirmed += () => { var action = _afterDiscard; _afterDiscard = null; action?.Invoke(); };
        DiscardDialog.Canceled += () => { _afterDiscard = null; RefreshList(); };
        Load(_library.Cards.FirstOrDefault() ?? new());
        if (_library.LoadError is { } error) _notice.Text = error;
    }
    private static PanelContainer Panel(float width) => new() { CustomMinimumSize = new(width, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
    /// <summary>分区面板：独立边距＋节标题——可读性硬要求（不把所有控件挤在一起）。</summary>
    private static VBoxContainer Section(VBoxContainer parent, string title)
    {
        var panel = new PanelContainer(); parent.AddChild(panel);
        var margin = new MarginContainer();
        foreach (var (name, value) in new[] { ("margin_left", 12), ("margin_top", 8), ("margin_right", 12), ("margin_bottom", 12) })
            margin.AddThemeConstantOverride(name, value);
        panel.AddChild(margin);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);
        box.AddChild(UiStyles.Label(title, 16, UiStyles.Gold));
        return box;
    }
    internal static Control Field(Node parent, string label, Control control)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 4); parent.AddChild(box);
        box.AddChild(UiStyles.Label(label, 12, UiStyles.Dim)); box.AddChild(control);
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return box;
    }
    internal static OptionButton Option(IEnumerable<UiDiyChoice> choices)
    {
        var option = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(90, 34) };
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
            Load(current with { Id = id, ArtFile=art, ArtSourceFile=source, Name = (current.Name + " 副本")[..Math.Min(40, current.Name.Length + 3)] });
        }
        catch(Exception e){_notice.Text="复制卡图失败："+e.Message;return;}
        Changed(); _notice.Text = "已创建副本，保存后才会进入卡库。";
    }
    internal void Load(DiyCardDraft draft)
    {
        if(draft.PackId.Length==0&&_scopePack.Length>0)draft=draft with{PackId=_scopePack};
        _blocked = _storedError(draft.Id) is null || draft.OrcEffects.Any(e=>e.DesktopJson is not null) ? null : draft.Snapshot();
        _loading = true; _draft = draft.Snapshot();
        RefreshWorkshopVocabulary(draft.PackId);
        CardName.Text = draft.Name; Select(CardType, draft.CardType); Select(Nation, draft.Nation); Select(Rarity, draft.Rarity);
        Select(UnitType, draft.UnitType);
        _importedArt = draft.ArtFile; ArtZoom.Value = draft.ArtScale;
        Cost.Value = draft.Cost; OpCost.Value = draft.OpCost; Attack.Value = draft.Attack; Defense.Value = draft.Defense; Armor.Value = draft.Armor;
        foreach (var pair in _keywords) pair.Value.ButtonPressed = draft.Keywords.Contains(pair.Key);
        Flavor.Text = draft.Flavor; EffectTextInput.Text = draft.EffectText;
        if (draft.OrcEffects.Length == 0 && draft.EffectText.Trim().Length > 0)
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
        RefreshList(); RefreshPreview(); RefreshTextNotice(); RefreshEffectsSummary(); EditorScroll.ScrollVertical = 0;
        if (_blocked is not null) _notice.Text = $"卡牌未通过校验，原声明保留，不能用于实战：{_storedError(draft.Id)}";
    }
    private DiyCardDraft Read() => _blocked?.Snapshot() ?? _draft with
    {
        Name = CardName.Text, CardType = Value(CardType), Nation = Value(Nation), Rarity = Value(Rarity), UnitType = Value(UnitType),
        Cost = (int)Cost.Value, OpCost = (int)OpCost.Value, Attack = (int)Attack.Value, Defense = (int)Defense.Value,
        Keywords = _draft.DesktopCardJson is not null ? _draft.Keywords : Value(CardType) == "unit" ? _keywords.Where(k => k.Value.ButtonPressed).Select(k => k.Key).ToArray() : [],
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
        _draft = _draft.Snapshot() with { OrcEffects = orcEffects, Effects = legacyEffects };
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
        UnitType.Disabled = !isUnit; OpCost.Editable = isUnit; Attack.Editable = isUnit; Defense.Editable = isUnit;
        foreach (var keyword in _keywords.Values) keyword.Disabled = !isUnit || draft.DesktopCardJson is not null;
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
        UiStyles.Clear(_preview);
        var control = new CardControl(); control.Bind(card, _textures, 200); _preview.AddChild(control);
        var name = UiStyles.Label(card.Name, 16); name.AutowrapMode = TextServer.AutowrapMode.WordSmart; _preview.AddChild(name);
        var text = UiStyles.Label(card.Text.Length == 0 ? "无附加效果" : card.Text, 13);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart; _preview.AddChild(text);
        if (card.Flavor.Length > 0)
        {
            var flavor = UiStyles.Label(card.Flavor, 12, UiStyles.Dim);
            flavor.AutowrapMode = TextServer.AutowrapMode.WordSmart; _preview.AddChild(flavor);
        }
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
            _listed.Add(card.Id); Cards.AddItem((_storedError(card.Id) is null ? "" : "⚠ ") + card.Name + (card.CardType == "unit" ? " · 单位" : " · 指令"));
            Cards.SetItemTooltip(_listed.Count - 1, card.Id);
            if (card.Id == _draft.Id) Cards.Select(_listed.Count - 1);
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
        if (!validation.Success && !draft.OrcEffects.Any(e=>e.DesktopJson is not null)) { _notice.Text = string.Join("；", validation.Errors); return false; }
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
        try
        {
            var compile = OrcInspector.CompileText(_draft.Id.Replace('/', '.'), text, Value(CardType), _catalog());
            TextNotice.Text = compile.Parse.Complete
                ? $"完整解析：{compile.Effects.Count} 条效果。" + (compile.AllCompiled ? "原生编译通过。" : "原生准入：" + string.Join("；", compile.Problems.Take(2))) + "点解析按钮保留完整结构。"
                : string.Join("\n", compile.Parse.Diagnostics.Select(d => d.ToString()).Take(4));
            TextNotice.AddThemeColorOverride("font_color", compile.Parse.Complete ? UiStyles.Dim : new Color("dca58b"));
        }
        catch (Exception e) { TextNotice.Text = "解析失败：" + e.Message; }
    }

    /// <summary>规则文本 → 结构（替换/追加）。解析不完整或有触发不在编辑面内时不动结构（诚实诊断）。</summary>
    private void ParseApply(bool replace)
    {
        var text = EffectTextInput.Text.Trim();
        if (text.Length == 0) { TextNotice.Text = "规则文本为空。"; return; }
        OrcCompileResult parse;
        try { parse = OrcInspector.CompileText(_draft.Id.Replace('/', '.'), text, Value(CardType), _catalog()).Parse; }
        catch (Exception e) { TextNotice.Text = "解析失败：" + e.Message; return; }
        if (!parse.Complete)
        {
            TextNotice.Text = "未解析完整，结构未改动：\n" + string.Join("\n", parse.Diagnostics.Select(d => d.ToString()).Take(5));
            TextNotice.AddThemeColorOverride("font_color", new Color("dca58b"));
            return;
        }
        List<OrcEffectDraft> drafts;
        try { drafts = parse.Effects.Select(OrcEffectEditing.ToDraft).ToList(); }
        catch (InvalidDataException e)
        {
            TextNotice.Text = "有触发不在结构编辑面内，结构未改动：" + e.Message;
            TextNotice.AddThemeColorOverride("font_color", new Color("dca58b"));
            return;
        }
        // 文本词条并入勾选：量化重甲落到重甲参值，其余参值保留在完整声明中。
        var notes = new List<string>();
        foreach (var keyword in parse.Keywords)
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
        if(!replace)
        {
            var used=_draft.OrcEffects.Where(e=>e.DesktopJson is not null)
                .SelectMany(e=>System.Text.Json.Nodes.JsonNode.Parse(e.DesktopJson!)!["targets"]?.AsArray().Select(t=>t?["id"]?.ToString())??[]).Where(id=>id is not null).ToArray();
            var merged=DesktopOrc.Request(new {method="merge",effects=drafts.Select(d=>System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(d.DesktopJson!)),used});
            drafts=merged.EnumerateArray().Select(e=>new OrcEffectDraft{Template=e.GetProperty("trigger").GetString()!,DesktopJson=e.GetRawText()}).ToList();
        }
        if (replace) ApplyEffects([.. drafts], []);
        else ApplyEffects([.. _draft.OrcEffects, .. drafts], _draft.Effects);
        if(parse.DesktopResultJson is { } resultJson)
        {
            using var result=System.Text.Json.JsonDocument.Parse(resultJson);
            var metadata=_draft.DesktopCardJson is { } previous?System.Text.Json.Nodes.JsonNode.Parse(previous)!.AsObject():new System.Text.Json.Nodes.JsonObject();
            foreach(var field in result.RootElement.GetProperty("cardFields").EnumerateObject())metadata[field.Name]=System.Text.Json.Nodes.JsonNode.Parse(field.Value.GetRawText());
            foreach(var name in new[]{"kwMap","kwValues"})
            {
                var merged=metadata[name]?.DeepClone() as System.Text.Json.Nodes.JsonObject??new();
                foreach(var p in result.RootElement.GetProperty(name).EnumerateObject())merged[p.Name]=System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());
                metadata[name]=merged;
            }
            _draft=_draft with {DesktopCardJson=metadata.ToJsonString(),Keywords=_draft.Keywords.Concat(parse.Keywords.Select(k=>DiyCardCompiler.KeywordToken(k.Id))).Distinct(StringComparer.Ordinal).ToArray()};
        }
        TextNotice.Text = $"已解析 {drafts.Count} 条效果并{(replace ? "替换" : "追加")}到效果结构（点「打开效果编辑器」查看）。" + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : "");
        TextNotice.AddThemeColorOverride("font_color", UiStyles.Dim);
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
        box.AddChild(UiStyles.Label("卡牌代码可重新导入；原生数据可用于引擎工具。", 13, UiStyles.Dim));
        var tabs = new TabContainer { CustomMinimumSize = new(640, 270) }; box.AddChild(tabs);
        void Tab(string name, string text)
        {
            var page = new VBoxContainer { Name = name }; tabs.AddChild(page);
            page.AddChild(new TextEdit { Text = text, Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, SizeFlagsVertical = SizeFlags.ExpandFill });
            page.AddChild(UiStyles.Button("复制到剪贴板", () => DisplayServer.ClipboardSet(text)));
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
        box.AddChild(UiStyles.Label("粘贴 CSV、单卡 JSON、卡牌数组或官方语料 {\"cards\":[…]}。原版 OrC 解析后经 OrC-KSD 编译与实例化；未通过准入的声明完整保留待核对。", 13, UiStyles.Dim));
        var text = new TextEdit { CustomMinimumSize = new(640, 260), WrapMode = TextEdit.LineWrappingMode.Boundary }; box.AddChild(text);
        var error = UiStyles.Label("", 13, new("dca58b")); error.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(error);
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
                error.AddThemeColorOverride("font_color", UiStyles.Gold);
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
        box.AddChild(UiStyles.Label("粘贴 ORCDIY1: 卡牌代码。校验后另存为新卡，不覆盖现有卡牌。", 13, UiStyles.Dim));
        var text = new TextEdit { CustomMinimumSize = new(590, 170), WrapMode = TextEdit.LineWrappingMode.Boundary }; box.AddChild(text); ImportText = text;
        var error = UiStyles.Label("", 13, new("dca58b")); error.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(error);
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
