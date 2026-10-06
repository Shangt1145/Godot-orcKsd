using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>Paper draw/reveal/retire motions. Outcome and visibility come from snapshots.</summary>
public partial class BattleSequence : Control
{
    private TextureCache _textures = null!;
    private AnimClock _clock = null!;
    private SfxPlayer _sfx = null!;
    private readonly List<Tween> _tweens = [];
    private int _epoch;
    private BattleCard? _orderPaper;
    public event Action<string>? PhaseChanged;
    public int LiveCardCount => GetChildCount();
    public void Initialize(TextureCache textures, AnimClock clock, SfxPlayer sfx)
    { _textures = textures; _clock = clock; _sfx = sfx; MouseFilter = MouseFilterEnum.Ignore; }
    private double Time(double seconds) => Math.Max(.03, seconds * _clock.Scale);
    private async Task Wait(double seconds, int epoch)
    {
        // See BattleCombat.Delay: a headless render does not advance the engine's frame clock, so a
        // timer-based wait never returns. Step-driven capture counts rendered frames instead.
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

    /// <summary>When &gt; 0, <see cref="Wait"/> counts rendered frames instead of using the engine timer.</summary>
    public int StepFrames { get; set; }
    private BattleCard Paper(UiCardView? card, Vector2 at, Vector2 size, float rotation = 0)
    {
        var paper = new BattleCard { Position = at, Size = size, Rotation = rotation, PivotOffset = size / 2,
            MouseFilter = MouseFilterEnum.Ignore };
        paper.Bind(card, card is null ? BattleCardMode.Hidden : BattleCardMode.Inspect, _textures);
        AddChild(paper); return paper;
    }
    private Tween Motion() { var tween = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut); _tweens.Add(tween); return tween; }
    public void Interrupt()
    {
        _epoch++;
        foreach (var t in _tweens) if (GodotObject.IsInstanceValid(t)) t.Kill();
        _tweens.Clear();
        _orderPaper = null;
        foreach (var child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
    }
    public async Task DrawAsync(UiDrawPresentation draw, Vector2 destination, float rotation, bool consecutive = false)
    {
        var epoch = _epoch; var self = draw.Side == "self";
        var revealSeconds = consecutive ? .52 : .78;
        var holdSeconds = consecutive ? .20 : .55;
        var settleSeconds = consecutive ? .30 : .42;
        // Ignore even accidentally supplied opponent card identity.
        var visible = self && draw.Card?.Visibility == Visibility.Full ? draw.Card : null;
        var size = self ? new Vector2(144, 202) : new Vector2(110, 154);
        var paper = Paper(null, self ? new(1156, 618) : new(1120, -96), size, self ? .7f : -.7f);
        _sfx.Play("shuffle");
        PhaseChanged?.Invoke("draw-start");
        if (_clock.ReducedMotion)
        {
            paper.Position = destination; paper.Rotation = rotation;
            if (visible is not null) paper.Bind(visible, BattleCardMode.Hand, _textures);
            PhaseChanged?.Invoke(self ? "draw-reveal" : "draw-hidden"); await Wait(consecutive ? .12 : .18, epoch); return;
        }
        if (self)
        {
            var start = paper.Position;
            var reveal = new Vector2(640, 360) - size / 2;
            var turned = false;
            var travel = CreateTween(); _tweens.Add(travel);
            travel.TweenMethod(Callable.From<float>(p =>
            {
                var ease = p * p * (3 - 2 * p);
                var flip = Math.Min(1, p / .78f);
                var enlargement = Mathf.Lerp(1, 1.42f, ease);
                paper.Position = start.Lerp(reveal, ease) + new Vector2(-MathF.Sin(p * MathF.PI) * 24, -MathF.Sin(p * MathF.PI) * 46);
                paper.Rotation = .7f * (1 - ease) - MathF.Sin(p * MathF.PI) * .24f;
                paper.Scale = new Vector2(Math.Max(.025f, MathF.Abs(MathF.Cos(flip * MathF.PI))) * enlargement,
                    enlargement * (1 - MathF.Sin(flip * MathF.PI) * .055f));
                paper.PaperShear = MathF.Sin(flip * MathF.PI * 2) * .10f;
                if (!turned && flip >= .5f)
                {
                    turned = true;
                    if (visible is not null) paper.Bind(visible, BattleCardMode.Hand, _textures);
                    PhaseChanged?.Invoke("draw-flip");
                }
                paper.QueueRedraw();
            }), 0f, 1f, Time(revealSeconds));
            await Wait(revealSeconds, epoch);
            paper.Position = reveal; paper.Rotation = 0; paper.Scale = Vector2.One * 1.42f; paper.PaperShear = 0;
        }
        else
        {
            var hiddenSeconds = consecutive ? .34 : .48;
            var travel = Motion(); travel.TweenProperty(paper, "position", destination, Time(hiddenSeconds));
            travel.TweenProperty(paper, "rotation", rotation, Time(hiddenSeconds)); await Wait(hiddenSeconds, epoch);
        }
        PhaseChanged?.Invoke(self ? "draw-reveal" : "draw-hidden"); await Wait(self ? holdSeconds : consecutive ? .08 : .12, epoch);
        var settle = Motion();
        settle.TweenProperty(paper, "position", destination, Time(settleSeconds));
        settle.TweenProperty(paper, "rotation", rotation, Time(settleSeconds));
        settle.TweenProperty(paper, "scale", Vector2.One, Time(settleSeconds));
        await Wait(settleSeconds, epoch); PhaseChanged?.Invoke("draw-settled");
    }
    public async Task RevealOrderAsync(UiCardView card, Vector2 from)
    {
        var epoch = _epoch;
        var paper = Paper(card, _clock.ReducedMotion ? new(540, 209) : from, new(200, 280));
        _orderPaper = paper;
        if (!_clock.ReducedMotion)
        {
            paper.Scale = Vector2.One * .65f; paper.Rotation = card.OwnerSide == "enemy" ? -.08f : .08f;
            var t = Motion(); t.TweenProperty(paper, "position", new Vector2(540, 209), Time(.38));
            t.TweenProperty(paper, "scale", Vector2.One, Time(.38)); t.TweenProperty(paper, "rotation", 0f, Time(.38));
            await Wait(.38, epoch);
        }
        _sfx.Play("intel"); PhaseChanged?.Invoke("order-reveal"); await Wait(.55, epoch);
        if (!_clock.ReducedMotion)
        {
            var dock = Motion(); dock.TweenProperty(paper, "position", new Vector2(1008, 284), Time(.32));
            dock.TweenProperty(paper, "scale", Vector2.One * .75f, Time(.32));
            await Wait(.32, epoch);
        }
        PhaseChanged?.Invoke("order-docked");
    }
    public async Task RetireOrderAsync()
    {
        var epoch = _epoch;
        if (_orderPaper is { } card && GodotObject.IsInstanceValid(card))
        {
            var t = Motion(); t.TweenProperty(card, "modulate:a", 0f, Time(.28));
            if (!_clock.ReducedMotion) t.TweenProperty(card, "position", card.Position + new Vector2(0, 26), Time(.28));
        }
        await Wait(.28, epoch);
        if (_orderPaper is { } retired && GodotObject.IsInstanceValid(retired)) { RemoveChild(retired); retired.QueueFree(); }
        _orderPaper = null; PhaseChanged?.Invoke("order-retired");
    }
    public async Task DeployAsync(UiCardView card, BattleCard field, Vector2 from)
    {
        var epoch = _epoch; field.Visible = false;
        var paper = Paper(card, from, new(144, 202), card.OwnerSide == "enemy" ? -.15f : .15f);
        PhaseChanged?.Invoke("deployment-start");
        if (!_clock.ReducedMotion)
        {
            var move = Motion();
            move.TweenProperty(paper, "position", field.RestPosition + field.Size / 2 - paper.Size / 2, Time(.46));
            move.TweenProperty(paper, "scale", field.Size / paper.Size, Time(.46));
            move.TweenProperty(paper, "rotation", 0f, Time(.46)); await Wait(.46, epoch);
        }
        RemoveChild(paper); paper.QueueFree(); field.Visible = true;
        PhaseChanged?.Invoke("deployment-settled");
        await SlamAsync(field, card, epoch);
    }
    /// <summary>Light / medium / heavy, graded by defense: the original slams heavier units harder.</summary>
    public static int SlamTier(int? defense) => defense <= 2 ? 0 : defense <= 5 ? 1 : 2;
    /// <summary>
    /// Deployment landing, measured frame by frame from the reference match
    /// (H:\Working Folder\8月9日.mp4, 04:12.2 = 252.2s, 60fps, 1920x1080).
    /// The original does NOT hop and drop: the card moves from the dragged position to the
    /// slot within a single frame (bottom edge 139 -> 149 px, card face brightens by ~8%), then
    /// holds still. The perceived impact comes from that brightness step plus dust, not travel.
    /// These numbers replace the earlier invented rise/fall curve.
    /// </summary>
    /// <summary>
    /// Build (body size) IS defense. Three tiers, and nothing else may influence the landing:
    /// no unit type, no family, no air/ground distinction. Measured on the reference match
    /// (H:\Working Folder\8月9日.mp4, 04:12.2 = 252.2s, 60fps, 1920x1080) — the card reaches
    /// its slot within one frame and the perceived weight comes from the brightness step plus
    /// dust, not from travel.
    /// </summary>
    public sealed record SlamProfile(
        /// <summary>Frames spent travelling to the slot. One frame at 60fps, every tier.</summary>
        int TravelFrames,
        /// <summary>How long the card stays put bearing its own weight, in seconds.</summary>
        double SettleSeconds,
        /// <summary>Card-face brightness step on landing, added on top of the resting value.</summary>
        float Flash,
        /// <summary>Vertical squash on the landing frame only.</summary>
        float Squash,
        /// <summary>Dust ring diameter in pixels; zero raises none.</summary>
        float Dust,
        /// <summary>Table shake amplitude in pixels; zero stays still.</summary>
        float Shake,
        /// <summary>Sfx gain.</summary>
        float Gain);

    /// <summary>The one landing table. Defense is the only input; both gallery and board read this.</summary>
    public static SlamProfile SlamStyle(int? defense)
    {
        var tier = SlamTier(defense);
        // Heavier builds read as heavier through flash, dust and shake — not through a longer
        // animation and not through a bigger hop. Travel stays one frame for every tier.
        return tier switch
        {
            0 => new(1, .12, .04f, .012f, 0f, 0f, .45f),
            1 => new(1, .34, .09f, .045f, 44f, 2.5f, .75f),
            _ => new(1, .48f, .14f, .075f, 68f, 5f, 1f),
        };
    }

    /// <summary>Total landing time. Graded by defense via the settle tail, not by travel.</summary>
    public static double SlamSeconds(int? defense)
    {
        var s = SlamStyle(defense);
        return s.TravelFrames / 60.0 + s.SettleSeconds;
    }

    /// <summary>The landing itself. The card is already at its slot; the impact is flash + dust.</summary>
    public Task SlamAsync(BattleCard card, UiCardView view) => SlamAsync(card, view, _epoch);

    private async Task SlamAsync(BattleCard card, UiCardView view, int epoch)
    {
        var defense = view.EffectiveDefense ?? view.Definition.BaseDefense;
        var style = SlamStyle(defense);
        var rest = card.RestPosition;
        card.Position = rest;
        if (_clock.ReducedMotion)
        {
            card.Scale = Vector2.One; card.Modulate = Colors.White;
            await Wait(.12, epoch); return;
        }
        PhaseChanged?.Invoke("deployment-slam-" + SlamTier(defense));
        _sfx.Play("deploy", view, style.Gain);

        // Travel: the original resolves the drop within one frame, so this is a single step.
        if (style.TravelFrames > 0)
        {
            card.Position = rest + new Vector2(0, style.Squash * 260f);
            card.Scale = new Vector2(1 + style.Squash, 1 - style.Squash);
        }
        // Centre the ring on the card's footprint, not 8% below it: the card itself covers the middle
        // of the ring, so anything under it reads as nothing at all.
        var dust = style.Dust > 0
            ? new SlamDust { Size = Size, Center = rest + card.Size * new Vector2(.5f, .86f),
                Diameter = style.Dust, Duration = Time(.42) }
            : null;
        if (dust is not null) AddChild(dust);
        // The brightness step is what sells the impact: a brief lift, then back to rest.
        var flash = card.Modulate;
        card.Modulate = new Color(1f + style.Flash, 1f + style.Flash, 1f + style.Flash, 1f);
        await Wait(1 / 60.0, epoch);

        card.Position = rest;
        card.Scale = Vector2.One;
        var settle = Motion();
        settle.TweenProperty(card, "modulate", flash, Time(style.SettleSeconds * .55));
        await Wait(style.SettleSeconds, epoch);

        card.Position = rest; card.Scale = Vector2.One; card.Modulate = flash;
        if (dust is not null && GodotObject.IsInstanceValid(dust) && dust.GetParent() == this)
        { RemoveChild(dust); dust.QueueFree(); }
    }
    private static float Ease(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
    public async Task CounterAsync(UiCounterPresentation counter, BattleCard? handCard, Func<Task>? resolve = null)
    {
        var epoch = _epoch;
        if (counter.Stage != UiCounterStage.Triggered)
        {
            // Arming never creates a field card or exposes an opponent's hand.
            if (counter.Side != "self" || counter.Card?.Visibility != Visibility.Full || handCard is null) return;
            handCard.UpdateView(counter.Card);
            _sfx.Play("counterSet");
            PhaseChanged?.Invoke(counter.Stage == UiCounterStage.Armed ? "counter-armed" : "counter-disarmed");
            await Wait(.45, epoch); return;
        }
        if (counter.Card is null) return;
        if (handCard is not null) handCard.Visible = false;
        if (counter.BlockedCard is { } order)
        {
            await RevealOrderAsync(order, order.OwnerSide == "enemy" ? new(585, -60) : new(568, 628));
            _orderPaper!.Modulate = new Color(.65f, .65f, .62f);
            _orderPaper.AddChild(new BattleStatusCue { Size = _orderPaper.Size, Center = _orderPaper.Size / 2,
                Caption = "已阻止", Ink = new("ad4b36"), Duration = Time(1.6), Stamp = true });
        }
        var paper = Paper(counter.Card, counter.Side == "enemy" ? new(585, -60) : handCard?.Position ?? new(568, 628), new(200, 280));
        if (!_clock.ReducedMotion)
        {
            paper.Scale = Vector2.One * .65f; paper.Rotation = counter.Side == "enemy" ? -.16f : .16f;
            var t = Motion(); t.TweenProperty(paper, "position", new Vector2(540, 209), Time(.40));
            t.TweenProperty(paper, "scale", Vector2.One, Time(.40)); t.TweenProperty(paper, "rotation", -.025f, Time(.40));
            await Wait(.40, epoch);
        }
        else paper.Position = new(540, 209);
        _sfx.Play("counterFire"); PhaseChanged?.Invoke("counter-reveal"); await Wait(.65, epoch);
        if (resolve is not null) { await resolve(); if (_epoch != epoch) throw new OperationCanceledException(); }
        var retire = Motion(); retire.TweenProperty(paper, "modulate:a", 0f, Time(.28));
        await Wait(.28, epoch); RemoveChild(paper); paper.QueueFree();
        await RetireOrderAsync(); PhaseChanged?.Invoke("counter-retired");
    }
    public async Task StatusAsync(BattleCard card, UiStatusPresentation change)
    {
        var epoch = _epoch;
        card.UpdateView(change.After);
        var caption = change.Status switch
        {
            UiStatusKind.Heal => "治疗 " + change.After.Health,
            UiStatusKind.Buff => $"{change.After.EffectiveAttack} / {change.After.Health}",
            UiStatusKind.Damaged => "-" + Math.Max(0, (change.Before.Health ?? 0) - (change.After.Health ?? 0)),
            UiStatusKind.Suppressed => "压制", UiStatusKind.Cleared => "已解除",
            _ => "费用 " + change.After.EffectiveCost
        };
        var cue = new BattleStatusCue { Size = Size, Center = card.Position + (card.Mode == BattleCardMode.Hand ? new Vector2(card.Size.X / 2, 20) : card.Size / 2), Caption = caption,
            Ink = change.Status is UiStatusKind.Heal or UiStatusKind.Buff ? new("a2b67d")
                : change.Status == UiStatusKind.Damaged ? new("d66142") : new("ded2a7"),
            Duration = Time(.80), Quiet = _clock.ReducedMotion, Stamp = change.Status is UiStatusKind.Suppressed or UiStatusKind.Cleared };
        AddChild(cue); _sfx.Play("intel");
        PhaseChanged?.Invoke("status-" + change.Status.ToString().ToLowerInvariant());
        if (!_clock.ReducedMotion)
        {
            var pulse = CreateTween().SetTrans(Tween.TransitionType.Sine); _tweens.Add(pulse);
            pulse.TweenProperty(card, "scale", Vector2.One * 1.025f, Time(.14));
            pulse.TweenProperty(card, "scale", Vector2.One, Time(.24));
        }
        await Wait(.80, epoch);
    }
    /// <summary>Opening-hand replacement: kept cards are decided outside; here the returned ones travel back to the stack.</summary>
    public async Task MulliganReturnAsync(IReadOnlyList<BattleCard> leaving)
    {
        if (leaving.Count == 0) return;
        var epoch = _epoch;
        foreach (var card in leaving) card.Visible = false;
        var papers = leaving.Select(c => Paper(c.View, c.Position, c.Size, c.Rotation)).ToArray();
        _sfx.Play("shuffle");
        PhaseChanged?.Invoke("mulligan-returned");
        if (_clock.ReducedMotion) await Wait(.12, epoch);
        else
        {
            foreach (var paper in papers)
            {
                var t = Motion();
                t.TweenProperty(paper, "position", new Vector2(1156, 618), Time(.38));
                t.TweenProperty(paper, "rotation", .7f, Time(.38));
                t.TweenProperty(paper, "scale", Vector2.One * .72f, Time(.38));
                t.TweenProperty(paper, "modulate:a", 0f, Time(.38));
            }
            await Wait(.38, epoch);
        }
        foreach (var paper in papers) if (GodotObject.IsInstanceValid(paper)) { RemoveChild(paper); paper.QueueFree(); }
        PhaseChanged?.Invoke("mulligan-cleared");
    }
    /// <summary>Opponent replacements stay anonymous: backs leave upwards and the same number of backs return.</summary>
    public async Task MulliganEnemyAsync(IReadOnlyList<BattleCard> backs, IReadOnlyList<(Vector2 Position, float Rotation)> destinations)
    {
        var epoch = _epoch;
        if (backs.Count > 0)
        {
            var leaving = backs.Select(back =>
            {
                back.Visible = false;
                return Paper(null, back.Position, back.Size, back.Rotation);
            }).ToArray();
            _sfx.Play("shuffle");
            PhaseChanged?.Invoke("mulligan-returned");
            foreach (var paper in leaving)
            {
                var t = Motion();
                t.TweenProperty(paper, "position", paper.Position + new Vector2(0, -190), Time(.34));
                t.TweenProperty(paper, "modulate:a", 0f, Time(.34));
            }
            await Wait(.34, epoch);
            foreach (var paper in leaving) if (GodotObject.IsInstanceValid(paper)) { RemoveChild(paper); paper.QueueFree(); }
            PhaseChanged?.Invoke("mulligan-cleared");
        }
        if (destinations.Count == 0) return;
        var incoming = destinations.Select(d => Paper(null, d.Position + new Vector2(0, -260), new Vector2(110, 154), 0f))
            .Zip(destinations).ToArray();
        _sfx.Play("shuffle");
        foreach (var (paper, target) in incoming)
        {
            var t = Motion();
            t.TweenProperty(paper, "position", target.Position, Time(.34));
            t.TweenProperty(paper, "rotation", target.Rotation, Time(.34));
        }
        await Wait(.34, epoch);
        PhaseChanged?.Invoke("mulligan-drawn");
        foreach (var (paper, _) in incoming) if (GodotObject.IsInstanceValid(paper)) { RemoveChild(paper); paper.QueueFree(); }
    }
    /// <summary>Discarded and burned cards leave the table; a burn shows why the card never reached the hand.</summary>
    public async Task DiscardAsync(UiCardView? card, BattleCard? source, UiDiscardKind kind)
    {
        var epoch = _epoch;
        var own = source?.View?.OwnerSide ?? card?.OwnerSide ?? "self";
        var burn = kind == UiDiscardKind.Burn;
        if (source is not null) source.Visible = false;
        var visible = card is not null && card.Visibility == Visibility.Full ? card : null;
        var size = source?.Size ?? new Vector2(144, 202);
        var paper = Paper(visible, source?.Position ?? new Vector2(640 - size.X / 2, 250), size, source?.Rotation ?? 0f);
        _sfx.Play(burn ? "burn" : "shuffle");
        var cue = new BattleStatusCue { Size = Size, Center = paper.Position + size / 2, Caption = burn ? "手牌已满" : "弃置",
            Ink = new("d66142"), Duration = Time(1.2), Quiet = _clock.ReducedMotion, Stamp = true };
        AddChild(cue);
        PhaseChanged?.Invoke(burn ? "burn-marked" : "discard-marked");
        await Wait(burn ? .34 : .2, epoch);
        if (!_clock.ReducedMotion)
        {
            var t = Motion();
            t.TweenProperty(paper, "position", paper.Position + new Vector2(210, own == "self" ? 210 : -210), Time(.44));
            t.TweenProperty(paper, "rotation", own == "self" ? 1.1f : -1.1f, Time(.44));
            t.TweenProperty(paper, "scale", Vector2.One * .62f, Time(.44));
            t.TweenProperty(paper, "modulate:a", 0f, Time(.44));
            await Wait(.44, epoch);
        }
        else await Wait(.12, epoch);
        foreach (var node in new Node[] { paper, cue })
            if (GodotObject.IsInstanceValid(node) && node.GetParent() == this) { RemoveChild(node); node.QueueFree(); }
        PhaseChanged?.Invoke(burn ? "burn-exit" : "discard-exit");
    }
    /// <summary>
    /// The match is decided. A wide band sweeps in and holds for a beat before the result panel
    /// takes over, so the ending lands as an event rather than a sudden repaint.
    /// </summary>
    public async Task ResultBannerAsync(bool victory, string reason)
    {
        var epoch = _epoch;
        PhaseChanged?.Invoke(victory ? "result-victory" : "result-defeat");
        var banner = new BattleResultBanner
        {
            Size = Size,
            Caption = victory ? "胜利" : "失败",
            Line = reason,
            Ink = victory ? new("dec775") : new("b0655a"),
            Duration = Time(1.5),
            Quiet = _clock.ReducedMotion,
        };
        AddChild(banner);
        await Wait(.85, epoch);
        if (epoch != _epoch) throw new OperationCanceledException();
    }

    public async Task TurnBannerAsync(UiTurnPresentation turn)
    {
        var epoch = _epoch;
        var own = turn.Side == "self";
        var banner = new BattleTurnBanner { Size = Size, Caption = own ? "你的回合" : "对手回合",
            Line = own ? $"第 {turn.Turn} 回合 · 资源已补充" : $"第 {turn.Turn} 回合 · 等待对手行动",
            Ink = own ? new("dec775") : new("b9b2a0"), Duration = Time(.95), Quiet = _clock.ReducedMotion };
        AddChild(banner);
        _sfx.Play("intel");
        PhaseChanged?.Invoke("turn-banner");
        await Wait(_clock.ReducedMotion ? .34 : .95, epoch);
        if (GodotObject.IsInstanceValid(banner) && banner.GetParent() == this) { RemoveChild(banner); banner.QueueFree(); }
        PhaseChanged?.Invoke("turn-settled");
    }
    public override void _ExitTree() => Interrupt();
}

/// <summary>Dust kicked up by a deployment slam. Purely a landing feedback layer with its own lifetime.</summary>
public partial class SlamDust : Control
{
    public Vector2 Center { get; set; }
    public float Diameter { get; set; }
    public double Duration { get; set; }
    private double _age;
    /// <summary>Lets a step-driven caller (capture, replay) set the fade without waiting on the frame clock.</summary>
    public double Age { set { _age = value; QueueRedraw(); } }
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; QueueRedraw(); }
    public override void _Process(double delta)
    {
        _age += delta;
        if (_age >= Duration) { GetParent()?.RemoveChild(this); QueueFree(); return; }
        QueueRedraw();
    }
    public override void _Draw()
    {
        const int segments = 18;
        var progress = Math.Clamp((float)(_age / Duration), 0f, 1f);
        var alpha = (1 - progress) * .72f;
        var width = Diameter * (.5f + progress * 1.1f);
        var height = width * .3f;
        var points = new Vector2[segments];
        for (var i = 0; i < segments; i++)
        {
            var angle = i / (float)segments * MathF.PI * 2;
            points[i] = Center + new Vector2(MathF.Cos(angle) * width, MathF.Sin(angle) * height);
        }
        // A pale scuffed ring on dark wood: low alpha reads as nothing at all.
        DrawColoredPolygon(points, new Color(.86f, .82f, .70f, alpha));
        DrawPolyline([.. points, points[0]], new Color(.92f, .88f, .76f, alpha * .9f), 1.5f, true);
    }
}

/// <summary>Centred turn notification band. Geometry only; the side and turn number come from the projection.</summary>
public partial class BattleTurnBanner : Control
{
    public string Caption { get; set; } = "";
    public string Line { get; set; } = "";
    public Color Ink { get; set; }
    public double Duration { get; set; }
    public bool Quiet { get; set; }
    private double _age;
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; }
    public override void _Process(double delta)
    {
        _age += delta;
        if (_age >= Duration) { GetParent()?.RemoveChild(this); QueueFree(); return; }
        QueueRedraw();
    }
    public override void _Draw()
    {
        var p = (float)Math.Min(1, _age / Duration);
        var slide = Quiet ? 1f : Ease(Math.Min(p / .28f, (1 - p) / .28f));
        var alpha = Math.Min(1, slide * 2.2f);
        var center = new Vector2(640, 336);
        var width = Mathf.Lerp(300, 780, slide);
        var rect = new Rect2(center.X - width / 2, center.Y - 48, width, 96);
        DrawRect(rect, new Color(.10f, .11f, .09f, alpha * .80f));
        DrawLine(rect.Position, rect.Position + new Vector2(width, 0), Ink with { A = alpha }, 2);
        DrawLine(rect.Position + new Vector2(0, 96), rect.Position + new Vector2(width, 96), Ink with { A = alpha }, 2);
        var font = GetThemeDefaultFont();
        DrawString(font, new Vector2(center.X - font.GetStringSize(Caption, fontSize: 40).X / 2, center.Y + 2), Caption,
            HorizontalAlignment.Left, -1, 40, Ink with { A = alpha });
        DrawString(font, new Vector2(center.X - font.GetStringSize(Line, fontSize: 16).X / 2, center.Y + 36), Line,
            HorizontalAlignment.Left, -1, 16, new Color("cfc8b0") with { A = alpha });
    }
    private static float Ease(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
}

/// <summary>
/// The end-of-match band. Wipes in from the centre, holds, then fades — long enough to read the
/// reason before the result panel settles over it.
/// </summary>
public partial class BattleResultBanner : Control
{
    public string Caption { get; set; } = "";
    public string Line { get; set; } = "";
    public Color Ink { get; set; }
    public double Duration { get; set; }
    public bool Quiet { get; set; }
    private double _age;
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; QueueRedraw(); }
    public override void _Process(double delta)
    {
        _age += delta;
        if (_age >= Duration) { GetParent()?.RemoveChild(this); QueueFree(); return; }
        QueueRedraw();
    }
    public override void _Draw()
    {
        var p = (float)Math.Min(1, _age / Duration);
        // Wipe in over the first third, hold, then fade the last third.
        var open = Quiet ? 1f : Ease(Math.Min(p / .3f, 1));
        var fade = Quiet ? 1f : Ease(Math.Min((1 - p) / .3f, 1));
        var alpha = Math.Min(open * 2.4f, fade);
        var center = new Vector2(Size.X / 2f, Size.Y / 2f);
        var width = Mathf.Lerp(0, Size.X * .82f, open);
        var rect = new Rect2(center.X - width / 2, center.Y - 74, width, 148);
        DrawRect(rect, new Color(.09f, .10f, .08f, alpha * .88f));
        DrawLine(rect.Position, rect.Position + new Vector2(width, 0), Ink with { A = alpha }, 3);
        DrawLine(rect.Position + new Vector2(0, rect.Size.Y), rect.Position + new Vector2(width, rect.Size.Y), Ink with { A = alpha }, 3);
        if (alpha <= .01f) return;
        var font = GetThemeDefaultFont();
        // DrawString takes an integer font size, so the wipe scales the drawn width rather than the glyphs:
        // a growing offset from the centre reads as the band opening without touching the type.
        var reveal = (int)Math.Round(font.GetStringSize(Caption, fontSize: 54).X * open);
        DrawString(font, new Vector2(center.X - reveal / 2f, center.Y + 6), Caption,
            HorizontalAlignment.Left, -1, 54, Ink with { A = alpha });
        if (open > .5f)
            DrawString(font, new Vector2(center.X - font.GetStringSize(Line, fontSize: 17).X / 2, center.Y + 46), Line,
                HorizontalAlignment.Left, -1, 17, new Color("d8d1b8") with { A = alpha });
    }
    private static float Ease(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
}

public partial class BattleStatusCue : Control
{
    public Vector2 Center { get; set; }
    public string Caption { get; set; } = "";
    public Color Ink { get; set; }
    public double Duration { get; set; }
    public bool Stamp { get; set; }
    public bool Quiet { get; set; }
    private double _age;
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; }
    public override void _Process(double delta)
    {
        _age += delta;
        if (_age >= Duration) { GetParent()?.RemoveChild(this); QueueFree(); return; }
        QueueRedraw();
    }
    public override void _Draw()
    {
        var p = (float)Math.Min(1, _age / Duration); var a = Math.Min(1, p * 10) * Math.Min(1, (1 - p) * 5);
        var c = Center + new Vector2(0, Quiet || Stamp ? 0 : -p * 24);
        var box = new Rect2(c - new Vector2(55, 19), new(110, 38));
        DrawRect(box, new Color(.13f, .15f, .11f, a * .9f));
        DrawRect(box, Ink with { A = a }, false, Stamp ? 2 : 1);
        var font = GetThemeDefaultFont(); var width = font.GetStringSize(Caption, fontSize: 20).X;
        DrawString(font, c + new Vector2(-width / 2, 7), Caption, HorizontalAlignment.Left, -1, 20, Ink with { A = a });
    }
}
