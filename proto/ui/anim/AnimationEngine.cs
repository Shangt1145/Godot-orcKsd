using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>All scene mutations happen in _Process. Interrupted jobs always resolve.</summary>
public partial class AnimationEngine : Node
{
    private sealed class Job
    {
        public required double Seconds;
        public required Action<float> Step;
        public required CancellationToken Token;
        public required TaskCompletionSource<bool> Completion;
        public double Elapsed;
    }
    private sealed record Lease(int Id, Vector2 Position, Vector2 Scale, float Rotation, Color Modulate, CancellationTokenSource Cancel);
    private readonly List<Job> _jobs = [];
    private readonly Dictionary<Control, Lease> _motion = new();
    private readonly Dictionary<Control, int> _attackOwners = new();
    private readonly HashSet<Node> _effects = [];
    private int _version;
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _order;
    public AnimClock Clock { get; set; } = null!;
    public SfxPlayer Sfx { get; set; } = null!;
    public Control Overlay { get; set; } = null!;
    public int ActiveEffects => _effects.Count;
    public int ActiveMotionCount => _motion.Count;
    public int MotionStarts
    {
        get; private set;
    }
    public bool BudgetOk() => !Clock.ReducedMotion && _effects.Count < 56;
    public bool IsAnimating(Control node) => _motion.ContainsKey(node);
    public static Rect2 RectOf(Control node) => node.GetGlobalRect();
    public Rect2 LayoutRectOf(CardControl node)
    {
        var rect = node.GetGlobalRect();
        for (var parent = node.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is Control c && _motion.TryGetValue(c, out var lease))
                rect.Position += lease.Position - c.Position;
        return rect;
    }
    public static Control[] ToEls(IEnumerable<Control> nodes) => nodes.Where(GodotObject.IsInstanceValid).ToArray();
    public void Tf(Control node, Vector2 position, Vector2 scale, float rotation = 0)
    {
        node.Position = position;
        node.Scale = scale;
        node.Rotation = rotation;
    }
    public override void _Process(double delta)
    {
        foreach (var job in _jobs.ToArray())
        {
            if (job.Token.IsCancellationRequested)
            {
                _jobs.Remove(job);
                job.Completion.TrySetResult(false);
                continue;
            }
            job.Elapsed += delta;
            var t = (float)Math.Clamp(job.Elapsed / job.Seconds, 0, 1);
            try
            {
                job.Step(t);
            }
            catch (Exception e) { _jobs.Remove(job); job.Completion.TrySetException(e); continue; }
            if (t >= 1)
            {
                _jobs.Remove(job);
                job.Completion.TrySetResult(true);
            }
        }
    }
    private Task<bool> Progress(double ms, Action<float> step, CancellationToken token = default)
    {
        if (!IsInsideTree() || _lifetime.IsCancellationRequested || token.IsCancellationRequested)
            return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>();
        _jobs.Add(new Job { Seconds = Math.Max(.001, ms / 1000), Step = step, Token = token, Completion = completion });
        return completion.Task;
    }
    public async Task Sleep(double ms, CancellationToken token = default)
    {
        if (!await Progress(ms, _ => { }, token))
            throw new OperationCanceledException("Animation clock was interrupted.");
    }
    private static float Ease(float t) => 1 - Mathf.Pow(1 - t, 3);
    private async Task<bool> Motion(Control node, double ms, Action<float, Lease> step)
    {
        if (!GodotObject.IsInstanceValid(node))
            return false;
        InterruptMotion(node);
        var lease = new Lease(++_version, node.Position, node.Scale, node.Rotation, node.Modulate, new());
        _motion[node] = lease;
        MotionStarts++;
        try
        {
            return await Progress(Clock.Ms(ms), t => { if (GodotObject.IsInstanceValid(node)) step(t, lease); }, lease.Cancel.Token);
        }
        finally { if (_motion.TryGetValue(node, out var current) && current.Id == lease.Id) { Restore(node, lease); _motion.Remove(node); } lease.Cancel.Dispose(); }
    }
    private static void Restore(Control n, Lease l)
    {
        if (!GodotObject.IsInstanceValid(n))
            return;
        n.Position = l.Position;
        n.Scale = l.Scale;
        n.Rotation = l.Rotation;
        n.Modulate = l.Modulate;
    }
    public void Interrupt(Control node)
    {
        _attackOwners[node] = ++_version;
        InterruptMotion(node);
    }
    private void InterruptMotion(Control node)
    {
        if (_motion.Remove(node, out var old))
        {
            old.Cancel.Cancel();
            Restore(node, old);
        }
    }
    public void InterruptAll()
    {
        _order?.Cancel();
        foreach (var n in _motion.Keys.ToArray())
            Interrupt(n);
        _attackOwners.Clear();
        // Resolve timer jobs too, so delayed callbacks cannot retain removed gallery nodes.
        _lifetime.Cancel();
        foreach (var job in _jobs.ToArray())
        {
            _jobs.Remove(job);
            job.Completion.TrySetResult(false);
        }
        foreach (var fx in _effects.ToArray())
            Kill(fx);
        _lifetime.Dispose();
        _lifetime = new();
    }
    public override void _ExitTree()
    {
        InterruptAll();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
    public void Kill(Node? n)
    {
        if (n is null)
            return;
        _effects.Remove(n);
        if (GodotObject.IsInstanceValid(n) && !n.IsQueuedForDeletion())
            n.QueueFree();
    }
    public Control Card(UiCardDefinition? definition = null, Vector2? size = null)
    {
        var s = size ?? new Vector2(92, 129);
        Control node;
        if (definition is not null)
        {
            var c = new CardControl();
            c.Bind(definition, Textures, s.X);
            node = c;
        }
        else
        {
            var p = new Panel();
            p.AddThemeStyleboxOverride("panel", UiStyles.Box(new("36382f"), UiStyles.Gold, 4, 0));
            node = p;
            node.Size = s;
        }
        node.MouseFilter = Control.MouseFilterEnum.Ignore;
        Overlay.AddChild(node);
        _effects.Add(node);
        return node;
    }
    public TextureCache Textures { get; set; } = null!;
    private Vector2 Local(Vector2 global) => Overlay.GetGlobalTransform().AffineInverse() * global;
    private Vector2 Center(Control c) => Local(c.GetGlobalRect().GetCenter());
    private FxShape Shape(Vector2 at, string shape, Color color, float radius = 10)
    {
        var f = new FxShape { Position = Local(at), Shape = shape, Tint = color, Radius = radius };
        Overlay.AddChild(f);
        _effects.Add(f);
        return f;
    }
    private async Task FadeShape(FxShape shape, double ms, Vector2 destination, float scale = 2)
    {
        var start = shape.Position;
        try
        {
            await Progress(Clock.Ms(ms), t => { if (!GodotObject.IsInstanceValid(shape)) return; shape.Position = start.Lerp(destination, Ease(t)); shape.Scale = Vector2.One * Mathf.Lerp(1, scale, t); shape.Modulate = new Color(1, 1, 1, 1 - t); });
        }
        finally { Kill(shape); }
    }
    public Task ShockRing(Vector2 global, string kind = "shell")
    {
        if (Clock.ReducedMotion)
            return Task.CompletedTask;
        var f = Shape(global, "ring", ColorFor(kind), 14);
        return FadeShape(f, 560, f.Position, 3.5f);
    }
    private static Color ColorFor(string kind) => kind switch { "energy" or "intel" => new("80dded"), "heal" => new("8fd6a0"), "debuff" => new("b398d9"), "earth" or "dust" => new("b29d75"), "smoke" => new("777d82"), _ => new("e2bd79") };
    public Task SparkBurst(Vector2 global, string kind = "shell", int count = 5, float spread = 34, bool up = false, double ms = 620)
    {
        var tasks = new List<Task>();
        for (var i = 0; i < count && BudgetOk(); i++)
        {
            var angle = up ? -Mathf.Pi / 2 + (i / (float)Math.Max(1, count - 1) - .5f) * 1.7f : Mathf.Tau * i / count;
            var f = Shape(global, "diamond", ColorFor(kind), 2 + i % 3);
            var offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * spread;
            tasks.Add(FadeShape(f, ms, f.Position + offset, .4f));
        }
        return Task.WhenAll(tasks);
    }
    public Task DustPuff(Vector2 p) => SparkBurst(p, "dust", 3, 18, true, 560);
    public Task SmokePuff(Vector2 p) => SparkBurst(p, "smoke", 3, 46, true, 900);
    public Task WakePuff(Vector2 p) => SparkBurst(p, "intel", 4, 30, ms: 700);
    public Task Pulse(Control n, double ms = 520) => Motion(n, ms, (t, l) => { n.Modulate = l.Modulate.Lerp(UiStyles.Gold, Mathf.Sin(t * Mathf.Pi) * .5f); });
    public async Task FloatAt(Vector2 p, string text, string kind = "gain")
    {
        var label = UiStyles.Label(text, 24, kind is "loss" or "damage" ? new("ff8f7a") : ColorFor(kind));
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        Overlay.AddChild(label);
        _effects.Add(label);
        label.Position = Local(p) - new Vector2(20, 12);
        var start = label.Position;
        try
        {
            await Progress(Clock.Ms(950), t => { if (!GodotObject.IsInstanceValid(label)) return; label.Position = start + new Vector2(0, Clock.ReducedMotion ? 0 : -46 * Ease(t)); label.Modulate = new(1, 1, 1, 1 - t); });
        }
        finally { Kill(label); }
    }
    public Task FloatValue(Control n, string text, string kind = "gain") => FloatAt(n.GetGlobalRect().Position + n.Size * new Vector2(.5f, .3f), text, kind);
    public Task KreditFloat(Control n, string text) => FloatAt(n.GetGlobalRect().Position + n.Size * new Vector2(.5f, .1f), text);
    public async Task TurnBanner(string text, string sub = "")
    {
        var panel = new PanelContainer { Size = new Vector2(540, 100), MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", UiStyles.Box(new("292a25"), UiStyles.Gold));
        var content = new VBoxContainer();
        panel.AddChild(content);
        var main = UiStyles.Label(text, 32, UiStyles.Gold);
        main.HorizontalAlignment = HorizontalAlignment.Center;
        content.AddChild(main);
        var subtitle = UiStyles.Label(sub, 14, UiStyles.Dim);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        content.AddChild(subtitle);
        Overlay.AddChild(panel);
        _effects.Add(panel);
        panel.Position = (Overlay.Size - panel.Size) / 2;
        try
        {
            await Sleep(Clock.Ms(1150));
            await Progress(Clock.Ms(360), t => { if (GodotObject.IsInstanceValid(panel)) panel.Modulate = new(1, 1, 1, 1 - t); });
        }
        finally { Kill(panel); }
    }
    public static string KindOf(UiCardView? v) => NormalizeKind(v?.Definition.UnitType ?? "infantry");
    private static string NormalizeKind(string k) => k switch { "ship" => "cruiser", "space" => "landcruiser", "tank" or "structure" or "artillery" or "cruiser" or "landcruiser" or "bomber" or "fighter" or "spacefighter" => k, _ => "infantry" };
    public static string FamilyOf(string kind) => kind is "bomber" or "fighter" or "spacefighter" ? "air" : kind is "cruiser" or "landcruiser" ? "ship" : "ground";
    public static string HitKindOf(string kind) => kind == "spacefighter" ? "energy" : kind == "bomber" ? "bomb" : kind is "tank" or "structure" or "artillery" or "cruiser" or "landcruiser" ? "shell" : "bullet";
    public sealed record AttackStyle(int Shots, int Gap, int Travel, float Recoil, bool Arc = false, bool Fly = false, bool Drop = false);
    public static AttackStyle AttackProfile(string kind) => NormalizeKind(kind) switch
    {
        "tank" => new(1, 0, 270, 5),
        "structure" => new(2, 80, 190, 1),
        "artillery" => new(1, 0, 380, 5, true),
        "cruiser" => new(2, 95, 250, 3),
        "landcruiser" => new(2, 95, 280, 4),
        "fighter" => new(3, 55, 130, 0, Fly: true),
        "bomber" => new(1, 0, 300, 0, Fly: true, Drop: true),
        "spacefighter" => new(2, 65, 160, 0, Fly: true),
        _ => new(3, 65, 120, 1.5f)
    };
    /// <summary>Gallery-facing view of the shared landing table. Defense is the only input.</summary>
    public sealed record DeploymentStyle(double Milliseconds, float Gain, int Tier);
    public static DeploymentStyle SlamProfile(int defense)
    {
        var s = BattleSequence.SlamStyle(defense);
        return new(s.TravelFrames / 60.0 + s.SettleSeconds, s.Gain, BattleSequence.SlamTier(defense));
    }
    public async Task Slam(CardControl card)
    {
        var defense = card.View?.EffectiveDefense ?? card.Definition.BaseDefense ?? 1;
        var style = SlamProfile(defense);
        var s = BattleSequence.SlamStyle(defense);
        var ms = style.Milliseconds;
        Sfx.Play("deploy", card.View, style.Gain);
        if (Clock.ReducedMotion)
        {
            await Sleep(Clock.Ms(ms));
            return;
        }
        // Measured landing: the card is already on the slot, the weight arrives in one frame.
        // Dust scales with build only — no unit-type or family branch.
        await Motion(card.Visual, Clock.Ms(ms), (t, l) =>
        {
            if (t <= 0f)
            {
                card.Visual.Position = l.Position + new Vector2(0, s.Squash * 260f);
                card.Visual.Scale = new(1 + s.Squash, 1 - s.Squash);
                card.Visual.Modulate = new Color(1f + s.Flash, 1f + s.Flash, 1f + s.Flash, 1f);
                if (s.Dust > 0)
                {
                    var point = card.GetGlobalRect().Position + card.Size * new Vector2(.5f, .85f);
                    Observe(DustPuff(point));
                }
                if (s.Shake > 0 && card.GetParent() is Control stage) Observe(Shake(stage, s.Shake, s.Shake * 62));
                return;
            }
            var settle = t * Math.Max(ms, 1) / 60f / (s.TravelFrames / 60.0 + s.SettleSeconds);
            var flash = s.Flash * (1f - Math.Min(1f, (float)settle));
            card.Visual.Position = l.Position;
            card.Visual.Scale = Vector2.One;
            card.Visual.Modulate = new Color(1f + flash, 1f + flash, 1f + flash, 1f);
        });
        card.Visual.Modulate = Colors.White;
    }
    private static async void Observe(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { GD.PushError(e.ToString()); }
    }
    private Task Shake(Control stage, float strength, double ms) => Motion(stage, ms, (t, l) => stage.Position = l.Position + new Vector2(Mathf.Sin(t * 42), Mathf.Sin(t * 31)) * strength * (1 - t));
    public async Task Glide(CardControl card, double ms = AnimTiming.Move)
    {
        await Sleep(Clock.Ms(ms));
        if (!GodotObject.IsInstanceValid(card))
            return;
        Sfx.Play("move", card.View);
        var point = card.GetGlobalRect().Position + card.Size * new Vector2(.5f, .85f);
        var fam = FamilyOf(KindOf(card.View));
        if (fam == "ground")
            Observe(DustPuff(point));
        else if (fam == "ship")
            Observe(WakePuff(point));
    }
    public static Vector2? ArcMidOf(Vector2 delta, bool arc = true)
    {
        var len = delta.Length();
        return !arc || len < 40 ? null : new Vector2(-delta.Y, delta.X) / len * Math.Min(28, len * .1f);
    }
    public async Task FlipByUid(IReadOnlyDictionary<CardNodeKey, CardControl> before, Func<Task> mutate, Func<IReadOnlyDictionary<CardNodeKey, CardControl>> after)
    {
        var lifetime = _lifetime.Token;
        var snapshot = before.ToDictionary(p => p.Key, p => (Rect: LayoutRectOf(p.Value), Zone: p.Value.View?.Zone));
        await mutate();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (lifetime.IsCancellationRequested)
            return;
        var tasks = new List<Task>();
        foreach (var (key, node) in after())
        {
            if (!snapshot.TryGetValue(key, out var old) || IsAnimating(node.Visual))
                continue;
            var delta = old.Rect.Position - LayoutRectOf(node).Position;
            if (Math.Abs(delta.X) < 1.5 && Math.Abs(delta.Y) < 1.5)
                continue;
            if (Clock.ReducedMotion)
            {
                tasks.Add(Sleep(Clock.Ms(AnimTiming.Move)));
                continue;
            }
            var mid = ArcMidOf(delta, old.Zone != node.View?.Zone);
            tasks.Add(Motion(node.Visual, AnimTiming.Move, (t, l) => { var e = Ease(t); node.Visual.Position = l.Position + delta * (1 - e) + (mid ?? Vector2.Zero) * (4 * e * (1 - e)); }));
        }
        await Task.WhenAll(tasks);
    }
    public Task Flip(IReadOnlyList<CardControl> nodes, Func<Task> mutate) => FlipByUid(nodes.ToDictionary(c => new CardNodeKey("layout", c.GetInstanceId().ToString())), mutate, () => nodes.ToDictionary(c => new CardNodeKey("layout", c.GetInstanceId().ToString())));
    public static Vector2 MuzzlePoint(Rect2 r, Vector2 target)
    {
        var p = r.GetCenter();
        var d = target - p;
        var t = Math.Min(Math.Abs(d.X) > .001f ? r.Size.X * .46f / Math.Abs(d.X) : float.PositiveInfinity, Math.Abs(d.Y) > .001f ? r.Size.Y * .46f / Math.Abs(d.Y) : float.PositiveInfinity);
        return float.IsFinite(t) ? p + d * t : p;
    }
    public Task MuzzleFlash(CardControl card, Control target)
    {
        if (!BudgetOk())
            return Task.CompletedTask;
        var point = MuzzlePoint(card.GetGlobalRect(), target.GetGlobalRect().GetCenter());
        var flash = Shape(point, "diamond", ColorFor(HitKindOf(KindOf(card.View))), 9);
        return FadeShape(flash, 130, flash.Position, 1.8f);
    }
    public async Task Tracer(Vector2 from, Vector2 to, string kind, double ms)
    {
        if (!BudgetOk())
        {
            await Sleep(Clock.Ms(ms));
            return;
        }
        var f = Shape(from, "tracer", ColorFor(HitKindOf(kind)), kind == "infantry" ? 5 : 8);
        var start = f.Position;
        var end = Local(to);
        var delta = end - start;
        var height = kind == "artillery" ? Math.Min(110, Math.Max(26, delta.Length() * .23f)) : 0;
        var bow = height > 0 && Math.Abs(delta.Y) > Math.Abs(delta.X) ? height * .55f : 0;
        try
        {
            await Progress(Clock.Ms(ms), t => { if (!GodotObject.IsInstanceValid(f)) return; var e = kind == "bomber" ? t * t : t; f.Position = start + delta * e + new Vector2(bow, -height) * (4 * e * (1 - e)); f.Rotation = delta.Angle(); });
        }
        finally { Kill(f); }
    }
    public async Task Lunge(CardControl attacker, CardControl target, Func<Task>? onHit = null)
    {
        var lifetime = _lifetime.Token;
        var kind = KindOf(attacker.View);
        var profile = AttackProfile(kind);
        var from = MuzzlePoint(attacker.GetGlobalRect(), target.GetGlobalRect().GetCenter());
        var to = target.GetGlobalRect().GetCenter();
        var visuals = new List<Task>();
        Interrupt(attacker.Visual);
        var owner = ++_version;
        _attackOwners[attacker.Visual] = owner;
        bool Owns() => _attackOwners.TryGetValue(attacker.Visual, out var current) && current == owner;
        try
        {
            if (profile.Fly && BudgetOk())
            {
                var aircraft = Shape(attacker.GetGlobalRect().GetCenter(), "aircraft", UiStyles.Gold);
                aircraft.Rotation = (to - from).Angle();
                visuals.Add(FadeShape(aircraft, 150 + profile.Travel + profile.Gap * (profile.Shots - 1) + 120, Local(to) + new Vector2(100, -30), 1));
                await Sleep(Clock.Ms(150), lifetime);
                from = to + new Vector2(-45, -65);
            }
            Sfx.Play("attack", attacker.View);
            for (var i = 0; i < profile.Shots; i++)
            {
                if (lifetime.IsCancellationRequested || !GodotObject.IsInstanceValid(attacker) || !GodotObject.IsInstanceValid(target))
                    return;
                if (Owns())
                {
                    var spread = new Vector2(0, (i - (profile.Shots - 1) / 2f) * 3);
                    visuals.Add(MuzzleFlash(attacker, target));
                    visuals.Add(Tracer(from + spread, to + spread, kind, profile.Travel));
                    if (!Clock.ReducedMotion && profile.Recoil > 0)
                        visuals.Add(Motion(attacker.Visual, 170, (t, l) => attacker.Visual.Position = l.Position - (to - from).Normalized() * profile.Recoil * Mathf.Sin(t * Mathf.Pi)));
                }
                if (i < profile.Shots - 1)
                    await Sleep(Clock.Ms(profile.Gap), lifetime);
            }
            await Sleep(Clock.Ms(profile.Travel), lifetime);
            // A visual interruption does not cause another settlement. The callback is invoked once.
            if (!lifetime.IsCancellationRequested && onHit is not null)
                await onHit();
            await Sleep(Clock.Ms(120), lifetime);
            await Task.WhenAll(visuals);
        }
        finally { if (Owns()) _attackOwners.Remove(attacker.Visual); }
    }
    public Task Impact(CardControl target, string kind = "shell", UiCardView? attacker = null)
    {
        Sfx.Play("hit", attacker, target: target.View);
        var p = target.GetGlobalRect().GetCenter();
        var material = target.Definition.UnitType is "tank" or "cruiser" or "landcruiser" ? "shell" : "earth";
        if (kind == "energy")
            material = "energy";
        Observe(Task.WhenAll(SparkBurst(p, material, kind == "bullet" ? 4 : 6), kind == "energy" ? ShockRing(p, kind) : Task.CompletedTask, kind == "bomb" ? SmokePuff(p) : Task.CompletedTask));
        return Task.CompletedTask;
    }
    public async Task Die(CardControl card)
    {
        Sfx.Play("die", card.View);
        var fam = FamilyOf(KindOf(card.View));
        var p = card.GetGlobalRect().GetCenter();
        Observe(SparkBurst(p, "death", 6));
        await Motion(card.Visual, AnimTiming.Die, (t, l) => { card.Visual.Modulate = new(1, 1, 1, 1 - t); if (!Clock.ReducedMotion) { card.Visual.Position = l.Position + new Vector2(0, t * (fam == "air" ? 60 : 15)); card.Visual.Rotation = l.Rotation + t * (fam == "ship" ? .32f : .1f); } });
        Kill(card);
    }
    public async Task FlyCard(Rect2 from, Control target, UiCardDefinition? art = null, double ms = AnimTiming.Fly)
    {
        var ghost = Card(art, from.Size);
        var end = Center(target) - ghost.Size / 2;
        var start = Local(from.GetCenter()) - ghost.Size / 2;
        ghost.Position = Clock.ReducedMotion ? end : start;
        try
        {
            await Progress(Clock.Ms(ms), t => { if (!GodotObject.IsInstanceValid(ghost)) return; ghost.Position = Clock.ReducedMotion ? end : start.Lerp(end, Ease(t)); ghost.Modulate = new(1, 1, 1, 1 - .85f * t); });
        }
        finally { Kill(ghost); }
    }
    public Task FlyFromEl(CardControl from, Control target) => FlyCard(from.GetGlobalRect(), target, from.Definition);
    public Task DrawToHand(Control deck, Control hand, UiCardDefinition art) => FlyCard(deck.GetGlobalRect(), hand, art);
    public Task SlideInFromRight(CardControl card, Control hand) => Motion(card.Visual, 460, (t, l) => { if (!Clock.ReducedMotion) card.Visual.Position = l.Position + new Vector2(Math.Max(48, hand.GetGlobalRect().End.X - card.GetGlobalRect().Position.X) * (1 - Ease(t)), 0); card.Visual.Modulate = new(1, 1, 1, Math.Min(1, t * 8)); });
    public async Task BurnCard(Control hand, Control discard, UiCardDefinition art)
    {
        Sfx.Play("burn");
        var ghost = Card(art);
        var start = new Vector2(hand.GetGlobalRect().End.X + 60, hand.GetGlobalRect().Position.Y);
        ghost.Position = Local(start);
        try
        {
            await Progress(Clock.Ms(420), t => { if (GodotObject.IsInstanceValid(ghost)) ghost.Position = Clock.ReducedMotion ? Center(hand) : Local(start).Lerp(Center(hand), Ease(t)); });
            var at = ghost.Position;
            await Progress(Clock.Ms(560), t => { if (!GodotObject.IsInstanceValid(ghost)) return; ghost.Position = Clock.ReducedMotion ? at : at.Lerp(Center(discard), t * t); ghost.Modulate = new(1, .7f, .4f, 1 - t); });
        }
        finally { Kill(ghost); }
    }
    public AimArrow Arrow(Vector2 global, int bend = 1)
    {
        var a = new AimArrow { Start = Local(global), Bend = bend, ReducedMotion = Clock.ReducedMotion };
        Overlay.AddChild(a);
        _effects.Add(a);
        return a;
    }
    public async Task AimShot(CardControl from, CardControl target)
    {
        var arrow = Arrow(from.GetGlobalRect().GetCenter());
        arrow.SetTarget(Local(target.GetGlobalRect().GetCenter()));
        try
        {
            await Sleep(Clock.Ms(340));
            arrow.Aim(true);
            await Sleep(Clock.Ms(140));
        }
        finally { Kill(arrow); }
    }
    public async Task PlayOrderCard(UiCardDefinition art, bool self = false, double holdMs = 1000)
    {
        _order?.Cancel();
        var cts = new CancellationTokenSource();
        _order = cts;
        var size = Overlay.Size.Y < 680 ? new Vector2(160, 224) : new Vector2(196, 276);
        var ghost = Card(art, size);
        var center = new Vector2(Overlay.Size.X * .5f - ghost.Size.X / 2, Math.Clamp(Overlay.Size.Y * .3f, 12, Math.Max(12, Overlay.Size.Y - ghost.Size.Y - 16)));
        var side = new Vector2(self ? Overlay.Size.X * .87f - ghost.Size.X : Overlay.Size.X * .13f, center.Y);
        var start = new Vector2(center.X, -ghost.Size.Y);
        ghost.Position = Clock.ReducedMotion ? side : start;
        try
        {
            if (!await Progress(Clock.Ms(420), t => { if (GodotObject.IsInstanceValid(ghost)) ghost.Position = Clock.ReducedMotion ? side : start.Lerp(center, Ease(t)); }, cts.Token))
                return;
            if (!await Progress(Clock.Ms(460), t => { if (GodotObject.IsInstanceValid(ghost)) ghost.Position = Clock.ReducedMotion ? side : center.Lerp(side, Ease(t)); }, cts.Token))
                return;
            await Sleep(Clock.Ms(holdMs), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            var exit = new Vector2(self ? Overlay.Size.X + ghost.Size.X : -ghost.Size.X * 1.2f, side.Y);
            await Progress(Clock.Ms(520), t => { if (!GodotObject.IsInstanceValid(ghost)) return; ghost.Position = Clock.ReducedMotion ? side : side.Lerp(exit, t * t); ghost.Modulate = new(1, 1, 1, 1 - t); }, cts.Token);
        }
        finally { Kill(ghost); if (_order == cts) _order = null; cts.Dispose(); }
    }
    public Task PlayOpponentOrder(UiCardDefinition art) => PlayOrderCard(art);
    public async Task IntelScan(Control region)
    {
        Sfx.Play("intel");
        var scan = new ColorRect { Color = new(.5f, .9f, 1, .2f), MouseFilter = Control.MouseFilterEnum.Ignore, Size = new(12, region.Size.Y) };
        Overlay.AddChild(scan);
        _effects.Add(scan);
        var start = Local(region.GetGlobalRect().Position);
        try
        {
            await Progress(Clock.Ms(520), t => { if (!GodotObject.IsInstanceValid(scan)) return; scan.Position = start + new Vector2(Clock.ReducedMotion ? region.Size.X / 2 : region.Size.X * t, 0); });
        }
        finally { Kill(scan); }
    }
    public async Task CounterSet(Control pile)
    {
        Sfx.Play("counterSet");
        var seal = Shape(pile.GetGlobalRect().GetCenter(), "ring", UiStyles.Gold, 24);
        try
        {
            await Progress(Clock.Ms(460), t => { if (GodotObject.IsInstanceValid(seal)) seal.Modulate = new(1, 1, 1, 1 - t * .6f); });
        }
        finally { Kill(seal); }
    }
    public Task CounterFire(Control pile)
    {
        Observe(ShockRing(pile.GetGlobalRect().GetCenter(), "gold"));
        Observe(SparkBurst(pile.GetGlobalRect().GetCenter()));
        Observe(FloatValue(pile, "反制触发"));
        return PlaySfxAndWait("counterFire", 520);
    }
    private async Task PlaySfxAndWait(string name, double ms)
    {
        Sfx.Play(name);
        await Sleep(Clock.Ms(ms));
    }
    public Task VeteranUp(CardControl card)
    {
        Observe(SparkBurst(card.GetGlobalRect().GetCenter(), "buff", 6, 38, true));
        Observe(FloatValue(card, "★ 老兵"));
        return Task.WhenAll(Pulse(card.Visual, 760), PlaySfxAndWait("veteran", 760));
    }
    public async Task DeckShuffle(Control deck, int count = 3)
    {
        Sfx.Play("shuffle");
        for (var i = 0; i < Math.Clamp(count, 1, 4); i++)
        {
            var from = new Rect2(deck.GetGlobalRect().Position + new Vector2((i - 1) * 60, -160), new Vector2(46, 64));
            Observe(FlyCard(from, deck, ms: 560));
        }
        await Pulse(deck, 420);
    }
    public async Task CounterReveal(Control pile, UiCardDefinition art, double holdMs = 700)
    {
        Sfx.Play("counterFire", gain: .9f);
        var ghost = Card(art, new Vector2(132, 185));
        var start = Center(pile) - ghost.Size / 2;
        var end = Overlay.Size * new Vector2(.5f, .42f) - ghost.Size / 2;
        ghost.Position = Clock.ReducedMotion ? end : start;
        try
        {
            await Progress(Clock.Ms(420), t => { if (GodotObject.IsInstanceValid(ghost)) ghost.Position = Clock.ReducedMotion ? end : start.Lerp(end, Ease(t)); });
            await Sleep(Clock.Ms(holdMs));
            await Progress(Clock.Ms(360), t => { if (GodotObject.IsInstanceValid(ghost)) ghost.Modulate = new(1, 1, 1, 1 - t); });
        }
        finally { Kill(ghost); }
    }
    public async Task HoldCounter(CardControl from, Control pile)
    {
        await FlyCard(from.GetGlobalRect(), pile, from.Definition);
        Observe(CounterSet(pile));
    }
    public async Task OrderFx(string category, Control region)
    {
        var layer = new ColorRect { Color = new Color(ColorFor(category), .13f), MouseFilter = Control.MouseFilterEnum.Ignore, Size = region.Size, Position = Local(region.GetGlobalRect().Position) };
        Overlay.AddChild(layer);
        _effects.Add(layer);
        Observe(SparkBurst(region.GetGlobalRect().GetCenter(), category, 6, 62, true));
        try
        {
            await Sleep(Clock.Ms(520));
        }
        finally { Kill(layer); }
    }
}
