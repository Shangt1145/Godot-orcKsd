using Godot;
using Kards.Ui.Core;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyNavigation()
    {
        try
        {
            _clock.SetReducedMotion(true);
            var folder = ProjectSettings.GlobalizePath("res://artifacts/navigation-20261023"); Directory.CreateDirectory(folder);
            async Task Capture(string name)
            {
                await MenuFrameAsync();
                var viewport = GetViewport().GetVisibleRect(); var rect = _screens[_active].GetGlobalRect();
                Require(rect.End.X <= viewport.End.X + 1 && rect.End.Y <= viewport.End.Y + 1, name + " 页面超出窗口");
                if (DisplayServer.GetName() == "headless") return;
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng(folder + "/" + name + ".png");
            }
            await DeckClickAsync(_home.PlayButton);
            Require(_active == "setup" && _feed is null, "首页绕过准备页开战");
            var friends = _setup.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "好友对战  /  局域网");
            await DeckClickAsync(friends);
            Require(_setup.OnlineMode, "好友模式未生效");
            await Capture("01-friends");
            _setup.RequestJoin("127.0.0.1", 4717);
            Require(_active == "decklib" && _deckLibrary.PlaySelection && _setup.HasPendingJoin, "无效卡组加入时丢失目标房间");
            await DeckClickAsync(_deckLibrary.BackButton);
            Require(_active == "setup" && _setup.OnlineMode && _feed is null, "取消选牌没有返回好友准备页");
            await DeckClickAsync(_setup.ChooseDeckButton);
            await DeckClickAsync(_deckLibrary.ActionButton("edit"));
            Require(_active == "deck", "卡组编辑未打开");
            _deckEditor.DeckName.Text = "尚未保存的修改";
            _deckEditor.DeckName.EmitSignal(LineEdit.SignalName.TextChanged, _deckEditor.DeckName.Text);
            var back = _deckEditor.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "返回卡组");
            await DeckClickAsync(back);
            var guard = _deckEditor.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Title == "卡组尚未保存" && d.Visible);
            Require(_active == "deck", "未保存修改被静默丢弃");
            await DeckClickAsync(guard.GetCancelButton());
            Require(_active == "deck" && _deckEditor.HasUnsavedChanges, "取消离开没有保留草稿");
            await DeckClickAsync(back);
            guard = _deckEditor.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Title == "卡组尚未保存" && d.Visible);
            guard.EmitSignal(ConfirmationDialog.SignalName.CustomAction, "discard"); await MenuFrameAsync();
            Require(_active == "decklib" && _deckLibrary.PlaySelection, "编辑返回丢失选牌任务");
            await DeckClickAsync(_deckLibrary.ActionButton("start"));
            Require(_active == "setup" && _setup.OnlineMode && _feed is null && _setup.HasPendingJoin, "确认卡组绕过准备或清除了模式／加入任务");
            await DeckClickAsync(_setup.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "训练  /  电脑对手"));
            Require(!_setup.HasPendingJoin, "切换训练模式仍遗留加入任务");
            Show("decklib"); _deckLibrary.SetPlaySelection(false);
            await DeckClickAsync(_deckLibrary.ActionButton("start"));
            Require(_active == "setup" && _feed is null, "卡组管理入口直接开战");
            await Capture("02-practice");
            await DeckClickAsync(_setup.StartButton);
            Require(await WaitUntil(() => _runner is not null && _battle.MulliganPanelVisible, 20), "训练未进入实际换牌阶段");
            var runner = _runner!; var view = runner.CurrentView!;
            Show("home"); await MenuFrameAsync();
            Require(_resume.Visible, "训练没有恢复入口"); await DeckClickAsync(_resume);
            Require(_active == "battle" && _runner == runner, "继续训练重建了对局");
            StopRealMatch();
            var replay = new ReplayMatchFeed(new ReplayRecording(new("orc-view-replay", 1, DateTimeOffset.UtcNow, "player-view", "navigation-probe"), [new(0, view)]));
            _replayFeed = replay; BindFeed(replay); replay.Seek(0); BuildReadOnlyBar(replay, null);
            Show("home"); await MenuFrameAsync();
            Require(_resume.Visible && _resume.Text.Contains("回放"), "回放没有恢复入口");
            await DeckClickAsync(_resume); Require(_feed == replay && _active == "battle", "恢复回放更换了会话");
            await LeaveSessionAsync(); Require(_feed is null && _active == "setup", "离开回放未释放状态");
            Show("editor"); _workshop.ShowHome(); await MenuFrameAsync();
            Require(_workshop.ChoiceCards.Count == 2, "工坊未保留两张抉择卡");
            await DeckClickAsync(_workshop.NativeManageButton); await MenuFrameAsync();
            var all = Enumerable.Range(1, _workshop.ModifyPick.ItemCount - 1).Select(i => _workshop.ModifyPick.GetItemMetadata(i).AsString()).ToArray();
            Require(all.Length == all.Distinct().Count() && all.Contains("source") && all.Contains("pack/orc.bulwark"), "统一卡包库缺包或重复");
            await Capture("03-workshop-library");
            _workshop.OpenSelection(false, true); await MenuFrameAsync();
            Require(Enumerable.Range(1, _workshop.ModifyPick.ItemCount - 1).All(i => all.Contains(_workshop.ModifyPick.GetItemMetadata(i).AsString())), "抉择入口与我的卡包不是同一份列表");
            var keyword = new DiyKeywordDraft { Name = "补给训练" };
            var custom = new DiyPackDraft { Name = "导航验证卡包", Keywords = [keyword] }; _workshopLibrary.Save(custom); RefreshDiy();
            _workshop.OpenPack(custom); await MenuFrameAsync();
            await DeckClickAsync(_workshop.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.IsVisibleInTree() && b.Text == "词条与效果"));
            var library = _workshop.FindChildren("*", "AcceptDialog", true, false).OfType<AcceptDialog>().Single(d => d.Visible && d.Title == "词条与效果");
            await Capture("04a-keyword-library");
            await DeckClickAsync(library.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.IsVisibleInTree() && b.Text == "编辑规则…"));
            var composer = library.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Visible && d.Title == "编排效果");
            var inspector = composer.FindChildren("*", "Control", true, false).OfType<NativeEffectInspector>().Single();
            await Capture("04-keyword-composer");
            await DeckClickAsync(inspector.NewEffectButton); await MenuFrameAsync();
            Require(inspector.Document.Root["effects"]!.AsArray().Count == 1, "点击添加效果没有创建规则");
            var timing = (OptionButton)inspector.FriendlyControls["/effects/0/trigger"];
            DiyEditorScreen.Select(timing, "deploy"); timing.EmitSignal(OptionButton.SignalName.ItemSelected, (long)timing.Selected); await MenuFrameAsync();
            inspector.EnsureFriendlyVisible(inspector.FriendlyControls["/effects/0/actions#add"]);
            await DeckClickAsync(inspector.FriendlyControls["/effects/0/actions#add"]);
            inspector.PickFriendlyAction("drawOne"); await DeckClickAsync(inspector.PickerAddButton);
            await DeckClickAsync(composer.GetOkButton()); await DeckClickAsync(_workshop.SaveKeyword);
            var authored = _workshopLibrary.Packs.Single(p => p.Id == custom.Id).Keywords.Single();
            Require(authored.Effects.Length == 1 && WorkshopAuthoring.ValidateKeyword(authored) is null, "统一图形编辑器没有保存可执行词条");
            await DeckClickAsync(library.GetOkButton());
            _workshop.ShowHome(); _workshop.OpenSelection(false, false); await MenuFrameAsync();
            _workshop.PackName.Text = "新卡包创建验证"; await DeckClickAsync(_workshop.ConfirmPack);
            Require(_nativeWorkshop.HasSession && _workshop.NativePromoteButton.IsVisibleInTree(), "统一创建入口丢失原生卡包制作功能");
            Require(_nativeWorkshop.WorkingPackage.Name == "新卡包创建验证", "创建的卡包名称不正确"); _nativeWorkshop.Discard();
            _workshop.ShowHome(); _workshop.OpenSelection(true, false); await MenuFrameAsync();
            DiyEditorScreen.Select(_workshop.BaseChoice, "pack/builtin"); _workshop.BaseChoice.EmitSignal(OptionButton.SignalName.ItemSelected, (long)_workshop.BaseChoice.Selected);
            _workshop.PackName.Text = "扩展包创建验证"; await DeckClickAsync(_workshop.ConfirmPack);
            Require(_nativeWorkshop.HasSession, "扩展包没有进入原生制作流程");
            using (var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_nativeWorkshop.TemporaryRoot!, "workshop.presentation.json"))))
                Require(metadata.RootElement.GetProperty("basePackId").GetString() == "builtin", "扩展包丢失基础卡包归属");
            _nativeWorkshop.Discard(); _workshop.ShowHome();
            Show("room");
            var opening = _room.OpenAsync();
            Show("home"); await opening;
            Require(await WaitUntil(() => _active == "home" && _room.Feed is null && _room.Port == 0, 15), "离开时仍有异步建房结果复活");
            Show("room"); await _room.OpenAsync();
            var observerScreen = new RoomScreen(); observerScreen.Initialize(Path.Combine(RuntimeGameData.Directory("kards"), "packs"), ProjectSettings.GlobalizePath("res://artifacts/navigation-observer"));
            AddChild(observerScreen); observerScreen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            await observerScreen.SpectateAsync("127.0.0.1", _room.Port);
            var previousObserver = observerScreen.Feed!;
            Require((await _room.AdminForVerificationAsync("conn.reject", new System.Text.Json.Nodes.JsonObject { ["connectionId"] = previousObserver.ConnectionId })).Ok, "无法拒绝观战申请");
            Require(await WaitUntil(() => previousObserver.AdmissionState == "rejected", 5), "观战申请没有收到拒绝状态");
            await MenuFrameAsync();
            var reapply = observerScreen.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "重新申请入场");
            Require(reapply.IsVisibleInTree(), "观战被拒后没有重新申请入口"); await DeckClickAsync(reapply);
            Require(await WaitUntil(() => observerScreen.Feed is { IsObserver: true, AdmissionState: "pending" } fresh && fresh != previousObserver, 10), "观战重新申请没有建立新的只读连接");
            await observerScreen.CloseAsync(); observerScreen.QueueFree(); await _room.CloseAsync(); Show("home");
            GD.Print("NAVIGATION_VERIFY_OK prepare-only-start mode-preserved pending-join deck-return dirty-cancel training-resume replay-resume unified-gallery keyword-graph-save native-create expansion-parent no-room-resurrection observer-reapply " + folder);
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); StopRealMatch(); await _room.CloseAsync(); GetTree().Quit(1); }
    }
}
