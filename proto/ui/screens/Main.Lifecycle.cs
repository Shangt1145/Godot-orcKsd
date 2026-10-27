using Kards.Ui.OrcBridge;
using Godot;

namespace Kards.Ui;

/// <summary>
/// 切后台 / 回前台的显式提示。
/// <para>
/// 手机被切到后台时系统会挂起进程，socket 随之断掉；玩家回到前台看到的是一张不动的战场，
/// 没有任何解释——这正是"画面卡住了"最容易被当成 bug 的形态。这里在回到前台时主动确认一次连接，
/// 并把结论说出来（正在确认 / 正在重连 / 已恢复 / 没恢复），而不是让玩家对着冻结的棋盘猜。
/// </para>
/// <para>
/// 只有"这一局在别人的服务器上"才提示：单机对局断不了，弹一句"连接可能中断"纯属制造焦虑。
/// 短时间的切出（权限弹窗、来电悬浮条）也不提示——低于 <see cref="BackgroundNoticeSeconds"/> 的
/// 挂起多半没断，说了反而误导。
/// </para>
/// </summary>
public partial class Main
{
    /// <summary>挂起超过这个秒数才提示。太短的切出多半没断线，提示只是噪音。</summary>
    internal const double BackgroundNoticeSeconds = 3;

    // 文案集中在这里：探针断言和界面用的是同一份常量，改字不会两边漂。
    internal const string NoticeConfirming = "切到后台期间连接可能已中断 · 正在确认连接…";
    internal const string NoticeReconnecting = "切到后台期间连接断了 · 正在重连…";
    internal const string NoticeRecovered = "已回到前台 · 连接正常";
    internal const string NoticeReconnected = "已恢复连接";
    internal const string NoticeLost = "连接没有恢复 · 请返回房间重新加入";
    internal const string NoticeLeftRoom = "切到后台期间已经离开房间";

    private CenterContainer? _noticeHost;
    private Label? _noticeText;
    private double _backgroundedAt = -1;          // <0 表示当前不在后台
    private bool _backgroundedInNetwork;
    private double _noticeHold;                   // 剩余显示秒数；<0 表示跟着连接状态走
    private string _noticeState = "";             // confirm / reconnecting / done

    /// <summary>探针读的提示文案（null＝没有提示）。</summary>
    internal string? LifecycleNotice => _noticeHost?.Visible == true ? _noticeText?.Text : null;
    internal bool BackgroundedInNetwork => _backgroundedInNetwork;

    /// <summary>
    /// 当前是否挂在一个远端会话上（房间或联机对局）。
    /// <para>
    /// 必须把"正在重连"也算进来：<see cref="RoomScreen"/> 进入重连时会先把 <c>_feed</c> 置空，
    /// 只看 feed 的话，正好在重连中切后台的玩家会拿到"没有远端会话"的结论，回来一句提示都没有——
    /// 而这恰恰是最需要说话的时候。这条是探针实测出来的，不是推想。
    /// </para>
    /// </summary>
    private bool InNetworkSession => _room is not null
        && (_room.Feed is not null || _room.IsReconnecting || _feed is RemoteMatchFeed);

