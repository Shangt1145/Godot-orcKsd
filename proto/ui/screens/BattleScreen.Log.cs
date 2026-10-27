using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>
/// P6 对局完整度：战报面板、弃牌堆、词条释义，以及我方回合的可见倒计时。
///
/// 数据来源全部是引擎已经给的东西——战报与弃牌堆来自表现步骤（<see cref="BattleLog"/>），
/// 词条来自投影里的 <c>Keywords</c>/<c>KeywordValues</c>。这里不推断规则、不补数字。
/// </summary>
public partial class BattleScreen
{
    private readonly BattleLog _log = new();
    private Control _logPanel = null!, _logList = null!, _discardList = null!, _keywordList = null!;
    private TabContainer _logTabs = null!;
    private BattleHistoryButton _historyToggle = null!;
    private Button _logClose = null!;
    private Label _turnClock = null!;
    private Godot.Timer _turnClockTimer = null!;
    /// <summary>我方回合剩余秒数；≤0＝已超时（只提示，不强制）。</summary>
    private double _turnSeconds;
    private bool _turnClockRunning;
    private int _turnClockTurn = -1;

    /// <summary>我方每回合的提示时长。超时不结束回合——alpha 只做提示。</summary>
    private const double TurnSeconds = 75;

    internal int LogEntryCount => _log.Entries.Count;
    internal int LogDiscardCount(string side) => _log.Discards(side).Count;
    internal bool LogPanelVisible => _logPanel?.Visible == true;
    internal string TurnClockText => _turnClock?.Text ?? "";

    private void BuildLogPanel()
    {
        // 回合倒计时贴着我方"结束回合"上方：玩家看回合剩余时间时眼睛本来就在那儿。
        // 右对齐、右缘与"结束回合"按钮对齐——倒计时文案会变长（"已超时（不强制）"），
        // 居中放在固定宽框里两边都会被切掉（截图实测：battle-log-0.png 只剩"回合 · 剩余 75 秒"）。
        _turnClock = Text("", new(1000, 414), new(255, 26), 15, "e9e4d0");
        _turnClock.HorizontalAlignment = HorizontalAlignment.Right; _turnClock.Visible = false; _canvas.AddChild(_turnClock);
        _turnClockTimer = new Godot.Timer { WaitTime = 0.25, Autostart = true, OneShot = false };
        _turnClockTimer.Timeout += TickTurnClock; AddChild(_turnClockTimer);

        _logPanel = new Control { Position = new(140, 52), Size = new(1000, 616), Visible = false, MouseFilter = MouseFilterEnum.Stop, ZIndex = 240 };
        _canvas.AddChild(_logPanel);
        var shade = new ColorRect { Size = _logPanel.Size, Color = new Color("12140f") with { A = .96f }, MouseFilter = MouseFilterEnum.Ignore };
        _logPanel.AddChild(shade);
        var head = new HBoxContainer { Position = new(18, 14), Size = new(964, 34) }; _logPanel.AddChild(head);
        head.AddChild(MenuStyles.Label("本局记录", 22, MenuStyles.Gold));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _logClose = MenuStyles.Button("关闭", () => _logPanel.Visible = false);
        head.AddChild(_logClose);
        _logTabs = new TabContainer { Position = new(18, 56), Size = new(964, 542) };
        _logPanel.AddChild(_logTabs);
        _logList = ScrollList(_logTabs, "战报");
        _discardList = ScrollList(_logTabs, "弃牌堆");
        _keywordList = ScrollList(_logTabs, "词条");
    }

