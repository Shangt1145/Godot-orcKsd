using Godot;

namespace Kards.Ui;

public partial class BattleScreen
{
    private Vector2 _dragPointer, _dragPrevious;
    private ulong _dragLastTick;
    private float _dragTiltTarget;
    private readonly Dictionary<string, int> _fieldMotionZ = new();

    private void ObserveHandDrag(Vector2 at)
    {
        var now = Time.GetTicksUsec();
        var seconds = Math.Clamp((now - _dragLastTick) / 1_000_000f, .002f, .08f);
        _dragTiltTarget = Math.Clamp((at.X - _dragPrevious.X) / seconds * .000065f, -.09f, .09f);
        _dragPrevious = _dragPointer = at; _dragLastTick = now;
        UpdateHeldPaper(); // Position follows input immediately; only its tilt has inertia.
    }
    private void UpdateHeldPaper()
    {
        if (_pressed is not { } card) return;
        card.Position = _dragPointer - (_grabOffset - card.PivotOffset).Rotated(card.Rotation) - card.PivotOffset;
        card.DeploymentElevation = _clock.ReducedMotion ? 0 : .65f; card.ZIndex = 140; card.QueueRedraw();
    }
    public override void _Process(double delta)
    {
        if (!_dragging || _pressed is not { Mode: BattleCardMode.Hand } card || IsTargetedOrder(card.Uid)) return;
        var settle = 1 - MathF.Exp(-24 * (float)delta);
        card.Rotation = _clock.ReducedMotion ? 0 : Mathf.Lerp(card.Rotation, _dragTiltTarget, settle);
        _dragTiltTarget *= MathF.Exp(-9 * (float)delta);
        UpdateHeldPaper();
    }

    private void MoveFieldPaper(BattleCard card, Vector2 from, float rotation, bool changingLine)
    {
        var to = card.RestPosition; var finalRotation = card.RestRotation;
        card.Position = from; card.Rotation = rotation;
        var z = card.ZIndex;
        if (changingLine) { _fieldMotionZ[card.Uid] = z; card.ZIndex = 40; }
        var motion = CreateTween(); _tweens.Add(motion);
        var lift = changingLine ? .42f : .10f;
        var side = Math.Sign(to.X - from.X);
        motion.TweenMethod(Callable.From<float>(p =>
        {
            var u = p * p * (3 - 2 * p); var arc = MathF.Sin(p * MathF.PI);
            card.Position = from.Lerp(to, u) + new Vector2(0, changingLine ? -arc * 9 : 0);
            card.Rotation = Mathf.Lerp(rotation, finalRotation, u) + side * arc * .018f;
            card.Scale = Vector2.One * (1 + arc * (changingLine ? .028f : 0));
            card.DeploymentElevation = arc * lift; card.QueueRedraw();
        }), 0f, 1f, (changingLine ? .40 : .27) * _clock.Scale);
        motion.Finished += () => { card.ZIndex = z; _fieldMotionZ.Remove(card.Uid); _tweens.Remove(motion); };
    }
}
