using Godot;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    /// <summary>
    /// <c>--verify-room-settings</c>：P7 房间设置验收（房间名 / 固定端口 / 房间成员）。
    ///
    /// <para>
    /// 全部走玩家点得到的路径：房间名写进真控件 → 真的进 UDP 广播 → 局域网列表里真的显示；
    /// 端口写进真控件 → 服务端真的绑在那个端口上；「房间成员」开的是玩家点的那个窗口，
    /// 「移除」按的是那个按钮。
    /// </para>
    /// <para>
    /// 特意不看"控件里的文字对不对"：那是探针最容易骗自己的地方（控件可以显示对的值，
    /// 而广播里还是旧名字）。这里的每一条都落在别人能看见的东西上——对端收到的宣告、服务端
    /// 实际监听的端口、服务端连接表里还在不在这个人。
    /// </para>
    /// </summary>
    private async void VerifyRoomSettings()
    {
        RoomScreen? guestScreen = null;
        try
        {
            Show("room");

            // ① 固定端口：挑一个此刻空闲的端口，让房间真的开在那儿。
            var port = FreePort();
            _room.ConfigureRoomForVerification("星辰作战", port);
            Require(_room.RequestedPort == port, $"设置没被读到：请求端口 {_room.RequestedPort}，期望 {port}");

            await _room.OpenAsync();
            Require(_room.Feed is not null, "房主没有连上：LastError=" + (_room.LastError ?? "（无）"));
            // 这一条是"固定端口"的全部意义：地址不再每次开房都变。
            Require(_room.Port == port, $"固定端口没生效：实际监听 {_room.Port}，期望 {port}（端口被占用时会退回自动分配）");

            // ② 名字进广播。先看宣告器，再看真正收到广播的那一端。
            Require(await WaitUntil(() => _room.AnnouncedRoomName == "星辰作战", 5),
                $"宣告器里的名字是「{_room.AnnouncedRoomName}」，期望「星辰作战」");
            DiscoveredRoom? seen = null;
            for (var i = 0; i < 150 && seen is null; i++)
            {
                await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                seen = _setup.DiscoveredRooms.FirstOrDefault(room => room.Port == port);
            }
            Require(seen is not null, "局域网列表没有收到这个房间的宣告");
            Require(seen!.Name == "星辰作战", $"局域网列表里显示的名字是「{seen.Name ?? "（空）"}」，期望「星辰作战」");

            // ③ 改名不必重开房间：下一轮广播就带新名字（房主改个名字不该把好友踢下线）。
            _room.ConfigureRoomForVerification("改名之后", null);
            Require(await WaitUntil(() => _room.AnnouncedRoomName == "改名之后", 5), "改名没有进宣告器");
            Require(await WaitUntil(() => _setup.DiscoveredRooms.Any(room => room.Port == port && room.Name == "改名之后"), 10),
                "改名没有随广播更新（好友那边还看到旧名字）");
            GD.Print("[room-settings] announce carries name; rename reaches the listener without reopening");

            // ④ 房间成员：客人加入并获批后，房主能在「房间成员」里看到他，并且能移除他。
            guestScreen = new RoomScreen { AllowSampleDeck = true };
            guestScreen.Initialize(Path.Combine(RuntimeGameData.Directory("kards"), "packs"),
                ProjectSettings.GlobalizePath("user://net-room-guest"));
            _room.GetParent().AddChild(guestScreen);
            guestScreen.Visible = false;
            Require(await guestScreen.JoinForVerificationAsync("127.0.0.1", port), "客人加入失败");
            var guestFeed = guestScreen.Feed ?? throw new Exception("客人没有拿到 feed");
            Require((await _room.AdmitForVerificationAsync(guestFeed.ConnectionId)).Ok, "批准客人失败");
            Require(await guestFeed.WaitForAdmissionAsync(), "客人许可未送达");

            var dialog = await _room.OpenMembersDialogAsync();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            // 房主自己不该在"已连接"里——移除自己＝把房间拆了，那是「离开房间」的事。
            var removeButtons = FindButtons(dialog, "RemoveConnection");
            Require(removeButtons.Count == 1,
                $"「已连接」里应当恰好一行（客人）；实际 {removeButtons.Count} 行"
                + "——0 行＝客人没列出来，2 行＝房主自己也被列进去了");
            Require(await ClickOrSignalAsync(removeButtons[0]), "「移除」按钮点不动");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            // 移除是二次确认的：第一次点只是问，第二次才真断。这句话必须成立，
            // 否则"点错一下就踢人"这种事会落到玩家头上。
            var confirm = FindButtons(dialog, "ConfirmRemove").FirstOrDefault()
                ?? throw new Exception("点了「移除」却没有出现确认步骤");
            Require(await ClickOrSignalAsync(confirm), "「确定移除」点不动");

            var gone = false;
            for (var i = 0; i < 100 && !gone; i++)
            {
                gone = await ConnectionMissingAsync(guestFeed.ConnectionId);
                if (!gone) await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            }
            Require(gone, "移除之后服务端连接表里还能看到这个人");

            GD.Print("ROOM_SETTINGS_VERIFY_OK fixed-port name-in-announce rename-without-reopen members-list remove-confirm");
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GD.Print("ROOM_SETTINGS_VERIFY_FAILED " + error.Message);
            guestScreen?.Close();
            _room.Close();
            GetTree().Quit(1);
            return;
        }

        guestScreen?.Close();
        _room.Close();
        GetTree().Quit();
    }

    /// <summary>
    /// 服务端连接表里还有没有这个人。
    /// <para>
    /// 用服务端的表判断"人走没走"，而不是看界面上的行没了没有——行可以只是没刷新，
    /// 而连接表是真正决定"这人还能不能操作"的地方。
    /// </para>
    /// </summary>
    private async Task<bool> ConnectionMissingAsync(string connectionId)
    {
        var result = await _room.AdminForVerificationAsync("connections.list", new JsonObject());
        var connections = result.Details?["connections"] as JsonArray;
        if (connections is null) return false;
        return !connections.Any(node => node?["connectionId"]?.GetValue<string>() == connectionId);
    }

    private static List<Button> FindButtons(Node root, string name)
        => root.FindChildren("*", "Button", true, false).OfType<Button>().Where(button => button.Name == name).ToList();

    /// <summary>
    /// 先真点，点不动再退回信号直发。
    /// <para>
    /// 子窗口里的合成点击打不中是第三次撞上（P4 组合设置、P5 重新绑定、本轮的房间成员）。
    /// 本轮实测排除了"坐标算错"这个猜测：把事件推到对话框自己的 viewport（local 坐标）与
    /// 主 viewport（屏幕坐标）两种都试过，<c>GuiGetHoveredControl()</c> 三次全是 null——
    /// 事件根本没进对话框那一层，不是落点偏了。详见 P7 记录的欠账。
    /// </para>
    /// <para>
    /// 所以这里不假装点过了：真点失败会打印留痕，让"这条断言到底验了什么"是可查的。
    /// 按钮本身可点（信号直发能正确改写状态），已实测。
    /// </para>
    /// </summary>
    private async Task<bool> ClickOrSignalAsync(Button button)
    {
        if (!GodotObject.IsInstanceValid(button)) return false;
        await DeckClickAsync(button);
        if (!GodotObject.IsInstanceValid(button)) return true;
        if (!button.IsVisibleInTree()) return false;
        GD.Print("[room-settings] NOTE dialog-button-click-missed -> EmitSignal(Pressed)");
        button.EmitSignal(BaseButton.SignalName.Pressed);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        return true;
    }

    /// <summary>找一个此刻真的空闲的端口：绑 0 让系统给一个，然后立刻放开。</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
