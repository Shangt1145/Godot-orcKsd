using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>Free positioning for animation QA; not a battle layout or rule implementation.</summary>
public partial class AnimationGallery : VBoxContainer
{
    private AnimationEngine _fx = null!;
    private TextureCache _textures = null!;
    private CardCatalog _catalog = null!;
    private Control _stage = null!, _pile = null!, _deck = null!, _hand = null!;
    private CardControl _source = null!, _target = null!;
    private Label _status = null!;
    private OptionButton _effect = null!, _kind = null!, _defense = null!;
    private Button _play = null!;
    private bool _busy;
    private int _runId;
    private static readonly string[] Effects = ["部署拍桌", "按兵种射击", "同线移动", "跨线弧线", "零位移", "死亡淡出", "抽牌飞入", "右侧滑入", "指令亮牌", "己方指令亮牌", "瞄准箭头", "反制埋设", "反制触发", "反制揭示", "反制挂起", "老兵升级", "洗入牌库", "情报扫描", "烧牌", "回合横幅", "指挥点飘字", "伤害演出", "增益演出", "治疗演出", "碎屑与冲击环"];
    public void Initialize(CardCatalog catalog, TextureCache textures, AnimationEngine fx)
    {
        _catalog = catalog;
        _textures = textures;
        _fx = fx;
        AddChild(UiStyles.Label("动画演示", 26));
        AddChild(UiStyles.Label("选择演出、兵种与防御值，再播放观察；速度和减弱动效可在设置中调整。", 14, UiStyles.Dim));
        var row = new HBoxContainer();
        AddChild(row);
        _effect = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var e in Effects)
            _effect.AddItem(e);
        row.AddChild(_effect);
        _kind = new OptionButton();
        foreach (var k in new[] { "infantry", "tank", "artillery", "fighter", "bomber", "cruiser", "landcruiser", "spacefighter" })
            _kind.AddItem(k);
        row.AddChild(_kind);
        _defense = new OptionButton();
        foreach (var d in new[] { 1, 4, 7 })
            _defense.AddItem($"防御 {d}", d);
        _defense.Select(1);
        row.AddChild(_defense);
        _play = UiStyles.Button("▶ 播放", () => _ = PlaySelectedAsync());
        row.AddChild(_play);
        row.AddChild(UiStyles.Button("打断 / 复位", Reset));
        _stage = new Control { CustomMinimumSize = new Vector2(900, 440), SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = false };
        AddChild(_stage);
        var bg = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        bg.AddThemeStyleboxOverride("panel", UiStyles.Box(new("181d22")));
        _stage.AddChild(bg);
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _status = UiStyles.Label("演示场景就绪", 14, UiStyles.Gold);
        AddChild(_status);
        BuildActors();
    }
    private void BuildActors()
    {
        foreach (var n in _stage.GetChildren().Skip(1).ToArray())
        {
            _stage.RemoveChild(n);
            n.QueueFree();
        }
        var baseCard = _catalog.Cards.First(c => c.CardType == "unit");
        var kind = _kind.GetItemText(_kind.Selected);
        var def = baseCard with
        {
            UnitType = kind
        };
        var view = new UiCardView { Uid = "source", Definition = def, EffectiveCost = def.Cost, EffectiveAttack = def.BaseAttack, EffectiveDefense = _defense.GetSelectedId(), EffectiveOpCost = def.BaseOpCost, CanAttack = true, CanBeTargeted = true, Zone = "support", Keywords = ["armor"], KeywordValues = new Dictionary<string, int> { { "armor", 3 } } };
        _source = new CardControl();
        _source.Bind(def, _textures, 140, view);
        _stage.AddChild(_source);
        _source.Position = new(210, 130);
        _target = new CardControl();
        _target.Bind(baseCard, _textures, 140, view with
        {
            Uid = "target",
            Definition = baseCard,
            OwnerSide = "enemy",
            CanAttack = false
        });
        _stage.AddChild(_target);
        _target.Position = new(650, 80);
        _target.SetState(false, true);
        _hand = CreateAnchor("手牌", new(35, 340), new(100, 56));
        _deck = CreateAnchor("牌库", new(45, 90), new(86, 80));
        _pile = CreateAnchor("反制", new(800, 340), new(86, 56));
        var l = UiStyles.Label("演出参照卡", 12, UiStyles.Dim);
        _stage.AddChild(l);
        l.Position = new(210, 105);
    }
    private Control CreateAnchor(string text, Vector2 pos, Vector2 size)
    {
        var p = new PanelContainer { Position = pos, Size = size };
        p.AddThemeStyleboxOverride("panel", UiStyles.Box(UiStyles.Panel2, UiStyles.Line));
        var label = UiStyles.Label(text, 14, UiStyles.Dim);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        p.AddChild(label);
        _stage.AddChild(p);
        return p;
    }
    public void Reset()
    {
        ++_runId;
        _fx.InterruptAll();
        BuildActors();
        _busy = false;
        _play.Disabled = false;
        _status.Text = "已复位";
    }
    private async Task PlaySelectedAsync()
    {
        if (_busy)
            return;
        var run = ++_runId;
        BuildActors();
        _busy = true;
        _play.Disabled = true;
        var selected = _effect.Selected;
        var ticks = Time.GetTicksMsec();
        _status.Text = $"正在播放：{Effects[selected]}";
        try
        {
            await PlayAsync(selected);
            if (run == _runId)
                _status.Text = $"{Effects[selected]} 完成 · {Time.GetTicksMsec() - ticks} ms";
        }
        catch (OperationCanceledException) { if (run == _runId) _status.Text = "演出已打断"; }
        catch (Exception e) { GD.PushError(e.ToString()); if (run == _runId) _status.Text = "演出失败，详情见日志"; }
        finally { if (run == _runId) { _busy = false; _play.Disabled = false; } }
    }
    public async Task PlayAsync(int index)
    {
        var order = _catalog.Cards.First(c => c.CardType == "order");
        switch (index)
        {
            case 0:
                await _fx.Slam(_source);
                break;
            case 1:
                await _fx.Lunge(_source, _target, () => _fx.Impact(_target, AnimationEngine.HitKindOf(AnimationEngine.KindOf(_source.View)), _source.View));
                break;
            case 2:
                await MoveAsync(false);
                break;
            case 3:
                await MoveAsync(true);
                break;
            case 4:
                await _fx.FlipByUid(Nodes(), () => Task.CompletedTask, Nodes);
                break;
            case 5:
                await _fx.Die(_source);
                break;
            case 6:
                await _fx.DrawToHand(_deck, _hand, order);
                break;
            case 7:
                await _fx.SlideInFromRight(_source, _stage);
                break;
            case 8:
                await _fx.PlayOpponentOrder(order);
                break;
            case 9:
                await _fx.PlayOrderCard(order, true);
                break;
            case 10:
                await _fx.AimShot(_source, _target);
                break;
            case 11:
                await _fx.CounterSet(_pile);
                break;
            case 12:
                await _fx.CounterFire(_pile);
                break;
            case 13:
                await _fx.CounterReveal(_pile, order);
                break;
            case 14:
                await _fx.HoldCounter(_source, _pile);
                break;
            case 15:
                await _fx.VeteranUp(_source);
                break;
            case 16:
                await _fx.DeckShuffle(_deck);
                break;
            case 17:
                await _fx.IntelScan(_stage);
                break;
            case 18:
                await _fx.BurnCard(_hand, _pile, order);
                break;
            case 19:
                await _fx.TurnBanner("你的回合", "指挥点已恢复");
                break;
            case 20:
                await _fx.KreditFloat(_deck, "+3");
                break;
            case 21:
                await _fx.OrderFx("fire", _stage);
                break;
            case 22:
                await _fx.OrderFx("buff", _stage);
                break;
            case 23:
                await _fx.OrderFx("heal", _stage);
                break;
            case 24:
                await Task.WhenAll(_fx.SparkBurst(_target.GetGlobalRect().GetCenter()), _fx.ShockRing(_target.GetGlobalRect().GetCenter()));
                break;
        }
    }
    private IReadOnlyDictionary<CardNodeKey, CardControl> Nodes() => new Dictionary<CardNodeKey, CardControl> { { new("gallery", "source"), _source }, { new("gallery", "target"), _target } };
    private Task MoveAsync(bool crossLine)
    {
        var before = Nodes();
        var moving = _fx.FlipByUid(before, () =>
        {
            var view = _source.View! with
            {
                Zone = crossLine ? "frontline" : "support"
            };
            var replacement = new CardControl();
            replacement.Bind(_source.Definition, _textures, 140, view);
            _stage.AddChild(replacement);
            replacement.Position = _source.Position + new Vector2(200, crossLine ? -110 : 0);
            _source.QueueFree();
            _source = replacement;
            return Task.CompletedTask;
        }, Nodes);
        return Task.WhenAll(moving, _fx.Glide(_source));
    }
    public async Task VerifyLifecycleAsync()
    {
        BuildActors();
        var starts = _fx.MotionStarts;
        await PlayAsync(4);
        if (_fx.MotionStarts != starts)
            throw new Exception("Zero displacement must not create motion.");
        var health = _source.View!.EffectiveDefense;
        var calls = 0;
        await _fx.Lunge(_source, _target, () => { calls++; return Task.CompletedTask; });
        if (calls != 1 || _source.View!.EffectiveDefense != health)
            throw new Exception("Attack visuals changed state or settled more than once.");
        var overlappingCalls = 0;
        var first = _fx.Lunge(_source, _target, () => { overlappingCalls++; return Task.CompletedTask; });
        await _fx.Sleep(70);
        var second = _fx.Lunge(_source, _target, () => { overlappingCalls++; return Task.CompletedTask; });
        await Task.WhenAll(first, second);
        if (overlappingCalls != 2 || _fx.ActiveMotionCount != 0)
            throw new Exception("Replacing attack visuals lost or duplicated a settlement.");
        await PlayAsync(3);
        if (_fx.ActiveMotionCount != 0)
            throw new Exception("FLIP retained a motion lease.");
        var death = _fx.Die(_source);
        await _fx.Sleep(30);
        if (!GodotObject.IsInstanceValid(_source) || _source.IsQueuedForDeletion())
            throw new Exception("Death removed the card before playback finished.");
        await death;
        if (GodotObject.IsInstanceValid(_source) && !_source.IsQueuedForDeletion())
            throw new Exception("Death did not release the card after playback.");
        Reset();
    }
    public async Task VerifyAllEffectsAsync()
    {
        for (var i = 0; i < Effects.Length; i++)
        {
            BuildActors();
            var start = Time.GetTicksMsec();
            await PlayAsync(i);
            GD.Print($"FX_PROBE {i} {Effects[i]} duration={Time.GetTicksMsec() - start}ms");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        foreach (var kind in new[] { "infantry", "tank", "artillery", "fighter", "bomber", "cruiser", "landcruiser", "spacefighter" })
        {
            _kind.Select(Array.IndexOf(new[] { "infantry", "tank", "artillery", "fighter", "bomber", "cruiser", "landcruiser", "spacefighter" }, kind));
            BuildActors();
            await PlayAsync(1);
            GD.Print($"FX_ATTACK_PROBE {kind}");
        }
        // Reduced-motion reveals still run for their readable hold interval.
        var reduced = _fx.Clock.ReducedMotion;
        _fx.Clock.SetReducedMotion(true);
        try
        {
            foreach (var i in new[] { 0, 3, 6, 8, 10, 13, 18, 19 })
            {
                BuildActors();
                await PlayAsync(i);
                GD.Print($"FX_REDUCED_PROBE {Effects[i]}");
            }
        }
        finally { _fx.Clock.SetReducedMotion(reduced); }
        // Replacing a motion resolves both tasks and restores the visual once.
        BuildActors();
        var first = _fx.Pulse(_source.Visual);
        await _fx.Sleep(30);
        var second = _fx.Pulse(_source.Visual, 120);
        await Task.WhenAll(first, second);
        var interrupted = _fx.PlayOrderCard(_catalog.Cards.First(c => c.CardType == "order"));
        await _fx.Sleep(30);
        _fx.InterruptAll();
        await interrupted;
        if (_fx.ActiveEffects != 0 || _fx.ActiveMotionCount != 0)
            throw new Exception("Interrupted animation leaked state.");
        GD.Print("FX_ALL_PROBES_OK");
        Reset();
    }
}
