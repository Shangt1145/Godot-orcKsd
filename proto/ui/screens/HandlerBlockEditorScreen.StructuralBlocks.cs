using Godot;
using Kards.Ui.OrcBridge.HandlerDsl;

namespace Kards.Ui;

public partial class HandlerBlockEditorScreen
{
    // Data for the value-block shelf; never creates a native option menu.
    private sealed class ValueBlockChoices
    {
        private readonly List<(string Text, int Id, bool Separator, bool Disabled)> _items = [];
        public int ItemCount => _items.Count;
        public void Clear() => _items.Clear();
        public void AddItem(string text, int id) => _items.Add((text, id, false, false));
        public void AddSeparator() => _items.Add(("", -1, true, false));
        public void SetItemDisabled(int index, bool disabled) { var item = _items[index]; _items[index] = (item.Text, item.Id, item.Separator, disabled); }
        public bool IsItemSeparator(int index) => _items[index].Separator;
        public bool IsItemDisabled(int index) => _items[index].Disabled;
        public string GetItemText(int index) => _items[index].Text;
        public int GetItemId(int index) => _items[index].Id;
    }

    private IReadOnlyList<HandlerParentOption> _triggerParents = [];
    private bool _showTriggerLibrary, _showValueLibrary;
    internal string? SelectedTrigger => _targetParent;
    internal IReadOnlyList<HandlerParentOption> TriggerOptions => _triggerParents;
    private HandlerParentOption? CurrentTrigger => _triggerParents.FirstOrDefault(p => p.ParentId == _targetParent);

    private bool IsPredicate(HandlerValue value, IReadOnlyList<ScopeVar> scope) => value.Kind switch
    {
        HandlerValueKind.Operator => HandlerDslValidator.OperatorResult(value.Name ?? "") == "boolean",
        HandlerValueKind.Literal => value.LiteralType == "boolean",
        HandlerValueKind.View => _view.GetValueOrDefault(value.Name ?? "")?.Type == "boolean",
        HandlerValueKind.Binding => scope.FirstOrDefault(v => v.Name == value.Name)?.Type == "boolean",
        HandlerValueKind.Read => _catalog.ReadableType(value.ReadTarget is null ? HandlerCatalog.KindsForLiteral(value.ReadOf ?? "self") : HandlerCatalog.UnitOrHq, value.Name ?? "") == "boolean",
        _ => false,
    };

    private static string GuardCaption(string guard) => guard switch
    {
        "Any" => "不限事件对象", "Self" => "事件对象是本单位",
        "OwnerSame" => "事件对象属于友方", "OwnerDifferent" => "事件对象属于敌方",
        "OwnerSameByPlayer" => "当前是我方回合", "OwnerDifferentByPlayer" => "当前是敌方回合",
        _ => "卡包定义的条件",
    };

    private void RestoreTrigger(string? parent)
    {
        _targetParent = parent;
        if (_authoring?.Invoke() is { } authoring && CurrentTrigger is { } option) ApplyView(authoring, option, false);
    }

    private void SelectTrigger(string parent)
    {
        if (_targetParent == parent) return;
        if (_triggerParents.All(p => p.ParentId != parent)) return;
        Edit(() => RestoreTrigger(parent));
    }

