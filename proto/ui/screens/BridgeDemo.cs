using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BridgeDemo : VBoxContainer
{
    private MockFeed _feed = new();
    private SegmentPlayer _player = null!;
    private AnimationEngine _fx = null!;
    private RichTextLabel _log = null!;
    private TargetInteraction _targets = null!;
    private VBoxContainer _choices = null!;
    private Label _hint = null!;
    private long _sequence;
    private Control _deck = null!, _hand = null!;
    private UiCardDefinition _demoCard = null!;
    private readonly Dictionary<string, List<string>> _selected = new();
    public void Initialize(AnimationEngine fx, CardCatalog catalog, TextureCache textures)
    {
        _fx = fx;
        _player = new("demo", _feed);
        _targets = new(() => new[] { "unit-1", "unit-2", "disallowed-unit" });
        AddChild(UiStyles.Label("事件与目标交互", 26));
        AddChild(UiStyles.Label("用示例事件观察顺序播放，再尝试多槽位选择、拒绝重试和取消。", 14, UiStyles.Dim));
        var buttons = new HBoxContainer();
        AddChild(buttons);
        buttons.AddChild(UiStyles.Button("播放示例事件段", () => Enqueue()));
        buttons.AddChild(UiStyles.Button("开始多槽位选择", StartChoice));
        buttons.AddChild(UiStyles.Button("清空日志", () => _log.Clear()));
        var anchors = new HBoxContainer();
        AddChild(anchors);
        _deck = new PanelContainer { CustomMinimumSize = new(150, 70) };
        _deck.AddChild(UiStyles.Label("牌库", 20, UiStyles.Gold));
        anchors.AddChild(_deck);
        _hand = new PanelContainer { CustomMinimumSize = new(350, 70) };
        _hand.AddChild(UiStyles.Label("手牌", 20, UiStyles.Gold));
        anchors.AddChild(_hand);
        _demoCard = catalog.Cards.First(c => c.CardType == "order" && c.ArtPath.Length > 0);
        _hint = UiStyles.Label("尚无待处理请求", 14, UiStyles.Gold);
        AddChild(_hint);
        _choices = new VBoxContainer();
        AddChild(_choices);
        _log = new RichTextLabel { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 200) };
        AddChild(_log);
        _targets.Changed += RenderChoices;
    }
    public override async void _Process(double delta)
    {
        if (_player is null)
            return;
        foreach (var e in _player.TakeImmediate())
            _log.AddText($"即时提示：{e.Text}\n");
        try
        {
            await _player.DrainAsync(async (e, token) =>
        {
            token.ThrowIfCancellationRequested();
            _log.AddText($"段 {_player.NextSequence} / {e.Kind}：{e.Text}\n");
            if (e.Kind == "kredit")
                await _fx.KreditFloat(_deck, $"+{e.Value}");
            else if (e.Kind == "draw")
                await _fx.DrawToHand(_deck, _hand, _demoCard);
            else
                await _fx.Sleep(_fx.Clock.Ms(120), token);
        });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { GD.PushError(e.ToString()); }
    }
    private void Enqueue()
    {
        var entries = new[] { new UiEvent("kredit", Value: 3, Text: "指挥点 +3"), new UiEvent("draw", Text: "抽牌"), new UiEvent("action-end", Text: "动作结束") };
        _feed.Enqueue(new("demo", ++_sequence, DateTimeOffset.UtcNow, entries, [new(entries, [])]));
        _feed.EmitImmediate(new("draw", Text: "这条演出不从即时口重复播放"));
        _feed.EmitImmediate(new("interaction-hint", Text: "事件已入队"));
    }
    private void StartChoice()
    {
        if (_targets.Active is not null)
            return;
        var req = new UiTargetRequest(Guid.NewGuid().ToString(), [
            new("单位",ChoiceKind.Reference,1,2,[new("unit-1","参照单位一",ChoiceKind.Reference),new("unit-2","参照单位二",ChoiceKind.Reference)]),
            new("行动",ChoiceKind.Option,1,1,[new("advance","推进",ChoiceKind.Option),new("hold","等待",ChoiceKind.Option)])]);
        _selected.Clear();
        _targets.BeginInteraction(req, new MockTargetResponder(req, true));
        _log.AddText("BeginInteraction 已返回；首次有效提交将被 mock 拒绝，请重试。\n");
    }
    private void RenderChoices()
    {
        UiStyles.Clear(_choices);
        _hint.Text = _targets.Hint;
        var request = _targets.Active;
        if (request is null)
            return;
        foreach (var slot in request.Slots)
        {
            _choices.AddChild(UiStyles.Label($"{slot.Name} · 选择 {slot.Min}–{slot.Max} 项", 14));
            if (!_selected.ContainsKey(slot.Name))
                _selected[slot.Name] = [];
            var row = new HBoxContainer();
            _choices.AddChild(row);
            foreach (var choice in slot.Allowed)
            {
                var b = new CheckButton { Text = choice.Label, ButtonPressed = _selected[slot.Name].Contains(choice.Id) };
                b.Toggled += on => { var list = _selected[slot.Name]; if (on) { if (slot.Max == 1) list.Clear(); list.Add(choice.Id); } else list.Remove(choice.Id); CallDeferred(MethodName.RenderDeferred); };
                row.AddChild(b);
            }
        }
        var actions = new HBoxContainer();
        _choices.AddChild(actions);
        actions.AddChild(UiStyles.Button("提交", () => _targets.Submit(request.RequestId, _selected.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.ToArray()))));
        actions.AddChild(UiStyles.Button("取消", () => _targets.Cancel(request.RequestId)));
    }
    private void RenderDeferred() => RenderChoices();
    public override void _ExitTree()
    {
        _player?.Dispose();
    }
}
