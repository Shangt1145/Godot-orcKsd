using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    internal Button EffectLibraryButton = null!;
    private void OpenEffectLibrary()
    {
        var dialog = new AcceptDialog { Title = "词条与效果", OkButtonText = "完成", MinSize = new(980, 640) }; AddChild(dialog);
        var split = new HBoxContainer { CustomMinimumSize = new(950, 550) }; split.AddThemeConstantOverride("separation", 28); dialog.AddChild(split);
        var left = new VBoxContainer { CustomMinimumSize = new(220, 0) }; split.AddChild(left);
        left.AddChild(MenuStyles.Label("包内效果", 22, MenuStyles.Gold));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; left.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(list);
        var detail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; detail.AddThemeConstantOverride("separation", 18); split.AddChild(detail);
        void Note(VBoxContainer parent, string message) { var label = MenuStyles.Label(message, 16, MenuStyles.Dim); label.CustomMinimumSize = new(620, 0); label.AutowrapMode = TextServer.AutowrapMode.WordSmart; parent.AddChild(label); }
        void Select(WorkshopEffect effect)
        {
            MenuStyles.Clear(detail); detail.AddChild(MenuStyles.Label(effect.Name, 28, MenuStyles.Gold));
            Note(detail, effect.Description.Length > 0 ? effect.Description : "这个效果还没有填写给玩家看的说明。");
            detail.AddChild(MenuStyles.Button("修改名称和说明…", () =>
            {
                var edit = new ConfirmationDialog { Title = "效果说明", OkButtonText = "保存说明", MinSize = new(640, 300) }; dialog.AddChild(edit);
                var form = new VBoxContainer(); edit.AddChild(form); var name = new LineEdit { Text = effect.Name }; DiyEditorScreen.Field(form, "名称", name);
                var description = new TextEdit { Text = effect.Description, CustomMinimumSize = new(600, 120), WrapMode = TextEdit.LineWrappingMode.Boundary }; DiyEditorScreen.Field(form, "给玩家看的说明", description);
                edit.Confirmed += () => NativeTry(() => { _native!.SaveEffectInfo(effect.Asset.Id, name.Text, description.Text); RefreshList(); Select(_native.Effects().Single(e => e.Asset.Id == effect.Asset.Id)); edit.QueueFree(); }); edit.Canceled += () => edit.QueueFree(); edit.PopupCentered();
            }));
            detail.AddChild(MenuStyles.Label("什么时候发生", 18, MenuStyles.Ink));
            Note(detail, effect.Occasions.Length == 0 ? "尚未设置" : string.Join("、", effect.Occasions));
            detail.AddChild(MenuStyles.Label("编辑效果", 18, MenuStyles.Ink));
            Note(detail, effect.Rules is null
                ? "这个已有效果使用脚本编写，目前不能完整还原成图形规则。现有行为会保留，你不需要填写底层字段。"
                : "按“发生时机 → 条件与目标 → 动作”编排。一条规则中可以组合多个条件和动作。");
            var button = MenuStyles.Button(effect.Rules is null ? "重新编排这个效果…" : "编辑规则…", () =>
            {
                void Edit() => OpenEffectComposer(effect, () => { RefreshList(); Select(_native!.Effects().Single(e => e.Asset.Id == effect.Asset.Id)); }, dialog);
                if (effect.Rules is not null) { Edit(); return; }
                var confirmation = new ConfirmationDialog { Title = "重新编排效果", DialogText = "这会替换原有效果的行为，并影响使用它的卡牌。保存时会备份原始卡包；取消编辑不会改变原有效果。", OkButtonText = "开始编排", CancelButtonText = "保留原有效果" }; dialog.AddChild(confirmation);
                confirmation.Confirmed += () => { confirmation.QueueFree(); Edit(); }; confirmation.Canceled += () => confirmation.QueueFree(); confirmation.PopupCentered();
            }); detail.AddChild(button); MenuStyles.Primary(button);
        }
        void RefreshList()
        {
            MenuStyles.Clear(list); foreach (var effect in _native!.Effects()) list.AddChild(MenuStyles.Button(effect.Name, () => Select(effect)));
        }
        left.AddChild(MenuStyles.Button("＋ 新建效果", () => NativeNameDialog("新建效果", "效果名称", name => NativeTry(() => { var effect = _native!.CreateEffect(name); RefreshList(); Select(effect); }), dialog)));
        RefreshList(); if (_native!.Effects().FirstOrDefault() is { } first) Select(first); else Note(detail, "为卡包创建一个效果，再设置触发时机、条件和动作。");
        dialog.Confirmed += () => dialog.QueueFree(); dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
    }

    private void OpenEffectComposer(WorkshopEffect effect, Action saved, Window owner)
    {
        var dialog = new ConfirmationDialog { Title = "编排效果", OkButtonText = "保存效果", CancelButtonText = "取消", DialogHideOnOk = false, MinSize = new(1100, 720) }; owner.AddChild(dialog);
        var scroll = new ScrollContainer { CustomMinimumSize = new(1060, 600), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; dialog.AddChild(scroll);
        var page = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(page);
        var name = new LineEdit { Text = effect.Name }; DiyEditorScreen.Field(page, "效果名称", name);
        var description = new LineEdit { Text = effect.Description, PlaceholderText = "给玩家看的效果说明" }; DiyEditorScreen.Field(page, "效果说明", description);
        var inspector = new NativeEffectInspector { CustomMinimumSize = new(1020, 560), SizeFlagsVertical = SizeFlags.ExpandFill, Catalog = () => _service.Cards }; page.AddChild(inspector); inspector.Initialize(); inspector.UsePlayerAuthoring(); inspector.UseSingleRuleAuthoring();
        var seed = new DiyCardDraft { Name = effect.Name, OrcEffects = effect.Rules ?? [], UseStructuredEffects = true }; inspector.Open(InspectorCardRecord.FromDraft(seed));
        if (seed.OrcEffects.Length == 0) inspector.NewEffectButton.EmitSignal(Button.SignalName.Pressed);
        void SingleRule() => inspector.NewEffectButton.Visible = inspector.Document.Root["effects"]!.AsArray().Count == 0;
        inspector.Edited += SingleRule; SingleRule();
        inspector.NewNativeEffectButton.Hide(); inspector.CardKeywordsButton.Hide();
        dialog.Confirmed += () => NativeTry(() =>
        {
            if (!inspector.CommitPendingRaw()) return;
            var updated = InspectorCardRecord.ApplyRecord(seed, inspector.Document.Element());
            if (updated.Effects.Length > 0) throw new InvalidDataException("请使用图形规则编排此效果。");
            _native!.SaveEffect(effect, name.Text, description.Text, updated.OrcEffects); _notice.Text = "效果已保存。点击“应用修改”后用于新对局，原始卡包已备份。";
            saved(); dialog.QueueFree();
        }); dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
    }
}