    private static BlockShape PaletteBlock(string label, Color color, BlockShape.Form form, Action? activate, bool hat = false)
    {
        var block = new BlockShape { Fill = color, Shape = form, ShowNotch = !hat, ShowTab = form == BlockShape.Form.Stack,
            TopRadius = hat ? 18 : 5, CustomMinimumSize = new(0, 38), SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseDefaultCursorShape = activate is null ? CursorShape.Arrow : CursorShape.PointingHand };
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(new Control { CustomMinimumSize = new(form == BlockShape.Form.Stack ? 10 : 16, 0), MouseFilter = MouseFilterEnum.Ignore });
        var text = MenuStyles.Label(label, 14, BlockTheme.InkFor(color)); text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart; text.MouseFilter = MouseFilterEnum.Ignore; row.AddChild(text);
        row.AddChild(new Control { CustomMinimumSize = new(12, 0), MouseFilter = MouseFilterEnum.Ignore }); block.SetBody(row);
        if (activate is not null)
        {
            block.GuiInput += input => { if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) activate(); };
            block.MouseEntered += () => block.Fill = color.Lightened(.12f); block.MouseExited += () => block.Fill = color;
        }
        return block;
    }

    private Control TriggerHat()
    {
        var block = new BlockShape { Name = "TriggerHat", Fill = BlockTheme.Hat, ShowNotch = false, TopRadius = 18, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 10);
        var current = CurrentTrigger;
        box.AddChild(MenuStyles.Label(current is null ? "从工具箱放入触发积木" : "当「" + current.DisplayName + "」时", 18, BlockTheme.InkFor(BlockTheme.Hat)));
        var guard = PaletteBlock(current is null ? "选择触发对象条件" : GuardCaption(current.Guard), BlockTheme.Query,
            BlockShape.Form.Predicate, null);
        guard.Name = "TriggerConditionBlock"; guard.TooltipText = "从工具箱放入对应的触发条件积木。"; box.AddChild(guard);
        if (current is not null) box.AddChild(MenuStyles.Label(TriggerLifetime(current), 12, BlockTheme.InkFor(BlockTheme.Hat)));
        block.SetBody(BlockPadding(box)); return block;
    }

    private static string TriggerLifetime(HandlerParentOption option) => "激活时点：" + (option.ActiveWhen switch { "Loaded" => "载入时", "Placed" => "在场时", _ => "由卡包定义" })
        + "  ·  适用：" + (option.RequiresHost switch { "Any" => "不限卡牌类型", "Unit" => "单位卡", "Order" => "指令卡", "Hq" or "Headquarters" => "总部", _ => "由卡包定义" });

    private Control TriggerSummary(HandlerParentOption option)
    {
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 8);
        box.AddChild(MenuStyles.Label("当「" + option.DisplayName + "」时", 17, BlockTheme.InkFor(BlockTheme.Hat)));
        box.AddChild(PaletteBlock(GuardCaption(option.Guard), BlockTheme.Query, BlockShape.Form.Predicate, null));
        var block = new BlockShape { Fill = BlockTheme.Hat, ShowNotch = false, TopRadius = 18 }; block.SetBody(BlockPadding(box)); return block;
    }

    private static MarginContainer BlockPadding(Control content)
    {
        var margin = new MarginContainer(); foreach (var side in new[] { "left", "right" }) margin.AddThemeConstantOverride("margin_" + side, 16);
        margin.AddThemeConstantOverride("margin_top", 12); margin.AddThemeConstantOverride("margin_bottom", 12); margin.AddChild(content); return margin;
    }

    private void AppendStructuralToolbox(string query)
    {
        var category = _toolboxCategory;
        bool Category(string name) => category == "全部分类" || category == name;
        bool Match(string text) => query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (Category("触发事件") && _triggerParents.Count > 0)
        {
            _toolboxList.AddChild(MenuStyles.Button($"触发事件 · {_triggerParents.Count} 种 {(_showTriggerLibrary ? "▾" : "▸")}", () => { _showTriggerLibrary = !_showTriggerLibrary; RefreshToolbox(); }));
            if (_showTriggerLibrary || query.Length > 0 || category == "触发事件")
                foreach (var option in _triggerParents.Where(p => Match(p.DisplayLabel)))
                {
                    var block = PaletteBlock("当 " + option.DisplayLabel + " 时", BlockTheme.Hat, BlockShape.Form.Stack, () => SelectTrigger(option.ParentId), true);
                    block.SetMeta("trigger_parent", option.ParentId); _toolboxList.AddChild(block);
                }
        }
        if (Category("触发条件") && (_showTriggerLibrary || query.Length > 0 || category == "触发条件"))
            foreach (var guard in _triggerParents.Select(p => p.Guard).Distinct().Where(g => Match(GuardCaption(g))))
                _toolboxList.AddChild(PaletteBlock(GuardCaption(guard), BlockTheme.Query, BlockShape.Form.Predicate, () =>
                {
                    var option = _triggerParents.FirstOrDefault(p => p.Signal == CurrentTrigger?.Signal && p.Guard == guard);
                    if (option is null) ShowSaveNote("当前事件不支持这项对象条件，请先选择适用的事件积木。", true); else SelectTrigger(option.ParentId);
                }));
        if (!Category("取值与运算")) return;
        _toolboxList.AddChild(MenuStyles.Button("取值与运算 " + (_showValueLibrary ? "▾" : "▸"), () => { _showValueLibrary = !_showValueLibrary; RefreshToolbox(); }));
        if (!_showValueLibrary && query.Length == 0 && category != "取值与运算") return;
        foreach (var op in HandlerDslValidator.OperatorNames.Where(op => Match(HandlerVocabulary.OperatorLabel(op))))
        {
            var predicate = HandlerDslValidator.OperatorResult(op) == "boolean";
            var block = PaletteBlock(HandlerVocabulary.OperatorLabel(op), BlockTheme.Query, predicate ? BlockShape.Form.Predicate : BlockShape.Form.Reporter, () => InsertValueBlock(DefaultOperator(op), predicate));
            block.SetMeta("value_operator", op); _toolboxList.AddChild(block);
        }
        foreach (var field in _view.Values.Where(f => Match(HandlerVocabulary.ViewFieldLabel(f.Name))))
            _toolboxList.AddChild(PaletteBlock("事件中的「" + HandlerVocabulary.ViewFieldLabel(field.Name) + "」", BlockTheme.Query, field.Type == "boolean" ? BlockShape.Form.Predicate : BlockShape.Form.Reporter, () => InsertValueBlock(HandlerValue.ViewField(field.Name), field.Type == "boolean")));
        foreach (var (kind, table) in _catalog.Readables)
            foreach (var (path, type) in table.Where(p => Match(HandlerVocabulary.ReadableLabel(p.Key))))
            {
                var target = kind == "player" ? "viewer" : "self";
                if (!HandlerCatalog.KindsForLiteral(target).Contains(kind)) continue;
                _toolboxList.AddChild(PaletteBlock((target == "self" ? "自己的" : "我方的") + HandlerVocabulary.ReadableLabel(path), BlockTheme.Query, type == "boolean" ? BlockShape.Form.Predicate : BlockShape.Form.Reporter, () => InsertValueBlock(HandlerValue.Read(target, path), type == "boolean")));
            }
    }

    private static HandlerValue DefaultOperator(string op) => HandlerValue.Call(op, Enumerable.Range(0, HandlerDslValidator.OperatorArity(op)).Select(_ => op is "and" or "or" or "not" ? HandlerValue.Bool(false) : HandlerValue.Num(0)).ToArray());

    private void InsertValueBlock(HandlerValue value, bool predicate)
    {
        var node = predicate ? new HandlerNode("branch").Set("test", value) : new HandlerNode("let").SetText("name", HandlerVocabulary.BindingId("结果" + (CountNodes(_doc.Actions) + 1))).Set("value", value);
        if (predicate) node.Body("then");
        Edit(() => InsertAtSelection(node));
        if (_nodeControls.TryGetValue(node, out var control)) Reveal(control);
    }

    private void ShowValueBlockPalette()
    {
        var dialog = new AcceptDialog { Title = "选择取值积木", OkButtonText = "返回" }; AddChild(dialog);
        var scroll = new ScrollContainer { CustomMinimumSize = new(540, 420), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; dialog.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; box.AddThemeConstantOverride("separation", 8); scroll.AddChild(box);
        for (var i = 0; i < _valueMenu.ItemCount; i++)
        {
            if (_valueMenu.IsItemSeparator(i)) { box.AddChild(new HSeparator()); continue; }
            var id = _valueMenu.GetItemId(i); var text = _valueMenu.GetItemText(i); var disabled = _valueMenu.IsItemDisabled(i);
            var predicate = id is 3 or 4 || id is >= 400 and < 500 && HandlerDslValidator.OperatorResult(HandlerDslValidator.OperatorNames.ElementAt(id - 400)) == "boolean";
            if (id is >= 100 and < 200) predicate = _valueScope[id - 100].Type == "boolean";
            if (id is >= 200 and < 300) predicate = IsPredicate(_readOptions[id - 200], _valueScope);
            if (id is >= 300 and < 400) predicate = _view.Values.ElementAt(id - 300).Type == "boolean";
            var block = PaletteBlock(text, disabled ? BlockTheme.Advanced : BlockTheme.Query, predicate ? BlockShape.Form.Predicate : BlockShape.Form.Reporter,
                disabled ? null : () => { dialog.Hide(); dialog.QueueFree(); OnValuePicked(id); });
            if (id is >= 500 and < 600) block.SetMeta("fixed_literal", _valueLiterals[id - 500]);
            box.AddChild(block);
        }
        dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
}

