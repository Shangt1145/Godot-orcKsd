using System.Collections.ObjectModel;
using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>Call at emission/projection time. Freezing after an engine action is too late.</summary>
public static class UiSnapshots
{
    private static IReadOnlyList<T> List<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    private static IReadOnlyDictionary<string, int> Values(IReadOnlyDictionary<string, int> values) => new ReadOnlyDictionary<string, int>(values.ToDictionary(p => p.Key, p => p.Value));
    public static UiCardDefinition Freeze(UiCardDefinition d) => d with { Keywords = List(d.Keywords), KeywordValues = Values(d.KeywordValues) };
    public static UiCardView Freeze(UiCardView c) => c with { Definition = Freeze(c.Definition), Keywords = List(c.Keywords), KeywordValues = Values(c.KeywordValues), BlockedReasons = List(c.BlockedReasons) };
    public static UiBattleActions Freeze(UiBattleActions actions) => actions with
    {
        PlayableUids = List(actions.PlayableUids), Moves = List(actions.Moves), AttackPreviews = List(actions.AttackPreviews)
    };
    public static UiCombatResolution Freeze(UiCombatResolution r) => r with
    {
        Attacker = Freeze(r.Attacker), Defender = Freeze(r.Defender),
        AttackerAfter = r.AttackerAfter is null ? null : Freeze(r.AttackerAfter),
        DefenderAfter = r.DefenderAfter is null ? null : Freeze(r.DefenderAfter), After = Freeze(r.After)
    };
    public static UiEvent Freeze(UiEvent e) => e with { Card = e.Card is null ? null : Freeze(e.Card) };
    public static UiPresentationResolution Freeze(UiPresentationResolution r) => r with
    {
        After = Freeze(r.After), Steps = List(r.Steps.Select(step => step switch
        {
            UiDrawPresentation draw => (UiPresentationStep)(draw with { Card = draw.Side == "self" && draw.Card is not null ? Freeze(draw.Card) : null }),
            UiOrderPresentation order => order with { Card = Freeze(order.Card), Impacts = List(order.Impacts.Select(i => i with
                { Before = Freeze(i.Before), After = i.After is null ? null : Freeze(i.After) })) },
            UiCounterPresentation counter => counter with
            {
                Card = counter.Side == "enemy" && counter.Stage != UiCounterStage.Triggered ? null : counter.Card is null ? null : Freeze(counter.Card),
                BlockedCard = counter.Stage == UiCounterStage.Triggered && counter.BlockedCard is not null ? Freeze(counter.BlockedCard) : null
            },
            UiStatusPresentation status => status with { Before = Freeze(status.Before), After = Freeze(status.After) },
            UiDeploymentPresentation deployment => deployment with { Card = Freeze(deployment.Card), Deployed = Freeze(deployment.Deployed) },
            UiRemovalPresentation removal => removal with { Card = Freeze(removal.Card) },
            UiDiscardPresentation discard => discard with
            {
                Card = discard.Card is { } lost && lost.Visibility == Visibility.Full && discard.Side == "self" ? Freeze(lost) : null
            },
            UiMulliganPresentation mulligan => mulligan with
            {
                // Opponent replacements are anonymous; only replacement counts cross the boundary.
                Drawn = mulligan.Side == "self"
                    ? List(mulligan.Drawn.Where(c => c.Visibility == Visibility.Full).Select(Freeze))
                    : Array.Empty<UiCardView>()
            },
            UiTurnPresentation turn => turn,
            // Pure numbers: the cause and the old/new pair are engine facts, and no card view crosses.
            UiResourcePresentation resource => resource,
            _ => throw new ArgumentException("Unknown presentation step.")
        }))
    };
    public static UiEventTree Freeze(UiEventTree tree) => new(List(tree.Entries.Select(Freeze)), List(tree.Children.Select(Freeze)));
    public static UiEventSegment Freeze(UiEventSegment segment) => segment with { Entries = List(segment.Entries.Select(Freeze)), Children = List(segment.Children.Select(Freeze)) };
    public static UiMatchView Freeze(UiMatchView match) => match with
    {
        SelfHand = List(match.SelfHand.Select(Freeze)),
        SelfLine = List(match.SelfLine.Select(Freeze)),
        EnemyLine = List(match.EnemyLine.Select(Freeze)),
        SelfHq = match.SelfHq is null ? null : Freeze(match.SelfHq),
        EnemyHq = match.EnemyHq is null ? null : Freeze(match.EnemyHq),
        SelectedTarget = match.SelectedTarget is null ? null : Freeze(match.SelectedTarget),
        PendingChoices = List(match.PendingChoices.Select(Freeze))
    };
}
