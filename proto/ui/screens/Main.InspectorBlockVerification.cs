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
        var original = _blockEditor.Document.Clone();
        var originalParent = _blockEditor.SelectedTrigger;
        var actions = Blocks().Where(b => b.HasMeta("action_node")).Select(b => b.GetMeta("action_node").AsString()).ToHashSet();
        Check(_blockEditor.Catalog.Nodes.All(n => actions.Contains(n.Name)), "Action catalog is missing block-shaped entries");
        var category = _blockEditor.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>()
            .Single(p => Enumerable.Range(0, p.ItemCount).Any(i => p.GetItemText(i) == "触发事件"));
        async Task Category(string name)
        {
            var i = Enumerable.Range(0, category.ItemCount).Single(i => category.GetItemText(i) == name);
            category.Select(i); category.EmitSignal(OptionButton.SignalName.ItemSelected, i); await Frame();
        }
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
        _blockEditor.Open("积木能力验证", original);
        _blockEditor.ConfigureTarget(null, originalParent);
        await Frame();
        GD.Print($"INSPECTOR_BLOCK_FORMS_OK actions={actions.Count} triggers={triggers.Length} operators={operators.Length} trigger-undo-redo nested-predicate");
    }
}
