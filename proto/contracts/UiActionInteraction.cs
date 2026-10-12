namespace Kards.Ui.Contracts;

/// <summary>Frontend gesture state, never a source of game rules.</summary>
public enum UiInteractionPhase { Ready, Dragging, Choosing, TurnTransition, Suspended }
public sealed record UiActionInteractionState(UiInteractionPhase Phase, IReadOnlyList<string> PendingActorUids)
{
    public bool AllowsActions => Phase is UiInteractionPhase.Ready or UiInteractionPhase.Dragging;
}

/// <summary>An engine-owned insertion boundary. GapId expires when the formation changes.</summary>
public sealed record UiDeploymentGap(string GapId, int Ordinal, string? LeftUid, string? RightUid);
