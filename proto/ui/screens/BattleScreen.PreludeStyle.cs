using Godot;

namespace Kards.Ui;

public partial class BattleScreen
{
    private Label _mulliganNote = null!;
    private Control _mulliganInstructionPlate = null!;
    private readonly Dictionary<string, Tween> _mulliganHoverMotion = new();

    private ColorRect PreludeShade()
    {
        var shader = new Shader { Code = """
            shader_type canvas_item;
            void fragment() {
                vec2 p = (UV - .5) * vec2(1.06, 1.22);
                float edge = smoothstep(.12, .70, length(p));
                float grain = fract(sin(dot(floor(UV * vec2(1280.,720.)),vec2(12.9898,78.233))) * 43758.5453);
                COLOR = vec4(vec3(.027,.034,.027) + (grain-.5)*.018, .28 + edge * .49);
            }
            """ };
        return new ColorRect { Size = BoardSize, Material = new ShaderMaterial { Shader = shader }, MouseFilter = MouseFilterEnum.Stop };
    }

    private static void StylePreludeButton(Button button, bool quiet = false)
    {
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
        {
            var fill = quiet ? new Color("20271f") : new Color(state == "hover" ? "cb9d53" : state == "pressed" ? "987037" : "b28a48");
            var box = UiStyles.Box(fill, new(state == "hover" ? "eddaab" : "8f825f"), 1, 10);
            box.ShadowColor = new(0, 0, 0, .4f); box.ShadowSize = 5; box.ShadowOffset = new(0, 3);
            button.AddThemeStyleboxOverride(state, box);
        }
        button.AddThemeColorOverride("font_color", new(quiet ? "d1c9ae" : "201f17"));
        button.AddThemeColorOverride("font_hover_color", new(quiet ? "eee5cc" : "201f17"));
        button.AddThemeColorOverride("font_pressed_color", new(quiet ? "eee5cc" : "201f17"));
        button.AddThemeFontSizeOverride("font_size", quiet ? 14 : 19);
    }

    private void RefreshMulliganCaption()
    {
        var count = _mulliganMarks.Values.Count(s => s.Marked);
        _mulliganConfirm.Text = count == 0 ? "保留全部" : $"确认换牌 · {count} 张";
        _mulliganNote.Text = count == 0 ? "点击卡牌标记替换，再次点击取消" : $"已选择 {count} 张 · 其余卡牌将保留";
    }

    private void HoverMulliganPaper(string uid, Control cell, BattleCard card, Vector2 rest, bool over)
    {
        if (!_mulliganOpen || OpeningAnimationActive) return;
        if (_mulliganHoverMotion.Remove(uid, out var old)) old.Kill();
        if (_clock.ReducedMotion) return;
        var tween = cell.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(cell, "position", rest + new Vector2(0, over ? -7 : 0), .16 * _clock.Scale);
        tween.TweenMethod(Callable.From<float>(p => { card.DeploymentElevation = p; card.QueueRedraw(); }), card.DeploymentElevation, over ? .42f : 0f, .16 * _clock.Scale);
        _mulliganHoverMotion[uid] = tween;
        tween.Finished += () => { if (_mulliganHoverMotion.GetValueOrDefault(uid) == tween) _mulliganHoverMotion.Remove(uid); };
    }

    private void StopMulliganHover()
    {
        foreach (var tween in _mulliganHoverMotion.Values) if (GodotObject.IsInstanceValid(tween)) tween.Kill();
        _mulliganHoverMotion.Clear();
    }

