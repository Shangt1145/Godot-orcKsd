using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyEditorScreen : VBoxContainer
{
    private DiyCardLibrary _library = null!;
    private Func<IReadOnlyList<UiCardDefinition>> _catalog = null!;
    private Func<string, int> _usage = null!;
    private Func<string, string?> _storedError = null!;
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
    internal TextEdit Flavor = null!;
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
    public DiyCardDraft Draft => Read();
    internal bool IsDirty => _dirty;
    internal string Notice => _notice.Text;
    public void Initialize(DiyCardLibrary library, Func<IReadOnlyList<UiCardDefinition>> catalog,
        TextureCache textures, Func<string, int> usage, Func<string, string?> storedError)
    {
        _library = library; _catalog = catalog; _textures = textures; _usage = usage; _storedError = storedError;
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
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; AddChild(body);
        var listPanel = Panel(205); body.AddChild(listPanel);
        var list = new VBoxContainer(); listPanel.AddChild(list);
        Search = new LineEdit { PlaceholderText = "搜索本地 DIY 卡牌" }; list.AddChild(Search);
        Search.TextChanged += _ => RefreshList();
        Cards = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(170, 100) }; list.AddChild(Cards);
        Cards.ItemSelected += index =>
        {
            if (index < 0 || index >= _listed.Count) return;
            var card = _library.Cards.First(c => c.Id == _listed[(int)index]);
            Guard(() => Load(card));
        };
        DeleteButton = UiStyles.Button("删除", () =>
        {
            var uses = _usage(_draft.Id);
            DeleteDialog.DialogText = uses == 0 ? "删除这张已保存的 DIY 卡牌？"
                : $"有 {uses} 副卡组引用这张卡。删除后这些卡组需要重新编辑。仍然删除？";
            DeleteDialog.PopupCentered();
        }); list.AddChild(DeleteButton);
        var editPanel = Panel(450); editPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill; body.AddChild(editPanel);
        var editScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; editPanel.AddChild(editScroll);
        EditorScroll = editScroll;
        var form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; editScroll.AddChild(form);
        _identity = UiStyles.Label("", 11, UiStyles.Dim); form.AddChild(_identity);
        CardName = new LineEdit { MaxLength = 40, PlaceholderText = "卡牌名称" }; Field(form, "名称", CardName);
        CardName.TextChanged += _ => Changed();
        var types = new GridContainer { Columns = 3 }; form.AddChild(types);
        CardType = Option([new("unit", "单位"), new("order", "指令")]); Field(types, "卡牌类别", CardType);
        Nation = Option(DiyCardCompiler.Nations); Field(types, "国籍", Nation);
        Rarity = Option(DiyCardCompiler.Rarities); Field(types, "稀有度", Rarity);
        UnitType = Option(DiyCardCompiler.UnitTypes); Field(types, "兵种", UnitType);
        Cost = Number(0, 99); Field(types, "部署 / 打出费用", Cost);
        OpCost = Number(0, 99); Field(types, "行动费用", OpCost);
        var stats = new GridContainer { Columns = 3 }; form.AddChild(stats);
        Attack = Number(0, 99); Field(stats, "攻击", Attack);
        Defense = Number(1, 99); Field(stats, "防御", Defense);
        Armor = Number(0, 3); Field(stats, "重甲参值", Armor);
        foreach (var o in new[] { CardType, Nation, Rarity, UnitType }) o.ItemSelected += _ => Changed();
        foreach (var n in new[] { Cost, OpCost, Attack, Defense, Armor }) n.ValueChanged += _ => Changed();
        form.AddChild(UiStyles.Label("单位词条", 16, UiStyles.Gold));
        var keywordGrid = new GridContainer { Columns = 4 }; form.AddChild(keywordGrid);
        foreach (var keyword in DiyCardCompiler.Keywords)
        {
            var box = new CheckBox { Text = keyword.Label, TooltipText = keyword.Label }; keywordGrid.AddChild(box);
            _keywords[keyword.Id] = box; box.Toggled += _ => Changed();
        }
        Art = Option(new[] { new UiDiyChoice("", "文字卡面") }.Concat(catalog()
            .Where(c => c.ArtPath.Length > 0 && !c.CardId.StartsWith("diy/", StringComparison.Ordinal))
            .OrderBy(c => c.Name).Select(c => new UiDiyChoice(c.CardId, c.Name + " · " + c.Set))).ToArray());
        Field(form, "卡图（现有素材）", Art); Art.ItemSelected += _ => Changed();
        _trigger = UiStyles.Label("", 14, UiStyles.Gold); form.AddChild(_trigger);
        form.AddChild(UiStyles.Label("效果按排列顺序执行；规则文本自动生成。", 12, UiStyles.Dim));
        _effectBox = new VBoxContainer(); form.AddChild(_effectBox);
        AddEffectButton = UiStyles.Button("＋ 添加效果", () => AddEffect(new()));
        form.AddChild(AddEffectButton);
        Flavor = new TextEdit { CustomMinimumSize = new(0, 65), WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = "风味文字，不作为规则执行（最多 300 字）" };
        Field(form, "风味文字", Flavor); Flavor.TextChanged += Changed;
        var previewPanel = Panel(245); body.AddChild(previewPanel);
        PreviewPanel = previewPanel;
        var previewSide = new VBoxContainer(); previewPanel.AddChild(previewSide);
        previewSide.AddChild(UiStyles.Label("卡面预览", 17, UiStyles.Gold));
        var previewScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; previewSide.AddChild(previewScroll);
        _preview = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; previewScroll.AddChild(_preview);
        ValidateButton = UiStyles.Button("校验", Validate); previewSide.AddChild(ValidateButton);
        ConstructButton = UiStyles.Button("用于构筑", () => { if (Save()) ConstructRequested?.Invoke(_draft.Id); }); previewSide.AddChild(ConstructButton);
        TrialButton = UiStyles.Button("试打", () => { if (Save()) TrialRequested?.Invoke(_draft.Snapshot()); }); previewSide.AddChild(TrialButton);
        TrialButton.TooltipText = "用临时 12 张卡牌的卡组随机发牌；按稀有度加入最多 2 张当前卡，不改动已保存卡组。";
        _notice = UiStyles.Label("", 13, UiStyles.Dim); _notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _notice.CustomMinimumSize = new(0, 35); AddChild(_notice);
        DeleteDialog = new ConfirmationDialog { Title = "删除 DIY 卡牌" }; AddChild(DeleteDialog);
        DeleteDialog.Confirmed += () => Run(() => { _library.Delete(_draft.Id); Saved?.Invoke(); Load(new()); _notice.Text = "卡牌已删除；引用它的卡组请重新编辑。"; });
        DiscardDialog = new ConfirmationDialog { Title = "未保存的卡牌", DialogText = "丢弃当前修改并继续？" }; AddChild(DiscardDialog);
        DiscardDialog.Confirmed += () => { var action = _afterDiscard; _afterDiscard = null; action?.Invoke(); };
        DiscardDialog.Canceled += () => { _afterDiscard = null; RefreshList(); };
        Load(_library.Cards.FirstOrDefault() ?? new());
        if (_library.LoadError is { } error) _notice.Text = error;
    }
    private static PanelContainer Panel(float width) => new() { CustomMinimumSize = new(width, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
    private static void Field(Node parent, string label, Control control)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 4); parent.AddChild(box);
        box.AddChild(UiStyles.Label(label, 12, UiStyles.Dim)); box.AddChild(control);
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
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
        Flavor.Text = draft.Flavor;
        foreach (var row in _effects) { _effectBox.RemoveChild(row); row.QueueFree(); } _effects.Clear();
        foreach (var effect in draft.Effects) AddEffect(effect);
        _loading = false; _dirty = false; SaveButton.Text = "保存"; _notice.Text = "";
        RefreshList(); RefreshPreview(); EditorScroll.ScrollVertical = 0;
        if (_blocked is not null) _notice.Text = $"卡牌未通过校验，原声明保留，不能用于实战：{_storedError(draft.Id)}";
    }
    private DiyCardDraft Read() => _blocked?.Snapshot() ?? _draft with
    {
        Name = CardName.Text, CardType = Value(CardType), Nation = Value(Nation), Rarity = Value(Rarity), UnitType = Value(UnitType),
        Cost = (int)Cost.Value, OpCost = (int)OpCost.Value, Attack = (int)Attack.Value, Defense = (int)Defense.Value,
        Keywords = Value(CardType) == "unit" ? _keywords.Where(k => k.Value.ButtonPressed).Select(k => k.Key).ToArray() : [],
        Armor = (int)Armor.Value, ArtCardId = Value(Art), Flavor = Flavor.Text, Effects = _effects.Select(r => r.Read()).ToArray()
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
        foreach (var row in _effects) row.UpdateType(isUnit);
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
        }
        else foreach (var option in new[] { CardType, Nation, Rarity, Art }) option.Disabled = false;
        SaveButton.Disabled = ConstructButton.Disabled = TrialButton.Disabled = ExportButton.Disabled = blocked;
    }
    private void RefreshList()
    {
        Cards.Clear(); _listed.Clear();
        foreach (var card in _library.Cards.Where(c => $"{c.Name} {c.Id}".Contains(Search.Text, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name))
        {
            _listed.Add(card.Id); Cards.AddItem((_storedError(card.Id) is null ? "" : "⚠ ") + card.Name + (card.CardType == "unit" ? " · 单位" : " · 指令"));
            Cards.SetItemTooltip(_listed.Count - 1, card.Id);
            if (card.Id == _draft.Id) Cards.Select(_listed.Count - 1);
        }
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
}
