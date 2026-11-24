using Godot;
using Kards.Ui.OrcBridge.HandlerDsl;

namespace Kards.Ui;

public partial class Main
{
    private async Task VerifyInspectorBlockForms()
    {
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        IEnumerable<BlockShape> Blocks() => _blockEditor.FindChildren("*", "Control", true, false).OfType<BlockShape>();
        async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        async Task Click(BlockShape block)
        {
            block.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            await Frame();
        }
        void PressButton(string text) =>
            _blockEditor.FindChildren("*", "Button", true, false).OfType<Button>()
                .Single(b => b.Text == text).EmitSignal(Button.SignalName.Pressed);
        var original = _blockEditor.Document.Clone();
        var originalParent = _blockEditor.SelectedTrigger;
        var actions = Blocks().Where(b => b.HasMeta("action_node")).Select(b => b.GetMeta("action_node").AsString()).ToHashSet();
        Check(_blockEditor.Catalog.Nodes.All(n => actions.Contains(n.Name)), "Action catalog is missing block-shaped entries");
        async Task Category(string name)
        {
            _blockEditor.FindChildren("*", "Button", true, false).OfType<Button>()
                .Single(b => b.HasMeta("block_category") && b.GetMeta("block_category").AsString() == name).EmitSignal(Button.SignalName.Pressed);
            await Frame();
        }
        Check(!_blockEditor.FindChildren("*", "OptionButton", true, false).Any(), "Inspector contains an option selector");
        Check(!_blockEditor.FindChildren("*", "PopupMenu", true, false).Any(), "Inspector contains an option menu");
        await Category("触发事件");
        var triggers = Blocks().Where(b => b.HasMeta("trigger_parent")).ToArray();
        Check(triggers.Length == _blockEditor.TriggerOptions.Count, "Trigger palette omitted engine templates");
        var selected = _blockEditor.TriggerOptions.First(p => p.ParentId != originalParent && p.Guard == "Self");
        await Click(triggers.Single(b => b.GetMeta("trigger_parent").AsString() == selected.ParentId));
        Check(_blockEditor.SelectedTrigger == selected.ParentId, "Trigger block click did not change template");
        Check(Blocks().Any(b => b.Name == "TriggerConditionBlock" && b.Shape == BlockShape.Form.Predicate), "Trigger guard is not a predicate block");
        var undo = _blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "撤销");
        undo.EmitSignal(Button.SignalName.Pressed); await Frame();
        Check(_blockEditor.SelectedTrigger == originalParent, "Undo failed to restore trigger");
        var redo = _blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "重做");
        redo.EmitSignal(Button.SignalName.Pressed); await Frame();
        Check(_blockEditor.SelectedTrigger == selected.ParentId, "Redo failed to restore trigger");
        Check(_blockEditor.Document.ToJsonText(_blockEditor.Catalog) == original.ToJsonText(_blockEditor.Catalog), "Trigger change altered actions");
        await Category("取值与运算");
        var operators = Blocks().Where(b => b.HasMeta("value_operator")).ToArray();
        Check(operators.Length == HandlerDslValidator.OperatorNames.Count, "Operator palette is incomplete");
        Check(operators.All(b => b.Shape == (HandlerDslValidator.OperatorResult(b.GetMeta("value_operator").AsString()) == "boolean" ? BlockShape.Form.Predicate : BlockShape.Form.Reporter)), "Operator block shape does not match its result");
        await Click(operators.Single(b => b.GetMeta("value_operator").AsString() == "eq"));
        Check(_blockEditor.Document.Actions.Last().Op == "branch", "Predicate block did not create a real branch");
        Check(Blocks().Any(b => b.Name == "ValueExpressionBlock" && b.Shape == BlockShape.Form.Predicate), "Canvas predicate lacks a nested block");
        var expression = Blocks().Single(b => b.Name == "ValueExpressionBlock");
        expression.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "等于").EmitSignal(Button.SignalName.Pressed);
        await Frame();
        var palette = _blockEditor.GetChildren().OfType<AcceptDialog>().Single(d => d.Visible && d.Title == "选择取值积木");
        var choices = palette.FindChildren("*", "Control", true, false).OfType<BlockShape>().ToArray();
        Check(choices.Length >= HandlerDslValidator.OperatorNames.Count, "Value editor did not expose block choices");
        var andBlock = choices.Single(b => b.FindChildren("*", "Label", true, false).OfType<Label>().Any(l => l.Text == "运算 · 并且"));
        Check(andBlock.Shape == BlockShape.Form.Predicate, "Boolean palette choice is not a predicate");
        await Click(andBlock);
        Check(_blockEditor.Document.Actions.Last().Value("test") is { Kind: HandlerValueKind.Operator, Name: "and" } value
            && value.Args[0] is { Kind: HandlerValueKind.Operator, Name: "eq" }
            && value.Args[1].LiteralType == "boolean", "Boolean block lost the nested predicate or created a non-boolean argument");
        _blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "＋ 添加积木").EmitSignal(Button.SignalName.Pressed);
        await Frame();
        await Category("手牌与牌堆");
        await Click(Blocks().Single(b => b.HasMeta("action_node") && b.GetMeta("action_node").AsString() == "draw"));
        Check(_blockEditor.Document.Actions.Last().Body("then") is [{ Op: "draw" }], "Nested predicate action was not inserted into its branch");
        Check(!_blockEditor.Document.Actions.Last().Bodies.ContainsKey("else"), "Rendering created an empty optional else branch");
        var dir = ProjectSettings.GlobalizePath("res://artifacts/inspector-blocks"); Directory.CreateDirectory(dir);
        if (DisplayServer.GetName() != "headless")
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, "conditions-and-values.png"));
        }
        _blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "＋ 添加「不然」").EmitSignal(Button.SignalName.Pressed);
        await Frame();
        Check(!_blockEditor.Document.Actions.Last().Bodies.ContainsKey("else"), "Selecting an optional branch changed the document");
        await Click(Blocks().Single(b => b.HasMeta("action_node") && b.GetMeta("action_node").AsString() == "draw"));
        Check(_blockEditor.Document.Actions.Last().Bodies["else"] is [{ Op: "draw" }], "Optional else branch did not attach on insertion");
        var literalPins = _blockEditor.Catalog.Nodes.SelectMany(spec => spec.Inputs
            .Where(pin => pin.EnumValues is { Count: > 0 }).Select(pin => (spec, pin))).ToArray();
        foreach (var (spec, pin) in literalPins)
        {
            var node = new HandlerNode(spec.Name);
            if (pin.Kind == HandlerPinKind.Enum) node.SetText(pin.Name, pin.EnumValues![0]);
            else node.Set(pin.Name, HandlerValue.Str(pin.EnumValues![0]));
            var doc = new HandlerDocument(); doc.Actions.Add(node);
            _blockEditor.Open("固定取值积木验证", doc); await Frame();
            var socket = Blocks().Single(b => b.HasMeta("handler_pin") && b.GetMeta("handler_pin").AsString() == pin.Name);
            await Click(socket);
            var shelf = _blockEditor.GetChildren().OfType<AcceptDialog>().Single(d => d.Visible && d.Title is "可拼入的积木" or "选择取值积木");
            Check(!shelf.FindChildren("*", "OptionButton", true, false).Any() && !shelf.FindChildren("*", "PopupMenu", true, false).Any(), "Fixed values contain a native option selector");
            var blocks = shelf.FindChildren("*", "Control", true, false).OfType<BlockShape>().ToArray();
            var expected = pin.EnumValues![^1];
            if (pin.Kind == HandlerPinKind.Enum)
            {
                Check(blocks.Length == pin.EnumValues.Count, "Fixed enum omitted a separate block"); await Click(blocks[^1]);
            }
            else await Click(blocks.Single(b => b.HasMeta("fixed_literal") && b.GetMeta("fixed_literal").AsString() == expected));
            var stored = _blockEditor.Document.Actions[0];
            Check((pin.Kind == HandlerPinKind.Enum ? stored.Text(pin.Name) : stored.Value(pin.Name)?.Text) == expected, "Fixed literal block did not preserve its engine value");
        }
        GD.Print($"INSPECTOR_FIXED_BLOCKS_OK literal-pins={literalPins.Length} no-dropdown no-popup-menu");

        // 伪代码与积木**并排**（不是切换）：默认就该看得见，且用的是游戏词。
        _blockEditor.Open("伪代码视图验证", HandlerDocument.Sample());
        await Frame();
        var codeEdit = _blockEditor.FindChildren("*", "CodeEdit", true, false).OfType<CodeEdit>().Single();
        var printed = codeEdit.Text;
        Check(codeEdit.IsVisibleInTree(), "伪代码面板默认没有显示（应与积木并排）。");
        Check(printed.Contains("当「", StringComparison.Ordinal), "伪代码没有打印触发行：" + printed);
        Check(printed.Contains("如果 ", StringComparison.Ordinal) && printed.Contains("否则 {", StringComparison.Ordinal),
            "伪代码没有打印分支与否则段：" + printed);
        Check(printed.Contains("造成伤害", StringComparison.Ordinal) && !printed.Contains("\"damage\"", StringComparison.Ordinal),
            "伪代码用了 op 英文名而不是游戏词：" + printed);
        Check(printed.Contains("遍历 ", StringComparison.Ordinal), "伪代码没有打印遍历：" + printed);
        // 回读自检：伪代码必须能被解析回同一棵树，否则"可编辑"就是改结构的许可证。
        var status = _blockEditor.FindChildren("*", "Label", true, false).OfType<Label>()
            .FirstOrDefault(l => l.Text.Contains("回读自检", StringComparison.Ordinal));
        Check(status is not null && status.Text.Contains("通过", StringComparison.Ordinal),
            "伪代码回读自检没有通过：" + (status?.Text ?? "没有状态行"));

        // 可编辑 + 应用：改一个数字 → 应用 → 积木跟着变 → 撤销能回去。
        var jsonBefore = _blockEditor.Document.ToJsonText(_blockEditor.Catalog);
        codeEdit.Text = codeEdit.Text.Replace("打多少: 3", "打多少: 7", StringComparison.Ordinal);
        codeEdit.EmitSignal(CodeEdit.SignalName.TextChanged);
        await Frame();
        Check(status!.Text.Contains("已改动", StringComparison.Ordinal), "改动文本后没有提示「已改动」：" + status.Text);
        PressButton("应用到积木");
        await Frame();
        var jsonAfter = _blockEditor.Document.ToJsonText(_blockEditor.Catalog);
        // JSON 是缩进格式：amount 下面是 {"lit": 7}，不是 "amount": 7。
        Check(jsonAfter.Contains("\"lit\": 7", StringComparison.Ordinal) && jsonAfter != jsonBefore,
            "应用伪代码后积木没有跟着变：" + jsonAfter);
        PressButton("撤销");
        await Frame();
        Check(_blockEditor.Document.ToJsonText(_blockEditor.Catalog) == jsonBefore, "应用之后撤销没有回到原来的结构");

        // 读不回来的文本必须**拒绝应用**，且不许动文档——这是这块面板唯一的安全边界。
        codeEdit.Text = "当「部署时」时 {\n  造成伤害(打多少: 7\n";
        codeEdit.EmitSignal(CodeEdit.SignalName.TextChanged);
        await Frame();
        PressButton("应用到积木");
        await Frame();
        Check(_blockEditor.Document.ToJsonText(_blockEditor.Catalog) == jsonBefore, "坏文本居然被应用了");
        Check(status.Text.Contains("行", StringComparison.Ordinal), "坏文本没有给出带行号的错误：" + status.Text);
        PressButton("还原");
        await Frame();
        if (DisplayServer.GetName() != "headless")
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var shotDir = ProjectSettings.GlobalizePath("res://artifacts/inspector-blocks");
            Directory.CreateDirectory(shotDir);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(shotDir, "code-view.png"));
        }
        GD.Print($"INSPECTOR_CODE_VIEW_OK lines={printed.Split('\n').Length}");
        _blockEditor.Open("积木能力验证", original);
        _blockEditor.ConfigureTarget(null, originalParent);
        await Frame();
        GD.Print($"INSPECTOR_BLOCK_FORMS_OK actions={actions.Count} triggers={triggers.Length} operators={operators.Length} trigger-undo-redo nested-predicate");
    }
}
