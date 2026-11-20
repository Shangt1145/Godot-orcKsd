using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    internal readonly Dictionary<string, Button> ProjectOpenButtons = [];
    private void BuildProjectDashboard()
    {
        MenuStyles.Clear(_navigation);
        var layout = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        layout.AddThemeConstantOverride("separation", 24); _navigation.AddChild(layout);
        var sidebar = new PanelContainer { CustomMinimumSize = new(244, 0) }; layout.AddChild(sidebar);
        sidebar.AddThemeStyleboxOverride("panel", MenuStyles.Box(new("19221f"), new("35473d"), 1, 20));
        var start = new VBoxContainer(); start.AddThemeConstantOverride("separation", 14); sidebar.AddChild(start);
        start.AddChild(MenuStyles.Label("创作台", 26, MenuStyles.Ink));
        var intro = MenuStyles.Label("从一张卡开始，\n把你的想法做成可玩的卡包。", 14, MenuStyles.Dim); start.AddChild(intro);
        PackChoice = MenuStyles.Button("＋ 创建卡包", () => ShowProjectCreation(false));
        MenuStyles.Primary(PackChoice); start.AddChild(PackChoice);
        ExpansionChoice = MenuStyles.Button("基于已有卡包扩展", () => ShowProjectCreation(true)); start.AddChild(ExpansionChoice);
        NativeImportButton = MenuStyles.Button("导入卡包 ZIP", ImportNative); start.AddChild(NativeImportButton);
        start.AddChild(new HSeparator());
        start.AddChild(MenuStyles.Label("第一次制作？", 16, MenuStyles.Gold));
        var guide = MenuStyles.Button("跟着做一张补给步兵 →", () => TutorialButton.EmitSignal(Button.SignalName.Pressed)); start.AddChild(guide);
        guide.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var steps = MenuStyles.Label("01  创建或打开卡包\n\n02  制作卡牌与效果\n\n03  预览、试打并应用\n\n04  导出 ZIP 分享", 14, MenuStyles.Dim);
        steps.SizeFlagsVertical = SizeFlags.ExpandFill; start.AddChild(steps);

        var projects = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; projects.AddThemeConstantOverride("separation", 14); layout.AddChild(projects);
        var heading = new HBoxContainer(); projects.AddChild(heading);
        heading.AddChild(MenuStyles.Label("我的作品与卡包", 24, MenuStyles.Ink));
        var count = MenuStyles.Label("", 13, MenuStyles.Dim); count.SizeFlagsHorizontal = SizeFlags.ExpandFill; count.HorizontalAlignment = HorizontalAlignment.Right; heading.AddChild(count);
        var search = new LineEdit { PlaceholderText = "搜索卡包名称…", ClearButtonEnabled = true }; projects.AddChild(search);
        var filters = new HBoxContainer(); projects.AddChild(filters);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; projects.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; list.AddThemeConstantOverride("separation", 10); scroll.AddChild(list);
        var filter = "全部";
        var filterButtons = new List<Button>();
        void Render()
        {
            MenuStyles.Clear(list);
            ProjectOpenButtons.Clear();
            var nativePacks = _native?.Packages().ToDictionary(p => "pack/" + p.Id) ?? [];
            bool IsBuiltIn(DiyPackDraft pack) => nativePacks.TryGetValue(pack.Id, out var native) ? !native.Installed : pack.IsBuiltIn;
            var packs = WorkshopPacks(nativePacks.Values).Where(p => p.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)
                && (filter == "全部" || (filter == "自带卡包" ? IsBuiltIn(p) : !IsBuiltIn(p)))).ToArray();
            count.Text = $"{packs.Length} 个卡包";
            foreach (var b in filterButtons) b.SetPressedNoSignal(b.Text == filter);
            foreach (var pack in packs)
            {
                var panel = new PanelContainer(); panel.AddThemeStyleboxOverride("panel", MenuStyles.Box(new("222e28"), new("405448"), 1, 18)); list.AddChild(panel);
                var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 18); panel.AddChild(row);
                var marker = MenuStyles.Label(pack.IsExpansion ? "扩\n展" : "卡\n包", 18, MenuStyles.Gold); marker.CustomMinimumSize = new(44, 0); row.AddChild(marker);
                var info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddChild(info);
                var name = MenuStyles.Label(pack.Name, 21, MenuStyles.Ink); name.AutowrapMode = TextServer.AutowrapMode.WordSmart; info.AddChild(name);
                var detail = MenuStyles.Label(PackSummary(pack).Replace("\n", "  ·  "), 13, MenuStyles.Dim);
                detail.AutowrapMode = TextServer.AutowrapMode.WordSmart; info.AddChild(detail);
                var state = nativePacks.GetValueOrDefault(pack.Id);
                if (state is not null && (state.HasDraft || !state.Enabled))
                    info.AddChild(MenuStyles.Label((state.HasDraft ? "有未应用的草稿" : "") + (!state.Enabled ? "  ·  当前已停用" : ""), 13, MenuStyles.Gold));
                var open = MenuStyles.Button("打开卡包 →", () => OpenPack(pack)); open.SizeFlagsVertical = SizeFlags.ShrinkCenter; row.AddChild(open);
                ProjectOpenButtons[pack.Id] = open;
                row.AddChild(MenuStyles.Button("管理 / 分享", () =>
                {
                    var dialog = new AcceptDialog { Title = pack.Name, Exclusive = false, MinSize = new(480, 260) }; AddChild(dialog);
                    var actions = new VBoxContainer(); actions.AddThemeConstantOverride("separation", 14); dialog.AddChild(actions);
                    BuildPackageActions(actions, pack);
                    foreach (var action in actions.FindChildren("*", "Button", true, false).OfType<Button>()) action.Pressed += dialog.QueueFree;
                    dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
                }));
            }
            if (_native is not null && filter != "自带卡包")
                foreach (var draft in _native.InterruptedDrafts().Where(d => d.IsNew && d.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
                {
                    var actions = new HFlowContainer();
                    list.AddChild(actions);
                    actions.AddChild(MenuStyles.Button($"继续未完成的草稿：{draft.Name} →", () => OpenNativePack(draft.Id)));
                    var expansion = _native.BasePackOf(draft.Root, draft.Id) is not null;
                    actions.AddChild(MenuStyles.Button($"删除{PackKind(expansion)}…", () => ConfirmNativeRemoval(actions, draft.Id, draft.Name, expansion)));
                }
            if (list.GetChildCount() == 0) list.AddChild(MenuStyles.Label("没有找到卡包。试试其他名称，或在左侧创建作品。", 16, MenuStyles.Dim));
        }
        foreach (var label in new[] { "全部", "我的制作", "自带卡包" })
        {
            var button = MenuStyles.Button(label, () => { filter = label; Render(); }); button.ToggleMode = true;
            filterButtons.Add(button); filters.AddChild(button);
        }
        search.TextChanged += _ => Render(); Render();
    }

    private void ShowProjectCreation(bool expansion)
    {
        var dialog = new ConfirmationDialog { Title = expansion ? "创建扩展包" : "创建卡包", OkButtonText = "创建并开始制作", CancelButtonText = "取消", DialogHideOnOk = false, MinSize = new(520, 220) };
        AddChild(dialog);
        var scroll = new ScrollContainer { CustomMinimumSize = new(500, 280), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; dialog.AddChild(scroll);
        var form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; form.AddThemeConstantOverride("separation", 16); scroll.AddChild(form);
        var hint = MenuStyles.Label(expansion ? "继承一个卡包的国家和词条，在它的基础上制作新卡。" : "给作品起个名字，接下来就可以制作第一张卡。", 14, MenuStyles.Dim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; form.AddChild(hint);
        PackName = new LineEdit { PlaceholderText = "例如：北境前线", MaxLength = 60 }; DiyEditorScreen.Field(form, "作品名称", PackName);
        BaseChoice = new OptionButton { FitToLongestItem = false, ClipText = true };
        if (expansion)
        {
            BaseChoice.AddItem("请选择基础卡包"); BaseChoice.SetItemMetadata(0, "");
            foreach (var pack in WorkshopPacks().Where(p => !p.IsExpansion)) { BaseChoice.AddItem(pack.Name); BaseChoice.SetItemMetadata(BaseChoice.ItemCount - 1, pack.Id); }
            DiyEditorScreen.Field(form, "基础卡包", BaseChoice);
        }
        else { form.AddChild(BaseChoice); BaseChoice.Hide(); }
        var error = MenuStyles.Label("", 13, MenuStyles.Gold); error.AutowrapMode = TextServer.AutowrapMode.WordSmart; form.AddChild(error);
        ConfirmPack = dialog.GetOkButton(); ConfirmPack.Disabled = true;
        void Validate() => ConfirmPack.Disabled = string.IsNullOrWhiteSpace(PackName.Text) || (expansion && BaseChoice.Selected <= 0);
        PackName.TextChanged += _ => Validate(); BaseChoice.ItemSelected += _ => Validate();
        dialog.Confirmed += () =>
        {
            try
            {
                dialog.Hide();
                var parent = expansion ? DiyEditorScreen.Value(BaseChoice) : null;
                var reference = expansion ? parent is not null && IsNativePackage(parent) ? parent[5..] : null
                    : _native?.Packages().Where(p => p.Enabled).OrderByDescending(p => p.Id == "builtin").FirstOrDefault()?.Id;
                if (reference is not null && _native is not null)
                {
                    _native.Suspend(); var id = "user.pack." + Guid.NewGuid().ToString("N");
                    _native.Create(id, PackName.Text.Trim(), reference, expansion ? reference : null); OpenNativePack(id);
                }
                else
                {
                    var pack = new DiyPackDraft { Name = PackName.Text.Trim(), BasePackId = parent };
                    _store.Save(pack); _changed(); OpenPack(pack);
                }
                dialog.QueueFree();
            }
            catch (Exception ex) { error.Text = ex.Message; dialog.PopupCentered(); }
        };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered(); PackName.GrabFocus();
    }
}
