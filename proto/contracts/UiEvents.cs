namespace Kards.Ui.Contracts;

/// <summary>Value snapshot captured by the adapter at emission, never a live engine object.</summary>
public sealed record UiEvent(string Kind, string? Uid = null, string? TargetUid = null,
    int? Value = null, string Text = "", UiCardView? Card = null);
public sealed record UiEventTree(IReadOnlyList<UiEvent> Entries, IReadOnlyList<UiEventTree> Children);
public sealed record UiEventSegment(string MatchId, long Sequence, DateTimeOffset Timestamp,
    IReadOnlyList<UiEvent> Entries, IReadOnlyList<UiEventTree> Children);

public interface IUiEventSource
{
    IReadOnlyList<UiEventSegment> TakeSegments();
    IDisposable OnImmediateUpdate(Action<UiEvent> callback);
}
