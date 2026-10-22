using Godot;
using Kards.Ui.Core;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;
public partial class Main
{
    private bool _viewerReplayVerificationFailed;
    private async void VerifyViewerReplay()
    {
        RemoteMatchFeed? guest = null, viewer = null;
        try
        {
            Show("room"); await _room.OpenAsync();
            var host = _room.Feed ?? throw new Exception("房主连接失败");
            guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", _room.Port, seat: 1);
            Require(await guest.ClaimSeatAsync(1, "remote", "玩家 B", _room.Deck!.CardIds) is not null, "玩家申请失败");
            Require(guest.AdmissionState == "pending" && guest.CurrentView is null && guest.SeatToken is null, "未批准玩家获得了数据或令牌");
            Require((await _room.AdmitForVerificationAsync(guest.ConnectionId)).Ok, "玩家批准失败");
            Require(await guest.WaitForAdmissionAsync() && guest.SeatToken is not null, "玩家许可或令牌丢失");
            Require(await _room.StartForVerificationAsync(), "双人开局失败");
            await host.RefreshViewAsync(); await guest.RefreshViewAsync();
            Require(host.CurrentView is not null && guest.CurrentView is not null, "玩家投影丢失");
            viewer = await RemoteMatchFeed.ConnectAsync("127.0.0.1", _room.Port);
            viewer.ArtPathResolver = id => _artById.GetValueOrDefault(id);
            Require(await viewer.AttachViewerAsync(), "viewer 申请失败");
            Require(viewer.AdmissionState == "pending" && viewer.CurrentView is null && viewer.SeatToken is null, "未批准观战者获得了数据或座位");
            Require((await _room.AdmitForVerificationAsync(viewer.ConnectionId)).Ok, "无 seat viewer 批准失败");
            Require(await viewer.WaitForAdmissionAsync(), "viewer 未收到许可");
            await viewer.RefreshViewAsync();
            Require(viewer.CurrentView is not null && viewer.SeatToken is null && !viewer.MulliganPending && !viewer.CurrentActions.CanEndTurn, "观战不是只读");
            var firstHand = viewer.CurrentView!.SelfHand.Select(c => c.Uid).ToArray();
            viewer.SetObserverPerspective(1);
            Require(viewer.CurrentView!.SelfHand.Select(c => c.Uid).SequenceEqual(guest.CurrentView!.SelfHand.Select(c => c.Uid)), "玩家 B 视角不一致");
            Require(!viewer.CurrentView.SelfHand.Select(c => c.Uid).SequenceEqual(firstHand), "切换视角没有换手牌");
            var refused = false; viewer.CommandRefused += _ => refused = true;
            await viewer.SubmitAsync(new EndTurn()); Require(refused, "观战命令未拒绝");
            var folder = ProjectSettings.GlobalizePath("res://artifacts/viewer-replay-20261023"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".orcreplay");
            using (var recorder = new MatchReplayRecorder(path, "omniscient-viewer", "verification"))
            {
                viewer.ObserverProjectionReady += (seat, view) => recorder.Record(view, perspective: seat);
                await viewer.RefreshViewAsync();
                Require(await host.MulliganDoneAsync(0) && await guest.MulliganDoneAsync(1), "双人换牌确认失败");
                await host.RefreshViewAsync(); await guest.RefreshViewAsync();
                var actor = host.CurrentView!.ActivePlayerSide == "self" ? host : guest;
                var turn = actor.CurrentView!.Turn;
                await ToSignal(GetTree().CreateTimer(.25), SceneTreeTimer.SignalName.Timeout);
                await actor.SubmitAsync(new EndTurn());
                await viewer.RefreshViewAsync();
                Require(viewer.CurrentView!.Turn > turn, "观战未跟随真实回合推进");
                recorder.Record(viewer.CurrentView, new UiPresentationResolution(viewer.MatchId,
                    [new UiTurnPresentation("self", viewer.CurrentView.Turn)], viewer.CurrentView), 1);
            }
            var recording = MatchReplayRecorder.Load(path);
            Require(recording.Frames.Any(f => f.Perspective == 0) && recording.Frames.Any(f => f.Perspective == 1), "回放未保存双方视角");
            Require(recording.Frames.Any(f => f.Presentation?.Steps.OfType<UiTurnPresentation>().Any() == true), "演出未能往返序列化");
            Require(recording.Duration >= .2 && recording.Frames.Select(f => f.View.Turn).Distinct().Count() > 1, "未记录真实时间线");
            var replay = new ReplayMatchFeed(recording); replay.SetPerspective(1); replay.Seek(recording.Duration);
            Require(replay.CurrentView!.SelfHand.Select(c => c.Uid).SequenceEqual(guest.CurrentView.SelfHand.Select(c => c.Uid)), "回放视角恢复错误");
            replay.Seek(double.MaxValue); Require(replay.Position == recording.Duration, "跳转没有限制范围");
            replay.Playing = true; replay.Advance(100); Require(!replay.Playing, "回放末尾未暂停");
            refused = false; replay.CommandRefused += _ => refused = true; await replay.SubmitAsync(new EndTurn()); Require(refused, "回放可操作");
            File.AppendAllText(path, "{interrupted"); Require(MatchReplayRecorder.Load(path).Frames.Count == recording.Frames.Count, "尾帧截断无法恢复");
            var bad = Path.Combine(folder, "unsupported.orcreplay"); File.WriteAllText(bad, "{\"Format\":\"orc-view-replay\",\"Version\":99}");
            var rejected = false; try { MatchReplayRecorder.Load(bad); } catch (InvalidDataException) { rejected = true; } Require(rejected, "未知回放版本未拒绝");
            Require((await _room.AdminForVerificationAsync("conn.revoke", new System.Text.Json.Nodes.JsonObject { ["connectionId"] = viewer.ConnectionId })).Ok, "撤销观战失败");
            for (var i = 0; i < 50 && viewer.AdmissionState != "revoked"; i++) await ToSignal(GetTree().CreateTimer(.05), SceneTreeTimer.SignalName.Timeout);
            Require(viewer.AdmissionState == "revoked" && viewer.CurrentView is null && viewer.BoundSeat is null, "撤销后仍保留活动观战");
            await viewer.DisposeAsync(); viewer = await RemoteMatchFeed.ConnectAsync("127.0.0.1", _room.Port);
            Require(await viewer.AttachViewerAsync() && viewer.AdmissionState == "pending", "观战未重新走审批");
            Require((await _room.AdmitForVerificationAsync(viewer.ConnectionId)).Ok && await viewer.WaitForAdmissionAsync(), "观战重新批准失败");
            Require((await _room.AdminForVerificationAsync("room.abort", new System.Text.Json.Nodes.JsonObject())).Ok, "终止房间失败");
            for (var i = 0; i < 100 && viewer.AdmissionState != "revoked"; i++) await ToSignal(GetTree().CreateTimer(.05), SceneTreeTimer.SignalName.Timeout);
            Require(viewer.AdmissionState == "revoked" && viewer.SeatToken is null, "房间轮换未撤销 viewer");
            await viewer.DisposeAsync(); viewer = await RemoteMatchFeed.ConnectAsync("127.0.0.1", _room.Port);
            Require(await viewer.AttachViewerAsync() && viewer.AdmissionState == "pending", "新房间观战未重新申请");
            OpenReplay(path);
            for (var i = 0; i < 100 && _feed is not ReplayMatchFeed; i++) await ToSignal(GetTree().CreateTimer(.05), SceneTreeTimer.SignalName.Timeout);
            Require(_feed is ReplayMatchFeed && _readOnlyBar is not null, "回放未接入实际战场");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _battle.VerifyReadOnlyInspection();
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng(Path.Combine(folder, "replay.png"));
            }
            GD.Print("VIEWER_REPLAY_VERIFY_OK pending admission no-seat dual-perspective read-only record presentation load seek playback corruption revoke rotation reapply UI");
        }
        catch (Exception e) { GD.PushError(e.ToString()); GD.Print("VIEWER_REPLAY_VERIFY_FAILED " + e.Message); _viewerReplayVerificationFailed = true; }
        finally { if (viewer is not null) await viewer.DisposeAsync(); if (guest is not null) await guest.DisposeAsync(); _room.Close(); }
        StopRealMatch(); GetTree().Quit(_viewerReplayVerificationFailed ? 1 : 0);
    }
}
