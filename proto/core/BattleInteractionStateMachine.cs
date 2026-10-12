using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>Owns gesture conflicts only. An animation does not reserve an untouched card:
/// draws, damage targets, stat changes and line reflow remain interactive.</summary>
public sealed class BattleInteractionStateMachine
{
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    public UiInteractionPhase Phase { get; set; } = UiInteractionPhase.Ready;
    public bool IsPending(string uid) => _pending.Contains(uid);
    public bool Reserve(UiCommand command) => Actor(command) is not { } uid || _pending.Add(uid);
    public void Refuse(UiCommand command) { if (Actor(command) is { } uid) _pending.Remove(uid); }
    public void Reset() { _pending.Clear(); Phase = UiInteractionPhase.Ready; }
    public UiActionInteractionState Read() => new(Phase, Array.AsReadOnly(_pending.ToArray()));

    public void Presented(UiPresentationResolution resolution)
    {
        foreach (var uid in OperatedActors(resolution)) _pending.Remove(uid);
    }

    public static string? Actor(UiCommand command) => command switch
    {
        PlayCard play => play.Uid, MoveUnit move => move.Uid,
        AttackUnit attack => attack.AttackerUid, CommandUnit commandUnit => commandUnit.Uid, _ => null
    };

    private static IEnumerable<string> OperatedActors(UiPresentationResolution resolution)
    {
        foreach (var step in resolution.Steps)
        {
            switch (step)
            {
                case UiDeploymentPresentation deployment: yield return deployment.Card.Uid; break;
                case UiOrderPresentation order: yield return order.Card.Uid; break;
                case UiCounterPresentation { Card: { } card, Stage: UiCounterStage.Armed or UiCounterStage.Disarmed }:
                    yield return card.Uid; break;
                case UiCounterPresentation { BlockedCard: { } blocked }: yield return blocked.Uid; break;
                case UiBoardImpactsPresentation hits:
                    foreach (var uid in hits.Impacts.Select(i => i.Source?.Uid).OfType<string>()) yield return uid;
                    break;
            }
        }
        if (resolution.Before is not { } before) yield break;
        foreach (var unit in before.SelfLine)
            if (resolution.After.SelfLine.FirstOrDefault(c => c.Uid == unit.Uid) is { } after && after.Zone != unit.Zone)
                yield return unit.Uid;
    }
}
