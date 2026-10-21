using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>
/// M2 连通性探针：拉起本地 Orc.Server → OrcClient 连上 → 读房间快照。
///
/// <para>
/// 只验证"客户端协议栈在这台机器上能跑"，不建局、不占座。占座与开局要校验卡组，
/// 那一步等接 UI 时用真实卡组做，不在这里用假数据糊过去。
/// </para>
///
/// <para>用法：<c>OrC-KSD.exe -- --net-probe</c>，成功打印 <c>NET_PROBE_OK</c> 并以 0 退出。</para>
/// </summary>
public partial class Main
{
    private async void NetProbe()
    {
        try
        {
            var packs = System.Environment.GetEnvironmentVariable("ORC_PROBE_PACKS")
                ?? @"H:\Working Folder\OrC-KSD-github\samples\card-pack-demo";
            var sandbox = ProjectSettings.GlobalizePath("user://net-probe");

            GD.Print($"[net-probe] packs={packs}");
            await using var server = await ServerLauncher.StartAsync(packs, sandbox);
            GD.Print($"[net-probe] server ready port={server.Port}");

            await using var feed = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
            GD.Print("[net-probe] client connected + initialized");

            var info = await feed.RoomInfoAsync();
            GD.Print($"[net-probe] room.info ok={info.Ok} code={info.Code}");
            if (info.Details is { } details)
            {
                var seats = details["seats"]?.AsArray();
                GD.Print($"[net-probe] state={details["state"]} seats={seats?.Count ?? 0} " +
                         $"packPolicy={details["packPolicy"]}");
                var addresses = details["addresses"]?.AsArray();
                if (addresses is not null)
                    foreach (var address in addresses)
                        GD.Print($"[net-probe]   address {address?["ip"]}:{address?["port"]}");
            }

            if (!info.Ok) throw new Exception("room.info 返回失败：" + info.Code);

            GD.Print("NET_PROBE_OK");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }
}
