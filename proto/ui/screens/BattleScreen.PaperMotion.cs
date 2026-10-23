using Godot;

namespace Kards.Ui;

public partial class BattleScreen
{
    // One owner per hand card: a new target starts at its current displayed pose.
    private void MoveHandPaper(BattleCard card, bool raised, double seconds = .18, float horizontal = 0)
    {
        StopHandMotion(card.Uid);
        card.HoldHoverHitArea = raised;
        var destination = card.RestPosition + (raised ? new Vector2(0, -90) : new Vector2(horizontal, 0));
        var rotation = raised ? 0 : card.RestRotation;
        card.ZIndex = raised ? 100 : 20 + (card.View?.SlotIndex ?? 0);
        if (_clock.ReducedMotion)
        {
            card.Position = destination; card.Rotation = rotation; card.DeploymentElevation = 0; card.QueueRedraw(); return;
        }
        var motion = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        motion.TweenProperty(card, "position", destination, seconds * _clock.Scale);
        motion.TweenProperty(card, "rotation", rotation, seconds * _clock.Scale);
        motion.TweenMethod(Callable.From<float>(e => { card.DeploymentElevation = e; card.QueueRedraw(); }), card.DeploymentElevation, raised ? .45f : 0, seconds * _clock.Scale);
        _handMotion[card.Uid] = motion; _tweens.Add(motion);
        motion.Finished += () => { if (_handMotion.GetValueOrDefault(card.Uid) == motion) _handMotion.Remove(card.Uid); _tweens.Remove(motion); };
    }
    private void FanAroundHover(BattleCard? focus)
    {
        foreach (var card in _cards.Values.Where(c => c.Mode == BattleCardMode.Hand && c != focus && c != _pressed
            && !HasPendingHandPlay(c.Uid) && !_sequence.IsDrawing(c.Uid)))
            MoveHandPaper(card, false, .18, focus is null ? 0 : Math.Sign(card.RestPosition.X - focus.RestPosition.X) * 12);
    }
}
