using Godot;

namespace Kards.Ui;

/// <summary>Each item is a separate, visible block. No popup or collapsed selector.</summary>
public partial class BlockShelf : VBoxContainer
{
    private readonly List<(string Text, BlockShape Block, Variant Metadata)> _items = [];
    private bool _disabled;
    public int Selected { get; private set; }
    public int ItemCount => _items.Count;
    public event Action<long>? ItemSelected;
    public bool Disabled
    {
        get => _disabled;
        set { _disabled = value; Modulate = new Color(1, 1, 1, value ? .45f : 1); }
    }
    public BlockShelf() { AddThemeConstantOverride("separation", 6); SizeFlagsHorizontal = SizeFlags.ExpandFill; }
    public void AddItem(string text)
    {
        var index = _items.Count;
        var block = new BlockShape { Shape = BlockShape.Form.Reporter, Fill = BlockTheme.Query,
            ShowNotch = false, ShowTab = false, CustomMinimumSize = new(180, 36), SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseDefaultCursorShape = CursorShape.PointingHand };
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 16); margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 8); margin.AddThemeConstantOverride("margin_bottom", 8);
        var label = MenuStyles.Label(text, 13, MenuStyles.Ink); label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.MouseFilter = MouseFilterEnum.Ignore; margin.AddChild(label); block.SetBody(margin);
        block.SetMeta("shelf_index", index);
        block.GuiInput += input =>
        {
            if (!_disabled && input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            { Select(index); ItemSelected?.Invoke(index); }
        };
        _items.Add((text, block, default)); AddChild(block); Select(Selected);
    }
    public void Select(int index)
    {
        Selected = Math.Clamp(index, 0, Math.Max(0, ItemCount - 1));
        for (var i = 0; i < ItemCount; i++) _items[i].Block.Fill = i == Selected ? BlockTheme.Query.Lightened(.22f) : BlockTheme.Query;
    }
    public string GetItemText(int index) => _items[index].Text;
    public Variant GetItemMetadata(int index) => _items[index].Metadata;
    public void SetItemMetadata(int index, Variant metadata)
    {
        var item = _items[index]; _items[index] = (item.Text, item.Block, metadata);
        item.Block.SetMeta("shelf_value", metadata);
    }
}
