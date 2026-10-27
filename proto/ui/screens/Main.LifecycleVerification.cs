using Godot;

namespace Kards.Ui;

/// <summary>
/// P8「切后台掉线的显式提示」的探针。
/// <para>
/// 验的是**玩家回到前台看到什么**，不是"事件接上了"：短切出必须不吭声（不制造噪音）、
/// 长切出且连接还在必须说出结论、长切出且连接真断了必须停在"正在重连"而不是谎报"已恢复"。
/// </para>
/// <para>
/// 平台无关：<see cref="HandleApplicationPaused"/> / <see cref="HandleApplicationResumed"/>
/// 就是 <c>_Notification</c> 在移动端转进去的同一对方法，探针直接调，走的是同一条路。
/// </para>
/// </summary>
public partial class Main
{
    internal async void VerifyBackgroundRecovery()
    {
        try
        {
            // ── 单机：断不了，不该弹任何东西 ───────────────────────────
            Show("setup");
            await BackgroundAsync(3.2);
            Require(!BackgroundedInNetwork, "单机时被判定成了「挂着远端会话」");
            Require(LifecycleNotice is null, "单机切后台再回来弹了提示（制造噪音）");
            GD.Print("[lifecycle] single-player background -> no notice");

            // ── 联机房间：短切出不吭声 ─────────────────────────────────
            Show("room");
            await _room.OpenAsync();
            Require(_room.Feed is not null, "房主没有连上自己的服务器");
            await BackgroundAsync(1.0);
            Require(LifecycleNotice is null, $"1 秒的切出也弹了提示：{LifecycleNotice}");

            // ── 长切出、连接还在：给结论，然后自己收掉 ─────────────────
            await BackgroundAsync(3.2);
            var first = LifecycleNotice;
            Require(first == NoticeConfirming || first == NoticeReconnecting,
                $"回到前台没有说明情况（提示={first ?? "无"}）");
            Require(await WaitUntil(() => LifecycleNotice == NoticeRecovered, 8),
                $"连接还在，却没有给出「已恢复」（当前提示={LifecycleNotice ?? "无"}）");
            Require(await WaitUntil(() => LifecycleNotice is null, 5),
                "提示挂在那里不走了（玩家会以为还断着）");
            GD.Print("[lifecycle] long background, connection alive -> confirming -> recovered -> auto-hide");

            // ── 长切出、连接真断了：不许谎报恢复 ───────────────────────
            // 真的把服务端子进程杀掉——比 room.abort 更贴近"房主那边没了"：
            // abort 只终止对局、连接还在；这里连接会硬断，重连也不可能成功。
            var serverPid = _room.ServerProcessId;
            Require(serverPid > 0, "拿不到服务端子进程 PID，这条测不了");
            using (var server = System.Diagnostics.Process.GetProcessById(serverPid)) server.Kill();
            Require(await WaitUntil(() => _room.IsReconnecting, 10), "服务端被杀掉了，房间屏却没有转入重连");
            await BackgroundAsync(3.2);
            Require(LifecycleNotice is not null, "连接真断了，回到前台却没有任何提示");
            Require(await WaitUntil(() => LifecycleNotice == NoticeReconnecting, 10),
                $"连接断了，提示却没说「正在重连」（当前={LifecycleNotice ?? "无"}）");
            // 再等一轮重连尝试：服务端已经没了，必须一直停在"正在重连"，不能翻成"已恢复"。
            await BackgroundAsync(4.0);
            Require(LifecycleNotice == NoticeReconnecting,
                $"重连不可能成功，提示却变成了「{LifecycleNotice ?? "无"}」");
            GD.Print("[lifecycle] long background, server gone -> stays on reconnecting (no false recovery)");

            GD.Print("BACKGROUND_RECOVERY_VERIFY_OK short-silent long-confirms alive-recovers gone-stays-reconnecting");
            await _room.CloseAsync();
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            try { await _room.CloseAsync(); } catch (Exception) { }
            GetTree().Quit(1);
        }
    }

    /// <summary>切后台 → 等 → 回前台。走的就是移动端 <c>_Notification</c> 转进来的那一对方法。</summary>
    private async Task BackgroundAsync(double seconds)
    {
        HandleApplicationPaused();
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        HandleApplicationResumed();
    }
}
