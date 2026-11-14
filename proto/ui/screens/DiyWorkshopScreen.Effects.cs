using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    internal Button EffectLibraryButton = null!;
    private void OpenEffectLibrary()
    {
        foreach (var previous in GetChildren().OfType<AcceptDialog>().Where(d => d.Title == "词条与效果"))
        { previous.Hide(); previous.QueueFree(); }
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
                ? "此效果没有旧版规则记录。若有积木源文件会自动载入；脚本效果需要重新搭建，保存前原行为保留。"
                : "按“发生时机 → 条件与目标 → 动作”编排。一条规则中可以组合多个条件和动作。");
            var authoring = _native!.HandlerAuthoring!;
            var hasBlocks = System.IO.File.Exists(System.IO.Path.Combine(authoring.PackRoot, "handler-dsl", effect.Asset.Id + ".hdsl.json")) || authoring.ReadExistingDsl(effect.Asset.Id) is not null;
            var button = MenuStyles.Button(hasBlocks ? "继续编辑积木…" : "用积木重写…", () =>
            {
                void Edit() => OpenEffectComposer(effect, dialog);
                if (hasBlocks) { Edit(); return; }
                var confirmation = new ConfirmationDialog { Title = "重新编排效果", DialogText = "这会替换原有效果的行为，并影响使用它的卡牌。保存时会备份原始卡包；取消编辑不会改变原有效果。", OkButtonText = "开始编排", CancelButtonText = "保留原有效果" }; dialog.AddChild(confirmation);
                confirmation.Confirmed += () => { confirmation.QueueFree(); Edit(); }; confirmation.Canceled += () => confirmation.QueueFree(); confirmation.PopupCentered();
            }); detail.AddChild(button); MenuStyles.Primary(button);
        }
        void RefreshList()
        {
            MenuStyles.Clear(list); foreach (var effect in _native!.Effects()) list.AddChild(MenuStyles.Button(effect.Name, () => Select(effect)));
        }
        left.AddChild(MenuStyles.Button("＋ 新建效果", () => NativeNameDialog("新建效果", "效果名称", name => NativeTry(() => { var packId = _native!.SessionPackage!; dialog.QueueFree(); LaunchBlocks(name, null, null, () => { OpenNativePack(packId); OpenEffectLibrary(); }); }), dialog)));
        // 复杂 DIY 的入口：积木搭出来的效果只能走 DSL → 草案 → 摊平这条路写回包，
        // 而这条路要求一个打开的编辑会话——所以入口放在工坊里，不放在卡牌草稿那边。
        left.AddChild(MenuStyles.Button("用积木搭…", () => { dialog.QueueFree(); RequestBlockAuthoring(); }));
        RefreshList(); if (_native!.Effects().FirstOrDefault() is { } first) Select(first); else Note(detail, "为卡包创建一个效果，再设置触发时机、条件和动作。");
        dialog.Confirmed += () => dialog.QueueFree(); dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
    }

    private void OpenEffectComposer(WorkshopEffect effect, Window owner)
    {
        if (owner != GetWindow()) owner.QueueFree();
        var packId = _native!.SessionPackage!;
        LaunchBlocks(effect.Name, effect.Asset.Id, null, () => { OpenNativePack(packId); OpenEffectLibrary(); });
    }
}
