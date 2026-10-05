namespace Kards.Ui.Contracts;

/// <summary>Resolved presentation snapshots, supplied by an adapter. No rules or targeting are inferred.</summary>
public abstract record UiPresentationStep;
public sealed record UiDrawPresentation(string Side, int SlotIndex, UiCardView? Card = null) : UiPresentationStep;
public sealed record UiOrderImpact(UiCardView Before, UiCardView? After, int Damage);
public sealed record UiOrderPresentation(UiCardView Card, IReadOnlyList<UiOrderImpact> Impacts) : UiPresentationStep;
public sealed record UiDeploymentPresentation(UiCardView Card, UiMatchView Deployed) : UiPresentationStep;
public sealed record UiRemovalPresentation(UiCardView Card) : UiPresentationStep;
public enum UiCounterStage { Armed, Disarmed, Triggered }
public sealed record UiCounterPresentation(string Side, UiCounterStage Stage, UiCardView? Card = null, UiCardView? BlockedCard = null) : UiPresentationStep;
public enum UiStatusKind { Heal, Buff, Suppressed, Cleared, CostChanged }
public sealed record UiStatusPresentation(UiCardView Before, UiCardView After, UiStatusKind Status) : UiPresentationStep;
public enum UiDiscardKind { Discard, Burn }
public sealed record UiDiscardPresentation(string Side, UiCardView? Card, UiDiscardKind Kind) : UiPresentationStep;
public sealed record UiMulliganPresentation(string Side, int Replaced, IReadOnlyList<UiCardView> Drawn) : UiPresentationStep;
public enum UiTurnKind { TurnStarted, Fatigue }
public sealed record UiTurnPresentation(string Side, int Turn, UiTurnKind Kind = UiTurnKind.TurnStarted, int? Damage = null) : UiPresentationStep;
public sealed record UiPresentationResolution(string MatchId, IReadOnlyList<UiPresentationStep> Steps, UiMatchView After);