    // Animate around the paper's centre; callers provide the final top-left at its native pivot.
    // A curve, a brief lift and a settling contact replace independent linear position/scale tracks.
    private void PreludePaperFlight(Tween timeline, Control paper, BattleCard face, Vector2 target,
        Vector2 endScale, float endRotation, double seconds, double delay = 0, bool parallel = true,
        bool reveal = false, Orc.UI.Contracts.UiCardView? definition = null, float lift = 70, float turn = .09f, bool fadeIn = false, bool retire = false, bool accelerate = false)
    {
        var start = paper.Position; var scale = paper.Scale; var rotation = paper.Rotation;
        var c1 = start.Lerp(target, .24f) + new Vector2(0, -lift);
        var c2 = start.Lerp(target, .72f) + new Vector2(0, -lift * .45f);
        var launched = false; var revealed = !reveal; var landed = false;
        var track = parallel ? timeline.Parallel() : timeline;
        track.TweenMethod(Callable.From<float>(p =>
        {
            if (!launched) { launched = true; _sfx.Play("shuffle", gain: .5f); }
            var u = accelerate ? p * p * p : p * p * (3 - 2 * p);
            var arc = MathF.Sin(p * MathF.PI);
            paper.Position = start.BezierInterpolate(c1, c2, target, u);
            paper.Rotation = Mathf.Lerp(rotation, endRotation, u) + turn * arc;
            var size = scale.Lerp(endScale, u) * (1 + .025f * arc);
            if (reveal)
            {
                var flip = Math.Clamp((p - .24f) / .56f, 0, 1);
                size.X *= Math.Max(.025f, MathF.Abs(MathF.Cos(flip * MathF.PI)));
                if (flip >= .5f && !revealed) { revealed = true; face.Bind(definition, BattleCardMode.Inspect, _textures); }
            }
            paper.Scale = size; face.DeploymentElevation = arc * .75f; face.QueueRedraw();
            paper.Modulate = Colors.White with { A = retire ? Math.Clamp((1 - p) / .22f, 0, 1) : fadeIn ? Math.Clamp(p / .09f, 0, 1) : 1 };
            if (p >= .93f && !landed) { landed = true; _sfx.Play("ui", gain: .16f); }
        }), 0f, 1f, seconds * _clock.Scale).SetDelay(delay * _clock.Scale);
    }
}

/// <summary>Printed name slip: public player identity, without inventing portraits or rank.</summary>
public partial class PreludeNameSlip : Control
{
    public override void _Draw()
    {
        DrawRect(new Rect2(4, 6, Size.X, Size.Y), new(0, 0, 0, .3f));
        DrawRect(new Rect2(Vector2.Zero, Size), new("d3c9a7"));
        DrawRect(new Rect2(0, 0, 5, Size.Y), new("9d713b"));
        DrawLine(new(15, 24), new(Size.X - 15, 24), new("9d967c"), 1);
        for (var i = 0; i < 4; i++) DrawLine(new(7 + i * 4, 4), new(4, 7 + i * 4), new Color(.25f, .25f, .19f, .1f), 1);
    }
}

public partial class PreludeDivider : Control
{
    public override void _Draw()
    {
        var ink = new Color("c4ad78");
        DrawLine(new(0, 12), new(72, 12), ink with { A = .55f }, 1);
        DrawLine(new(114, 12), new(186, 12), ink with { A = .55f }, 1);
        var c = new Vector2(93, 12);
        DrawColoredPolygon([c + new Vector2(0, -5), c + new Vector2(5, 0), c + new Vector2(0, 5), c + new Vector2(-5, 0)], ink);
    }
}

public partial class PreludeInstructionPlate : Control
{
    public override void _Draw()
    {
        // The board remains visible around the instruction strip; HQ lettering cannot bleed into it.
        for (var i = 0; i < 32; i++)
        {
            var opacity = Math.Min(1, Math.Min(i / 5f, (31 - i) / 5f));
            DrawRect(new Rect2(i * Size.X / 32, 0, Size.X / 32 + 1, Size.Y), new Color(.065f, .082f, .065f, opacity * .96f));
        }
        DrawLine(new(112, 0), new(Size.X - 112, 0), new Color(.71f, .60f, .37f, .42f), 1);
        DrawLine(new(112, Size.Y), new(Size.X - 112, Size.Y), new Color(.71f, .60f, .37f, .25f), 1);
    }
}