    private void MountLifecycleNotice()
    {
        _noticeHost = new CenterContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 200 };
        AddChild(_noticeHost);
        _noticeHost.SetAnchorsPreset(LayoutPreset.TopWide);
        _noticeHost.OffsetTop = 14; _noticeHost.OffsetBottom = 64;
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var box = new StyleBoxFlat { BgColor = new Color("1c221af2"), BorderColor = new Color("c7aa71") };
        box.SetBorderWidthAll(1); box.SetCornerRadiusAll(6);
        panel.AddThemeStyleboxOverride("panel", box);
        _noticeHost.AddChild(panel);
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        foreach (var side in new[] { "margin_left", "margin_right" }) margin.AddThemeConstantOverride(side, 18);
        foreach (var side in new[] { "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(side, 9);
        panel.AddChild(margin);
        _noticeText = UiStyles.Label("", 15, UiStyles.MenuInk);
        _noticeText.MouseFilter = MouseFilterEnum.Ignore;
        margin.AddChild(_noticeText);
    }

    /// <summary>切到后台（平台无关：<see cref="_Notification"/> 只在移动端转进来，探针可直接调）。</summary>
    internal void HandleApplicationPaused()
    {
        if (_backgroundedAt >= 0) return;                 // 重复通知不重置起点
        _backgroundedInNetwork = InNetworkSession;
        _backgroundedAt = Time.GetTicksMsec() / 1000.0;
        GD.Print($"[lifecycle] paused network={_backgroundedInNetwork}");
    }

    /// <summary>回到前台。只有"后台期间挂着远端会话"才提示，单机一律不弹。</summary>
    internal void HandleApplicationResumed()
    {
        if (_backgroundedAt < 0) return;
        var seconds = Time.GetTicksMsec() / 1000.0 - _backgroundedAt;
        var wasNetwork = _backgroundedInNetwork;
        _backgroundedAt = -1;
        _backgroundedInNetwork = false;
        GD.Print($"[lifecycle] resumed after {seconds:0.0}s network={wasNetwork}");
        if (!wasNetwork || seconds < BackgroundNoticeSeconds) return;

        // 后台期间房间没了（房主关房、被移出）：说清楚，别让玩家对着空房间等。
        if (!InNetworkSession)
        {
            _noticeState = "done"; _noticeHold = 5;
            ShowLifecycleNotice(NoticeLeftRoom);
            return;
        }

        _noticeState = "confirm";
        _noticeHold = -1;
        ShowLifecycleNotice(_room!.IsReconnecting ? NoticeReconnecting : NoticeConfirming);
        if (_room.IsReconnecting) _noticeState = "reconnecting";

        // 顺手把 Android 前台服务重新点亮：被系统回收过的话，房间已经不"活"了。
        _room.ReassertHosting();
        _ = ConfirmAfterBackgroundAsync();
    }

    private async Task ConfirmAfterBackgroundAsync()
    {
        var room = _room;
        var ok = await room.ProbeConnectionAsync();
        if (!ReferenceEquals(room, _room)) return;        // 已经换了房间，提示不归这一轮管
        if (_active is not ("room" or "battle")) return;  // 玩家已经走开，别追着弹
        if (_noticeState == "reconnecting" || room.IsReconnecting) return;  // 状态机接手
        _noticeState = "done";
        if (ok) { _noticeHold = 1.5; ShowLifecycleNotice(NoticeRecovered); }
        else { _noticeHold = 6; ShowLifecycleNotice(NoticeLost); }
    }

    /// <summary>每帧推进提示状态：重连一开始就换成"正在重连"，结束后给结论，再自己收掉。</summary>
    private void UpdateLifecycleNotice(double delta)
    {
        if (_noticeHost is not { Visible: true }) return;
        if (_room is { IsReconnecting: true })
        {
            if (_noticeState != "reconnecting")
            {
                _noticeState = "reconnecting";
                _noticeText!.Text = NoticeReconnecting;
            }
            return;
        }
        if (_noticeState == "reconnecting")
        {
            _noticeState = "done";
            _noticeHold = InNetworkSession ? 1.5 : 6;
            _noticeText!.Text = InNetworkSession ? NoticeReconnected : NoticeLost;
            return;
        }
        if (_noticeState == "confirm") return;            // 结论由 ConfirmAfterBackgroundAsync 给
        if (_noticeHold < 0) return;
        _noticeHold -= delta;
        if (_noticeHold <= 0) HideLifecycleNotice();
    }

    private void ShowLifecycleNotice(string text)
    {
        if (_noticeHost is null) return;
        _noticeText!.Text = text;
        _noticeHost.Visible = true;
    }

    private void HideLifecycleNotice()
    {
        if (_noticeHost is not null) _noticeHost.Visible = false;
        _noticeState = "";
        _noticeHold = -1;
    }
}
