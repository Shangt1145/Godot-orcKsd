using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>Workshop card setting: one complete imported bitmap and manually positioned numbers.</summary>
public partial class CardSkinEditor : HBoxContainer
{
    private CardSkin _skin = new();
    private byte[]? _pending;
    private Action<CardSkin, byte[]?> _save = null!;
    private Action _reset = null!;
    private OptionButton _slot = null!;
    private readonly List<SpinBox> _region = [];
    private SkinPositioner _preview = null!;
    private Label _status = null!;
    private bool _loading, _validRegion = true;
    internal Button SaveButton = null!;
    internal CardSkin Draft => _skin;
    internal string Status => _status.Text;

    public void Initialize(UiCardDefinition card, CardSkin? skin, TextureCache textures, Texture2D? completeCard,
        Action<CardSkin, byte[]?> save, Action reset)
    {
        _skin = skin ?? new(); _save = save; _reset = reset;
        AddThemeConstantOverride("separation", 24);
        var form = new VBoxContainer { CustomMinimumSize = new(410, 0) }; form.AddThemeConstantOverride("separation", 14); AddChild(form);
        form.AddChild(MenuStyles.Label("异画 · " + card.Name, 24, MenuStyles.Gold));
        var hint = MenuStyles.Label("导入包含卡框、卡名和规则的完整卡面，任意格式、尺寸、比例与体积。\n原图原样使用；这里只定位实时数值，不重新拼装卡面。", 14, MenuStyles.Dim); form.AddChild(hint);
        form.AddChild(MenuStyles.Button("导入完整卡面…", ChooseImage));
        form.AddChild(MenuStyles.Label("选择数值；拖动右侧彩框可移动位置。", 14));
        _slot = new OptionButton();
        foreach (var caption in card.CardType == "unit" ? new[] { "部署费用", "攻击力", "防御 / 剩余生命", "行动费用" } : new[] { "部署费用" }) _slot.AddItem(caption);
        form.AddChild(_slot); _slot.ItemSelected += _ => RefreshRegion();
        var grid = new GridContainer { Columns = 2 }; grid.AddThemeConstantOverride("h_separation", 12); form.AddChild(grid);
        foreach (var label in new[] { "X %", "Y %", "宽度 %", "高度 %" })
        {
            grid.AddChild(MenuStyles.Label(label, 14));
            var number = new SpinBox { MinValue = 0, MaxValue = 100, Step = .1, CustomMinimumSize = new(180, 36) };
            grid.AddChild(number); _region.Add(number); number.ValueChanged += _ => ChangeRegion();
        }
        var colors = new HBoxContainer(); form.AddChild(colors);
        colors.AddChild(MenuStyles.Label("数字颜色", 14));
        var ink = new ColorPickerButton { Color = new(_skin.Ink), EditAlpha = false, CustomMinimumSize = new(75, 32) }; colors.AddChild(ink);
        colors.AddChild(MenuStyles.Label("覆盖底色", 14));
        var paper = new ColorPickerButton { Color = new(_skin.Paper), EditAlpha = false, CustomMinimumSize = new(75, 32) }; colors.AddChild(paper);
        ink.ColorChanged += c => { _skin = _skin with { Ink = c.ToHtml(false) }; RefreshPreview(); };
        paper.ColorChanged += c => { _skin = _skin with { Paper = c.ToHtml(false) }; RefreshPreview(); };
        form.AddChild(MenuStyles.Label("覆盖底色用于遮住完整卡面上印好的原数值。", 13, MenuStyles.Dim));
        SaveButton = MenuStyles.Button("保存异画设置", Save); MenuStyles.Primary(SaveButton); form.AddChild(SaveButton);
        form.AddChild(MenuStyles.Button("移除异画，恢复默认卡面", () =>
        {
            try { _reset(); _pending = null; _skin = new(); _preview.ArtOverride = null; RefreshRegion(); _status.Text = "已移除异画。"; }
            catch (Exception e) { _status.Text = e.Message; }
        }));
        _status = MenuStyles.Label("保存后作为这张工坊卡牌的展示设置。", 13, MenuStyles.Ink); _status.ClipText = true; form.AddChild(_status);
        var center = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; AddChild(center);
        _preview = new SkinPositioner { CustomMinimumSize = new(350, 491), Size = new(350, 491), Definition = card, Skin = _skin, Textures = textures, ArtOverride = completeCard };
        center.AddChild(_preview);
        _preview.Moved += (index, box) => { _slot.Select(index); SetRegion(index, box); RefreshRegion(); };
        RefreshRegion();
    }
    private SkinRect Region() => SkinPositioner.Region(_skin, _slot.Selected);
    private void SetRegion(int index, SkinRect box)
    {
        _skin = index switch { 1 => _skin with { AttackBox = box }, 2 => _skin with { DefenseBox = box }, 3 => _skin with { OpBox = box }, _ => _skin with { CostBox = box } };
    }
    private void RefreshRegion()
    {
        _loading = true; var b = Region(); var values = new[] { b.X, b.Y, b.Width, b.Height };
        for (var i = 0; i < 4; i++) _region[i].Value = Math.Round(values[i] * 100, 2);
        _loading = false; _validRegion = true; RefreshPreview();
    }
    private void ChangeRegion()
    {
        if (_loading) return;
        var b = new SkinRect((float)_region[0].Value / 100, (float)_region[1].Value / 100, (float)_region[2].Value / 100, (float)_region[3].Value / 100);
        try { b.Check(); _validRegion = true; SetRegion(_slot.Selected, b); RefreshPreview(); _status.Text = "位置已修改，点击保存。"; }
        catch (Exception e) { _validRegion = false; _status.Text = e.Message; }
    }
    private void RefreshPreview() { if (_preview is null) return; _preview.Skin = _skin; _preview.Selected = _slot.Selected; _preview.QueueRedraw(); }
    internal void ImportImage(string path)
    {
        // 原图字节直接进包：不缩放、不重编码、不限制形状与体积；只有数值位置仍按百分比对齐。
        var imported = AppearanceRuntime.ReadCardSkinImage(path);
        _pending = imported.Bytes; _skin = _skin with { Art = "pending" + imported.Extension };
        _preview.ArtOverride = ImageTexture.CreateFromImage(imported.Preview);
        RefreshPreview(); _status.Text = "完整卡面已导入（原图原样使用）；拖动彩框或输入坐标设置数值位置。";
    }
    private void ChooseImage()
    {
        var dialog = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile,
            UseNativeDialog = true, Title = "导入完整卡面（任意尺寸与格式）" }; AddChild(dialog);
        dialog.FileSelected += path => { try { ImportImage(path); } catch (Exception e) { _status.Text = e.Message; } finally { dialog.QueueFree(); } };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered(new(780, 520));
    }
    private void Save()
    {
        try { if (!_validRegion) throw new InvalidDataException("请先修正当前数值区域。"); _skin.Checked(); _save(_skin, _pending); _status.Text = "异画设置已应用；按工坊的保存流程生效。"; }
        catch (Exception e) { _status.Text = e.Message; }
    }
    internal void SetVerificationPosition(int index, SkinRect box)
    { _slot.Select(index); RefreshRegion(); var values = new[] { box.X, box.Y, box.Width, box.Height }; for (var i = 0; i < 4; i++) _region[i].Value = values[i] * 100; }
}

