using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

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
            await Capture("00-dashboard");
            Click(_workshop.PackChoice);
            Require(_workshop.ConfirmPack.Disabled, "空名称不应允许创建卡包");
            _workshop.PackName.Text = "工作台创建验证";
            await Capture("00-create");
            Click(_workshop.ConfirmPack);
            Require(_nativeWorkshop.HasSession && _nativeWorkshop.WorkingPackage.Name == "工作台创建验证", "创建卡包未进入工作台");
            _nativeWorkshop.Discard(); _workshop.ShowHome();
            Click(_workshop.ExpansionChoice);
            DiyEditorScreen.Select(_workshop.BaseChoice, "pack/builtin");
            _workshop.BaseChoice.EmitSignal(OptionButton.SignalName.ItemSelected, (long)_workshop.BaseChoice.Selected);
            _workshop.PackName.Text = "工作台扩展验证"; Click(_workshop.ConfirmPack);
            Require(_nativeWorkshop.HasSession && _nativeWorkshop.WorkingPackage.Name == "工作台扩展验证", "扩展包未进入工作台");
            using (var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_nativeWorkshop.TemporaryRoot!, "workshop.presentation.json"))))
                Require(metadata.RootElement.GetProperty("basePackId").GetString() == "builtin", "扩展包未保留基础卡包");
            Button OnScreen(string label) => _workshop.FindChildren("*", "Button", true, false).OfType<Button>()
                .Single(b => b.Text == label && b.IsVisibleInTree());
            foreach (var (template, action) in new[] { ("火力打击", "damage"), ("增援指令", "buff"), ("补给步兵", "draw") })
            {
                Click(OnScreen("从模板新建…"));
                var templates = _workshop.GetChildren().OfType<AcceptDialog>().Single(d => d.Visible && d.Title == "卡牌模板");
                Click(templates.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text.StartsWith(template + " ·")));
                Require(_nativeWorkshop.PreviewCards().Any(c => c.Name == template && c.CardId.StartsWith("pack/" + _nativeWorkshop.SessionPackage + "/")),
                    "模板卡没有进入所选扩展包：" + _workshop.NoticeText);
                Require(_nativeWorkshop.Assets().Where(a => a.Kind == "Card")
                    .Any(a => _nativeWorkshop.CardPresentation(a.Id).EditableRules.Any(r => WorkshopQuickEffect.From(r)?.Action == action)),
                    "模板卡没有写入实际效果");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                Click(OnScreen("‹ 卡牌一览"));
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            Click(OnScreen("说明书"));
            var guide = _workshop.GetChildren().OfType<AcceptDialog>().Single(d => d.Visible && d.Title == "工坊说明书");
            Click(guide.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "保存和试打"));
            Require(guide.FindChildren("*", "TextEdit", true, false).OfType<TextEdit>().Single().Text.Contains("返回改卡"), "说明书章节无法打开");
            Click(guide.GetOkButton());
            var removedExpansion = _nativeWorkshop.SessionPackage;
            _workshop.ShowNativePackages();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Click(OnScreen("删除扩展包…"));
            var remove = _workshop.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Visible && d.Title == "删除扩展包");
            Click(remove.GetOkButton());
            Require(!_nativeWorkshop.InterruptedDrafts().Any(d => d.Id == removedExpansion) &&
                _workshop.NoticeText.Contains("已删除"), "工坊删除扩展包入口失败：" + _workshop.NoticeText);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
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
            async Task SetDraw(string button, int amount)
            {
                Click(OnScreen(button));
                var dialog = _workshop.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Visible && d.Title == "效果");
                dialog.FindChildren("*", "SpinBox", true, false).OfType<SpinBox>().First().Value = amount;
                Click(dialog.GetOkButton());
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            async Task Trial(int expected)
            {
                Click(_workshop.TutorialNext);
                var deadline = Time.GetTicksMsec() + 30_000;
                while (_workshopTrialOpening is null && _active == "battle" && Time.GetTicksMsec() < deadline)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                Require(_runner?.CurrentView is not null && _workshopTrialOpening.HasValue, "试打没有启动：" + _workshop.NoticeText);
                var before = _runner!.CurrentView!;
                var unit = before.SelfHand.Single();
                var deadlineForInput = Time.GetTicksMsec() + 15_000;
                while (_battle.ReadInteractionState().Phase != UiInteractionPhase.Ready && Time.GetTicksMsec() < deadlineForInput)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await _battle.DragAsync(unit.Uid, _battle.DropAtRowEdge(false, false));
                var deadlineForDeployment = Time.GetTicksMsec() + 15_000;
                while (!_runner.CurrentView!.SelfLine.Any(card => card.Uid == unit.Uid) && Time.GetTicksMsec() < deadlineForDeployment)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                Require(_runner.CurrentView!.SelfLine.Any(card => card.Uid == unit.Uid)
                    && before.SelfDeckCount - _runner.CurrentView.SelfDeckCount == expected, "试打抽牌数量不符：" + _runner.LastRejectionReason);
                Click(_battle.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "返回改卡"));
            }
            await SetDraw("＋ 添加效果", 1);
            await Capture("03-effect");
            Click(_workshop.TutorialNext); Step(4);
            Click(OnScreen("暂时退出"));
            Click(_workshop.TutorialButton); Step(4);
            var lessonId = _nativeWorkshop.SessionPackage;
            await Trial(1); Step(5);
            await SetDraw("编辑", 2);
            Click(_workshop.TutorialNext); Step(6);
            await Trial(2); Step(7);
            await Capture("05-complete");
            Require(!_nativeWorkshop.Packages().Any(p => p.Id == lessonId && p.Installed), "试打不应应用练习草稿");
            // The retained legacy card entry creates a native working copy and enters the same Inspector.
            var customNation = new Kards.Ui.Contracts.DiyNationDraft { Name = "副本测试国家" };
            var customKeyword = new Kards.Ui.Contracts.DiyKeywordDraft { Name = "副本测试词条", EffectText = "部署：抽一张牌。" };
            var oldPack = new Kards.Ui.Contracts.DiyPackDraft { Name = "旧草稿路由测试", Nations = [customNation], Keywords = [customKeyword] };
            _workshopLibrary.Save(oldPack); RefreshDiy(); _workshop.OpenPack(oldPack);
            _workshop.OpenCompatibleCardForVerification(oldPack, new() { PackId = oldPack.Id, Name = "保留原件", Nation = customNation.Id, Keywords = [customKeyword.Id] });
            Click(_diyEditor.OpenEffectsButton);
            Require(_active == "hdsl" && _nativeWorkshop.HasSession, "旧卡入口没有进入可保存的积木工作副本：" + _workshop.NoticeText);
            Require(_diy.Cards.Any(c => c.Name == "保留原件"), "旧卡原件丢失");
            GD.Print("DIY_TUTORIAL_VERIFY_OK: attributes keyword quick-effect save resume draft-trial draw-one draw-two return");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
