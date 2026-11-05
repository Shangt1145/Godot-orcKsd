using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class AppearanceScreen
{
    private Button _importModel = null!;
    private DeskPropPreview _propPreview = null!;
    private VBoxContainer _propControls = null!;
    private OptionButton _clickMode = null!, _animation = null!;
    private SpinBox _propSize = null!, _propYaw = null!;
    private bool _refreshingProp;
    private static readonly string[] ClickModes = ["auto", "none", "rock", "spin", "lift"];
    private void InitializePropEditor(VBoxContainer right)
    {
        _propPreview = new DeskPropPreview { CustomMinimumSize = new(420, 260), SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _propPreview.Initialize(); right.AddChild(_propPreview);
        _propControls = new VBoxContainer(); right.AddChild(_propControls);
        var row = new HBoxContainer(); _propControls.AddChild(row);
        row.AddChild(MenuStyles.Label("点击演出", 14, MenuStyles.Ink));
        _clickMode = new OptionButton();
        foreach (var label in new[] { "自动：模型动画 / 轻晃", "关闭", "轻晃", "转一圈", "抬起落下" }) _clickMode.AddItem(label);
        row.AddChild(_clickMode); row.AddChild(MenuStyles.Button("播放预览", () => _propPreview.PlayClick()));
        _animation = new OptionButton(); _propControls.AddChild(_animation);
        var pose = new HBoxContainer(); _propControls.AddChild(pose);
        pose.AddChild(MenuStyles.Label("大小", 14, MenuStyles.Ink));
        _propSize = new SpinBox { MinValue = .25, MaxValue = 3, Step = .05, Value = 1, CustomMinimumSize = new(100, 32) }; pose.AddChild(_propSize);
        pose.AddChild(MenuStyles.Label("朝向（度）", 14, MenuStyles.Ink));
        _propYaw = new SpinBox { MinValue = -180, MaxValue = 180, Step = 5, CustomMinimumSize = new(100, 32) }; pose.AddChild(_propYaw);
        _clickMode.ItemSelected += _ => ChangePropSettings(); _animation.ItemSelected += _ => ChangePropSettings();
        _propSize.ValueChanged += _ => ChangePropSettings(); _propYaw.ValueChanged += _ => ChangePropSettings();
    }
    private void ChangePropSettings()
    {
        if (_refreshingProp) return;
        _draft = _draft with { Prop = _draft.Prop with { Click = ClickModes[_clickMode.Selected],
            Animation = _animation.Selected <= 0 ? "" : _animation.GetItemText(_animation.Selected), Size = (float)_propSize.Value, Yaw = (float)_propYaw.Value } };
        UpdatePreview();
    }
    private void RefreshPropEditor()
    {
        if (_propPreview is null) return;
        _propPreview.Visible = _propControls.Visible = Kind == "decoration";
        _preview.Visible = Kind != "decoration";
        if (Kind != "decoration") return;
        _refreshingProp = true;
        try
        {
            _propPreview.Bind(_draft.Decoration, _draft.Prop);
            _clickMode.Select(Array.IndexOf(ClickModes, _draft.Prop.Click));
            _propSize.Value = _draft.Prop.Size; _propYaw.Value = _draft.Prop.Yaw;
            _animation.Clear(); _animation.AddItem("模型默认动作（无动画则轻晃）");
            foreach (var name in _propPreview.Animations) _animation.AddItem(name);
            var selected = Array.IndexOf(_propPreview.Animations, _draft.Prop.Animation) + 1;
            _animation.Select(selected); _animation.Disabled = _draft.Prop.Click != "auto" || _propPreview.Animations.Length == 0;
        }
        catch (Exception e) { if (_status is not null) _status.Text = e.Message; }
        finally { _refreshingProp = false; }
    }
    private void ChooseModel()
    {
        var dialog = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile,
            UseNativeDialog = true, Title = "导入桌饰：GLB（内嵌纹理，可含动画）", Filters = ["*.glb ; GLB 2.0 模型"] };
        AddChild(dialog);
        dialog.FileSelected += async path =>
        {
            try
            {
                if (!path.StartsWith("content://") && new FileInfo(path).Length > CosmeticModel.MaximumBytes)
                    throw new InvalidDataException("桌饰 GLB 不能超过 20 MiB。");
                var bytes = Godot.FileAccess.GetFileAsBytes(path);
                var hash = _library.CacheModel(bytes);
                _status.Text = "正在读取桌饰并生成预览…";
                var capture = new DeskPropPreview { Position = new(-2048, 0), Size = new(512, 512) };
                capture.Initialize(); GetTree().Root.AddChild(capture);
                CosmeticAsset asset;
                try
                {
                    capture.Bind("", new(Model: hash));
                    if (!capture.HasProp) throw new InvalidDataException("模型中没有可显示的网格。");
                    asset = _library.ImportModel(Path.GetFileNameWithoutExtension(path), bytes, await capture.ThumbnailAsync());
                }
                finally { capture.QueueFree(); }
                SelectCategory(1); _search.Text = asset.Name; RefreshControls();
                SelectImage(Array.FindIndex(_visibleAssets, a => a.Image == asset.Image) + 1);
                _status.Text = "桌饰已导入。点击模型或“播放预览”检查动作，再装备并保存。";
            }
            catch (Exception e) { _status.Text = e.Message; }
            finally { dialog.QueueFree(); }
        };
        dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered(new(780, 520));
    }
}
