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
    private BattleCard Paper(UiCardView? card, Vector2 at, Vector2 size, float rotation = 0,
        BattleCardMode mode = BattleCardMode.Inspect)
    {
        var paper = new BattleCard { Position = at, Size = size, Rotation = rotation, PivotOffset = size / 2,
            MouseFilter = MouseFilterEnum.Ignore };
        paper.Bind(card, card is null ? BattleCardMode.Hidden : mode, _textures);
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
    /// <summary>One actor owns the card from the released hand pose through impact.</summary>
    public Task DeployAsync(UiCardView card, BattleCard field, Vector2 from) =>
        DeployAsync(card, field, new DeploymentPose(from, new(144, 202), Vector2.One, 0));

    public async Task DeployAsync(UiCardView card, BattleCard field, DeploymentPose from)
    {
        var epoch = _epoch;
        var style = SlamStyle(card.EffectiveDefense ?? card.Definition.BaseDefense);
        var enemy = card.OwnerSide == "enemy";
        field.Visible = false;
        var paper = Paper(card, from.Position, from.Size, from.Rotation,
            enemy ? BattleCardMode.Hidden : BattleCardMode.Inspect);
        paper.Scale = from.Scale;
        var completed = false;
        try
        {
            PhaseChanged?.Invoke("deployment-start");
            if (_clock.ReducedMotion)
            {
                paper.Bind(card, BattleCardMode.Field, _textures);
                paper.Size = field.Size; paper.PivotOffset = paper.Size / 2;
                paper.Position = field.RestPosition; paper.Scale = Vector2.One; paper.Rotation = 0;
                _sfx.Play("deploy", card, style.Gain);
                await Wait(.12, epoch);
            }
            else
            {
                var center = from.Center;
                var width = from.Size.X * from.Scale.X;
                var destination = field.RestPosition + field.Size / 2;
                // The opponent exposes a card from its hidden hand before the same landing.
                // This reveal is separate from the defense-dependent impact profile.
                if (enemy)
                {
                    var reveal = destination + new Vector2(-field.Size.X * .65f, -field.Size.Y * .12f);
                    var start = center;
                    var hiddenWidth = width;
                    var revealWidth = Math.Max(width, field.Size.X * 1.5f);
                    var revealed = false;
                    await Track(.30, p =>
                    {
                        if (!revealed && p >= .5f)
                        {
                            paper.Bind(card, BattleCardMode.Inspect, _textures);
                            revealed = true;
                            PhaseChanged?.Invoke("deployment-reveal");
                        }
                        var q = Ease(p);
                        center = start.Lerp(reveal, q);
                        width = Mathf.Lerp(hiddenWidth, revealWidth, q);
                        paper.Position = center - paper.Size / 2;
                        var scale = width / paper.Size.X;
                        paper.Scale = new Vector2(Math.Max(.025f, MathF.Abs(MathF.Cos(p * MathF.PI))) * scale, scale);
                        paper.Rotation = Mathf.Lerp(from.Rotation, 0, q);
                        paper.PaperShear = MathF.Sin(p * MathF.PI * 2) * .08f;
                        paper.DeploymentElevation = .65f;
                        paper.QueueRedraw();
                    }, epoch);
                    paper.PaperShear = 0;
                }

                var releaseCenter = center;
                var releaseWidth = width;
                if (style.LiftSeconds > 0)
                {
                    PhaseChanged?.Invoke("deployment-lift");
                    await Track(style.LiftSeconds, p =>
                    {
                        var q = 1 - MathF.Pow(1 - p, 3);
                        center = releaseCenter + new Vector2(0, -style.Lift * q);
                        width = releaseWidth * Mathf.Lerp(1, style.LiftScale, q);
                        SetPaperPose(paper, center, width, Mathf.Lerp(from.Rotation, 0, q), q);
                    }, epoch);
                }
                if (style.HoldSeconds > 0)
                {
                    PhaseChanged?.Invoke("deployment-hold");
                    var peakCenter = center;
                    var peakWidth = width;
                    await Track(style.HoldSeconds, p =>
                    {
                        // The heavy sample gently relaxes from its enlarged peak before snapping down.
                        center = peakCenter.Lerp(releaseCenter + new Vector2(0, -style.Lift * .72f), Ease(p));
                        width = Mathf.Lerp(peakWidth, releaseWidth * (1 + (style.LiftScale - 1) * .78f), Ease(p));
                        SetPaperPose(paper, center, width, 0, style.LiftSeconds > 0 ? 1 : .55f);
                    }, epoch);
                }

                var collapseCenter = center;
                var collapseWidth = width;
                var rotation = paper.Rotation;
                var compact = false;
                PhaseChanged?.Invoke("deployment-collapse");
                await Track(style.CollapseSeconds, p =>
                {
                    // Light units become field cards at the start of the gentle resize;
                    // medium/heavy units keep their large face until the terminal snap.
                    if (!compact && (SlamTier(card.EffectiveDefense ?? card.Definition.BaseDefense) == 0 || p >= .98f))
                    {
                        paper.Bind(card, BattleCardMode.Field, _textures);
                        paper.Size = field.Size; paper.PivotOffset = paper.Size / 2;
                        compact = true;
                        PhaseChanged?.Invoke("deployment-field-form");
                    }
                    var q = Ease(p);
                    SetPaperPose(paper, collapseCenter.Lerp(destination, q),
                        Mathf.Lerp(collapseWidth, field.Size.X, q), Mathf.Lerp(rotation, 0, q), 1 - q);
                }, epoch);
                paper.Position = field.RestPosition; paper.Scale = Vector2.One; paper.Rotation = 0;
                paper.DeploymentElevation = 0; paper.QueueRedraw();
                PhaseChanged?.Invoke("deployment-landed");
                await ImpactAsync(paper, card, style, epoch);
            }
            completed = true;
        }
        finally
        {
            if (GodotObject.IsInstanceValid(paper) && paper.GetParent() == this)
            { RemoveChild(paper); paper.QueueFree(); }
            // A cancelled historical projection must never reveal a discarded field node.
            if (epoch == _epoch && GodotObject.IsInstanceValid(field))
            {
                field.Visible = true;
                if (completed) PhaseChanged?.Invoke("deployment-settled");
            }
        }
    }

    private static void SetPaperPose(BattleCard paper, Vector2 center, float width, float rotation, float elevation)
    {
        paper.Position = center - paper.Size / 2;
        paper.Scale = Vector2.One * (width / paper.Size.X);
        paper.Rotation = rotation;
        paper.DeploymentElevation = elevation;
        paper.QueueRedraw();
    }

    /// <summary>Frame capture and live playback use the same pose evaluator.</summary>
    private async Task Track(double seconds, Action<float> step, int epoch)
    {
        step(0);
        if (StepFrames > 0)
        {
            var frames = Math.Max(1, (int)Math.Round(seconds * 60));
            for (var i = 1; i <= frames; i++)
            {
                if (epoch != _epoch || !IsInsideTree()) throw new OperationCanceledException();
                step(i / (float)frames);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
        }
        else
        {
            var tween = CreateTween(); _tweens.Add(tween);
            tween.TweenMethod(Callable.From<float>(step), 0f, 1f, Time(seconds));
            try { await Wait(seconds, epoch); step(1); }
            finally
            {
                if (GodotObject.IsInstanceValid(tween)) tween.Kill();
                _tweens.Remove(tween);
            }
        }
    }

    /// <summary>User supplied body boundaries: defense 1–3 / 4–6 / 7+.</summary>
    public static int SlamTier(int? defense) => (defense ?? 1) < 4 ? 0 : defense < 7 ? 1 : 2;

    /// <summary>
    /// Visible phases in the original-speed 30fps reference 8月9日(1).mp4:
    /// defense 3 resizes over 8 frames; defense 6 holds its exposed face for 13 frames
    /// then resizes/slides over 11; defense 10 enlarges about 18%, relaxes, and snaps
    /// compact over 2 frames. Effect strengths below are scaled to our 1280×720 board.
    /// The medium sample is an opponent reveal; its own-side anticipation is an approximation.
    /// </summary>
    public sealed record SlamProfile(double LiftSeconds, double HoldSeconds, double CollapseSeconds,
        float LiftScale, float Lift, double SettleSeconds, double DustDelay, float Dust,
        float Shake, float Gain)
    {
        public double AnticipationSeconds => LiftSeconds + HoldSeconds;
        public double Seconds => AnticipationSeconds + CollapseSeconds + SettleSeconds;
    }

    /// <summary>Defense is the sole input for weight, timing, dust and table shake.</summary>
    public static SlamProfile SlamStyle(int? defense) => SlamTier(defense) switch
    {
        0 => new(0, 0, 8 / 30.0, 1, 0, .70, .27, 28, 0, .45f),
        1 => new(0, 13 / 30.0, 11 / 30.0, 1, 0, .80, .30, 52, 4.5f, .75f),
        _ => new(15 / 30.0, 8 / 30.0, 2 / 30.0, 1.18f, 9, 1.0, .30, 90, 12.5f, 1),
    };

    public static double SlamSeconds(int? defense) => SlamStyle(defense).Seconds;

    /// <summary>A field card that has already landed receives only impact feedback.</summary>
    public async Task SlamAsync(BattleCard card, UiCardView view)
    {
        var style = SlamStyle(view.EffectiveDefense ?? view.Definition.BaseDefense);
        card.Position = card.RestPosition;
        if (_clock.ReducedMotion) { _sfx.Play("deploy", view, style.Gain); await Wait(.12, _epoch); return; }
        await ImpactAsync(card, view, style, _epoch);
    }

    private async Task ImpactAsync(BattleCard card, UiCardView view, SlamProfile style, int epoch)
    {
        PhaseChanged?.Invoke("deployment-slam-" + SlamTier(view.EffectiveDefense ?? view.Definition.BaseDefense));
        _sfx.Play("deploy", view, style.Gain);
        SlamDust? dust = null;
        try
        {
            if (style.DustDelay > 0) await Wait(style.DustDelay, epoch);
            dust = new SlamDust { Size = Size, Center = card.Position + card.Size / 2,
                Footprint = card.Size, Diameter = style.Dust, Tier = SlamTier(view.EffectiveDefense ?? view.Definition.BaseDefense),
                Duration = Time(.50), ManualAge = true, ZIndex = -1 };
            AddChild(dust);
            await Track(style.SettleSeconds - style.DustDelay,
                p => dust.Age = p * Time(style.SettleSeconds - style.DustDelay), epoch);
        }
        finally
        {
            if (dust is not null && GodotObject.IsInstanceValid(dust) && dust.GetParent() == this)
            { RemoveChild(dust); dust.QueueFree(); }
        }
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
    public async Task StatusAsync(BattleCard card, UiStatusPresentation change, UiCardView? visibleAfter = null)
    {
        var epoch = _epoch;
        card.UpdateView(visibleAfter ?? change.After);
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
    public Vector2 Footprint { get; set; } = new(92, 129);
    public float Diameter { get; set; }
    public int Tier { get; set; }
    public double Duration { get; set; }
    public bool ManualAge { get; set; }
    private double _age;
    /// <summary>Lets a step-driven caller (capture, replay) set the fade without waiting on the frame clock.</summary>
    public double Age { set { _age = value; QueueRedraw(); } }
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; QueueRedraw(); }
    public override void _Process(double delta)
    {
        if (ManualAge) return;
        _age += delta;
        if (_age >= Duration) { GetParent()?.RemoveChild(this); QueueFree(); return; }
        QueueRedraw();
    }
    public override void _Draw()
    {
        var progress = Math.Clamp((float)(_age / Duration), 0f, 1f);
        var fade = MathF.Sin(progress * MathF.PI) * (1 - progress);
        // Uneven diffuse brown/gray clouds around the footprint, with no solid impact disc.
        for (var i = 0; i < 17; i++)
        {
            var angle = i * 2.399963f;
            var variation = .65f + .35f * MathF.Sin(i * 7.31f + .8f);
            var radius = Diameter * (.09f + progress * .18f) * variation;
            var offset = new Vector2(MathF.Cos(angle) * (Footprint.X * .48f + progress * Diameter * .53f),
                MathF.Sin(angle) * (Footprint.Y * .47f + progress * Diameter * .34f));
            var point = Center + offset;
            for (var layer = 5; layer > 0; layer--)
                DrawCircle(point, radius * layer / 3f,
                    new Color(.56f, .49f, .36f, fade * (Tier == 0 ? .023f : .033f)));
        }
        if (Tier < 2) return;
        // The heavy sample adds short orange flecks outside the card, delayed after landing.
        for (var i = 0; i < 22; i++)
        {
            var angle = i * 2.399963f + .3f;
            var variation = .65f + .35f * MathF.Sin(i * 4.71f);
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var point = Center + direction * Footprint * .48f + direction * Diameter * (.12f + progress * .8f) * variation;
            var alpha = fade * (.50f + variation * .4f);
            DrawLine(point, point - direction * (2 + variation * 3), new Color(.93f, .48f, .17f, alpha), 1.2f, true);
        }
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
