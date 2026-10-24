using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>Board combat: anchored textures, weapon travel, damage badges and persistent debris.
/// The adapter owns damage and deaths. Presentation timings are tunable estimates, not extracted original timings.</summary>
public partial class BattleCombat : Control
{
    private AnimClock _clock = null!;
    private SfxPlayer _sfx = null!;
    private Texture2D _burst = null!;
    private readonly List<Tween> _tweens = [];
    private readonly List<BattleCard> _actors = [];
    private readonly Dictionary<BattleCard, Vector2> _flightPositions = [];
    private readonly Dictionary<BattleCard, float> _flightAges = [];
    private bool _landing;
    private float _landingAge;
    private const float TakeoffSeconds = .45f;
    private const float FlightHoldSeconds = .25f;
    private const float LandingSeconds = .35f;
    private const float TakeoffForwardDistance = 20;
    private const float TakeoffLiftDistance = 12;
    private int _epoch;
    public bool IsPlaying { get; private set; }
    public int LiveEffectCount => GetChildCount();
    public event Action<string>? PhaseChanged;
    public void Initialize(AnimClock clock, TextureCache textures, SfxPlayer sfx)
    {
        _clock = clock; _sfx = sfx;
        _burst = textures.Get("res://proto/assets/battle/explosion-v2.png") ?? throw new InvalidOperationException("Missing combat texture.");

    }
    private double Time(double seconds) => Math.Max(.03, seconds * _clock.Scale);
    private static bool Flies(string? type) => type is "fighter" or "bomber" or "spacefighter" or "space_fighter" or "space";
    private static float EaseFlight(float progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        return t * t * (3 - 2 * t);
    }
    private static Vector2 CenterOf(BattleCard actor) => actor.Position + actor.Size / 2 + actor.FlightOffset * actor.Scale;
    public override void _Process(double delta)
    {
        var step = (float)(delta / _clock.Scale);
        if (_landing) _landingAge += step;
        foreach (var card in _flightPositions.Keys)
        {
            if (!GodotObject.IsInstanceValid(card) || !card.Visible) continue;
            var age = _flightAges.GetValueOrDefault(card) + step; _flightAges[card] = age;
            var fade = EaseFlight(age / TakeoffSeconds) * (_landing ? 1 - EaseFlight(_landingAge / LandingSeconds) : 1);
            var direction = (_flightPositions[card] - card.RestPosition).Normalized();
            var drift = direction * (2 * EaseFlight(age * 7 / 2));
            var bob = new Vector2(MathF.Sin(age * 3.1f) * 1.3f, MathF.Sin(age * 4.5f) * 3.5f);
            card.SetFlightPose((drift + bob) * fade, MathF.Sin(age * 3) * .018f * fade);
            card.DeploymentElevation = fade * .5f;
        }
    }
    private async Task Delay(double seconds, int epoch)
    {
        // Step-driven callers (frame capture) advance the clock themselves: the engine's timer is
        // driven by the frame loop, which a headless render does not advance, so a plain timer wait
        // would never return and the whole choreography would collapse into one frame.
        if (StepFrames > 0)
        {
            var frames = (int)Math.Max(1, Math.Round(seconds * 60));
            for (var i = 0; i < frames; i++)
            {
                if (epoch != _epoch || !IsInsideTree()) throw new OperationCanceledException();
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
            return;
        }
        await ToSignal(GetTree().CreateTimer(Time(seconds)), SceneTreeTimer.SignalName.Timeout);
        if (epoch != _epoch || !IsInsideTree()) throw new OperationCanceledException();
    }

    /// <summary>When &gt; 0, <see cref="Delay"/> waits that many rendered frames instead of using the engine timer.</summary>
    public int StepFrames { get; set; }
    public void Interrupt()
    {
        ResetMotion();
        foreach (var child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
    }
    private void ResetMotion()
    {
        _epoch++; IsPlaying = false;
        foreach (var t in _tweens) if (GodotObject.IsInstanceValid(t)) t.Kill();
        _tweens.Clear(); _paperResponses.Clear();
        foreach (var card in _actors.Where(GodotObject.IsInstanceValid))
        { card.Position = card.RestPosition; card.Scale = Vector2.One; card.Rotation = card.RestRotation; card.Modulate = Colors.White; card.SetFlightPose(Vector2.Zero, 0); card.DeploymentElevation = 0; card.QueueRedraw(); }
        _actors.Clear(); _flightPositions.Clear(); _flightAges.Clear(); _landing = false; _landingAge = 0;
    }
    public async Task PlayAsync(UiCombatResolution r, BattleCard attacker, BattleCard defender)
    {
        ResetMotion(); var epoch = _epoch; IsPlaying = true; _actors.Add(attacker); _actors.Add(defender);
        var from = attacker.RestPosition + attacker.Size / 2; var to = defender.RestPosition + defender.Size / 2;
        PhaseChanged?.Invoke("ready"); await Delay(.16, epoch);
        await Shoot(r.Attacker, attacker, from, to, epoch);
        Hit(r.Defender, r.DefenderAfter, defender, to, r.DamageToDefender, from);
        PhaseChanged?.Invoke(r.DefenderAfter is null ? r.Defender.IsHq ? "hq-destroy" : "death" : "impact");
        // Counterfire is a second readable impact, even if either participant dies in the resolution.
        if (r.DamageToAttacker > 0)
        {
            await Delay(.18, epoch);
            var airborneTarget = CenterOf(attacker);
            await Shoot(r.Defender, defender, to, airborneTarget, epoch);
            Hit(r.Attacker, r.AttackerAfter, attacker, CenterOf(attacker), r.DamageToAttacker, to);
            PhaseChanged?.Invoke("counter");
        }
        else if (r.AttackerAfter is not null) attacker.UpdateView(r.AttackerAfter);
        var tail = r.Defender.IsHq && r.DefenderAfter is null ? 1.6 : r.DefenderAfter is null ? .40 : .22;
        tail = Math.Max(0, tail - await LandAircraftAsync(epoch));
        await Delay(tail, epoch);
        if (epoch != _epoch) throw new OperationCanceledException();
        ResetMotion(); PhaseChanged?.Invoke("settled");
    }
    private async Task Shoot(UiCardView source, BattleCard actor, Vector2 from, Vector2 to, int epoch)
    {
        var type = source.Definition.UnitType ?? "infantry";
        var direction = (to - from).Normalized();
        if (!_clock.ReducedMotion && GodotObject.IsInstanceValid(actor) && actor.Visible)
        {
            if (Flies(type))
            {
                if (!_flightPositions.ContainsKey(actor))
                {
                    PhaseChanged?.Invoke("aircraft-takeoff");
                    var raised = actor.RestPosition + direction * TakeoffForwardDistance + new Vector2(0, -TakeoffLiftDistance);
                    _flightPositions[actor] = raised;
                    _flightAges[actor] = 0;
                    var takeoff = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
                    takeoff.TweenProperty(actor, "position", raised, Time(TakeoffSeconds));
                    takeoff.TweenProperty(actor, "scale", Vector2.One * 1.05f, Time(TakeoffSeconds)); _tweens.Add(takeoff);
                    await Delay(TakeoffSeconds, epoch);
                }
                from = CenterOf(actor); direction = (to - from).Normalized();
            }

        }
        _sfx.Play("attack", source);
        var weapon = BattleWeaponProfile.Resolve(source);
        WeaponPaperResponse(actor, direction, weapon);
        if (weapon.Trace == "flame")
        {
            AddEffect(new CombatFlame { Position = from, To = to - from, Duration = Time(weapon.Travel), Quiet = _clock.ReducedMotion });
            await Delay(weapon.Travel, epoch); return;
        }
        for (var i = 0; i < weapon.Shots; i++)
        {
            var firingFrom = _flightPositions.ContainsKey(actor) ? CenterOf(actor) : from;
            var offset = direction.Orthogonal() * (weapon.Shots > 1 ? i % 2 == 0 ? 3 : -3 : 0);
            if (weapon.Trace == "bomb") firingFrom = to + new Vector2(14, -112);
            else Flash(firingFrom + direction * 20 + offset, weapon.Flash);
            AddTrace(firingFrom + offset, to + offset, weapon.Travel, weapon.Trace, 0);
            if (!_clock.ReducedMotion) AddEffect(new CombatContact { Position = to + offset,
                Duration = Time(weapon.Travel + .09), DelayFraction = (float)(weapon.Travel / (weapon.Travel + .09)), Heavy = weapon.Trace != "bullet", ZIndex = 4 });
            await Delay(weapon.Interval, epoch);
        }
        if (weapon.Travel > weapon.Interval) await Delay(weapon.Travel - weapon.Interval, epoch);
    }
    private void AddTrace(Vector2 from, Vector2 to, double life, string type, float offset)
    {
        AddEffect(new CombatTrace { From = from, To = to, Kind = type, Duration = Time(life), Quiet = _clock.ReducedMotion });
    }
    /// <summary>
    /// Plays an attack that the engine attributed to a unit: the attacker's own weapon runs first,
    /// then each impact lands. Sourceless impacts go through <see cref="PresentImpactsAsync"/> and
    /// stay bare hits — no shot is invented for a hit the engine never paired.
    /// </summary>
    public async Task PresentAssaultAsync(BattleCard attacker, IReadOnlyList<(UiOrderImpact Impact, BattleCard Card)> targets)
    {
        if (attacker is not { Visible: true }) { await PresentImpactsAsync(targets); return; }
        ResetMotion(); var epoch = _epoch; IsPlaying = true; _actors.Add(attacker);
        PhaseChanged?.Invoke("assault");
        var from = CenterOf(attacker);
        var victims = targets.Where(t => t.Card != attacker).ToArray();
        // Fire at each victim in order; a unit that struck two things shoots twice.
        foreach (var (impact, card) in victims)
        {
            if (epoch != _epoch) return;
            if (!GodotObject.IsInstanceValid(card) || !card.Visible) continue;
            await Shoot(attacker.View!, attacker, from, CenterOf(card), epoch);
            if (epoch != _epoch) return;
            _actors.Add(card);
            Hit(impact.Before, impact.After, card, CenterOf(card), impact.Damage, CenterOf(attacker));
            PhaseChanged?.Invoke("assault-target-impact");
        }
        foreach (var (impact, card) in targets.Where(t => t.Card == attacker))
            Hit(impact.Before, impact.After, card, CenterOf(card), impact.Damage);
        PhaseChanged?.Invoke("assault-impact");
        var tail = victims.Any(t => t.Impact.Before.IsHq && t.Impact.After is null) ? 1.6 : victims.Any(t => t.Impact.After is null) ? .40 : .22;
        await Delay(Math.Max(0, tail - await LandAircraftAsync(epoch)), epoch);
        ResetMotion();
    }

    public async Task PresentImpactsAsync(IReadOnlyList<(UiOrderImpact Impact, BattleCard Card)> targets)
    {
        if (targets.Count == 0) { PhaseChanged?.Invoke("order-impact"); return; }
        ResetMotion(); var epoch = _epoch; IsPlaying = true;
        // Capture all target positions before hiding any card; effects outlive their actors.
        foreach (var (impact, card) in targets)
        {
            _actors.Add(card);
            Hit(impact.Before, impact.After, card, CenterOf(card), impact.Damage);
        }
        PhaseChanged?.Invoke("order-impact");
        await Delay(targets.Any(t => t.Impact.Before.IsHq && t.Impact.After is null) ? 1.6 : targets.Any(t => t.Impact.After is null) ? .40 : .22, epoch);
        ResetMotion();
    }
    private void Flash(Vector2 at, float diameter)
    {
        AddEffect(new CombatMuzzle { Position = at, Radius = diameter / 2, Duration = Time(.09), Quiet = _clock.ReducedMotion, ZIndex = 3 });
    }
    public Task PresentRemovalAsync(UiCardView supplied, BattleCard card) => PresentRemovalsAsync([(supplied, card)]);
    public async Task PresentRemovalsAsync(IReadOnlyList<(UiCardView View, BattleCard Card)> cards)
    {
        if (cards.Count == 0) return;
        ResetMotion(); var epoch = _epoch; IsPlaying = true;
        foreach (var (view, card) in cards)
        { _actors.Add(card); Hit(view, null, card, CenterOf(card), null); PhaseChanged?.Invoke("unit-destroyed"); }
        await Delay(cards.Any(c => c.View.IsHq) ? 1.6 : .40, epoch); ResetMotion();
    }
    private void Hit(UiCardView before, UiCardView? after, BattleCard card, Vector2 at, int? damage, Vector2? source = null)
    {
        _sfx.Play("hit", before, target: before);
        if (damage is { } amount) AddEffect(new CombatDamage { Position = at + new Vector2(0, -20), Amount = amount, Duration = Time(.40), Quiet = _clock.ReducedMotion, ZIndex = 10 });
        var dead = after is null; var hq = before.IsHq && dead;
        var aircraft = before.Definition.UnitType is "fighter" or "bomber" or "spacefighter";
        var armored = before.Definition.UnitType is "tank" or "cruiser" or "landcruiser";
        var radius = hq ? 270 : dead ? 147 : 73;
        // Infantry leaves dust; aircraft and fuel-bearing vehicles leave a longer fireball.
        AddCloud(at, hq ? radius : aircraft ? radius : armored ? radius * .8f : radius * .5f,
            hq ? 1.08 : aircraft && dead ? .32 : armored && dead ? .24 : .16, false, Colors.White, .13f);
        for (var i = 0; i < (hq ? 7 : dead ? 4 : 2); i++)
        {
            var angle = i * 2.399963f;
            var offset = Vector2.FromAngle(angle) * (hq ? 42 : 17);
            AddCloud(at + offset, radius * .82f, hq ? 1.6 : .38, true, new Color(.83f, .8f, .72f, .65f), angle);
        }
        if (!_clock.ReducedMotion)
            for (var i = 0; i < (hq ? 25 : dead ? 14 : 6); i++)
            {
                var direction = Vector2.FromAngle(i * 2.399963f + MathF.Sin(i * 4.76f) * .31f);
                AddEffect(new CombatDebris { Position = at, Direction = direction, Distance = (hq ? 150 : dead ? 78 : 32) * (.65f + MathF.Abs(MathF.Sin(i * 13.71f)) * .8f),
                    Duration = Time(hq ? 1.2 : .35), Fragment = dead && i % 3 == 0, Index = i });
            }
        if (after is not null)
        {
            card.UpdateView(after);
            card.Visible = after.Visibility == Visibility.Full;
            ImpactPaperResponse(card, damage, source);
        }
        else { card.Visible = false; _sfx.Play("die", before); }
    }
    private void AddEffect(CombatEffect effect)
    {
        if (GetChildCount() >= 192)
        {
            var oldest = GetChildren().OfType<CombatEffect>().FirstOrDefault();
            if (oldest is not null) { RemoveChild(oldest); oldest.QueueFree(); }
        }
        AddChild(effect);
    }
    private void AddCloud(Vector2 at, float diameter, double duration, bool smoke, Color tint, float angle)
    {
        if (smoke) AddEffect(new CombatDust { Position = at, Diameter = diameter, Duration = Time(duration), Tint = tint, Quiet = _clock.ReducedMotion, ZIndex = 0 });
        else AddEffect(new CombatCloud { Position = at, Texture = _burst, Diameter = diameter, Duration = Time(duration), Tint = tint, Angle = angle, Quiet = _clock.ReducedMotion, ZIndex = 1 });
    }
    public override void _ExitTree() => Interrupt();
}

public abstract partial class CombatEffect : Control
{
    public double Duration { get; set; }
    protected float Progress;
    private double _age;
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; }
    public override void _Process(double delta)
    {
        _age += delta; Progress = (float)Math.Min(1, _age / Duration);
        if (Progress >= 1) { GetParent()?.RemoveChild(this); QueueFree(); return; }
        QueueRedraw();
    }
}
public partial class CombatCloud : CombatEffect
{
    public Texture2D Texture { get; set; } = null!;
    public float Diameter { get; set; }
    public bool Smoke { get; set; }
    public bool Quiet { get; set; }
    public Color Tint { get; set; }
    public float Angle { get; set; }
    public override void _Draw()
    {
        var p = Progress; var expansion = Quiet ? 1 : Smoke ? .45f + p * .80f : .3f + MathF.Sqrt(p) * .8f;
        var opacity = Smoke ? Math.Min(p * 8, 1) * MathF.Pow(1 - p, .65f) : Math.Min(p * 16, 1) * (1 - p);
        var d = Diameter * expansion;
        DrawSetTransform(new(0, Smoke && !Quiet ? -p * 24 : 0), Angle + (Quiet ? 0 : p * .09f));
        DrawTextureRect(Texture, new(-d / 2, -d / 2, d, d), false, Tint with { A = Tint.A * opacity });
        DrawSetTransform(Vector2.Zero);
    }
}
public partial class CombatTrace : CombatEffect
{
    public Vector2 From { get; set; }
    public Vector2 To { get; set; }
    public string Kind { get; set; } = "bullet";
    public bool Quiet { get; set; }
    private Vector2 Point(float p) => From.Lerp(To, p) + (Kind is "shell" or "rocket" && !Quiet ? new Vector2(MathF.Sin(p * MathF.PI) * 45, -MathF.Sin(p * MathF.PI) * 68) : Vector2.Zero);
    public override void _Draw()
    {
        var head = Point(Progress); var tail = Point(Math.Max(0, Progress - (Kind == "bullet" ? .10f : .07f)));
        if (Kind == "bomb")
        {
            DrawSetTransform(head, .14f); DrawRect(new(-3, -11, 6, 14), new("22241f"));
            DrawColoredPolygon([new(-5, -15), new(5, -15), new(2, -8), new(-2, -8)], new("3c4137"));
            DrawSetTransform(Vector2.Zero); return;
        }
        DrawLine(tail, head, new Color(.93f, .72f, .36f, .45f), Kind == "bullet" ? 2 : 4, true);
        DrawLine(tail.Lerp(head, .40f), head, new("fff0c2"), Kind == "bullet" ? 1 : 2, true);
    }
}
public partial class CombatDamage : CombatEffect
{
    public int Amount { get; set; }
    public bool Quiet { get; set; }
    public override void _Draw()
    {
        var y = Quiet ? 0 : -Progress * 26; var a = Math.Min(1, (1 - Progress) * 5);
        DrawColoredPolygon([new(-21, y - 25), new(21, y - 25), new(21, y + 8), new(0, y + 23), new(-21, y + 8)], new Color(.12f, .13f, .105f, a));
        var text = "−" + Amount; var font = GetThemeDefaultFont(); var width = font.GetStringSize(text, fontSize: 26).X;
        DrawString(font, new(-width / 2, y + 8), text, HorizontalAlignment.Left, -1, 26, new Color(.86f, .25f, .11f, a));
    }
}
public partial class CombatDebris : CombatEffect
{
    public Vector2 Direction { get; set; }
    public float Distance { get; set; }
    public bool Fragment { get; set; }
    public int Index { get; set; }
    public override void _Draw()
    {
        var p = Progress; var at = Direction * Distance * (1 - MathF.Pow(1 - p, 2)) + new Vector2(0, p * p * 37);
        var tint = Fragment ? new Color(.63f, .57f, .38f, 1 - p) : new Color(1, .73f, .24f, 1 - p);
        if (Fragment)
        {
            DrawSetTransform(at, p * (Index % 2 == 0 ? 4 : -4));
            DrawColoredPolygon([new(-4, -7), new(5, -3), new(2, 5), new(-3, 3)], tint); DrawSetTransform(Vector2.Zero);
        }
        else DrawLine(at - Direction * (Index % 3 + 2), at, tint, 1, true);
    }
}
