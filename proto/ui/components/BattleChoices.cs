using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>Engine-supplied choices presented as paper on the table.</summary>
public partial class BattleChoices : Control
{
    private TextureCache _textures = null!;
    private AnimClock _clock = null!;
    private readonly List<(UiChoice Choice, BattleCard Paper)> _cards = [];
    private readonly List<Tween> _motions = [];
    private int _epoch;
    private bool _accepting;
    private ChoiceSelection? _selection;
    private UiChoiceSlot? _slot;
    private UiCardView? _source;
    private DeploymentPose? _origin;
    private int _page;
    private Label? _selectionCount;
    private readonly Dictionary<string, MulliganStamp> _marks = new();
    public Button? NextPageButton { get; private set; }
    public IReadOnlyList<string> SelectedIds => _selection?.Selected ?? [];
    public event Action<IReadOnlyList<string>>? Confirmed;
    public event Action<string>? Chosen;
    public event Action? Cancelled;
    public IReadOnlyList<BattleCard> Papers => _cards.Select(c => c.Paper).ToArray();
    public bool IsEntering => _accepting && _motions.Any(t => GodotObject.IsInstanceValid(t) && t.IsRunning());
    public int StepFrames { get; set; }
    public void Initialize(TextureCache textures, AnimClock clock)
    { _textures = textures; _clock = clock; Size = new(1280, 720); MouseFilter = MouseFilterEnum.Stop; Visible = false; }

    public void Open(UiChoiceSlot slot, UiCardView? source, DeploymentPose? origin, IReadOnlyList<string>? selected = null)
    {
        Reset(); Visible = true; _accepting = true;
        _slot = slot; _source = source; _origin = origin; _selection = new(slot);
        if (selected is not null) foreach (var id in selected) _selection.Toggle(id);
        RenderPage();

    }

