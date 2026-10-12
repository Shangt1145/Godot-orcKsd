using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>The product submits exactly Max picks; native membership/legality remains engine-owned.</summary>
public sealed class ChoiceSelection(UiChoiceSlot slot)
{
    private readonly List<string> _selected = [];
    public IReadOnlyList<string> Selected => _selected.ToArray();
    public bool RequiresConfirmation => slot.Min != 1 || slot.Max != 1;
    public bool CanConfirm => slot.Max > 0 && _selected.Count >= slot.Min && _selected.Count == slot.Max;
    public bool Contains(string id) => _selected.Contains(id);
    public bool Toggle(string id)
    {
        if (slot.Allowed.All(c => c.Id != id)) return false;
        if (_selected.Remove(id)) return true;
        if (_selected.Count >= slot.Max) return false;
        _selected.Add(id); return true;
    }
}
