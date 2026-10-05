namespace Kards.Ui.Contracts;

public enum ChoiceKind
{
    Reference, Option, CardDefinition
}
public sealed record UiChoice(string Id, string Label, ChoiceKind Kind, UiCardView? Card = null);
public sealed record UiChoiceSlot(string Name, ChoiceKind Kind, int Min, int Max, IReadOnlyList<UiChoice> Allowed);
public sealed record UiTargetRequest(string RequestId, IReadOnlyList<UiChoiceSlot> Slots);

/// <summary>Adapter returns true for any terminal result, including engine-side failure.</summary>
public interface IUiTargetResponder
{
    bool Complete(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> selectionsBySlot);
    bool Cancel(string requestId);
}

/// <summary>Only the engine adapter implements this interface with Ref&lt;Entity&gt; mapping.</summary>
public interface IUiInteractionBridge
{
    Task<IReadOnlyList<string>> CollectCandidateUidsAsync(string requestId);
    void BeginInteraction(UiTargetRequest request, IUiTargetResponder responder);
}
