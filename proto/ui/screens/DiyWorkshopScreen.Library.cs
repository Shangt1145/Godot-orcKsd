using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    private DiyPackDraft[] WorkshopPacks()
    {
        var known = BuiltInWorkshopCatalog.Packs(_store);
        var installed = _native?.Packages().Select(p => new DiyPackDraft { Id = "pack/" + p.Id, Name = p.Name }) ?? [];
        return installed.Concat(known).GroupBy(p => p.Id, StringComparer.Ordinal).Select(g => g.Last()).ToArray();
    }

    private void BuildPackageActions(VBoxContainer parent, DiyPackDraft pack)
    {
        var native = _native?.Packages().FirstOrDefault(p => "pack/" + p.Id == pack.Id);
        if (native is null) return;
        parent.AddChild(MenuStyles.Label((native.Enabled ? "已启用" : "已停用") + (native.HasDraft ? " · 有未应用的修改" : ""), 14, MenuStyles.Gold));
        parent.AddChild(MenuStyles.Button("导出卡包 ZIP…", () => NativeFile(true, path => NativeTry(() => _native!.Export(native.Id, path)))));
        if (native.Installed)
            parent.AddChild(MenuStyles.Button(native.Enabled ? "停用此卡包" : "启用此卡包", () => NativeTry(() =>
            { _native!.SetEnabled(native.Id, !native.Enabled); _nativeChanged!(); ShowNativePackages(); })));
        if (native.HasDraft)
            parent.AddChild(MenuStyles.Button("放弃未应用的修改…", () =>
            {
                var confirm = new ConfirmationDialog { Title = "放弃草稿", DialogText = "当前草稿会归档保留，下次编辑从已应用的版本开始。", OkButtonText = "放弃草稿", CancelButtonText = "保留草稿" }; AddChild(confirm);
                confirm.Confirmed += () => NativeTry(() => { _native!.DiscardStoredDraft(native.Id); confirm.QueueFree(); ShowNativePackages(); });
                confirm.Canceled += confirm.QueueFree; confirm.PopupCentered();
            }));
        if (native.Diagnostics.Count > 0)
            parent.AddChild(MenuStyles.Button($"查看 {native.Diagnostics.Count} 条检查结果", () => NativeText("卡包检查结果", string.Join("\n", native.Diagnostics))));
    }

    private void EditCompatiblePackInfo(DiyPackDraft pack)
    {
        var dialog = new ConfirmationDialog { Title = "卡包信息", OkButtonText = "保存", DialogHideOnOk = false, MinSize = new(480, 180) }; AddChild(dialog);
        var form = new VBoxContainer(); dialog.AddChild(form);
        var name = new LineEdit { Text = pack.Name, MaxLength = 60 }; DiyEditorScreen.Field(form, "卡包名称", name);
        var notice = MenuStyles.Label("", 13, MenuStyles.Dim); form.AddChild(notice);
        dialog.Confirmed += () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { notice.Text = "请填写卡包名称。"; return; }
            NativeTry(() => { var updated = pack with { Name = name.Text.Trim() }; _store.Save(updated); _changed(); dialog.QueueFree(); OpenCompatiblePack(updated); });
        };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
}
