using Godot;

namespace Kards.Ui;

/// <summary>
/// P8「服务端子进程随 UI 退出」的探针（配套外部驱动 <c>artifacts/watchdog-probe.ps1</c>）。
/// <para>
/// 这条**没法自己在进程内判定**：要证明"UI 死了子进程也会走"，必须真的把 UI 杀掉，
/// 而杀掉之后这个进程里就没有代码能继续断言了。所以这里的职责只是把开房结果如实打出来
/// （端口 + 子进程 PID + 本进程 PID），然后原地等，让外面的脚本去杀、去看端口能不能再绑。
/// </para>
/// <para>
/// 它不进 <c>-Verify</c>：正常跑完会打印 <c>HOST_WATCHDOG_IDLE_OK</c> 表示"没被杀，白等一场"，
/// 真正的结论由外部脚本给（<c>WATCHDOG_OK</c> / <c>WATCHDOG_FAIL</c>）。
/// </para>
/// </summary>
public partial class Main
{
    internal async void VerifyHostWatchdog()
    {
        try
        {
            Show("room");
            await _room.OpenAsync();
            Require(_room.Feed is not null, "房主没有连上自己的服务器");
            var serverPid = _room.ServerProcessId;
            var uiPid = System.Environment.ProcessId;
            Require(serverPid > 0, "拿不到服务端子进程 PID（桌面端应当是独立子进程）");
            Require(serverPid != uiPid, "服务端和 UI 是同一个进程，这条测不到东西");
            // 机器可读的一行：外部脚本只认这一行，别的都是给人看的。
            GD.Print($"HOST_WATCHDOG_READY port={_room.Port} serverPid={serverPid} uiPid={uiPid}");
            GD.Print($"[watchdog] 现在杀掉 UI 进程 {uiPid}（不要 /T），子进程 {serverPid} 应在 2 秒左右自行退出");

            for (var i = 0; i < 45; i++)
                await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
            GD.Print("HOST_WATCHDOG_IDLE_OK 没有被杀，等满 45 秒");
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
}
