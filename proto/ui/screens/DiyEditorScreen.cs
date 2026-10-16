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
    private readonly List<EffectRow> _effects = [];
    private readonly List<string> _listed = [];
    private VBoxContainer _effectBox = null!, _preview = null!;
    private Label _notice = null!, _identity = null!, _trigger = null!;
    internal ItemList Cards = null!;
    internal LineEdit CardName = null!, Search = null!;
    internal OptionButton CardType = null!, Nation = null!, Rarity = null!, UnitType = null!, Art = null!;
    internal SpinBox Cost = null!, OpCost = null!, Attack = null!, Defense = null!, Armor = null!;
    internal TextEdit Flavor = null!, EffectTextInput = null!;
    internal Label TextNotice = null!;
    private Godot.Timer _textTimer = null!;
    // ── OrC Inspector（结构编辑面）──
    private readonly List<OrcEffectRow> _orcRows = [];
    private VBoxContainer _orcBox = null!, _rowsSection = null!;
    internal Label OrcNotice = null!;
    internal Button ParseReplaceButton = null!, ParseAppendButton = null!, UndoButton = null!, RedoButton = null!, OrcJsonButton = null!, AddOrcButton = null!;
    internal OptionButton RecipePick = null!;
    private Godot.Timer _orcTimer = null!;
    private readonly List<string> _history = [];
    private int _historyPos = -1;
    internal Button SaveButton = null!, NewButton = null!, CopyButton = null!, DeleteButton = null!, AddEffectButton = null!, ValidateButton = null!, TrialButton = null!, ConstructButton = null!, ExportButton = null!, ImportButton = null!;
    internal ConfirmationDialog DeleteDialog = null!, DiscardDialog = null!;
    internal ConfirmationDialog? ActiveImport;
    internal TextEdit? ImportText;
    internal AcceptDialog? ActiveExport;
    internal ScrollContainer EditorScroll = null!;
    internal PanelContainer PreviewPanel = null!;
    internal IReadOnlyList<EffectRow> EffectRows => _effects;
    internal string LastExportCode = "";
    public event Action? Saved;
    public event Action<string>? ConstructRequested;
    public event Action<DiyCardDraft>? TrialRequested;
    /// <summary>OrC 批量导入落盘后通知（Main 重编译快照并刷新构筑池）。</summary>
    public event Action? ImportedChanged;
    /// <summary>试打一张 OrC 导入卡（与 DIY 试打同一临时卡组流程）。</summary>
    public event Action<OrcImportOutcome>? ImportedTrialRequested;
    public DiyCardDraft Draft => Read();
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
        NewButton = UiStyles.Button("新建", () => Guard(() => Load(new()))); heading.AddChild(NewButton);
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
        basic.AddChild(UiStyles.Label("单位词条", 14, UiStyles.Gold));
        var keywordGrid = new GridContainer { Columns = 4 }; basic.AddChild(keywordGrid);
        foreach (var keyword in DiyCardCompiler.Keywords)
        {
            var box = new CheckBox { Text = keyword.Label, TooltipText = keyword.Label }; keywordGrid.AddChild(box);
            _keywords[keyword.Id] = box; box.Toggled += _ => Changed();
        }
        Art = Option(new[] { new UiDiyChoice("", "文字卡面") }.Concat(catalog()
            .Where(c => c.ArtPath.Length > 0 && !c.CardId.StartsWith("diy/", StringComparison.Ordinal))
            .OrderBy(c => c.Name).Select(c => new UiDiyChoice(c.CardId, c.Name + " · " + c.Set))).ToArray());
        Field(basic, "卡图（现有素材）", Art); Art.ItemSelected += _ => Changed();

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

        var inspSec = Section(form, "效果结构（Inspector，可编辑）");
        var toolRow = new HBoxContainer(); toolRow.AddThemeConstantOverride("separation", 8); inspSec.AddChild(toolRow);
        UndoButton = UiStyles.Button("撤销", Undo); toolRow.AddChild(UndoButton);
        RedoButton = UiStyles.Button("重做", Redo); toolRow.AddChild(RedoButton);
        OrcJsonButton = UiStyles.Button("效果 JSON", OrcJsonDialog); toolRow.AddChild(OrcJsonButton);
        OrcJsonButton.TooltipText = "查看当前结构的编译产物 JSON（只读）；编辑面是结构本身，改结构即改效果。";
        RecipePick = Option(new[] { new UiDiyChoice("", "从常见效果开始…") }
            .Concat(OrcEffectEditing.Recipes.Select((r, i) => new UiDiyChoice(i.ToString(), r.Name))).ToArray());
        toolRow.AddChild(RecipePick);
        toolRow.AddChild(UiStyles.Button("添加", () =>
        {
            if (RecipePick.Selected <= 0) { _notice.Text = "先选一个常见效果。"; return; }
            AddOrc(OrcEffectEditing.Recipes[RecipePick.Selected - 1].Effect);
            _notice.Text = "已添加「" + OrcEffectEditing.Recipes[RecipePick.Selected - 1].Name + "」。";
        }));
        inspSec.AddChild(UiStyles.Label("① 选发生时机 → ② 改对象和动作 → ③ 保存并试打。结构是编译真源；规则文本不随结构修改回写。", 12, UiStyles.Dim));
        _orcBox = new VBoxContainer(); _orcBox.AddThemeConstantOverride("separation", 10); inspSec.AddChild(_orcBox);
        AddOrcButton = UiStyles.Button("＋ 再加一条效果", () => AddOrc(new OrcEffectDraft()));
        inspSec.AddChild(AddOrcButton);
        OrcNotice = UiStyles.Label("", 12, UiStyles.Dim);
        OrcNotice.AutowrapMode = TextServer.AutowrapMode.WordSmart; inspSec.AddChild(OrcNotice);
        _orcTimer = new Godot.Timer { OneShot = true, WaitTime = 0.5 }; AddChild(_orcTimer);
        _orcTimer.Timeout += OrcCheck;

        _rowsSection = Section(form, "简易效果行（旧卡兼容）");
        _rowsSection.AddChild(UiStyles.Label("九宫格行编辑器，表达不了引擎 21 动作面；新效果请用上方结构编辑或规则文本解析。", 12, UiStyles.Dim));
        _trigger = UiStyles.Label("", 14, UiStyles.Gold); _rowsSection.AddChild(_trigger);
        _effectBox = new VBoxContainer(); _rowsSection.AddChild(_effectBox);
        AddEffectButton = UiStyles.Button("＋ 添加效果", () => AddEffect(new()));
        _rowsSection.AddChild(AddEffectButton);

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
    private static Control Field(Node parent, string label, Control control)
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
    private static SpinBox Number(int min, int max) => new() { MinValue = min, MaxValue = max, Step = 1, CustomMinimumSize = new(65, 34) };
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
        Load(current with { Id = "diy/" + Guid.NewGuid().ToString("N"), Name = (current.Name + " 副本")[..Math.Min(40, current.Name.Length + 3)] });
        Changed(); _notice.Text = "已创建副本，保存后才会进入卡库。";
    }
    internal void Load(DiyCardDraft draft)
    {
        _blocked = _storedError(draft.Id) is null ? null : draft.Snapshot();
        _loading = true; _draft = draft.Snapshot();
        CardName.Text = draft.Name; Select(CardType, draft.CardType); Select(Nation, draft.Nation); Select(Rarity, draft.Rarity);
        Select(UnitType, draft.UnitType); Select(Art, draft.ArtCardId);
        Cost.Value = draft.Cost; OpCost.Value = draft.OpCost; Attack.Value = draft.Attack; Defense.Value = draft.Defense; Armor.Value = draft.Armor;
        foreach (var pair in _keywords) pair.Value.ButtonPressed = draft.Keywords.Contains(pair.Key);
        Flavor.Text = draft.Flavor; EffectTextInput.Text = draft.EffectText;
        foreach (var row in _effects) { _effectBox.RemoveChild(row); row.QueueFree(); } _effects.Clear();
        foreach (var effect in draft.Effects) AddEffect(effect);
        foreach (var row in _orcRows) { _orcBox.RemoveChild(row); row.QueueFree(); } _orcRows.Clear();
        if (draft.OrcEffects.Length > 0)
            foreach (var orc in draft.OrcEffects) AddOrcRow(orc);
        else if (draft.EffectText.Trim().Length > 0)
        {
            // 旧版文本卡自动升级：完整解析则落到结构编辑面；不完整保持原样（文本模式仍可编译）。
            var hydration = OrcInspector.CompileText(draft.Id.Replace('/', '.'), draft.EffectText.Trim(), draft.CardType);
            if (hydration.Parse.Complete)
                foreach (var effect in hydration.Parse.Effects)
                { try { AddOrcRow(OrcEffectEditing.ToDraft(effect)); } catch (InvalidDataException) { } }
        }
        RenumberOrc();
        _loading = false; _dirty = false; SaveButton.Text = "保存"; _notice.Text = "";
        _history.Clear(); _historyPos = -1; Snap();
        RefreshList(); RefreshPreview(); RefreshTextNotice(); EditorScroll.ScrollVertical = 0;
        if (_blocked is not null) _notice.Text = $"卡牌未通过校验，原声明保留，不能用于实战：{_storedError(draft.Id)}";
    }
    private DiyCardDraft Read() => _blocked?.Snapshot() ?? _draft with
    {
        Name = CardName.Text, CardType = Value(CardType), Nation = Value(Nation), Rarity = Value(Rarity), UnitType = Value(UnitType),
        Cost = (int)Cost.Value, OpCost = (int)OpCost.Value, Attack = (int)Attack.Value, Defense = (int)Defense.Value,
        Keywords = Value(CardType) == "unit" ? _keywords.Where(k => k.Value.ButtonPressed).Select(k => k.Key).ToArray() : [],
        Armor = (int)Armor.Value, ArtCardId = Value(Art), Flavor = Flavor.Text, EffectText = EffectTextInput.Text,
        Effects = _effects.Select(r => r.Read()).ToArray(), OrcEffects = _orcRows.Select(r => r.Read()).ToArray()
    };
    private void Changed()
    {
        if (_loading) return;
        _dirty = true; SaveButton.Text = "保存 *"; _notice.Text = "有未保存的修改。"; RefreshPreview();
    }
    private void RefreshPreview()
    {
        var draft = Read(); var isUnit = draft.CardType == "unit";
        UnitType.Disabled = !isUnit; OpCost.Editable = isUnit; Attack.Editable = isUnit; Defense.Editable = isUnit;
        foreach (var keyword in _keywords.Values) keyword.Disabled = !isUnit;
        Armor.Editable = isUnit && _keywords["armor"].ButtonPressed;
        _trigger.Text = isUnit ? "部署完成时执行" : "打出时执行";
        _identity.Text = draft.Id; _identity.ClipText = true; _identity.TooltipText = draft.Id;
        AddEffectButton.Disabled = _effects.Count >= 8;
        var orcMode = _orcRows.Count > 0;
        var textMode = EffectTextInput.Text.Trim().Length > 0;
        _rowsSection.Visible = !textMode && !orcMode; // 分区可见性：结构/文本模式下旧行编辑器整节隐藏，不留挤在一起的假入口
        AddEffectButton.Disabled = textMode || orcMode || _effects.Count >= 8;
        AddEffectButton.TooltipText = textMode || orcMode ? "效果由规则文本/结构编辑面承载，行编辑器不参与编译。" : "";
        foreach (var row in _effects)
        {
            row.UpdateType(isUnit);
            if (textMode)
                foreach (var childControl in row.FindChildren("*", "", true, false))
                {
                    if (childControl is BaseButton button) button.Disabled = true;
                    if (childControl is SpinBox spin) spin.Editable = false;
                }
        }
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
            foreach (var option in new[] { CardType, Nation, Rarity, UnitType, Art }) option.Disabled = true;
            foreach (var spin in new[] { OpCost, Attack, Defense, Armor }) spin.Editable = false;
            foreach (var keyword in _keywords.Values) keyword.Disabled = true;
            foreach (var row in _effects)
                foreach (var childControl in row.FindChildren("*", "", true, false))
                {
                    if (childControl is BaseButton button) button.Disabled = true;
                    if (childControl is SpinBox spin) spin.Editable = false;
                }
            AddEffectButton.Disabled = true;
            _orcRows.ForEach(r => r.SetEnabled(false));
            ParseReplaceButton.Disabled = ParseAppendButton.Disabled = true;
            UndoButton.Disabled = RedoButton.Disabled = OrcJsonButton.Disabled = true;
        }
        else
        {
            foreach (var option in new[] { CardType, Nation, Rarity, Art }) option.Disabled = false;
            _orcRows.ForEach(r => r.SetEnabled(true));
            ParseReplaceButton.Disabled = ParseAppendButton.Disabled = false;
            OrcJsonButton.Disabled = false; SyncHistory();
        }
        SaveButton.Disabled = ConstructButton.Disabled = TrialButton.Disabled = ExportButton.Disabled = blocked;
    }
    private void RefreshList()
    {
        Cards.Clear(); _listed.Clear(); _importedListed.Clear();
        foreach (var card in _library.Cards.Where(c => $"{c.Name} {c.Id}".Contains(Search.Text, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name))
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
        if (!validation.Success) { _notice.Text = string.Join("；", validation.Errors); return false; }
        try
        {
            _library.Save(draft); _draft = draft.Snapshot() with { Name = draft.Name.Trim() };
            _dirty = false; SaveButton.Text = "保存"; Saved?.Invoke(); RefreshList(); RefreshPreview();
            _notice.Text = "已校验并保存，可构筑或试打。";
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
    /// <summary>规则文本实时解析状态（600ms 防抖，只读提示——落结构必须点解析按钮）。</summary>
    private void RefreshTextNotice()
    {
        var text = EffectTextInput.Text.Trim();
        if (text.Length == 0) { TextNotice.Text = ""; return; }
        try
        {
            var compile = OrcInspector.CompileText(_draft.Id.Replace('/', '.'), text, Value(CardType));
            TextNotice.Text = compile.Parse.Complete
                ? $"可解析：{compile.Effects.Count} 条效果，编译通过。点「解析并替换 / 追加」落到效果结构。"
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
        try { parse = OrcInspector.CompileText(_draft.Id.Replace('/', '.'), text, Value(CardType)).Parse; }
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
        // 文本词条并入勾选：量化重甲落到重甲参值；表达不了的诚实提示，不静默丢。
        var notes = new List<string>();
        foreach (var keyword in parse.Keywords)
        {
            var token = DiyCardCompiler.KeywordToken(keyword.Id);
            if (_keywords.TryGetValue(token, out var box))
            {
                box.ButtonPressed = true;
                if (keyword.Quantified && token == "armor") Armor.Value = keyword.Value;
                else if (keyword.Quantified)
                    notes.Add($"词条「{keyword.Id}」的参值 {keyword.Value} 没有独立输入框；如需精确参值请用「获得词条」动作。");
            }
            else notes.Add($"词条「{keyword.Id}」不在 DIY 词条表，未并入勾选。");
        }
        if (replace) { foreach (var row in _orcRows) { _orcBox.RemoveChild(row); row.QueueFree(); } _orcRows.Clear(); }
        foreach (var draft in drafts) AddOrcRow(draft);
        RenumberOrc();
        TextNotice.Text = $"已解析 {drafts.Count} 条效果并{(replace ? "替换" : "追加")}到效果结构。" + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : "");
        TextNotice.AddThemeColorOverride("font_color", UiStyles.Dim);
        OrcChanged();
    }

    private void AddOrc(OrcEffectDraft draft)
    {
        // 指令卡的部署触发自动换打出触发（配方与空白效果同款口径）。
        if (Value(CardType) == "order" && draft.Template == "deploy_basic")
            draft = draft with { Template = "played_basic", Hook = "card.played", Guard = "SelfCard" };
        AddOrcRow(draft);
        RenumberOrc();
        OrcChanged();
    }

    private void AddOrcRow(OrcEffectDraft draft)
    {
        var row = new OrcEffectRow(draft, OrcChanged, r =>
        {
            _orcRows.Remove(r); _orcBox.RemoveChild(r); r.QueueFree(); RenumberOrc(); OrcChanged();
        }, (r, offset) =>
        {
            var index = _orcRows.IndexOf(r); var target = Math.Clamp(index + offset, 0, _orcRows.Count - 1);
            _orcRows.RemoveAt(index); _orcRows.Insert(target, r); _orcBox.MoveChild(r, target); RenumberOrc(); OrcChanged();
        });
        _orcRows.Add(row); _orcBox.AddChild(row);
    }

    private void RenumberOrc()
    {
        for (var i = 0; i < _orcRows.Count; i++) _orcRows[i].SetIndex(i + 1);
        AddOrcButton.Disabled = _orcRows.Count >= 8;
    }

    /// <summary>结构编辑统一入口：脏标＋快照＋防抖编译校验。</summary>
    private void OrcChanged()
    {
        if (_loading) return;
        RenumberOrc(); Changed(); Snap(); _orcTimer.Start();
    }

    /// <summary>防抖编译校验：逐条编译编辑面，状态行＋行级错误。</summary>
    private void OrcCheck()
    {
        var drafts = OrcDrafts();
        if (drafts.Count == 0) { OrcNotice.Text = ""; return; }
        var compiled = OrcEffectEditing.Compile(_draft.Id.Replace('/', '.'), drafts);
        var problems = OrcEffectEditing.Problems(compiled);
        for (var i = 0; i < _orcRows.Count; i++)
            _orcRows[i].SetError(i < compiled.Count ? compiled[i].Error : null);
        OrcNotice.Text = problems.Count == 0
            ? $"编译通过：{drafts.Count} 条效果。保存后进对局生效。"
            : $"{problems.Count} 项需要检查：{string.Join("；", problems.Take(3))}";
        OrcNotice.AddThemeColorOverride("font_color", problems.Count == 0 ? UiStyles.Dim : new Color("dca58b"));
    }

    private IReadOnlyList<OrcEffectDraft> OrcDrafts() => _orcRows.Select(r => r.Read()).ToArray();

    // ── 撤销 / 重做（对齐 Electron 检查器：结构快照差分，50 步上限）──
    private void Snap()
    {
        if (_loading) return;
        var state = System.Text.Json.JsonSerializer.Serialize(OrcDrafts());
        if (_historyPos >= 0 && _history[_historyPos] == state) return;
        if (_historyPos < _history.Count - 1) _history.RemoveRange(_historyPos + 1, _history.Count - _historyPos - 1);
        _history.Add(state);
        if (_history.Count > 50) _history.RemoveAt(0);
        _historyPos = _history.Count - 1;
        SyncHistory();
    }
    private void Undo()
    {
        if (_historyPos <= 0) return;
        _historyPos--; ApplyHistory();
    }
    private void Redo()
    {
        if (_historyPos >= _history.Count - 1) return;
        _historyPos++; ApplyHistory();
    }
    private void ApplyHistory()
    {
        var drafts = System.Text.Json.JsonSerializer.Deserialize<OrcEffectDraft[]>(_history[_historyPos]) ?? [];
        _loading = true;
        foreach (var row in _orcRows) { _orcBox.RemoveChild(row); row.QueueFree(); } _orcRows.Clear();
        foreach (var draft in drafts) AddOrcRow(draft);
        RenumberOrc();
        _loading = false;
        Changed(); OrcCheck(); SyncHistory();
    }
    private void SyncHistory()
    {
        UndoButton.Disabled = _historyPos <= 0;
        RedoButton.Disabled = _historyPos >= _history.Count - 1;
    }

    /// <summary>效果 JSON 查看器（只读）：结构 → 编译产物；失败条目显错误。</summary>
    private void OrcJsonDialog()
    {
        var drafts = OrcDrafts();
        if (drafts.Count == 0) { _notice.Text = "还没有效果；先添加或解析一条。"; return; }
        var compiled = OrcEffectEditing.Compile(_draft.Id.Replace('/', '.'), drafts);
        var text = string.Join("\n\n", compiled.Select((e, i) =>
            e.Error is null
                ? $"// 效果 {i + 1}（{OrcEffectEditing.Templates.FirstOrDefault(t => t.Id == e.Draft.Template && t.Guard.ToString() == e.Draft.Guard)?.Label ?? e.Draft.Template}）\n{e.JsonText}"
                : $"// 效果 {i + 1} 编译失败：{e.Error}"));
        var dialog = new AcceptDialog { Title = "效果 JSON（编译产物，只读）", MinSize = new(660, 430) }; AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        box.AddChild(UiStyles.Label("这是当前结构的编译产物。编辑面是上面的结构；改结构即改效果。", 12, UiStyles.Dim));
        var view = new TextEdit { Text = text, Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(620, 300) };
        box.AddChild(view);
        box.AddChild(UiStyles.Button("复制到剪贴板", () => DisplayServer.ClipboardSet(text)));
        dialog.CloseRequested += () => dialog.QueueFree();
        dialog.PopupCentered();
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { _notice.Text = e.Message; }
    }
    internal EffectRow AddEffect(DiyEffectDraft effect)
    {
        var row = new EffectRow(effect, Changed, r =>
        {
            _effects.Remove(r); _effectBox.RemoveChild(r); r.QueueFree(); Changed();
        }, (r, offset) =>
        {
            var index = _effects.IndexOf(r); var target = Math.Clamp(index + offset, 0, _effects.Count - 1);
            _effects.RemoveAt(index); _effects.Insert(target, r); _effectBox.MoveChild(r, target); Changed();
        });
        _effects.Add(row); _effectBox.AddChild(row);
        if (!_loading) Changed();
        return row;
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
        Tab("卡牌代码", LastExportCode); Tab("引擎单卡 JSON", result.EngineCardJson!);
        dialog.Confirmed += () => { ActiveExport = null; dialog.QueueFree(); };
        dialog.Canceled += () => { ActiveExport = null; dialog.QueueFree(); };
        dialog.PopupCentered();
    }
    private void OrcImport()
    {
        var dialog = new ConfirmationDialog { Title = "OrC 批量导入", MinSize = new(700, 460), DialogHideOnOk = false }; AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        box.AddChild(UiStyles.Label("粘贴官方语料 JSON（行数组或 {\"cards\":[…]}）。解析成功的卡直接编译入池；解析不了的保留原文与诊断（待核对），可随解析器演进而重导。", 13, UiStyles.Dim));
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
                var card = DiyCardLibrary.Import(text.Text);
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
    internal partial class EffectRow : PanelContainer
    {
        internal readonly OptionButton Op, Target, Until;
        internal readonly SpinBox Amount, Attack, Defense;
        private readonly Label _amountLabel, _attackLabel, _defenseLabel;
        private bool _updating;
        internal EffectRow(DiyEffectDraft draft, Action changed, Action<EffectRow> remove, Action<EffectRow, int> move)
        {
            var box = new VBoxContainer(); AddChild(box);
            var top = new HBoxContainer(); box.AddChild(top);
            Op = Option(DiyCardCompiler.Operations); top.AddChild(Op);
            Target = Option(DiyCardCompiler.Targets); top.AddChild(Target);
            top.AddChild(UiStyles.Button("↑", () => move(this, -1)));
            top.AddChild(UiStyles.Button("↓", () => move(this, 1)));
            top.AddChild(UiStyles.Button("×", () => remove(this)));
            var values = new HBoxContainer(); box.AddChild(values);
            _amountLabel = UiStyles.Label("数量", 12, UiStyles.Dim); values.AddChild(_amountLabel);
            Amount = Number(-20, 99); values.AddChild(Amount);
            _attackLabel = UiStyles.Label("攻击", 12, UiStyles.Dim); values.AddChild(_attackLabel);
            Attack = Number(-20, 20); values.AddChild(Attack);
            _defenseLabel = UiStyles.Label("防御", 12, UiStyles.Dim); values.AddChild(_defenseLabel);
            Defense = Number(-20, 20); values.AddChild(Defense);
            Until = Option(DiyCardCompiler.Durations); values.AddChild(Until);
            Select(Op, draft.Op); Select(Target, draft.Target); Select(Until, draft.Until);
            Amount.Value = draft.Amount; Attack.Value = draft.Attack; Defense.Value = draft.Defense;
            foreach (var o in new[] { Op, Target, Until }) o.ItemSelected += _ => { if (!_updating) changed(); };
            foreach (var n in new[] { Amount, Attack, Defense }) n.ValueChanged += _ => { if (!_updating) changed(); };
        }
        internal DiyEffectDraft Read() => new()
        {
            Op = Value(Op), Target = Value(Op) is "draw" or "gainPoint" or "gainSlot" ? "self" : Value(Target),
            Amount = Value(Op) == "pin" ? 1 : (int)Amount.Value,
            Attack = (int)Attack.Value, Defense = (int)Defense.Value,
            Until = Value(Op) is "buff" or "costMod" ? Value(Until) : ""
        };
        internal void UpdateType(bool unit)
        {
            _updating = true;
            var op = Value(Op); var resource = op is "draw" or "gainPoint" or "gainSlot";
            Target.SetItemText(0, resource ? "自己" : "本单位");
            Target.Disabled = resource;
            Target.SetItemDisabled(0, !unit && !resource);
            if (!unit && !resource && Target.Selected == 0) Target.Select(1);
            var buff = op == "buff";
            _attackLabel.Visible = Attack.Visible = _defenseLabel.Visible = Defense.Visible = buff;
            _amountLabel.Visible = Amount.Visible = !buff && op != "silence";
            Amount.Editable = op != "pin"; if (op == "pin") Amount.Value = 1;
            Until.Visible = buff || op == "costMod";
            _updating = false;
        }
    }

    // ───────────────────────── OrC Inspector 结构编辑行 ─────────────────────────

    /// <summary>结构效果行：触发条目（模板＋守卫成对切换）＋动作序列（增删/排序/逐参数编辑）。</summary>
    internal sealed partial class OrcEffectRow : PanelContainer
    {
        private readonly OptionButton _trigger;
        private readonly Label _title, _error;
        private readonly VBoxContainer _opsBox;
        private readonly List<OrcOpRow> _ops = [];
        private readonly Action _changed;
        private readonly Action<OrcEffectRow> _remove;
        private readonly Action<OrcEffectRow, int> _move;
        private bool _mute = true;

        internal OrcEffectRow(OrcEffectDraft draft, Action changed, Action<OrcEffectRow> remove, Action<OrcEffectRow, int> move)
        {
            _changed = changed; _remove = remove; _move = move;
            var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 8); AddChild(box);
            var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 8); box.AddChild(head);
            _title = UiStyles.Label("效果", 14, UiStyles.Gold); head.AddChild(_title);
            _trigger = Option(OrcEffectEditing.Templates.Select(t => new UiDiyChoice(t.Id + "|" + t.Guard, t.Label)));
            _trigger.CustomMinimumSize = new(250, 34); head.AddChild(_trigger);
            head.AddChild(UiStyles.Button("×", () => _remove(this)));
            _opsBox = new VBoxContainer(); _opsBox.AddThemeConstantOverride("separation", 8); box.AddChild(_opsBox);
            var addRow = new HBoxContainer(); addRow.AddThemeConstantOverride("separation", 8); box.AddChild(addRow);
            var pick = Option(new[] { new UiDiyChoice("", "选择动作…") }
                .Concat(OrcEffectEditing.Ops.Select(o => new UiDiyChoice(o.Id, o.Label + " · " + o.Group))));
            pick.CustomMinimumSize = new(210, 34); addRow.AddChild(pick);
            addRow.AddChild(UiStyles.Button("添加动作", () =>
            {
                if (pick.Selected <= 0) return;
                AddOp(OrcEffectEditing.Seed(OrcEffectEditing.Ops[pick.Selected - 1].Id));
                _changed();
            }));
            _error = UiStyles.Label("", 12, new("dca58b"));
            _error.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(_error);
            foreach (var op in draft.Ops) AddOp(op);
            SelectTrigger(draft);
            _trigger.ItemSelected += _ => { if (!_mute) _changed(); };
            _mute = false;
        }

        private static string Key(OrcTemplateEntry t) => t.Id + "|" + t.Guard;

        private void SelectTrigger(OrcEffectDraft draft)
        {
            for (var i = 0; i < _trigger.ItemCount; i++)
                if (_trigger.GetItemMetadata(i).AsString() == draft.Template + "|" + draft.Guard) { _trigger.Select(i); return; }
        }

        private void AddOp(OrcOpDraft draft)
        {
            var row = new OrcOpRow(draft, _changed, r =>
            {
                _ops.Remove(r); _opsBox.RemoveChild(r); r.QueueFree(); _changed();
            }, (r, offset) =>
            {
                var index = _ops.IndexOf(r); var target = Math.Clamp(index + offset, 0, _ops.Count - 1);
                _ops.RemoveAt(index); _ops.Insert(target, r); _opsBox.MoveChild(r, target); _changed();
            });
            _ops.Add(row); _opsBox.AddChild(row);
        }

        internal OrcEffectDraft Read()
        {
            var entry = _trigger.Selected >= 0 && _trigger.Selected < OrcEffectEditing.Templates.Count
                ? OrcEffectEditing.Templates[_trigger.Selected] : null;
            return new OrcEffectDraft
            {
                Template = entry?.Id ?? "deploy_basic", Hook = entry?.Hook,
                Guard = (entry?.Guard ?? OrcTriggerGuard.SelfUnit).ToString(),
                Ops = _ops.Select(o => o.Read()).ToArray(),
            };
        }

        internal void SetIndex(int index) => _title.Text = "效果 " + index;
        internal void SetError(string? error) => _error.Text = error ?? "";

        internal void SetEnabled(bool enabled)
        {
            foreach (var child in FindChildren("*", "", true, false))
            {
                switch (child)
                {
                    case OptionButton or Button or CheckBox or CheckButton:
                        ((BaseButton)child).Disabled = !enabled; break;
                    case SpinBox spin: spin.Editable = enabled; break;
                    case LineEdit line: line.Editable = enabled; break;
                }
            }
        }
    }

    /// <summary>动作行：参数面按 op 目录显隐（目录＝编译面合法取值，编辑即所见）。</summary>
    internal sealed partial class OrcOpRow : VBoxContainer
    {
        private readonly OrcOpInfo _info;
        private readonly OptionButton _sel = null!, _side = null!, _selZone = null!, _fUnit = null!, _fKeyword = null!,
            _fFaction = null!, _keyword = null!, _opZone = null!, _until = null!, _cond = null!, _condDim = null!;
        private readonly SpinBox _selCount = null!, _amount = null!, _count = null!, _attack = null!, _defense = null!, _value = null!;
        private readonly LineEdit _name = null!, _condValue = null!;
        private readonly CheckBox _excludeSelf = null!;
        private readonly Label _summary = null!;
        private readonly List<(Control Wrap, Func<bool> Show)> _visibility = [];
        private readonly Action _changed;
        private bool _mute = true;

        internal OrcOpRow(OrcOpDraft draft, Action changed, Action<OrcOpRow> remove, Action<OrcOpRow, int> move)
        {
            _changed = changed;
            _info = OrcEffectEditing.Op(draft.Op) ?? OrcEffectEditing.Ops[0];
            var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 6); AddChild(head);
            _summary = UiStyles.Label("", 13);
            _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill; head.AddChild(_summary);
            head.AddChild(UiStyles.Button("↑", () => move(this, -1)));
            head.AddChild(UiStyles.Button("↓", () => move(this, 1)));
            head.AddChild(UiStyles.Button("×", () => remove(this)));
            var grid = new GridContainer { Columns = 4 };
            grid.AddThemeConstantOverride("h_separation", 12); grid.AddThemeConstantOverride("v_separation", 6);
            AddChild(grid);

            // 参数面：每个控件带小标签（标签在上），按 op 目录显隐——挤在一起的可读性问题在源头杜绝。
            _sel = Option(OrcEffectEditing.Sels); When(Field(grid, "选靶方式", _sel), () => _info.Target);
            _side = Option(OrcEffectEditing.Sides); When(Field(grid, "阵营", _side), () => _info.Target && Value(_sel) != "self");
            _selZone = Option(OrcEffectEditing.SelZones); When(Field(grid, "区域", _selZone), () => _info.Target && Value(_sel) != "self");
            _selCount = Number(1, 10); When(Field(grid, "数量", _selCount), () => _info.Target && Value(_sel) == "random");
            _fUnit = Option(OrcEffectEditing.UnitTypes); When(Field(grid, "限兵种", _fUnit), () => _info.Target && Value(_sel) != "self");
            _fKeyword = Option(new[] { new UiDiyChoice("", "不限词条") }.Concat(OrcEffectEditing.KeywordChoices));
            When(Field(grid, "限词条", _fKeyword), () => _info.Target && Value(_sel) != "self");
            _fFaction = Option(OrcEffectEditing.Factions); When(Field(grid, "限阵营", _fFaction), () => _info.Target && Value(_sel) != "self");
            _excludeSelf = new CheckBox { Text = "排除自身" }; When(Field(grid, "排除自身", _excludeSelf), () => _info.Target && Value(_sel) != "self");
            _amount = Number(_info.AmountMin, Math.Max(_info.AmountMax, _info.AmountMin));
            When(Field(grid, _info.AmountLabel is { Length: > 0 } ? _info.AmountLabel : "数值", _amount), () => _info.Amount);
            _count = Number(1, 10); When(Field(grid, "张数", _count), () => _info.Count);
            _attack = Number(-20, 20); When(Field(grid, "攻击增减", _attack), () => _info.AttackDefense);
            _defense = Number(-20, 20); When(Field(grid, "防御增减", _defense), () => _info.AttackDefense);
            _keyword = Option(OrcEffectEditing.KeywordChoices); When(Field(grid, "词条", _keyword), () => _info.Keyword);
            _value = Number(0, 99); When(Field(grid, "词条参值（0＝无）", _value), () => _info.Keyword);
            _name = new LineEdit { MaxLength = 40 }; When(Field(grid, "卡名（需与卡牌全名一致）", _name), () => _info.Name);
            _opZone = Option(OrcEffectEditing.OpZones); When(Field(grid, "落点", _opZone), () => _info.Zone);
            _until = Option(OrcEffectEditing.UntilChoices); When(Field(grid, "期限", _until), () => _info.Until);
            _cond = Option(OrcEffectEditing.Conditions); When(Field(grid, "条件", _cond), () => true);
            _condDim = Option(OrcEffectEditing.ConditionDims); When(Field(grid, "过滤维度", _condDim), () => Value(_cond) == OrcEffectEditing.EventCardFilter);
            _condValue = new LineEdit { MaxLength = 40 }; When(Field(grid, "过滤取值", _condValue), () => Value(_cond) == OrcEffectEditing.EventCardFilter);

            Load(draft);
            foreach (var option in new[] { _sel, _side, _selZone, _fUnit, _fKeyword, _fFaction, _keyword, _opZone, _until, _cond, _condDim })
                option.ItemSelected += _ => { if (!_mute) { UpdateVisibility(); _changed(); } };
            foreach (var spin in new[] { _selCount, _amount, _count, _attack, _defense, _value })
                spin.ValueChanged += _ => { if (!_mute) _changed(); };
            _name.TextChanged += _ => { if (!_mute) _changed(); };
            _condValue.TextChanged += _ => { if (!_mute) _changed(); };
            _excludeSelf.Toggled += _ => { if (!_mute) _changed(); };
            UpdateVisibility();
            _mute = false;
        }

        private void Load(OrcOpDraft draft)
        {
            Select(_sel, draft.Sel ?? "one"); Select(_side, draft.Side ?? "enemy");
            Select(_selZone, draft.SelZone ?? ""); Select(_fUnit, draft.Filter?.UnitType ?? "");
            Select(_fKeyword, draft.Filter?.Keyword ?? ""); Select(_fFaction, draft.Filter?.Faction ?? "");
            _excludeSelf.ButtonPressed = draft.Filter?.ExcludeSelf ?? false;
            _selCount.Value = draft.SelCount ?? 1; _amount.Value = draft.Amount ?? _info.AmountMin;
            _count.Value = draft.Count ?? 1; _attack.Value = draft.Attack ?? 1; _defense.Value = draft.Defense ?? 1;
            Select(_keyword, draft.Keyword ?? OrcEffectEditing.DefaultKeyword);
            _value.Value = draft.Value ?? 0; _name.Text = draft.Name ?? "";
            Select(_opZone, draft.OpZone ?? "frontline"); Select(_until, draft.Until ?? "");
            Select(_cond, draft.Condition ?? ""); Select(_condDim, Dims(draft.ConditionRaw).dim);
            _condValue.Text = Dims(draft.ConditionRaw).value;
        }

        private static (string dim, string value) Dims(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return ("keyword", "");
            var split = raw.IndexOf(':', StringComparison.Ordinal);
            return split < 0 ? ("keyword", raw) : (raw[..split], raw[(split + 1)..]);
        }

        internal OrcOpDraft Read() => new()
        {
            Op = _info.Id,
            Sel = _info.Target ? Value(_sel) : null,
            Side = _info.Target && Value(_sel) != "self" ? Value(_side) : null,
            SelZone = _info.Target && Value(_sel) != "self" ? Null(Value(_selZone)) : null,
            SelCount = _info.Target && Value(_sel) == "random" ? (int)_selCount.Value : null,
            Filter = _info.Target && Value(_sel) != "self" &&
                (Value(_fUnit) != "" || Value(_fKeyword) != "" || Value(_fFaction) != "" || _excludeSelf.ButtonPressed)
                ? new OrcFilterDraft { UnitType = Null(Value(_fUnit)), Keyword = Null(Value(_fKeyword)), Faction = Null(Value(_fFaction)), ExcludeSelf = _excludeSelf.ButtonPressed }
                : null,
            Amount = _info.Amount ? (int)_amount.Value : null,
            Count = _info.Count ? (int)_count.Value : null,
            Attack = _info.AttackDefense ? (int)_attack.Value : null,
            Defense = _info.AttackDefense ? (int)_defense.Value : null,
            Keyword = _info.Keyword ? Value(_keyword) : null,
            Value = _info.Keyword && _value.Value > 0 ? (int)_value.Value : null,
            OpZone = _info.Zone ? Value(_opZone) : null,
            Name = _info.Name ? _name.Text : null,
            Until = _info.Until ? Null(Value(_until)) : null,
            Condition = Null(Value(_cond)),
            ConditionRaw = Value(_cond) == OrcEffectEditing.EventCardFilter ? Value(_condDim) + ":" + _condValue.Text.Trim() : null,
        };

        private void UpdateVisibility()
        {
            var hasTarget = _info.Target && Value(_sel) != "self";
            foreach (var (wrap, show) in _visibility) wrap.Visible = show();
            _summary.Text = OrcEffectEditing.Summarize(Read());
        }

        private void When(Control wrap, Func<bool> show) => _visibility.Add((wrap, show));

        private static string? Null(string s) => s.Length == 0 ? null : s;
    }
}
