using Kards.Ui.Contracts;
using Orc.Game.Cards;
using Orc.Game.Targeting;

namespace Kards.Ui.OrcBridge;

public sealed partial class OrcMatchSession
{
    private ChoiceResponder? _targetChoice;
    public event Action<UiTargetRequest, IUiTargetResponder>? TargetRequested;
    public event Action? TargetClosed;
    public bool TargetPending => _targetChoice is not null;
    private void PresentTargetChoice(TargetingRequestDescription description, ITargetingResponder responder)
    {
        if (_disposed || _activeCommandToken.IsCancellationRequested) { responder.Cancel(description.RequestId); return; }
        if (TargetRequested is null) { LastRejectionReason = "SelectionRequired"; responder.Cancel(description.RequestId); return; }
        var request = new UiTargetRequest(description.RequestId, description.Slots.Select(slot =>
        {
            var kind = slot.Kind == TargetSlotKind.OptionSelect ? ChoiceKind.Option : ChoiceKind.Reference;
            var choices = kind == ChoiceKind.Option
                ? slot.Options!.Select(o => new UiChoice(o.Id, o.Text, kind)).ToArray()
                : (slot.AllowedReferences ?? description.AllowedTargets).Where(r => r.IsAlive).Select(r =>
                {
                    var uid = OrcRefs.KeyOf(r.Value);
                    var card = View.SelfLine.Concat(View.EnemyLine).Concat(new[] { View.SelfHq, View.EnemyHq }.OfType<UiCardView>())
                        .FirstOrDefault(c => c.Uid == uid);
                    return new UiChoice(uid, card?.Definition.Name ?? (r.Value as CardBase)?.Name ?? uid, kind, card);
                }).ToArray();
            return new UiChoiceSlot(slot.Name, kind, slot.Min, slot.Max, choices);
        }).ToArray());
        var choice = new ChoiceResponder(this, description, responder);
        _targetChoice = choice;
        if (_activeCommandToken.IsCancellationRequested) { CancelTargetChoice(); return; }
        TargetRequested.Invoke(request, choice);
    }
    private void CancelTargetChoice()
    {
        var choice = _targetChoice;
        if (choice is null) return;
        choice.Cancel(choice.RequestId);
        // Interaction faults can end the engine request before the UI responder is closed.
        if (_targetChoice == choice) { _targetChoice = null; TargetClosed?.Invoke(); }
    }
    private sealed class ChoiceResponder(OrcMatchSession session, TargetingRequestDescription description,
        ITargetingResponder responder) : IUiTargetResponder
    {
        public string RequestId => description.RequestId;
        public bool Complete(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> selectionsBySlot)
        {
            if (requestId != RequestId || session._targetChoice != this) return false;
            var map = new Dictionary<string, IReadOnlyList<TargetSelection>>(StringComparer.Ordinal);
            foreach (var (name, ids) in selectionsBySlot)
            {
                var slot = description.Slots.FirstOrDefault(s => s.Name == name);
                if (slot is null) return false;
                var allowed = slot.AllowedReferences ?? description.AllowedTargets;
                var elements = new List<TargetSelection>();
                foreach (var id in ids)
                {
                    if (slot.Kind == TargetSlotKind.OptionSelect) elements.Add(TargetSelection.FromIdentifier(id));
                    else
                    {
                        var reference = allowed.FirstOrDefault(r => r.IsAlive && OrcRefs.KeyOf(r.Value) == id);
                        if (reference is null) return false;
                        elements.Add(TargetSelection.FromReference(reference));
                    }
                }
                map.Add(name, elements);
            }
            if (!responder.Complete(requestId, map)) return false;
            Close(); return true;
        }
        public bool Cancel(string requestId)
        {
            if (requestId != RequestId || session._targetChoice != this || !responder.Cancel(requestId)) return false;
            Close(); return true;
        }
        private void Close() { session._targetChoice = null; session.TargetClosed?.Invoke(); }
    }
}
