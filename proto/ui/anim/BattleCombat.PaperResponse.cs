using Godot;

namespace Kards.Ui;

public partial class BattleCombat
{
    private readonly Dictionary<BattleCard, Tween> _paperResponses = new();

    private void StopPaperResponse(BattleCard card)
    {
        if (_paperResponses.Remove(card, out var old) && GodotObject.IsInstanceValid(old)) old.Kill();
    }

    private void WeaponPaperResponse(BattleCard actor, Vector2 direction, BattleWeaponProfile weapon)
    {
        if (_clock.ReducedMotion || Flies(actor.View?.Definition.UnitType) || !actor.Visible) return;
        StopPaperResponse(actor);
        if (!_actors.Contains(actor)) _actors.Add(actor);
        var origin = actor.Position; var rotation = actor.Rotation;
        var weight = weapon.Trace is "shell" or "cannon" or "rocket" ? 7f : 2.7f;
        var tween = CreateTween(); _paperResponses[actor] = tween; _tweens.Add(tween);
        tween.TweenMethod(Callable.From<float>(p =>
        {
            var recoil = p < .23f ? MathF.Sin(p / .23f * MathF.PI / 2) : MathF.Pow((1 - p) / .77f, 2);
            actor.Position = origin - direction * weight * recoil;
            actor.Rotation = rotation + direction.X * .018f * recoil;
            actor.DeploymentElevation = recoil * .16f; actor.QueueRedraw();
        }), 0f, 1f, Time(.24));
    }

    private void ImpactPaperResponse(BattleCard actor, int? damage, Vector2? source)
    {
        if (_clock.ReducedMotion || !actor.Visible) return;
        StopPaperResponse(actor);
        var origin = _flightPositions.GetValueOrDefault(actor, actor.RestPosition);
        var rotation = actor.RestRotation;
        var direction = source is { } from ? (CenterOf(actor) - from).Normalized() : new Vector2(0, -1);
        var magnitude = actor.View?.IsHq == true ? 2f : Math.Clamp(2.5f + (damage ?? 0) * .45f, 3, 7);
        var tween = CreateTween(); _paperResponses[actor] = tween; _tweens.Add(tween);
        tween.TweenMethod(Callable.From<float>(p =>
        {
            var impulse = MathF.Sin(p * MathF.PI * 3) * MathF.Pow(1 - p, 2);
            var press = MathF.Sin(p * MathF.PI) * (1 - p);
            actor.Position = origin + direction * magnitude * impulse;
            actor.Rotation = rotation + direction.X * .025f * impulse;
            actor.Scale = Vector2.One * (1 - press * .025f);
            actor.QueueRedraw();
        }), 0f, 1f, Time(.24));
    }

    private async Task<double> LandAircraftAsync(int epoch)
    {
        var flyers = _flightPositions.Keys.Where(c => GodotObject.IsInstanceValid(c) && c.Visible).ToArray();
        if (flyers.Length == 0) return 0;
        PhaseChanged?.Invoke("aircraft-hold");
        await Delay(FlightHoldSeconds, epoch); _landing = true; _landingAge = 0;
        PhaseChanged?.Invoke("aircraft-land");
        foreach (var card in flyers)
        {
            StopPaperResponse(card);
            var landing = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
            landing.TweenProperty(card, "position", card.RestPosition, Time(LandingSeconds));
            landing.TweenProperty(card, "scale", Vector2.One, Time(LandingSeconds)); _tweens.Add(landing);
        }
        await Delay(LandingSeconds, epoch);
        foreach (var card in flyers.Where(GodotObject.IsInstanceValid))
        { card.SetFlightPose(Vector2.Zero, 0); card.DeploymentElevation = 0; card.QueueRedraw(); }
        _flightPositions.Clear(); _flightAges.Clear(); _landing = false;
        return FlightHoldSeconds + LandingSeconds;
    }
}

/// <summary>A visual contact at each projectile arrival, without repeating engine damage or badges.</summary>
public partial class CombatContact : CombatEffect
{
    public float DelayFraction { get; set; }
    public bool Heavy { get; set; }
    public override void _Draw()
    {
        if (Progress < DelayFraction) return;
        var p = (Progress - DelayFraction) / Math.Max(.001f, 1 - DelayFraction);
        var ink = new Color(1, .78f, .36f, (1 - p) * .72f);
        var radius = (Heavy ? 10 : 5) * (1 + p * .5f);
        DrawCircle(Vector2.Zero, (Heavy ? 3 : 1.8f) * (1 - p), ink);
        for (var i = 0; i < 5; i++)
        {
            var direction = Vector2.FromAngle(i * 2.399963f);
            DrawLine(direction * 2, direction * radius, ink, Heavy ? 1.6f : 1, true);
        }
    }
}
