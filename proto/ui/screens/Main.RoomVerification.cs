using Godot;

namespace Kards.Ui;

/// <summary>
/// <c>--verify-room</c>：作战房间的无头验收。
///
/// <para>
/// 验的是**房间这层封装**（起实例 → 连接 → 占本座 → 拉 AI → 开局），
/// 不是视觉——布局对不对靠 <c>--capture-room</c> 的截图，两者分开才不会互相掩护。
/// </para>
/// </summary>
public partial class Main
{
    private async void VerifyRoom()
    {
        try
        {
            Show("room");
            await _room.OpenAsync();
            Require(_room.Port > 0, "服务器实例没有起来（端口 0）");
            Require(_room.Feed is not null, "没有连上服务器");
            Require(_room.Deck is { CardIds.Count: 39 }, "大厅卡组不是 39 张");
            GD.Print($"[room] server port={_room.Port} pack={_room.Deck!.PackageId} " +
                     $"faction={_room.Deck.Faction} deck={_room.Deck.CardIds.Count}");

            var feed = _room.Feed!;
            await feed.RefreshViewAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(feed.SeatToken is { Length: > 0 }, "房主没有拿到 seatToken");

            var ai = await _room.AddAiToSeatAsync(1);
            Require(ai, "座位 1 添加 AI 失败");

            var started = await _room.StartForVerificationAsync();
            Require(started, "开局失败");

            for (var i = 0; i < 100 && feed.CurrentView is null; i++)
                await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            Require(feed.CurrentView is { } view, "开局后没有收到视图");

            GD.Print($"[room] view phase={feed.CurrentView!.Phase} hand={feed.CurrentView.SelfHand.Count} " +
                     $"hq={feed.CurrentView.SelfHq?.Uid}");

            // 第二段：开局后必须真的切进战场，并由远端 feed 驱动（BindFeed 那条正式接线）。
            await EnterRemoteMatchAsync();
            Require(_active == "battle", $"开局后没有切到战场（当前 {_active}）");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(_battle.Visible, "战场屏没有显示");

            // 换牌确认 + 出牌：证明战场侧的输入确实能走到服务端。
            if (feed.CurrentView?.Phase == "mulligan")
            {
                await feed.MulliganDoneAsync(0);
                for (var i = 0; i < 100 && feed.CurrentView?.Phase == "mulligan"; i++)
                    await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            }

            var playable = feed.CurrentView?.SelfHand
                .Where(card => (card.EffectiveCost ?? int.MaxValue) <= (feed.CurrentView?.SelfKredits ?? 0))
                .OrderBy(card => card.EffectiveCost ?? int.MaxValue)
                .FirstOrDefault();
            if (playable is not null)
            {
                var before = feed.CurrentView!.SelfHand.Count;
                await feed.SubmitAsync(new Kards.Ui.Contracts.PlayCard(playable.Uid, 0));
                for (var i = 0; i < 100 && feed.CurrentView?.SelfHand.Count == before; i++)
                    await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                GD.Print($"[room] battle play {playable.CardId}: hand {before} -> {feed.CurrentView?.SelfHand.Count} " +
                         $"line={feed.CurrentView?.SelfLine.Count}");
                Require(feed.CurrentView?.SelfHand.Count < before, "战场里出牌没有生效");
            }
            else
            {
                GD.Print("[room] 首回合没有负担得起的牌，跳过出牌检查");
            }

            GD.Print("ROOM_VERIFY_OK server claim ai start view battle play");
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GD.Print("ROOM_VERIFY_FAILED " + error.Message);
            _room.Close();
            GetTree().Quit(1);
            return;
        }

        _room.Close();
        GetTree().Quit();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    /// <summary>
    /// <c>--capture-room</c>：拍房间屏真实布局（不是原型图）。需要图形模式运行。
    /// 先让实例起来、把 AI 放到座位 1，拍的才是"半满房间"这个真实中间态。
    /// </summary>
    private async void CaptureRoom()
    {
        try
        {
            Show("room");
            // 等快门遮罩揭开再拍，否则截到的是遮罩本身。
            await ToSignal(GetTree().CreateTimer(.45), SceneTreeTimer.SignalName.Timeout);

            _ = _room.OpenAsync();
            for (var i = 0; i < 120 && _room.Feed is null; i++)
                await ToSignal(GetTree().CreateTimer(.1), SceneTreeTimer.SignalName.Timeout);
            Require(_room.Feed is not null, "房间没有就绪，拍不到内容");

            // 必须等占座完成（seatToken）再拉 AI：房间在 claim 之前不接受 add_ai。
            for (var i = 0; i < 100 && _room.Feed!.SeatToken is not { Length: > 0 }; i++)
                await ToSignal(GetTree().CreateTimer(.1), SceneTreeTimer.SignalName.Timeout);
            Require(_room.Feed!.SeatToken is { Length: > 0 }, "房主没有完成占座");

            await _room.AddAiToSeatAsync(1);
            // 座位卡是 deferred 写入，主动刷一次再等两帧，否则截到的是加 AI 之前的画面。
            await _room.RefreshForVerificationAsync();
            // 座位状态是 deferred 更新，多等几帧再按快门。
            await ToSignal(GetTree().CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

            await MenuCaptureAsync("room-layout");
            SaveMenuImage("room");
            GD.Print("ROOM_CAPTURE_OK layout ai-seat endpoint checks");
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
            return;
        }

        _room.Close();
        GetTree().Quit();
    }
}
