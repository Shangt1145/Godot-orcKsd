using Godot;
using Kards.Ui.OrcBridge.HandlerDsl;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyDiyCapabilities()
    {
        try
        {
            _clock.SetReducedMotion(true); Show("editor"); await MenuSettledAsync();
            async Task Capture(string name)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (DisplayServer.GetName() == "headless") return;
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var dir = ProjectSettings.GlobalizePath("res://artifacts/inspector-restoration"); Directory.CreateDirectory(dir);
                GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
            }
            Button Button(Node root, string text) => root.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == text && b.IsVisibleInTree());
            _workshop.OpenNativePack("orc.bulwark");
            var asset = _nativeWorkshop.CreateCard("capability.ui");
            var named = new Kards.Ui.OrcBridge.NativeCardDocument(_nativeWorkshop.Read(asset.Path)); named.Rename("定点打击"); _nativeWorkshop.Commit(asset.Path, named.Json);
            _workshop.OpenNativeCardForVerification("orc.bulwark", asset);
            await DeckClickAsync(Button(_workshop, "02  词条与效果"));
            Require(_workshop.NativeRuleText.IsVisibleInTree() && _workshop.NativeParseReplace.IsVisibleInTree(), "原生卡牌没有自动解析入口");
            const string source = "部署：对一个敌方单位造成2点伤害。";
            _workshop.NativeRuleText.Text = source;
            await DeckClickAsync(_workshop.NativeParseReplace);
            Require(_nativeWorkshop.CardPresentation(asset.Id).EditableRules.Length == 1, "自动解析没有保存可执行效果：" + _workshop.NativeParseStatus.Text);
            await Capture("01-parser");
            var before = _nativeWorkshop.Read(asset.Path);
            _workshop.NativeRuleText.Text = "部署：抽一张牌。完全无法解析的句子。";
            await DeckClickAsync(_workshop.NativeParseReplace);
            Require(_nativeWorkshop.Read(asset.Path) == before && _workshop.NativeParseStatus.Text.Contains("未应用"), "解析失败改写了已有规则");
            _workshop.NativeRuleText.Text = source; await DeckClickAsync(_workshop.NativeSaveButton);
            var edit = Button(_workshop, "编辑效果 · 积木 Inspector…");
            ((ScrollContainer)edit.GetParent().GetParent()).EnsureControlVisible(edit);
            await DeckClickAsync(edit);
            var choose = _workshop.GetChildren().OfType<AcceptDialog>().Single(d => d.Visible && d.Title == "卡牌效果 · 积木 Inspector");
            await DeckClickAsync(Button(choose, "＋ 添加积木效果"));
            var damage = _blockEditor.FindChildren("*", "Label", true, false).OfType<Label>().Single(l => l.Text == "造成伤害");
            damage.GetParent().GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            Require(_blockEditor.Document.Actions is [{ Op: "damage" }], "工具箱不能添加伤害动作");
            await DeckClickAsync(Button(_blockEditor, "选择作用目标…"));
            var target = _blockEditor.GetChildren().OfType<AcceptDialog>().Single(d => d.Visible && d.Title == "选择作用目标");
            await Capture("02-target-picker");
            var use = Button(target, "使用此目标范围");
            target.GetChildren().OfType<ScrollContainer>().Single().EnsureControlVisible(use);
            await DeckClickAsync(use);
            Require(_blockEditor.Document.Actions is [{ Op: "selectTargets" }, { Op: "iterate" }], "目标选择没有构建可执行积木");
            Require(_blockEditor.Document.Actions[1].Body("actions") is [{ Op: "damage" }], "伤害动作没有作用于选中的目标");
            var targetJson = _blockEditor.Document.ToJsonText(_blockEditor.Catalog);
            await DeckClickAsync(Button(_blockEditor, "展开选目标步骤…"));
            Require(_blockEditor.Document.ToJsonText(_blockEditor.Catalog) == targetJson, "展开目标步骤改变了效果");
            await DeckClickAsync(Button(_blockEditor, "收起选目标步骤"));
            Require(_blockEditor.Document.ToJsonText(_blockEditor.Catalog) == targetJson, "收起目标步骤改变了效果");
            var drawLabel = _blockEditor.FindChildren("*", "Label", true, false).OfType<Label>().Single(l => l.Text == "抽牌");
            drawLabel.GetParent().GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            var up = _blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().Last(b => b.TooltipText == "上移" && b.IsVisibleInTree());
            await DeckClickAsync(up);
            Require(_blockEditor.Document.Actions is [{ Op: "draw" }, { Op: "selectTargets" }, { Op: "iterate" }], "移动普通动作拆散了目标步骤");
            var down = _blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.TooltipText == "下移" && b.IsVisibleInTree());
            await DeckClickAsync(down);
            Require(_blockEditor.Document.Actions is [{ Op: "selectTargets" }, { Op: "iterate" }, { Op: "draw" }], "移动动作越过目标步骤失败");
            await DeckClickAsync(_blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().Last(b => b.TooltipText == "删除" && b.IsVisibleInTree()));
            Require(_blockEditor.Document.ToJsonText(_blockEditor.Catalog) == targetJson, "删除普通动作改变了目标效果");
            await Capture("03-target-damage");
            await DeckClickAsync(Button(_blockEditor, "保存到卡包"));
            var save = _blockEditor.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Visible && d.Title == "保存到卡包");
            await Capture("04-save-dialog");
            await DeckClickAsync(save.GetOkButton());
            Require(_blockEditor.SaveNote?.Contains("已保存") == true, "目标伤害积木保存失败：" + _blockEditor.SaveNote);
            await DeckClickAsync(Button(_blockEditor, "自动解析器"));
            Require(_active == "editor" && _workshop.NativeRuleText.IsVisibleInTree(), "Inspector 自动解析按钮没有返回当前卡牌解析器");
            Require(_nativeWorkshop.CardPresentation(asset.Id).EditableRules.Length == 1, "添加积木丢失了自动解析的效果");
            var scriptDoc = new HandlerDocument(); scriptDoc.Actions.Add(HandlerNode.Script("// 原始脚本\nvar x = 1;"));
            _blockEditor.Open("高级脚本验证", scriptDoc); Show("hdsl"); await MenuFrameAsync();
            await DeckClickAsync(Button(_blockEditor, "编辑脚本与输入输出…"));
            var script = _blockEditor.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Visible && d.Title == "高级脚本");
            var code = script.FindChildren("*", "CodeEdit", true, false).OfType<CodeEdit>().Single();
            code.Text = "// 多行正文\nvar output = 2;";
            await DeckClickAsync(Button(script, "＋ 添加输出"));
            script.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(l => l.PlaceholderText == "脚本中的变量名").Text = "output";
            await DeckClickAsync(script.GetOkButton());
            Require(_blockEditor.Document.Actions[0].Text("body") == "// 多行正文\nvar output = 2;" && _blockEditor.Document.Actions[0].ScriptPins["outputs"].Count == 1, "脚本正文或输出声明没有应用");
            GD.Print("DIY_CAPABILITY_VERIFY_OK native-parser atomic-reject targeted-damage blocks-save inspector-parser-route source-preserved");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
