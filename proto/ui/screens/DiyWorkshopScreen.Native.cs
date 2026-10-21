using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    private HBoxContainer _nativeToolbar = null!;
    private NativeWorkshop? _native;
    private Action? _nativeChanged;
    private NativeWorkshopAsset? _nativeAsset;
    private bool _nativeDirty;
    private readonly JsonSerializerOptions _nativeJson = new() { WriteIndented = true };
    internal Button NativeImportButton = null!, NativeManageButton = null!, NativeSaveButton = null!, NativePromoteButton = null!;
    private bool IsNativePackage(string id) => id.StartsWith("pack/", StringComparison.Ordinal)
        && _native?.Packages().Any(p => p.Id == id[5..]) == true;

    public void ConfigureNative(NativeWorkshop native, Action changed)
    {
        _native = native; _nativeChanged = changed;
        NativeImportButton = MenuStyles.Button("导入卡包 ZIP…", ImportNative);
        NativeManageButton = MenuStyles.Button("卡包管理", () => _editor.GuardNavigation(() => GuardNative(ShowNativePackages)));
        _nativeToolbar.AddChild(NativeImportButton); _nativeToolbar.AddChild(NativeManageButton);
        foreach (var error in native.StartupDiagnostics) GD.PushWarning(error);
    }

    private void NativeTry(Action action)
    { try { action(); } catch (Exception ex) { _notice.Text = ex.Message; GD.PushWarning("工坊：" + ex.Message); } }
    private void GuardNative(Action action)
    {
        if (!_nativeDirty) { action(); return; }
        var dialog = new ConfirmationDialog { Title = "还有未保存的修改", DialogText = "保存到草稿后继续，或放弃当前表单改动。已保存的临时包仍然保留。", OkButtonText = _saveNativeCard is null ? "放弃表单改动" : "保存后继续", CancelButtonText = "继续编辑", DialogHideOnOk = false };
        AddChild(dialog); dialog.Confirmed += () => { if (_saveNativeCard is not null && !_saveNativeCard()) return; _nativeDirty = false; action(); dialog.QueueFree(); };
        if (_saveNativeCard is not null) { dialog.AddButton("不保存，继续", true, "discard"); dialog.CustomAction += key => { if (key == "discard") { _nativeDirty = false; action(); dialog.QueueFree(); } }; }
        dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
    }
    private void NativeFile(bool save, Action<string> selected)
    {
        var dialog = new FileDialog { Title = save ? "导出卡包" : "导入卡包 ZIP", FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Filters = ["*.zip ; 卡包压缩包"], CurrentFile = save ? "card-pack.zip" : "" };
        AddChild(dialog); dialog.FileSelected += path => { selected(path); dialog.QueueFree(); };
        dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered(new Vector2I(900, 600));
    }
    private void ImportNative() => _editor.GuardNavigation(() => GuardNative(() => NativeFile(false, path =>
    {
        void Install(bool replace)
        {
            try
            {
                _native!.Suspend();
                var result = _native!.Import(path, replace); _nativeChanged!(); ShowNativePackages();
                _notice.Text = result.Unchanged ? "内容相同，已保留现有卡包。" : $"已安装并启用「{result.Pack.Name}」：{result.Pack.Cards} 张原生准入卡牌。";
                if (result.Pack.Diagnostics.Count > 0) _notice.Text += " 有待核对资产，请在卡包管理中查看诊断。";
            }
            catch (WorkshopPackageConflictException ex)
            {
                ShowNativePackages();
                var confirm = new ConfirmationDialog { Title = "替换已安装卡包", DialogText = ex.Message, OkButtonText = "备份并替换", CancelButtonText = "取消" };
                AddChild(confirm); confirm.Confirmed += () => { Install(true); confirm.QueueFree(); }; confirm.Canceled += () => confirm.QueueFree(); confirm.PopupCentered();
            }
            catch (Exception ex) { ShowNativePackages(); _notice.Text = ex.Message; }
        }
        Install(false);
    })));

    private void NativeWorkspace()
    {
        _compatiblePack = null;
        _saveNativeCard = null;
        StopChoiceMotion(); _navigationVersion++; _navigation.Hide(); _editor.Hide(); _workspace.Show(); _pack = null;
        if (_editor.GetParent() != this) { _editor.GetParent()?.RemoveChild(_editor); AddChild(_editor); _editor.Hide(); }
        MenuStyles.Clear(_workspace);
    }
    internal void ShowNativePackages()
    {
        if (_native is null) return;
        _native.Suspend();
        NativeWorkspace();
        _nativeDirty = false;
        _workspace.AddChild(MenuStyles.Label("已安装与自带卡包", 24, MenuStyles.Gold));
        _workspace.AddChild(MenuStyles.Button("新建卡包…", CreateNativePack));
        if (_native.HasSession)
            _workspace.AddChild(MenuStyles.Button("放弃当前卡包草稿…", () =>
            {
                var dialog = new ConfirmationDialog { Title = "放弃草稿", DialogText = "删除当前卡包的临时编辑内容，正式版保持不变。", OkButtonText = "放弃草稿" }; AddChild(dialog);
                dialog.Confirmed += () => NativeTry(() => { _native.Discard(); ShowNativePackages(); dialog.QueueFree(); }); dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
            }));
        foreach (var draft in _native.InterruptedDrafts().Where(d => d.IsNew))
        {
            _workspace.AddChild(MenuStyles.Button("继续制作：" + draft.Name, () => OpenNativePack(draft.Id)));
            _workspace.AddChild(MenuStyles.Button("导出草稿：" + draft.Name, () => NativeFile(true, path => NativeTry(() => _native.ExportInterruptedDraft(draft.Root, path)))));
        }
        _notice.Text = "管理卡包、继续上次修改或导出。编辑完成后点击“应用修改”，更新构筑与新对局。";
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; _workspace.AddChild(scroll);
        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill }; grid.AddThemeConstantOverride("h_separation", 24); grid.AddThemeConstantOverride("v_separation", 20); scroll.AddChild(grid);
        scroll.Resized += () => grid.Columns = Math.Max(1, (int)((scroll.Size.X + 24) / 300));
        foreach (var pack in _native.Packages())
        {
            var panel = new PanelContainer { CustomMinimumSize = new(280, 0) }; grid.AddChild(panel);
            panel.AddThemeStyleboxOverride("panel", MenuStyles.Box(MenuStyles.Panel, MenuStyles.Line, 1, 10));
            var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 10); panel.AddChild(column);
            var samples = new HBoxContainer(); column.AddChild(samples);
            foreach (var card in _service.Cards.Where(c => c.CardId.StartsWith("pack/" + pack.Id + "/", StringComparison.Ordinal)).Take(2))
            { var face = new CardControl(); samples.AddChild(face); face.Bind(card, _textures, 105); }
            column.AddChild(MenuStyles.Label(pack.Name, 20, MenuStyles.Gold));
            column.AddChild(MenuStyles.Label($"{pack.Cards} 张可用卡牌 · {(pack.Installed ? "本机安装" : "自带内容")} · {(pack.Enabled ? "已启用" : "已停用")}" + (pack.HasDraft ? " · 有草稿" : ""), 12));
            column.AddChild(MenuStyles.Button(_native.SessionPackage == pack.Id || pack.HasDraft ? "继续上次修改" : "编辑卡包", () => OpenNativePack(pack.Id)));
            if(pack.HasDraft) column.AddChild(MenuStyles.Button("放弃未应用的修改…", () =>
            {
                var confirmation = new ConfirmationDialog { Title = "放弃这份草稿", DialogText = "下次将从已应用的版本开始。当前草稿会归档保留，正式卡包不会改变。", OkButtonText = "放弃草稿", CancelButtonText = "继续保留" }; AddChild(confirmation);
                confirmation.Confirmed += () => NativeTry(() => { _native.DiscardStoredDraft(pack.Id); ShowNativePackages(); confirmation.QueueFree(); }); confirmation.Canceled += () => confirmation.QueueFree(); confirmation.PopupCentered();
            }));
            column.AddChild(MenuStyles.Button("导出正式包 ZIP…", () => NativeFile(true, path => NativeTry(() => { _native.Export(pack.Id, path); _notice.Text = "已导出：" + path; }))));
            if (pack.Installed) column.AddChild(MenuStyles.Button(pack.Enabled ? "停用" : "启用", () => NativeTry(() => { _native.SetEnabled(pack.Id, !pack.Enabled); _nativeChanged!(); ShowNativePackages(); })));
            if (pack.Diagnostics.Count > 0)
                column.AddChild(MenuStyles.Button($"查看 {pack.Diagnostics.Count} 条诊断", () => NativeText("原生资产诊断", string.Join("\n", pack.Diagnostics))));
        }
        if (_native.StartupDiagnostics.Count > 0) _notice.Text += "\n" + string.Join("\n", _native.StartupDiagnostics);
    }

    internal void OpenNativePack(string id) => GuardNative(() => NativeTry(() =>
    {
        if (_cards.Cards.Any(c => c.PackId == "pack/" + id && c.SourceCardId.Length > 0))
        { OpenCompatiblePack(BuiltInWorkshopCatalog.Packs(_store).Single(p => p.Id == "pack/" + id)); return; }
        _native!.Begin(id); NativeWorkspace(); _nativeAsset = null; _saveNativeCard = null;
        BuildNativeSessionHeader(id);
        BuildNativeLibrary(id);
    }));

    private static string KindLabel(string kind) => kind switch { "Card" => "卡牌", "Effect" => "效果", "Trigger" => "触发器", "Handler" => "处理器", _ => kind };
    internal void GuardLeaving(Action action) => _editor.GuardNavigation(() => GuardNative(action));
    private void NativeTouched() { _nativeDirty = true; if (GodotObject.IsInstanceValid(NativeSaveButton)) NativeSaveButton.Text = "保存草稿 *"; }
    private static void NativeField(VBoxContainer parent, string label, Control editor)
    {
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 12); parent.AddChild(row);
        var caption = MenuStyles.Label(label, 14, MenuStyles.Dim); caption.CustomMinimumSize = new(126, 0); row.AddChild(caption);
        editor.SizeFlagsHorizontal = SizeFlags.ExpandFill; row.AddChild(editor);
    }
    private void NativeText(string title, string text)
    {
        var dialog = new AcceptDialog { Title = title, MinSize = new(760, 500) }; AddChild(dialog);
        var editor = new TextEdit { Text = text, Editable = false, CustomMinimumSize = new(740, 460), WrapMode = TextEdit.LineWrappingMode.Boundary }; dialog.AddChild(editor);
        dialog.Confirmed += () => dialog.QueueFree(); dialog.PopupCentered();
    }

    private void NativeNameDialog(string title, string label, Action<string> submit, Node? owner = null)
    {
        var dialog = new ConfirmationDialog { Title = title, OkButtonText = "创建", MinSize = new(440, 150) }; (owner ?? this).AddChild(dialog);
        var fields = new VBoxContainer(); dialog.AddChild(fields); var input = new LineEdit(); DiyEditorScreen.Field(fields, label, input);
        dialog.Confirmed += () => NativeTry(() => { submit(input.Text.Trim()); dialog.QueueFree(); }); dialog.PopupCentered();
    }
    private void CreateNativePack()
    {
        var dialog = new ConfirmationDialog { Title = "新建卡包", OkButtonText = "创建草稿", MinSize = new(480, 260) }; AddChild(dialog);
        var form = new VBoxContainer(); dialog.AddChild(form);
        var id = "user.pack." + Guid.NewGuid().ToString("N");
        var name = new LineEdit { PlaceholderText = "显示名称" }; DiyEditorScreen.Field(form, "卡包名称", name);
        var dependencies = new OptionButton();
        foreach (var pack in _native!.Packages().Where(p => p.Enabled)) { dependencies.AddItem(pack.Name); dependencies.SetItemMetadata(dependencies.ItemCount - 1, pack.Id); }
        DiyEditorScreen.Field(form, "参考卡包", dependencies);
        dialog.Confirmed += () => NativeTry(() =>
        {
            if (dependencies.ItemCount == 0) throw new InvalidDataException("请先安装或启用一个依赖卡包。");
            _native.Create(id, name.Text.Trim(), dependencies.GetItemMetadata(dependencies.Selected).AsString());
            OpenNativePack(id); dialog.QueueFree();
        }); dialog.PopupCentered();
    }
}
