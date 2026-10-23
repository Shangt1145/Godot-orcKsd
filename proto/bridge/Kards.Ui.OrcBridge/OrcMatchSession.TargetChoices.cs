using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Orc.Game.Cards;
using Orc.Game.Targeting;

namespace Kards.Ui.OrcBridge;

public sealed partial class OrcMatchSession
{
    private ChoiceResponder? _targetChoice;
    private string? _choiceSourceUid;
    private long _choiceDelivery;
    public event Action<UiTargetRequest, IUiTargetResponder>? TargetRequested;
    public event Action? TargetClosed;
    public bool TargetPending => _targetChoice is not null;
    /// <summary>Selector waiting on an answer, for diagnosing an opponent turn that never passes.</summary>
    internal string? PendingSelectorName => _targetChoice?.SelectorName;
    private void PresentTargetChoice(ISelectorInstance selector)
    {
        if (_disposed || _activeCommandToken.IsCancellationRequested) { selector.Submit(new CancelEvent()); return; }
        if (TargetRequested is null) { LastRejectionReason = "SelectionRequired"; selector.Submit(new CancelEvent()); return; }
        var data = selector.Presentation;
        var tag = data.Parameter as OrderSelectorTag;
        var name = tag?.SlotName ?? "default";
        var kind = data.Identifiers is null ? ChoiceKind.Reference
            : selector.SelectorName == SelectorNames.CardPicker ? ChoiceKind.CardDefinition : ChoiceKind.Option;
        var visible = View.SelfLine.Concat(View.EnemyLine)
            .Concat(new[] { View.SelfHq, View.EnemyHq }.OfType<UiCardView>()).Concat(View.SelfHand)
            .DistinctBy(c=>c.Uid).ToArray();
        var boardTargets=kind==ChoiceKind.Reference&&(selector.SelectorName==SelectorNames.FieldUnit
            ||(selector.SelectorName is not (SelectorNames.Hand or SelectorNames.CardPickerReference)
                &&data.Candidates.Any(r=>r.IsAlive&&visible.Any(c=>c.Uid==OrcRefs.KeyOf(r.Value)
                    &&(c.IsHq||c.Zone is "support" or "frontline")))));
        var choices = data.Identifiers is { } identifiers
            ? identifiers.Select(id =>
            {
                var card = kind == ChoiceKind.CardDefinition ? _host.ReadChoiceDefinition(id) : null;
                return new UiChoice(id, tag?.Labels?.GetValueOrDefault(id) ?? card?.Definition.Name ?? id, kind, card);
            }).ToArray()
            : data.Candidates.Where(r => r.IsAlive).Select(r =>
            {
                var uid = OrcRefs.KeyOf(r.Value);
                var card = visible.FirstOrDefault(c => c.Uid == uid);
                return new UiChoice(uid, card?.Definition.Name ?? uid, kind, card);
            }).ToArray();
        // Engine retries reuse the instance Id. Each UI delivery gets a fresh token so stale answers cannot win.
        var request = new UiTargetRequest($"{MatchId}:{selector.Id}:{Interlocked.Increment(ref _choiceDelivery)}",
            [new(name, kind, data.Min, data.Max, choices){BoardTargets=boardTargets}])
        {
            SourceCard = View.SelfHand.FirstOrDefault(c=>c.Uid==_choiceSourceUid) ?? visible.FirstOrDefault(c => c.Uid == _choiceSourceUid),
            SelectorName = selector.SelectorName,
            IsDrag = selector.Mode == SelectorInteractionMode.Drag
        };
        var choice = new ChoiceResponder(this, selector, request.RequestId, name);
        _targetChoice = choice;
        if (_activeCommandToken.IsCancellationRequested) { CancelTargetChoice(); return; }
        try { TargetRequested.Invoke(UiSnapshots.Freeze(request), choice); }
        catch { LastRejectionReason = "InteractionFault"; CancelTargetChoice(); }
    }
    private void CancelTargetChoice()
    {
        var choice = _targetChoice;
        if (choice is null) return;
        choice.Cancel(choice.RequestId);
        if (_targetChoice == choice) { _targetChoice = null; TargetClosed?.Invoke(); }
    }
    private sealed class ChoiceResponder(OrcMatchSession session, ISelectorInstance selector,
        string requestId, string slotName) : IUiTargetResponder
    {
        public string RequestId => requestId;
        public string SelectorName => selector.SelectorName;
        public bool Complete(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> selectionsBySlot)
        {
            if (requestId != RequestId || session._targetChoice != this || selectionsBySlot.Count != 1
                || !selectionsBySlot.TryGetValue(slotName, out var ids)) return false;
            SelectorEvent input;
            if (selector.Presentation.Identifiers is not null)
                input = new PickEvent(identifiers: ids.ToArray());
            else
            {
                var references = ids.Select(id => selector.Presentation.Candidates
                    .FirstOrDefault(r => r.IsAlive && OrcRefs.KeyOf(r.Value) == id)).ToArray();
                if (references.Any(r => r is null)) return false;
                input = selector.Mode == SelectorInteractionMode.Drag && references.Length <= 1
                    ? new DropEvent(references.FirstOrDefault()) : new PickEvent(references!);
            }
            if (!selector.Submit(input)) return false;
            Close(); return true;
        }
        public bool Cancel(string requestId)
        {
            if (requestId != RequestId || session._targetChoice != this || !selector.Submit(new CancelEvent())) return false;
            Close(); return true;
        }
        private void Close()
        {
            if (session._targetChoice != this) return;
            session._targetChoice = null; session.TargetClosed?.Invoke();
        }
    }
}
