using Godot;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using Kards.Ui.OrcBridge.HandlerDsl;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyDiyTutorial()
    {
        try
        {
            _clock.SetReducedMotion(true); Show("editor"); await MenuSettledAsync();
            void Click(Button button) => button.EmitSignal(Button.SignalName.Pressed);
            void Step(int expected) => Require(_workshop.TutorialStep == expected,
                $"教程应在步骤 {expected}，实际 {_workshop.TutorialStep}：{_workshop.TutorialStatus} / {_workshop.NoticeText}");
            T Field<T>(string caption) where T : Control => _workshop.FindChildren("*", "Label", true, false).OfType<Label>()
                .Single(l => l.Text == caption && l.IsVisibleInTree() && l.GetParent().GetChildren().OfType<T>().Any()).GetParent().GetChildren().OfType<T>().Single();
            async Task Capture(string file)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (DisplayServer.GetName() == "headless") return;
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var directory = ProjectSettings.GlobalizePath("res://artifacts/diy-tutorial"); Directory.CreateDirectory(directory);
                GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, file + ".png"));
            }
            Click(_workshop.OpenInspectorButton);
            Require(_active == "editor" && _workshop.NoticeText.Contains("先打开一个卡包"), "无卡包会话时积木入口没有引导到保存目标");
            Click(_workshop.TutorialButton); Step(0); await Capture("01-intro");
            Click(_workshop.TutorialNext); Step(1);
            Click(_workshop.TutorialNext); Step(1);
            Require(_workshop.TutorialStatus.Contains("请检查"), "未完成属性时教程没有阻止跳步");
            Field<LineEdit>("名称").Text = DiyTutorialLesson.CardName;
            Field<LineEdit>("名称").EmitSignal(LineEdit.SignalName.TextChanged, DiyTutorialLesson.CardName);
            var type = Field<OptionButton>("卡牌类型"); DiyEditorScreen.Select(type, "unit"); type.EmitSignal(OptionButton.SignalName.ItemSelected, (long)type.Selected);
            var unit = Field<OptionButton>("兵种"); DiyEditorScreen.Select(unit, "infantry"); unit.EmitSignal(OptionButton.SignalName.ItemSelected, (long)unit.Selected);
            Field<SpinBox>("部署费用").Value = 2; Field<SpinBox>("攻击").Value = 2;
            Field<SpinBox>("防御").Value = 3; Field<SpinBox>("行动费用").Value = 1;
            await Capture("02-attributes"); Click(_workshop.TutorialNext); Step(2);
            var blitz = _workshop.FindChildren("*", "CheckBox", true, false).OfType<CheckBox>().Single(b => b.Text == "闪击" && b.IsVisibleInTree());
            blitz.ButtonPressed = true; Click(_workshop.TutorialNext); Step(3);
            Click(_workshop.TutorialNext); Step(3);
            Click(_workshop.NativeSaveButton);
            Click(_workshop.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "编辑效果 · 积木 Inspector…"));
            var selection = _workshop.GetChildren().OfType<AcceptDialog>().Single(d => d.Title == "卡牌效果 · 积木 Inspector");
            Click(selection.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "＋ 添加积木效果"));
            Require(_active == "hdsl", "卡牌没有打开积木 Inspector");
            var timing = _blockEditor.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().Single(b => b.IsVisibleInTree());
            var deployed = _nativeWorkshop.HandlerAuthoring!.DescribeParents().Single(p => p.Signal == "unit.deployed" && p.Guard == "Self");
            DiyEditorScreen.Select(timing, deployed.ParentId); timing.EmitSignal(OptionButton.SignalName.ItemSelected, (long)timing.Selected);
            var drawLabel = _blockEditor.FindChildren("*", "Label", true, false).OfType<Label>().Single(l => l.Text == "抽牌");
            drawLabel.GetParent().GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            Require(_blockEditor.Document.Actions is [{ Op: "draw" }], "工具箱没有插入抽牌积木");
            await Capture("03-effect");
            Click(_blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "保存到卡包"));
            var save = _blockEditor.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Title == "保存到卡包");
            Click(save.GetOkButton());
            Require(_blockEditor.SaveNote?.Contains("已保存") == true, "积木未保存：" + _blockEditor.SaveNote);
            Click(_blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "完成"));
            Require(_active == "editor", "积木完成后没有返回卡牌");
            // Reopen from the card: load the saved blocks, keep edits when save options change,
            // and save twice without producing a .dsl.dsl chain.
            for (var pass = 0; pass < 2; pass++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                Click(_workshop.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.IsVisibleInTree() && b.Text == "编辑效果 · 积木 Inspector…"));
                var pick = _workshop.GetChildren().OfType<AcceptDialog>().Single(d => d.Visible && d.Title == "卡牌效果 · 积木 Inspector");
                Click(pick.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text.StartsWith("编辑 · ")));
                Require(_active == "hdsl" && _blockEditor.Document.Actions is [{ Op: "draw" }], "重开效果没有恢复原积木");
                var json = _blockEditor.Document.ToJsonText(_blockEditor.Catalog);
                Click(_blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "保存到卡包"));
                var again = _blockEditor.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Visible && d.Title == "保存到卡包");
                Require(_blockEditor.Document.ToJsonText(_blockEditor.Catalog) == json, "保存选项覆盖了当前积木");
                var product = again.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(l => l.Name == "ProductId");
                Require(!product.Text.EndsWith(".dsl.dsl"), "再次编辑产生了递归后缀");
                Click(again.GetOkButton()); Require(_blockEditor.SaveNote?.Contains("已保存") == true, "再次保存失败");
                Click(_blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "完成"));
            }
            Click(_workshop.TutorialNext); Step(4);
            Field<TextEdit>("卡面说明（独立于规则执行）").Text = DiyTutorialLesson.DisplayText;
            Field<TextEdit>("卡面说明（独立于规则执行）").EmitSignal(TextEdit.SignalName.TextChanged);
            Click(_workshop.TutorialNext); Step(5);
            await Capture("04-save"); Click(_workshop.TutorialNext); Step(6);
            // Leave and resume through the player-facing controls; the saved card must reopen intact.
            Click(_workshop.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "暂时退出"));
            Click(_workshop.TutorialButton); Step(5); Click(_workshop.TutorialNext); Step(6);
            Click(_workshop.TutorialNext); Step(7); await Capture("05-complete");
            Require(_nativeWorkshop.Packages().Any(p => p.Name == "DIY 练习 · 补给步兵" && p.Cards == 1 && p.Enabled), "教程卡包未应用并启用");
            var lessonPack = _nativeWorkshop.Packages().Single(p => p.Name == "DIY 练习 · 补给步兵");
            _workshop.OpenNativePack(lessonPack.Id);
            var lessonAsset = _nativeWorkshop.Assets().Single(a => a.Kind == "Card");
            _workshop.OpenNativeCardForVerification(lessonPack.Id, lessonAsset);
            Click(_workshop.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.IsVisibleInTree() && b.Text == "应用并试打"));
            var deadline = Time.GetTicksMsec() + 30_000;
            while (_feed is null && Time.GetTicksMsec() < deadline) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(_active == "battle" && _feed is not null, "原生卡牌试打入口没有建立真实对局");
            StopRealMatch(); Show("editor");
            // The retained legacy card entry creates a native working copy and enters the same Inspector.
            var customNation = new Kards.Ui.Contracts.DiyNationDraft { Name = "副本测试国家" };
            var customKeyword = new Kards.Ui.Contracts.DiyKeywordDraft { Name = "副本测试词条", EffectText = "部署：抽一张牌。" };
            var oldPack = new Kards.Ui.Contracts.DiyPackDraft { Name = "旧草稿路由测试", Nations = [customNation], Keywords = [customKeyword] };
            _workshopLibrary.Save(oldPack); RefreshDiy(); _workshop.OpenPack(oldPack);
            _workshop.OpenCompatibleCardForVerification(oldPack, new() { PackId = oldPack.Id, Name = "保留原件", Nation = customNation.Id, Keywords = [customKeyword.Id] });
            Click(_diyEditor.OpenEffectsButton);
            Require(_active == "hdsl" && _nativeWorkshop.HasSession, "旧卡入口没有进入可保存的积木工作副本：" + _workshop.NoticeText);
            Require(_diy.Cards.Any(c => c.Name == "保留原件"), "旧卡原件丢失");
            GD.Print("DIY_TUTORIAL_VERIFY_OK: validation -> attributes -> keyword -> block effect -> display -> save -> resume -> promote");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
