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
        NativeManageButton = MenuStyles.Button("我的卡包", () => _editor.GuardNavigation(() => GuardNative(ShowNativePackages)));
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
    /// <summary>
    /// 探针专用：非空时「导出/导入卡包 ZIP…」**跳过系统文件对话框**，直接用这个路径。
    ///
    /// <para>
    /// 无头模式下 <see cref="FileDialog"/> 的原生对话框弹不出来，而这两个按钮是 P3
    /// 「DIY 包拿得到 ZIP」唯一的玩家入口——没有这个缝，整条链在探针里就只能靠读代码。
    /// 只由 <c>--verify-diy</c> 设置，正常运行永远是 null。
    /// </para>
    /// </summary>
    internal static string? VerificationFileTarget;

    private void NativeFile(bool save, Action<string> selected)
    {
        if (VerificationFileTarget is { } fixedPath)
        {
            try { selected(save ? fixedPath : PlatformFiles.ReadLocalCopy(fixedPath, ".zip")); }
            catch (Exception error) { _notice.Text = error.Message; }
            return;
        }
        var dialog = new FileDialog { Title = save ? "导出卡包" : "导入卡包 ZIP", FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Filters = ["*.zip ; 卡包压缩包"], CurrentFile = save ? "card-pack.zip" : "" };
        AddChild(dialog); dialog.FileSelected += path => {
            try { if (save) PlatformFiles.WriteDocument(path, selected, ".zip"); else selected(PlatformFiles.ReadLocalCopy(path, ".zip")); }
            catch (Exception error) { _notice.Text = error.Message; }
            finally { dialog.QueueFree(); }
        };
        dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered(new Vector2I(900, 600));
    }

    /// <summary>探针用：把工坊底部那行提示读出来，失败时能直接说清"为什么没导出"。</summary>
    internal string NoticeForVerification => _notice.Text;
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
    // Both shortcuts and the decision cards use the same gallery.
    internal void ShowNativePackages()
    {
        _native?.Suspend();
        OpenSelection(false, true, showAll: true);
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

}
