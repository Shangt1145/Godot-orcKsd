namespace Kards.Ui.Contracts;

/// <summary>Permitted actions and combat forecasts are supplied by an adapter, never calculated by the view.</summary>
public sealed record UiMoveOption(string Uid, string ToZone, int? SlotIndex = null);
public sealed record UiAttackPreview(string AttackerUid, string DefenderUid,
    int? DamageToDefender, int? DamageToAttacker, bool DefenderDies, bool AttackerDies);
/// <summary>A single-target order and its current candidates, supplied by the effect adapter.</summary>
public sealed record UiOrderTargeting(string Uid, IReadOnlyList<string> TargetUids);
public sealed record UiCombatResolution(string MatchId, UiCardView Attacker, UiCardView Defender,
    int DamageToDefender, int DamageToAttacker, UiCardView? AttackerAfter, UiCardView? DefenderAfter, UiMatchView After);
public sealed record UiBattleActions
{
    public IReadOnlyList<string> PlayableUids { get; init; } = Array.Empty<string>();
    /// <summary>False means PlayableUids is a legacy attemptable set, not a confirmed legality query.</summary>
    public bool HandPlayabilityKnown { get; init; } = true;
    public IReadOnlyList<UiMoveOption> Moves { get; init; } = Array.Empty<UiMoveOption>();
    public IReadOnlyList<UiAttackPreview> AttackPreviews { get; init; } = Array.Empty<UiAttackPreview>();
    public IReadOnlyList<UiOrderTargeting> OrderTargets { get; init; } = Array.Empty<UiOrderTargeting>();
    public IReadOnlyList<UiDeploymentGap> DeploymentGaps { get; init; } = Array.Empty<UiDeploymentGap>();
    public bool CanEndTurn { get; init; }
    public bool AttacksEnabled { get; init; }
    /// <summary>The opening-hand phase is waiting for a keep/replace answer. Never decides which cards are legal to replace.</summary>
    public bool MulliganOpen { get; init; }
}
