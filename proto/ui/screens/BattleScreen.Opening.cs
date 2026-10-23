using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    private Tween? _openingMotion;
    private Control? _openingIdentity;
    private Button? _openingSkip;
    private string? _openingMatchId;
    private bool _openingHqHidden;
    private readonly List<(BattleCard Card, UiCardView Face, Vector2 Position)> _openingCards = new();
    public bool OpeningAnimationActive => _openingMotion is not null;

    // A visual prelude to an actual mulligan request. No deck reads, commands or inferred first player.
    private void BeginOpening(UiMatchView view)
    {
        FinishOpening();
        if (_openingMatchId == view.MatchId) return; // reconnect/resume must not replay the introduction
        _openingMatchId = view.MatchId;
        if (_clock.ReducedMotion) return;

        _openingIdentity = new Control { Size = BoardSize, ZIndex = 10, MouseFilter = MouseFilterEnum.Ignore };
        _mulligan.AddChild(_openingIdentity);
        var heading = Text("作 战 准 备", new(490, 218), new(300, 26), 14, "bfae81");
        heading.HorizontalAlignment = HorizontalAlignment.Center; _openingIdentity.AddChild(heading);
        var title = Text("对 战", new(490, 262), new(300, 80), 48, "e7ddbc");
        title.AddThemeFontOverride("font", ResourceLoader.Load<FontFile>("res://proto/assets/cardface/fonts/SourceHanSansCN-Bold.otf"));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        _openingIdentity.AddChild(title);
        _openingIdentity.AddChild(new PreludeDivider { Position = new(547, 350), Size = new(186, 24), MouseFilter = MouseFilterEnum.Ignore });
        var sides = new List<Control>();
        foreach (var (hq, name, x, tilt) in new[] {
            (view.SelfHq, view.SelfPlayerName, 250f, -.09f),
            (view.EnemyHq, view.EnemyPlayerName, 826f, .09f) })
        {
            var identity = new Control { Position = new(x, 170), Size = new(204, 350), PivotOffset = new(102, 150), Rotation = tilt };
            var paper = new BattleCard { Size = new(204, 286), PivotOffset = new(102, 143), MouseFilter = MouseFilterEnum.Ignore };
            paper.Bind(hq, hq is null ? BattleCardMode.Hidden : BattleCardMode.Hq, _textures);
            identity.AddChild(paper);
            var slip = new PreludeNameSlip { Position = new(-26, 290), Size = new(256, 76), MouseFilter = MouseFilterEnum.Ignore };
            slip.AddChild(Text(x < 640 ? "我方指挥官" : "对方指挥官", new(16, 5), new(224, 19), 11, "6c6954"));
            var label = Text(name, new(16, 29), new(224, 34), 25, "30372b");
            label.ClipText = true; label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            slip.AddChild(label); identity.AddChild(slip);
            _openingIdentity.AddChild(identity); sides.Add(identity);
        }
        _openingSkip = new Button { Text = "跳过入场  ›", Position = new(1100, 650), Size = new(140, 38), ZIndex = 20 };
        StylePreludeButton(_openingSkip, quiet: true);
        _openingSkip.Pressed += FinishOpening; _mulligan.AddChild(_openingSkip);
        _mulliganConfirm.Disabled = true; _mulliganConfirm.Visible = false; _mulliganCaption.Visible = _mulliganNote.Visible = _mulliganInstructionPlate.Visible = false;
        _mulliganRow.Modulate = Colors.Transparent;

        var motion = CreateTween(); _openingMotion = motion;
        _openingHqHidden = true; HideOpeningHeadquarters();
        for (var i = 0; i < sides.Count; i++)
        {
            var panel = sides[i]; var destination = panel.Position;
            panel.Position += new Vector2(i == 0 ? -110 : 110, 32); panel.Modulate = Colors.Transparent;
            var step = i == 0 ? motion : motion.Parallel();
            step.TweenProperty(panel, "position", destination, .4 * _clock.Scale).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            motion.Parallel().TweenProperty(panel, "modulate:a", 1f, .28 * _clock.Scale);
        }
        motion.TweenInterval(.44 * _clock.Scale);
        motion.TweenProperty(_openingIdentity, "modulate:a", 0f, .2 * _clock.Scale);
        motion.TweenCallback(Callable.From(() => { _openingIdentity!.Visible = false; _mulliganRow.Modulate = Colors.White; RestoreOpeningHeadquarters(); }));

        var faces = view.SelfHand.OrderBy(c => c.SlotIndex).ToArray();
        var cells = _mulliganRow.GetChildren().OfType<Control>().ToArray();
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = cells[i]; var card = cell.GetChildren().OfType<BattleCard>().First();
            var target = card.Position;
            _openingCards.Add((card, faces[i], target));
            card.Bind(null, BattleCardMode.Hidden, _textures);
            card.Position = new Vector2(1210, 690) - _mulliganRow.Position - cell.Position - card.PivotOffset;
            card.Scale = Vector2.One * .62f; card.Rotation = .22f; card.Modulate = Colors.Transparent;
            PreludePaperFlight(motion, card, card, target, Vector2.One, 0, .66, i * .085,
                reveal: true, definition: faces[i], lift: 95 + i * 9, turn: -.08f, fadeIn: true);
        }
        motion.Finished += FinishOpening;
    }

    private void HideOpeningHeadquarters()
    {
        if (_openingHqHidden) foreach (var card in _cards.Values.Where(c => c.View?.IsHq == true)) card.Visible = false;
    }
    private void RestoreOpeningHeadquarters()
    {
        if (!_openingHqHidden) return;
        _openingHqHidden = false;
        foreach (var card in _cards.Values.Where(c => c.View?.IsHq == true)) card.Visible = true;
    }

    private void FinishOpening()
    {
        var motion = _openingMotion; _openingMotion = null;
        RestoreOpeningHeadquarters();
        if (motion is not null && GodotObject.IsInstanceValid(motion)) motion.Kill();
        foreach (var (card, face, position) in _openingCards)
        {
            if (!GodotObject.IsInstanceValid(card) || card.IsQueuedForDeletion()) continue;
            card.Bind(face, BattleCardMode.Inspect, _textures); card.Position = position;
            card.Rotation = 0; card.Scale = Vector2.One; card.Modulate = Colors.White;
            card.DeploymentElevation = 0; card.QueueRedraw();
        }
        _openingCards.Clear();
        foreach (var node in new Control?[] { _openingIdentity, _openingSkip })
            if (GodotObject.IsInstanceValid(node)) { node!.GetParent()?.RemoveChild(node); node.QueueFree(); }
        _openingIdentity = null; _openingSkip = null;
        if (GodotObject.IsInstanceValid(_mulliganRow)) _mulliganRow.Modulate = Colors.White;
        if (GodotObject.IsInstanceValid(_mulliganConfirm)) { _mulliganConfirm.Disabled = false; _mulliganConfirm.Visible = true; _mulliganCaption.Visible = _mulliganNote.Visible = _mulliganInstructionPlate.Visible = true; }
    }
}
