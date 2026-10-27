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
        if (native is null) { BuildCompatiblePackageActions(parent, pack); return; }
        parent.AddChild(MenuStyles.Label((native.Enabled ? "已启用" : "已停用") + (native.HasDraft ? " · 有未应用的修改" : ""), 14, MenuStyles.Gold));
        parent.AddChild(MenuStyles.Button("导出卡包 ZIP…", () => NativeFile(true, path => NativeTry(() =>
        {
            // 坏包会随 ZIP 原样传给下载方，对方连修的机会都没有——导出前先报一声。
            var orphans = _native!.CountOrphanPresentationReferences(native.Id);
            _native.Export(native.Id, path);
            if (orphans > 0)
                _notice.Text = $"已导出到 {path}，但包内有 {orphans} 条失效的展示引用（卡本身不受影响）。建议先在编辑会话里清理，再分享给别人。";
        }))));
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

    /// <summary>
    /// DIY 卡包（草稿库里的包，没有原生安装目录）的操作面。
    /// <para>
    /// 以前这里直接 <c>return</c>：DIY 包连一个按钮都没有，玩家根本拿不到 ZIP。现在导出走
    /// <see cref="DiyPackExport"/>——把草稿编译成原生卡包格式，对方用「导入卡包 ZIP…」装上即用。
    /// </para>
    /// </summary>
    private void BuildCompatiblePackageActions(VBoxContainer parent, DiyPackDraft pack)
    {
        var cards = _cards.Cards.Where(c => c.PackId == pack.Id).ToArray();
        parent.AddChild(MenuStyles.Label(
            $"DIY 卡包 · {cards.Length} 张卡 · {pack.Keywords.Length} 个词条"
            + (pack.IsExpansion ? " · 扩展包" : ""), 14, MenuStyles.Gold));
        AddCompatibleExportButtons(parent, pack, cards);
    }

    /// <summary>
    /// 导出按钮组。卡包详情页与「修改卡包」面板共用这一份实现——两处都要能导出，
    /// 否则玩家在自己的卡包页里找不到分享入口，只能绕去"修改卡包"。
    /// </summary>
    private void AddCompatibleExportButtons(Container parent, DiyPackDraft pack, DiyCardDraft[] cards)
    {
        parent.AddChild(MenuStyles.Button("导出卡包 ZIP…", () => NativeFile(true, path => NativeTry(() =>
        {
            var result = DiyPackExport.Export(pack, cards, _service.Cards, _cards.ArtDirectory, path);
            if (result.Archive is null)
            {
                _notice.Text = "导出前检查没通过，未写文件：" + string.Join("；", result.Problems);
                return;
            }
            _notice.Text = $"已导出 {result.Cards} 张卡到 {path}。对方在工坊用「导入卡包 ZIP…」安装后即可直接使用。"
                + (result.Problems.Count > 0 ? " 注意：" + string.Join("；", result.Problems) : "");
        }))));
        if (cards.Length == 0) return;
        parent.AddChild(MenuStyles.Button("导出前检查", () => NativeTry(() =>
        {
            var problems = DiyPackExport.Check(pack, cards, _service.Cards, _cards.ArtDirectory);
            _notice.Text = problems.Count == 0
                ? $"检查通过：{cards.Length} 张卡都能编译，卡图与词条齐全，可以分享。"
                : "分享前需要先处理：" + string.Join("；", problems);
        })));
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