    /// <summary>一个可滚动的列表页；返回里面的纵向容器。</summary>
    private static Control ScrollList(TabContainer tabs, string title)
    {
        var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        tabs.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4); scroll.AddChild(list);
        return list;
    }

    private void ToggleLogPanel()
    {
        _logPanel.Visible = !_logPanel.Visible;
        if (_logPanel.Visible) RefreshLogPanel();
    }

    /// <summary>把当前战报/弃牌堆/词条重画一遍。面板没打开时只记不画，省掉每回合几十次重建。</summary>
    private void RefreshLogPanel()
    {
        if (_logPanel is null || !_logPanel.Visible) return;
        MenuStyles.Clear(_logList);
        if (_log.Dropped > 0)
            _logList.AddChild(MenuStyles.Label($"（更早的 {_log.Dropped} 条已超出记录上限）", 12, MenuStyles.Dim));
        foreach (var entry in _log.Entries)
            _logList.AddChild(MenuStyles.Label($"{entry.Turn} 回合 · {entry.SideLabel} · {entry.Text}", 13,
                entry.Kind switch
                {
                    BattleLogKind.Death or BattleLogKind.Result => MenuStyles.Gold,
                    BattleLogKind.Damage or BattleLogKind.Discard or BattleLogKind.Burn => new Color("dca58b"),
                    _ => MenuStyles.Ink,
                }));
        if (_log.Entries.Count == 0) _logList.AddChild(MenuStyles.Label("本局还没有记录。", 13, MenuStyles.Dim));

        MenuStyles.Clear(_discardList);
        foreach (var side in new[] { "self", "enemy" })
        {
            var rows = _log.Discards(side);
            _discardList.AddChild(MenuStyles.Label($"{(side == "enemy" ? "对手" : "我方")} · {rows.Count} 张", 15, MenuStyles.Gold));
            if (rows.Count == 0) _discardList.AddChild(MenuStyles.Label("（空）", 13, MenuStyles.Dim));
            foreach (var row in rows)
                _discardList.AddChild(MenuStyles.Label($"第 {row.Turn} 回合 · {row.Text}", 13,
                    row.Kind == BattleLogKind.Burn ? new Color("dca58b") : MenuStyles.Ink));
        }

        MenuStyles.Clear(_keywordList);
        var keywords = MatchKeywords();
        if (keywords.Count == 0) _keywordList.AddChild(MenuStyles.Label("本局场上没有词条。", 13, MenuStyles.Dim));
        foreach (var (id, value, carriers) in keywords)
        {
            var title = value > 0 ? $"{CardControl.KeywordLabel(id, new Dictionary<string, int> { [id] = value })}" : CardControl.KeywordLabel(id, new Dictionary<string, int>());
            _keywordList.AddChild(MenuStyles.Label(title, 15, MenuStyles.Gold));
            // 释义只写核对过的内建词条与作者自己填的 DIY 词条；两者都没有就说"没有"，不编一句。
            var text = KeywordGlossary.Describe(id) ?? Kards.Ui.OrcBridge.WorkshopAuthoring.Keyword(id)?.Description;
            _keywordList.AddChild(MenuStyles.Label(
                string.IsNullOrWhiteSpace(text) ? "（引擎未提供这个词条的释义文本）" : text,
                13, string.IsNullOrWhiteSpace(text) ? MenuStyles.Dim : MenuStyles.Ink));
            _keywordList.AddChild(MenuStyles.Label("本局携带：" + string.Join("、", carriers), 12, MenuStyles.Dim));
        }
    }

    /// <summary>本局场上/手上所有词条，带参值与携带它的卡名。</summary>
    private List<(string Id, int Value, List<string> Carriers)> MatchKeywords()
    {
        var order = new List<string>();
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        var carriers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(UiCardView? card)
        {
            if (card is null || card.Visibility != Visibility.Full) return;
            foreach (var keyword in card.Keywords)
            {
                // 老兵/隐蔽/被抑制是卡面自己就有徽记的状态位，在词条页再列一遍只是噪声。
                // 其余词条一律列出——包括还没核对过释义的，那样界面会明说"未提供"，而不是悄悄消失。
                if (keyword is "inhibited" or "veteran" or "covert") continue;
                if (!carriers.TryGetValue(keyword, out var list))
                {
                    order.Add(keyword); carriers[keyword] = list = [];
                    if (card.KeywordValues.TryGetValue(keyword, out var value) && value > 0) values[keyword] = value;
                }
                var name = card.Definition.Name.Length > 0 ? card.Definition.Name : card.CardId;
                if (seen.Add(keyword + "\u0000" + name)) list.Add(name);
            }
        }
        Visit(_state.SelfHq); Visit(_state.EnemyHq);
        foreach (var card in _state.SelfHand) Visit(card);
        foreach (var card in _state.SelfLine) Visit(card);
        foreach (var card in _state.EnemyLine) Visit(card);
        return order.Select(id => (id, values.GetValueOrDefault(id), carriers[id])).ToList();
    }

    /// <summary>
    /// 回合倒计时：只在我方回合、对局进行中显示。超时**不**结束回合，只把文案改成"已超时"。
    /// 这是刻意的：alpha 阶段强制超时会把正在想事情的玩家直接判掉，收益远小于风险。
    /// </summary>
    private void TickTurnClock()
    {
        // 计时器从构造起就在跑，第一帧可能早于第一次投影——此时没有状态可读，直接跳过。
        if (_turnClock is null || _state is null) return;
        var mine = _state.Phase == "play" && _state.ActivePlayerSide == "self";
        if (!mine)
        {
            if (_turnClockRunning || _turnClock.Visible) { _turnClockRunning = false; _turnClock.Visible = false; }
            return;
        }
        // 回合号变了才算新回合：否则每次投影都会把倒计时重置回满。
        if (_state.Turn != _turnClockTurn) { _turnClockTurn = _state.Turn; _turnClockRunning = false; }
        if (!_turnClockRunning) { _turnClockRunning = true; _turnSeconds = TurnSeconds; }
        // 战报面板开着的时候不显示倒计时：面板右缘压到这一行，露半个"余 75 秒"很难看，
        // 而玩家此刻在看战报，不需要这个数字。
        _turnClock.Visible = !_logPanel.Visible;
        _turnSeconds -= _turnClockTimer.WaitTime;
        _turnClock.Text = _turnSeconds > 0
            ? $"我方回合 · 剩余 {Mathf.CeilToInt(_turnSeconds)} 秒"
            : "我方回合 · 已超时（不强制）";
        _turnClock.AddThemeColorOverride("font_color", _turnSeconds switch
        {
            > 20 => new Color("e9e4d0"),
            > 0 => new Color("dca58b"),
            _ => new Color("c98b7a"),
        });
    }
}
