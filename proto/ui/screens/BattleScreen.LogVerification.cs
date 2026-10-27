using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    /// <summary>
    /// P6 探针：战报面板、弃牌堆页签、词条页签、我方回合倒计时。
    ///
    /// 全程走玩家真会按的那两个控件（「行动记录」按钮 / 「关闭」按钮），不直接改 Visible——
    /// 否则测到的只是"我把面板设成可见了"，而不是"玩家点得开"。
    /// </summary>
    public async Task VerifyLogPanelAsync()
    {
        // 素材用真打出来的一次攻击：StartScenario 会先 ResetDemo（战报清空），条目数才是确定的。
        StartScenario("tank");
        if (LogEntryCount != 0) throw new Exception($"重置后战报仍有 {LogEntryCount} 条。");
        _demo.TryResolveAttack(new("self-front", "enemy-0"), out var sample, out _);
        await PresentCombatAsync(sample!, _demo.Actions);
        if (LogEntryCount == 0) throw new Exception("打完一次攻击，战报里一条都没有。");
        var attack = _log.Entries.FirstOrDefault(e => e.Kind == BattleLogKind.Attack)
            ?? throw new Exception("战报里没有攻击条目。");
        if (!attack.Text.Contains('→')) throw new Exception($"攻击条目不像一句给玩家看的话：{attack.Text}");
        if (attack.Turn <= 0) throw new Exception("攻击条目没有回合号。");

        // 打开：点按钮。
        if (LogPanelVisible) throw new Exception("战报面板开局就是打开的。");
        _historyToggle.EmitSignal(BaseButton.SignalName.Pressed);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!LogPanelVisible) throw new Exception("按「行动记录」没有打开战报面板。");
        if (_logTabs.GetTabCount() != 3) throw new Exception($"战报面板页签数不对：{_logTabs.GetTabCount()}");
        if (_logList.GetChildCount() == 0) throw new Exception("战报页签是空的。");

        // 弃牌堆页签：这一局还没弃牌，也必须写明"（空）"，不能是一片空白。
        _logTabs.CurrentTab = 1;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_discardList.GetChildCount() < 4) throw new Exception($"弃牌堆页签只有 {_discardList.GetChildCount()} 行——两方标题与「（空）」都该在。");

        // 词条页签：每个词条三行（标题／释义／携带者）。没有释义时界面必须明说，
        // 有释义时逐字等于词条表——防止以后有人往表里塞一句自己编的话。
        _logTabs.CurrentTab = 2;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var keywords = MatchKeywords();
        if (keywords.Count == 0) GD.Print("LOG_NOTE no-keyword-on-board");
        if (_keywordList.GetChildCount() != Math.Max(1, keywords.Count * 3))
            throw new Exception($"词条页签行数不对：keywords={keywords.Count} rows={_keywordList.GetChildCount()}");
        var rows = _keywordList.GetChildren().OfType<Label>().ToArray();
        foreach (var row in rows)
            if (row.Text.Trim().Length == 0) throw new Exception("词条页签出现空行。");
        for (var i = 0; i < keywords.Count; i++)
        {
            var id = keywords[i].Id;
            var expected = KeywordGlossary.Describe(id) ?? Kards.Ui.OrcBridge.WorkshopAuthoring.Keyword(id)?.Description;
            var shown = rows[i * 3 + 1].Text;
            if (string.IsNullOrWhiteSpace(expected))
            {
                if (shown != "（引擎未提供这个词条的释义文本）")
                    throw new Exception($"词条 {id} 没有释义，界面却显示了别的东西：{shown}");
            }
            else if (shown != expected)
                throw new Exception($"词条 {id} 的释义与词条表不一致：{shown}");
        }

        // 关闭：点按钮。
        _logClose.EmitSignal(BaseButton.SignalName.Pressed);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (LogPanelVisible) throw new Exception("按「关闭」没有关上战报面板。");

        // 倒计时：我方回合显示剩余秒数且真的在走；对手回合不显示。
        var playing = _state with { Phase = "play", ActivePlayerSide = "self", Turn = _state.Turn + 1 };
        ApplyProjection(playing, _demo.Actions);
        await ToSignal(GetTree().CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
        if (!TurnClockText.Contains("剩余")) throw new Exception($"我方回合没有显示倒计时：'{TurnClockText}'");
        var first = TurnClockText;
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        if (TurnClockText == first) throw new Exception($"回合倒计时没有在走，一直是 '{first}'");
        ApplyProjection(playing with { ActivePlayerSide = "enemy" }, _demo.Actions);
        await ToSignal(GetTree().CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
        if (_turnClock.Visible) throw new Exception("对手回合仍然显示我方倒计时。");

        GD.Print($"BATTLE_LOG_VERIFY_OK entries={LogEntryCount} tabs=3 clock={first}");
    }

    /// <summary>
    /// 给截图脚本用：把面板三个页签各拍一张。内容全是真打出来的，不往里塞假条目——
    /// 弃牌堆这一局确实是空的，截图上就写"（空）"，那也是真话。
    /// </summary>
    public async Task CaptureLogPanelAsync(string dir)
    {
        StartScenario("tank");
        _demo.TryResolveAttack(new("self-front", "enemy-0"), out var sample, out _);
        await PresentCombatAsync(sample!, _demo.Actions);
        // 先拍一张"面板没开"的：倒计时在正常对局状态下的样子，面板一开就会盖住它。
        await ToSignal(GetTree().CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng($"{dir}/battle-log-clock.png");
        _historyToggle.EmitSignal(BaseButton.SignalName.Pressed);
        for (var tab = 0; tab < 3; tab++)
        {
            _logTabs.CurrentTab = tab;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng($"{dir}/battle-log-{tab}.png");
        }
        _logClose.EmitSignal(BaseButton.SignalName.Pressed);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