public partial class SkinPositioner : Control
{
    public CardSkin Skin { get; set; } = new();
    public UiCardDefinition Definition { get; set; } = null!;
    public TextureCache Textures { get; set; } = null!;
    public Texture2D? ArtOverride { get; set; }
    public int Selected { get; set; }
    private bool _drag;
    private Vector2 _start;
    private SkinRect _initial = new(0, 0, 1, 1);
    public event Action<int, SkinRect>? Moved;
    private int Count => Definition.CardType == "unit" ? 4 : 1;
    public static SkinRect Region(CardSkin skin, int i) => i switch { 1 => skin.AttackBox, 2 => skin.DefenseBox, 3 => skin.OpBox, _ => skin.CostBox };
    private Rect2 Box(SkinRect b) => new(b.X * Size.X, b.Y * Size.Y, b.Width * Size.X, b.Height * Size.Y);
    public override void _Draw()
    {
        if (ArtOverride is not null) AlternateCardFace.Paint(this, Skin, Definition, null, Textures, artOverride: ArtOverride);
        else DrawRect(new(Vector2.Zero, Size), new Color("28332D"));
        for (var i = 0; i < Count; i++) DrawRect(Box(Region(Skin, i)), new Color(i == Selected ? "FAD675" : "70D7D7"), false, i == Selected ? 3 : 1);
    }
    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (!button.Pressed) { _drag = false; return; }
            for (var i = 0; i < Count; i++)
                if (Box(Region(Skin, i)).HasPoint(button.Position)) { Selected = i; _drag = true; _start = button.Position; _initial = Region(Skin, i); Moved?.Invoke(i, _initial); AcceptEvent(); return; }
        }
        if (_drag && input is InputEventMouseMotion motion)
        {
            var delta = (motion.Position - _start) / Size;
            Moved?.Invoke(Selected, _initial with { X = Math.Clamp(_initial.X + delta.X, 0, 1 - _initial.Width), Y = Math.Clamp(_initial.Y + delta.Y, 0, 1 - _initial.Height) });
            AcceptEvent();
        }
    }
}
