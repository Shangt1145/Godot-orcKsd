namespace Kards.Ui.Contracts;

public enum ChoiceKind
{
    Reference, Option, CardDefinition
}
public sealed record UiChoice(string Id, string Label, ChoiceKind Kind, UiCardView? Card = null);
public sealed record UiChoiceSlot(string Name, ChoiceKind Kind, int Min, int Max, IReadOnlyList<UiChoice> Allowed)
{
    /// <summary>Battlefield references are answered only by dragging an arrow, never by a card picker.</summary>
    public bool BoardTargets { get; init; }
}
public sealed record UiTargetRequest(string RequestId, IReadOnlyList<UiChoiceSlot> Slots)
{
    public UiCardView? SourceCard { get; init; }
    public string SelectorName { get; init; } = "";
    public bool IsDrag { get; init; }

    /// <summary>
    /// 呈现给玩家的提示文案（引擎 <c>choice.prompt</c>；没有＝空串）。
    /// <para>
    /// 只有「选项请求」（<c>choice</c> 节点）会带它——引用类选靶没有这句话。
    /// 引擎 R38 起 <c>pending.prompt</c> 只在有文案时出现，<c>ask</c> 的输出逐条不变，
    /// 所以这里缺省空串、界面按空串当「没有话要说」。
    /// </para>
    /// </summary>
    public string Prompt { get; init; } = "";
}

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
