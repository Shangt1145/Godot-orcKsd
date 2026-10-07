namespace Kards.Ui.Core;

/// <summary>
/// The opening hand a demonstration match starts the player with: one unit at each deployment-slam
/// size tier, so the whole ladder can be seen in a single game instead of waiting on the draw.
///
/// The slam's amplitude is driven by nothing but the unit's defense (see BATTLE_SLAM_REWORK.md), so
/// the four cards are chosen by defense and not by unit type:
///
///   def 1  tier 0  plain placement — no dust, no shake
///   def 3  tier 1  medium slam
///   def 6  tier 2  heaviest slam and a camera shake
///   def 8  tier 2  printed above seven defense, to show the top of the range
///
/// Costs are 1 / 2 / 3 / 4 so the ladder is payable turn by turn — the first turn grants a single
/// command point, so the def-1 card has to cost exactly one or the opening turn is a dead turn with
/// nothing the player can do but pass.
/// This is a presentation aid, never the default deal: a pinned hand measures nothing about the
/// pool, so the verification and capture paths deliberately leave it off.
/// </summary>
public static class DemoOpeningHand
{
    /// <summary>Card ids in draw order, one per slam tier.</summary>
    public static readonly IReadOnlyList<string> CardIds =
    [
        "USG/units/_2",     // Winter师             def 1  cost 1 — the only one affordable turn one
        "av76/units/-4",    // 卡尔拉第八装甲步兵团    def 3
        "deran/units/_6q",  // 卡维拉尔第二步兵团     def 6
        "deran/units/_10",  // 帝国三号坦克          def 8
    ];

    /// <summary>Expected printed defense per slot, in the same order as <see cref="CardIds"/>.</summary>
    public static readonly IReadOnlyList<int> ExpectedDefense = [1, 3, 6, 8];
}