    private void RenderPage()
    {
        NextPageButton = null; _selectionCount = null;
        _marks.Clear();
        StopMotions(); _cards.Clear();
        foreach (var child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
        var slot = _slot!; var source = _source; var origin = _origin;
        var pageChoices = slot.Allowed.Skip(_page * 5).Take(5).ToArray();
        // The board remains visible. Clicking its empty space cancels the pending play.
        AddChild(new ColorRect { Size = Size, Color = new(0, 0, 0, .12f), MouseFilter = MouseFilterEnum.Ignore });
        var count = pageChoices.Length;
        var width = Math.Min(180f, 960f / Math.Max(1, count) - 20);
        var size = new Vector2(width, width * 1.4f);
        foreach (var (choice, index) in pageChoices.Select((c, i) => (c, i)))
        {
            var face = choice.Card ?? (source is null || choice.Kind != ChoiceKind.Option
                ? new UiCardView { Uid = "choice-" + choice.Id, Definition = new() { CardId = "choice-" + choice.Id,
                    Name = "候选卡牌", CardType = "order", Text = choice.Label } }
                : source with { Uid = "choice-" + choice.Id });
            // Hidden candidates never acquire identity through a choice.
            var paper = new BattleCard { Size = size, PivotOffset = size / 2, MouseFilter = MouseFilterEnum.Stop,
                ChoiceCaption = choice.Kind == ChoiceKind.Option ? choice.Label : null, ZIndex = index + 1,
                TooltipText = _selection!.RequiresConfirmation ? "选满自动提交；再次点击取消选择；右键取消" : "点击选择；点击空白或右键取消" };
            paper.Bind(face, face.IsHq ? BattleCardMode.Hq : BattleCardMode.Inspect, _textures);
            var hidden = choice.Kind == ChoiceKind.Reference && choice.Card is null;
            if (hidden) paper.Bind(null, BattleCardMode.Hidden, _textures);
            var destination = new Vector2(640 + (index - (count - 1) / 2f) * (width + 18), 356);
            paper.RestPosition = destination - size / 2; AddChild(paper); _cards.Add((choice, paper));
            if (_selection!.RequiresConfirmation)
            {
                var mark = new MulliganStamp { Size = size, PivotOffset = size / 2, Rotation = -.10f,
                    MouseFilter = MouseFilterEnum.Ignore };
                mark.SetMarked(_selection.Contains(choice.Id));
                paper.AddChild(mark); _marks[choice.Id] = mark;
            }
            paper.Pressed += (_, _) =>
            {
                if (!_accepting) return;
                if (!_selection!.RequiresConfirmation)
                {
                    if (!_selection.Toggle(choice.Id)) return;
                    UpdateSelection(); Chosen?.Invoke(choice.Id); return;
                }
                if (!_selection.Toggle(choice.Id)) return;
                UpdateSelection();
                if (_selection.Selected.Count == slot.Max && _selection.CanConfirm)
                    Confirmed?.Invoke(_selection.Selected);
            };
            paper.Hovered += (_, on) =>
            { if (_accepting) { paper.Targeted = on; paper.ZIndex = on ? 100 : index + 1; paper.QueueRedraw(); } };
            if (_clock.ReducedMotion) paper.Position = paper.RestPosition;
            else
            {
                var from = origin?.Center ?? new Vector2(1170, 625);
                var reveal = !hidden && choice.Card is not null && choice.Kind != ChoiceKind.Option;
                if (reveal) paper.Bind(null, BattleCardMode.Hidden, _textures);
                paper.Position = from - size / 2;
                var motion = CreateTween(); _motions.Add(motion);
                motion.TweenInterval(index * .06 * _clock.Scale);
                motion.TweenMethod(Callable.From<float>(p =>
                {
                    if (!GodotObject.IsInstanceValid(paper)) return;
                    var q = p * p * (3 - 2 * p);
                    paper.Position = from.Lerp(destination, q) - size / 2;
                    paper.Rotation = -.12f * (1 - q);
                    paper.Scale = reveal ? new(Math.Max(.025f, MathF.Abs(MathF.Cos(p * MathF.PI))), 1) : Vector2.One;
                    if (reveal && p >= .5f && paper.Mode == BattleCardMode.Hidden)
                        paper.Bind(face, face.IsHq ? BattleCardMode.Hq : BattleCardMode.Inspect, _textures);
                }), 0f, 1f, Math.Max(.03, .32 * _clock.Scale));
            }
        }
        var footer = new Panel { Position = new(330, 520), Size = new(620, 58), MouseFilter = MouseFilterEnum.Ignore };
        footer.AddThemeStyleboxOverride("panel", UiStyles.Box(new Color(.06f, .07f, .06f, .92f), new Color("79705b"), 5));
        AddChild(footer);
        if (slot.Allowed.Count > 5)
        {
            var pages = (slot.Allowed.Count + 4) / 5;
            var previous = UiStyles.Button("上一页", () => { if (_accepting && _page > 0) { _page--; RenderPage(); } });
            previous.Position = new(360, 532); previous.Size = new(110, 34); previous.Disabled = _page == 0; AddChild(previous);
            NextPageButton = UiStyles.Button("下一页", () => { if (_accepting && _page + 1 < pages) { _page++; RenderPage(); } });
            NextPageButton.Position = new(810, 532); NextPageButton.Size = new(110, 34);
            NextPageButton.Disabled = _page + 1 == pages; AddChild(NextPageButton);
        }
        _selectionCount = UiStyles.Label(""); _selectionCount.Position = new(600, 535);
        _selectionCount.Size = new(80, 28); _selectionCount.HorizontalAlignment = HorizontalAlignment.Center;
        AddChild(_selectionCount);
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        foreach (var (choice, paper) in _cards)
        {
            if (_marks.TryGetValue(choice.Id, out var mark)) mark.SetMarked(_selection!.Contains(choice.Id));
            paper.QueueRedraw();
        }
        if (_selectionCount is not null) _selectionCount.Text = $"{_selection!.Selected.Count}/{_slot!.Max}";
    }

    public async Task<DeploymentPose?> FinishAsync(string? chosenId, UiCardView? source, DeploymentPose? returnPose, bool commitMany = false)
    {
        _accepting = false; var epoch = _epoch;
        foreach (var mark in _marks.Values) mark.Visible = false;
        StopMotions();
        var poses = _cards.Select(c => DeploymentPose.From(c.Paper)).ToArray();
        var duration = _clock.ReducedMotion ? .08 : .26;
        var selected = _cards.FindIndex(c => c.Choice.Id == chosenId);
        if (selected >= 0 && (_cards[selected].Choice.Card ?? source) is { } chosenFace)
            _cards[selected].Paper.Bind(chosenFace, chosenFace.IsHq ? BattleCardMode.Hq : BattleCardMode.Inspect, _textures);
        var returned = source is not null && selected < 0 ? _cards.FirstOrDefault().Paper : null;
        if (commitMany && returned is null && source is not null)
        {
            var paper = new BattleCard { Size = new(180, 252), PivotOffset = new(90, 126), Position = new(550, 230) };
            AddChild(paper); _cards.Add((new UiChoice("source", "", ChoiceKind.Option), paper));
            returned = paper; poses = _cards.Select(c => DeploymentPose.From(c.Paper)).ToArray();
        }
        if (returned is not null) { returned.Bind(source, BattleCardMode.Hand, _textures); returned.ChoiceCaption = null; }
        void Step(float p)
        {
            var q = p * p * (3 - 2 * p);
            for (var i = 0; i < _cards.Count; i++)
            {
                var paper = _cards[i].Paper; var pose = poses[i];
                if (i == selected)
                {
                    paper.Position = pose.Center.Lerp(new(420, 456), q) - paper.Size / 2;
                    paper.Rotation = pose.Rotation * (1 - q); paper.Scale = Vector2.One;
                }
                else if (paper == returned && (returnPose is not null || commitMany))
                {
                    var rest = commitMany ? new DeploymentPose(new Vector2(420, 456) - paper.Size / 2, paper.Size, Vector2.One, 0) : returnPose!.Value;
                    paper.Position = pose.Center.Lerp(rest.Center, q) - paper.Size / 2;
                    paper.Rotation = Mathf.Lerp(pose.Rotation, rest.Rotation, q);
                    paper.Scale = Vector2.One * Mathf.Lerp(pose.Size.X * pose.Scale.X / paper.Size.X,
                        rest.Size.X * rest.Scale.X / paper.Size.X, q);
                }
                else
                {
                    paper.Position = pose.Position + new Vector2(-260 * q, -35 * q);
                    paper.Scale = new(Math.Max(.025f, MathF.Abs(MathF.Cos(p * MathF.PI))), 1);
                    if (p >= .5f && paper.Mode != BattleCardMode.Hidden) paper.Bind(null, BattleCardMode.Hidden, _textures);
                    paper.Modulate = new(1, 1, 1, 1 - q);
                }
            }
        }
        if (StepFrames > 0)
        {
            var frames = (int)Math.Max(1, Math.Round(duration * 60));
            for (var i = 0; i <= frames; i++)
            {
                if (epoch != _epoch) return null;
                Step((float)i / frames);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
        }
        else
        {
            var motion = CreateTween(); _motions.Add(motion);
            motion.TweenMethod(Callable.From<float>(Step), 0f, 1f, Math.Max(.03, duration * _clock.Scale));
            await ToSignal(GetTree().CreateTimer(Math.Max(.03, duration * _clock.Scale)), SceneTreeTimer.SignalName.Timeout);
        }
        if (epoch != _epoch) return null;
        Step(1);
        if (selected < 0) return commitMany && returned is not null ? DeploymentPose.From(returned) : null;
        return DeploymentPose.From(_cards[selected].Paper);
    }

    private void StopMotions()
    { foreach (var tween in _motions) if (GodotObject.IsInstanceValid(tween)) tween.Kill(); _motions.Clear(); }
    public void Reset()
    {
        _epoch++; _selection = null; _slot = null; _page = 0; NextPageButton = null; _selectionCount = null;
        _marks.Clear(); StopMotions(); _accepting = false; Visible = false; _cards.Clear();
        foreach (var child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
    }
    public override void _GuiInput(InputEvent e)
    {
        if (_accepting && e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        { Cancelled?.Invoke(); AcceptEvent(); }
    }
}
