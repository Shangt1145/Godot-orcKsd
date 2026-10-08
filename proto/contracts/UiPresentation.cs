namespace Kards.Ui.Contracts;

/// <summary>Resolved presentation snapshots, supplied by an adapter. No rules or targeting are inferred.</summary>
public abstract record UiPresentationStep;
public sealed record UiDrawPresentation(string Side, int SlotIndex, UiCardView? Card = null) : UiPresentationStep
{
    public bool Consecutive { get; init; }
}
/// <summary>
/// One hit on the board. <paramref name="Source"/> is the attacker the engine reported
/// (unit.damage.dealt), so the UI can play the weapon's own trajectory; null means the engine
/// reported no attacker and the hit lands without a shot.
/// </summary>
public sealed record UiOrderImpact(UiCardView Before, UiCardView? After, int Damage, UiCardView? Source = null);
public sealed record UiBoardImpactsPresentation(IReadOnlyList<UiOrderImpact> Impacts) : UiPresentationStep;
public sealed record UiOrderPresentation(UiCardView Card, IReadOnlyList<UiOrderImpact> Impacts) : UiPresentationStep;
public sealed record UiDeploymentPresentation(UiCardView Card, UiMatchView Deployed) : UiPresentationStep;
public sealed record UiRemovalPresentation(UiCardView Card) : UiPresentationStep;
public enum UiCounterStage { Armed, Disarmed, Triggered }
public sealed record UiCounterPresentation(string Side, UiCounterStage Stage, UiCardView? Card = null, UiCardView? BlockedCard = null) : UiPresentationStep;
public enum UiStatusKind { Heal, Buff, Damaged, Suppressed, Cleared, CostChanged }
public sealed record UiStatusPresentation(UiCardView Before, UiCardView After, UiStatusKind Status) : UiPresentationStep;

/// <summary>Where a command-point change came from, so the bar can animate for the right reason.</summary>
public enum UiResourceCause { Turn, Spend, Gain }
public sealed record UiResourcePresentation(string Side, UiResourceCause Cause, int OldValue, int NewValue, int? OldSlots = null, int? NewSlots = null) : UiPresentationStep;
public enum UiDiscardKind { Discard, Burn }
public sealed record UiDiscardPresentation(string Side, UiCardView? Card, UiDiscardKind Kind) : UiPresentationStep;
public sealed record UiMulliganPresentation(string Side, int Replaced, IReadOnlyList<UiCardView> Drawn) : UiPresentationStep;
public enum UiTurnKind { TurnStarted, Fatigue }
public sealed record UiTurnPresentation(string Side, int Turn, UiTurnKind Kind = UiTurnKind.TurnStarted, int? Damage = null) : UiPresentationStep;
public enum UiPresentationPriority { Indirect, Direct }
public sealed record UiPresentationResolution(string MatchId, IReadOnlyList<UiPresentationStep> Steps, UiMatchView After)
{
    public long Sequence { get; init; }
    public UiMatchView? Before { get; init; }
    public UiPresentationPriority Priority { get; init; }
}
