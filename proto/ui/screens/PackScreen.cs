using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class PackScreen : VBoxContainer
{
    private PackLibrary _packs = null!;
    private VBoxContainer _list = null!;
    private Label _status = null!;
    private ConfirmationDialog _delete = null!;
    private string? _deleteId;
    public event Action? Changed;

    public void Initialize(PackLibrary packs)
    {
        _packs = packs; SizeFlagsVertical = SizeFlags.ExpandFill;
        var title = new HBoxContainer(); AddChild(title);
        title.AddChild(UiStyles.Label("卡包", 30, UiStyles.MenuInk));
        title.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        title.AddChild(UiStyles.Button("导入卡包…", ImportDialog));
        _status = UiStyles.Label("", 13, UiStyles.MenuDim); AddChild(_status);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _list.AddThemeConstantOverride("separation", 10); scroll.AddChild(_list);
        _delete = new ConfirmationDialog { Title = "删除卡包", DialogHideOnOk = false, MinSize = new(420, 140) }; AddChild(_delete);
        _delete.Confirmed += () =>
        {
            if (_deleteId is null) return;
            try { _packs.Remove(_deleteId); } catch (Exception e) { _status.Text = e.Message; }
            _deleteId = null; Refresh(); Changed?.Invoke();
        };
        _delete.CloseRequested += () => _delete.QueueFree();
        _delete.Canceled += () => _delete.QueueFree();
        Refresh();
    }

    public void Refresh()
    {
        var entries = _packs.Entries;
        _status.Text = _packs.LoadError ?? (entries.Count == 0
            ? "还没有卡包。卡包提供新词条与单位；扩展包只能基于卡包添加单位。"
            : $"{entries.Count(e => e.Enabled)} / {entries.Count} 个卡包已启用");
        UiStyles.Clear(_list);
        if (entries.Count == 0) return;
        foreach (var entry in entries)
        {
            var row = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeStyleboxOverride("panel", UiStyles.Box(new("1b231cde"), entry.Enabled ? UiStyles.Gold : new("545c4a"), 1, 12));
            _list.AddChild(row);
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; box.AddThemeConstantOverride("separation", 4); row.AddChild(box);
            var head = new HBoxContainer(); box.AddChild(head);
            head.AddChild(UiStyles.Label(entry.Name, 18, UiStyles.MenuInk));
            head.AddChild(UiStyles.Label("  " + (entry.Kind == "expansion" ? "扩展包" : "卡包")
                + (entry.BasePackId is { } baseId ? $" · 基于 {baseId}" : ""), 13, UiStyles.MenuDim));
            head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
            if (entry.Enabled)
                head.AddChild(UiStyles.Button("停用", () => SetEnabled(entry.Id, false)));
            else
                head.AddChild(UiStyles.Button("启用", () => SetEnabled(entry.Id, true)));
            var remove = UiStyles.Button("删除", () => { _deleteId = entry.Id; _delete.PopupCentered(); });
            if (entry.Enabled) remove.Disabled = true;
            head.AddChild(remove);
            var note = entry.LoadError is { } error ? error
                : entry.Kind == "expansion" && entry.Enabled ? "已启用——只能添加单位卡" : "";
            if (note.Length > 0)
                box.AddChild(UiStyles.Label(note, 13, entry.LoadError is null ? UiStyles.MenuDim : new("dca58b")));
        }
    }

    private void SetEnabled(string id, bool enabled)
    {
        _packs.SetEnabled(id, enabled);
        Refresh();
        Changed?.Invoke();
    }

    private void ImportDialog()
    {
        var dialog = new ConfirmationDialog { Title = "导入卡包目录", MinSize = new(650, 240), DialogHideOnOk = false }; AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        box.AddChild(UiStyles.Label("粘贴含 pack.json 的卡包目录完整路径；导入后默认停用。", 14, UiStyles.Dim));
        var text = new TextEdit { CustomMinimumSize = new(600, 80), SizeFlagsVertical = SizeFlags.ExpandFill }; box.AddChild(text);
        var error = UiStyles.Label("", 13, new("dca58b")); error.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(error);
        dialog.Confirmed += () =>
        {
            try
            {
                var path = text.Text.Trim().Trim('"');
                _packs.Import(Directory.Exists(path) ? path : throw new InvalidDataException("目录不存在：" + path));
                Refresh(); Changed?.Invoke(); dialog.QueueFree();
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { error.Text = e.Message; }
        };
        dialog.CloseRequested += () => dialog.QueueFree(); dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
    }
}
