using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>
/// 联机探针。<c>--net-probe</c> 只验证客户端协议栈能连上；
/// <c>--net-play</c> 真的开一局（占座 + 加 AI + 开局），用来验证整条对局链路。
/// </summary>
public partial class Main
{
    private static string ProbePacks()
        => System.Environment.GetEnvironmentVariable("ORC_PROBE_PACKS")
           ?? @"H:\Working Folder\OrC-KSD-github\samples\card-pack-demo";

    private static string ProbeSandbox() => ProjectSettings.GlobalizePath("user://net-probe");

    private async void NetProbe()
    {
        try
        {
            var packs = ProbePacks();
            GD.Print($"[net-probe] packs={packs}");
            await using var server = await ServerLauncher.StartAsync(packs, ProbeSandbox());
            GD.Print($"[net-probe] server ready port={server.Port}");

            await using var feed = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
            GD.Print("[net-probe] client connected + initialized");

            var info = await feed.RoomInfoAsync();
            GD.Print($"[net-probe] room.info ok={info.Ok} code={info.Code}");
            if (info.Details is { } details)
            {
                GD.Print($"[net-probe] state={details["state"]} packPolicy={details["packPolicy"]}");
                if (details["addresses"] is { } addresses)
                    foreach (var address in addresses.AsArray())
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

    /// <summary>
    /// 真的开一局：房主占座 → 加 AI → 开局 → 等首帧视图 → 打印概况并尝试出一张单位。
    /// 只打印不评判，用来确认"链路能走多远、卡在哪一步"。
    /// </summary>
    private async void NetPlay()
    {
        RemoteMatchFeed? feed = null;
        ServerLauncher? server = null;
        try
        {
            // 示例包只有三张卡，先按最小卡组试；服务端若要求更严格的构筑会回 deck-invalid。
            var deck = new[]
            {
                "demo.rifleman", "demo.rifleman", "demo.rifleman", "demo.rifleman",
                "demo.grenadier", "demo.grenadier", "demo.grenadier", "demo.grenadier",
                "demo.quartermaster", "demo.quartermaster", "demo.quartermaster", "demo.quartermaster",
            };

            server = await ServerLauncher.StartAsync(ProbePacks(), ProbeSandbox());
            GD.Print($"[net-play] server port={server.Port}");

            feed = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
            feed.ErrorRaised += message => GD.Print($"[net-play] error: {message}");
            feed.ProjectionReady += (view, actions) =>
                GD.Print($"[net-play] view phase={view.Phase} turn={view.Turn} active={view.ActivePlayerSide} " +
                         $"hand={view.SelfHand.Count} line={view.SelfLine.Count} kredits={view.SelfKredits}");

            var token = await feed.ClaimSeatAsync(0, "local", "probe", deck);
            GD.Print($"[net-play] claim={(token is null ? "FAILED" : "ok token=" + token[..Math.Min(8, token.Length)])}");
            if (token is null) { GetTree().Quit(1); return; }

            var ai = await feed.AddAiAsync(1, deck);
            GD.Print($"[net-play] add_ai={ai}");
            if (!ai) { GetTree().Quit(1); return; }

            var started = await feed.StartMatchAsync();
            GD.Print($"[net-play] start={started}");
            if (!started) { GetTree().Quit(1); return; }

            for (var i = 0; i < 100 && feed.CurrentView is null; i++)
                await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);

            var view = feed.CurrentView;
            if (view is null) { GD.Print("NET_PLAY_NO_VIEW"); GetTree().Quit(1); return; }

            GD.Print($"[net-play] first view phase={view.Phase} hand={view.SelfHand.Count} " +
                     $"line={view.SelfLine.Count} hq={view.SelfHq?.Uid}");

            // 能出就出一张：验证 play.unit 的 ref/owner/slot 三参是不是这条链。
            if (view.SelfHand.Count > 0)
            {
                var uid = view.SelfHand[0].Uid;
                var cardId = view.SelfHand[0].CardId;
                var before = view.SelfHand.Count;
                await feed.SubmitAsync(new Kards.Ui.Contracts.PlayCard(uid, 0));
                for (var i = 0; i < 50 && feed.CurrentView?.SelfHand.Count == before; i++)
                    await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                var after = feed.CurrentView;
                GD.Print($"[net-play] play {cardId}: hand {before} -> {after?.SelfHand.Count} " +
                         $"line={after?.SelfLine.Count} kredits={after?.SelfKredits}");
            }

            GD.Print("NET_PLAY_OK");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
        finally
        {
            if (feed is not null) await feed.DisposeAsync();
            if (server is not null) await server.DisposeAsync();
        }
    }
}
