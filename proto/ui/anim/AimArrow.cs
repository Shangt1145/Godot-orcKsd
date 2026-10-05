using Godot;

namespace Kards.Ui;

public partial class AimArrow : Node2D
{
    private Vector2 _target, _current;
    private bool _aim;
    public Vector2 Start
    {
        get; set;
    }
    public int Bend { get; set; } = 1;
    public bool ReducedMotion
    {
        get; set;
    }
    public void SetTarget(Vector2 target)
    {
        _target = target;
        SetProcess(true);
    }
    public void Aim(bool on)
    {
        _aim = on;
        QueueRedraw();
    }
    public override void _Ready()
    {
        _current = Start;
        _target = Start;
    }
    public override void _Process(double delta)
    {
        var factor = ReducedMotion ? 1 : 1 - Mathf.Pow(.54f, (float)delta * 60);
        _current = _current.Lerp(_target, factor);
        QueueRedraw();
        if (_current.DistanceTo(_target) < .15f)
        {
            _current = _target;
            SetProcess(false);
        }
    }
    public override void _Draw()
    {
        var d = _current - Start;
        var length = Math.Max(1, d.Length());
        var normal = new Vector2(-d.Y, d.X) / length;
        var control = (Start + _current) / 2 + normal * Math.Min(32, length * .12f) * Bend;
        var points = new Vector2[33];
        for (var i = 0; i < points.Length; i++)
        {
            var t = i / 32f;
            points[i] = (1 - t) * (1 - t) * Start + 2 * (1 - t) * t * control + t * t * _current;
        }
        var color = _aim ? new Color("e2bd79") : UiStyles.Gold;
        DrawPolyline(points, color, _aim ? 4 : 2, true);
        DrawCircle(Start, 4, color);
        var tangent = (_current - control).Normalized();
        var n = new Vector2(-tangent.Y, tangent.X);
        DrawColoredPolygon([_current, _current - tangent * 12 + n * 6, _current - tangent * 12 - n * 6], color);
    }
}
