using Godot;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>A native Godot step before creating a deck; selection is persisted with the deck.</summary>
public partial class DeckPackPicker : ConfirmationDialog
{
    private DeckConstructionService _service = null!;
    private VBoxContainer _choices = null!;
    private Label _summary = null!;
    private Action<string[]>? _accept;
    internal Dictionary<string, CheckBox> Choices { get; } = new(StringComparer.Ordinal);
    internal ScrollContainer Scroll { get; private set; } = null!;
    public void Initialize(DeckConstructionService service)
    {
        _service = service; Title = "选择卡包"; MinSize = new(560, 470);
        GetOkButton().Text = "开始构筑"; GetCancelButton().Text = "取消";
        var box = new VBoxContainer(); AddChild(box);
        var hint = UiStyles.Label("勾选要使用的卡包，可多选。所属扩展内容一并纳入，无需另选。", 15);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; hint.CustomMinimumSize = new(510, 0); box.AddChild(hint);
        var scroll = new ScrollContainer { CustomMinimumSize = new(510, 300), SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; box.AddChild(scroll);
        Scroll = scroll;
        _choices = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(_choices);
        _summary = UiStyles.Label("", 14, UiStyles.Gold); box.AddChild(_summary);
        Confirmed += () => { var selected = Choices.Where(c => c.Value.ButtonPressed).Select(c => c.Key).ToArray();
            if (selected.Length > 0) _accept?.Invoke(selected); };
    }
    public void Open(IEnumerable<string> selected, Action<string[]> accept, bool creating = true)
    {
        _accept = accept; var current = selected.Select(DeckCardPacks.Family).ToHashSet(StringComparer.Ordinal);
        GetOkButton().Text = creating ? "开始构筑" : "应用卡包";
        UiStyles.Clear(_choices); Choices.Clear();
        foreach (var pack in _service.Packs)
        {
            var box = new CheckBox { Text = $"{pack.Name}  ·  {pack.CardCount} 张可用卡牌", ButtonPressed = current.Contains(pack.Id),
                CustomMinimumSize = new(0, 38) };
            Choices.Add(pack.Id, box); _choices.AddChild(box); box.Toggled += _ => RefreshSummary();
        }
        RefreshSummary(); PopupCentered();
    }
    private void RefreshSummary()
    {
        var selected = Choices.Where(c => c.Value.ButtonPressed).Select(c => c.Key).ToHashSet();
        _summary.Text = selected.Count == 0 ? "请至少选择一个卡包" : $"已选 {selected.Count} 个卡包 · {_service.Packs.Where(p => selected.Contains(p.Id)).Sum(p => p.CardCount)} 种可用卡牌";
        GetOkButton().Disabled = selected.Count == 0;
    }
}
