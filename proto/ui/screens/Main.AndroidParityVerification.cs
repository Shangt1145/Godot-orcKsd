using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Core;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyAndroidParity()
    {
        var report = new JsonObject { ["platform"] = OS.GetName(), ["managedCompiler"] = DesktopOrc.UsesManagedRuntime };
        void Record(string key) { report[key] = true; GD.Print("ANDROID_PARITY_STAGE " + key); }
        try
        {
            var directory = ProjectSettings.GlobalizePath("user://parity-verification/" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            if (MobilePlatform.IsAndroid)
            {
                var document = ProjectSettings.GlobalizePath("user://parity-document.bin");
                System.IO.File.WriteAllBytes(document, []);
                var uri = MobilePlatform.DocumentUri(document);
                var expected = Enumerable.Range(0, 1048593).Select(n => (byte)(n % 251)).ToArray();
                PlatformFiles.WriteDocument(uri, path => System.IO.File.WriteAllBytes(path, expected), ".zip");
                if (!System.IO.File.ReadAllBytes(PlatformFiles.ReadLocalCopy(uri, ".zip")).SequenceEqual(expected)) throw new Exception("系统文档读写不一致。");
                using var image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8); image.Fill(Colors.Red);
                PlatformFiles.WriteDocument(uri, path => image.SavePng(path), ".png");
                using var loaded = PlatformFiles.LoadImage(uri);
                if (loaded.GetSize() != new Vector2I(2, 2)) throw new Exception("系统文档图片导入失败。");
                Record("document-uri-import-export-image");
            }
            if (InspectorVocabulary.Operations.Length != 161) throw new Exception("工坊动作目录缺失。"); Record("catalog");
            var pack = new DiyPackDraft { Name = "安卓对齐验证卡包" };
            var library = new DiyWorkshopLibrary(System.IO.Path.Combine(directory, "packs.json")); library.Save(pack);
            var card = new DiyCardDraft { Name = "安卓验证步兵", PackId = pack.Id, Cost = 0, OpCost = 0, EffectText = "部署：抽1张牌。" };
            WorkshopAuthoring.Configure(library, [card]);
            var validation = DiyCardCompiler.Validate(card, _catalog.Cards);
            if (!validation.Success) throw new Exception(string.Join("；", validation.Errors)); Record("compile");
            using (var training = await OrcMatchSession.CreateFromCatalogAsync(_catalog.Cards, 71, "android-training", "unused",
                sourceDirectory: RuntimeGameData.Directory("nations"), customCards: [card], openingHand: [card.Id], firstPlayerIndex: 0))
            {
                training.DriveOpponent = false; await training.SettleMulliganAsync(false);
                var before = training.View.SelfHand.Count;
                var selected = training.View.SelfHand.Single(c => c.CardId == card.Id);
                if (await training.SubmitAsync(new PlayCard(selected.Uid, 0)) != UiSubmitOutcome.Applied) throw new Exception("训练出牌失败。");
                training.Pump();
                if (training.View.SelfLine.Count != 1 || training.View.SelfHand.Count != before) throw new Exception("训练部署/抽牌没有执行。");
            }
            Record("training");
            var session = NetworkWorkshopSession.Prepare(directory, [], [card], _catalog.Cards, [card.Id]);
            var deck = session.MapDeck(Enumerable.Repeat(card.Id, 39).ToArray());
            await using var server = await ServerLauncher.StartAsync(session.Roots, System.IO.Path.Combine(directory, "server"), fixedDeckForVerification: true, inProcess: true);
            MobilePlatform.SetHosting(true);
            await using var host = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
            await using var guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port, seat: 1);
            host.ConfigurePackTransfer(session, deck); guest.ConfigurePackTransfer(session, deck); server.Configure(host);
            await server.AdminAsync("pool.transfer.policy", new JsonObject { ["requireAlignment"] = true });
            foreach (var (feed, seat) in new[] { (host, 0), (guest, 1) })
            {
                var connection = await feed.ClaimSeatAsync(seat, "remote", "安卓验证", deck);
                var admitted = await server.AdmitAsync(connection!);
                if (!admitted.Ok || !await feed.WaitForAdmissionAsync()) throw new Exception("房间批准失败。");
            }
            for (int n = 0; n < 3; n++) { await host.SynchronizePacksAsync(); await guest.SynchronizePacksAsync(); }
            if (!host.PacksAligned || !guest.PacksAligned) throw new Exception(host.PackSyncStatus + " / " + guest.PackSyncStatus);
            if (!await host.StartMatchAsync()) throw new Exception("手机开房失败。");
            Record("host-and-sync");
            await host.MulliganDoneAsync(0); await guest.MulliganDoneAsync(1);
            await host.RefreshViewAsync(); await guest.RefreshViewAsync();
            var active = host.CurrentView!.ActivePlayerSide == "self" ? host : guest;
            var played = active.CurrentView!.SelfHand.First(); var hand = active.CurrentView.SelfHand.Count;
            await active.SubmitAsync(new PlayCard(played.Uid, 0)); await active.RefreshViewAsync();
            if (active.CurrentView!.SelfLine.Count != 1 || active.CurrentView.SelfHand.Count != hand) throw new Exception("联机部署/效果未执行。");
            Record("network-effects");
            if (Godot.FileAccess.FileExists("user://android-parity-background.flag"))
            {
                System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("user://android-parity-host-port.txt"), server.Port.ToString());
                GD.Print("ANDROID_PARITY_BACKGROUND_READY " + server.Port);
                await Task.Delay(TimeSpan.FromSeconds(30));
                await active.RefreshViewAsync();
                if (active.CurrentView?.SelfLine.Count != 1) throw new Exception("后台房间状态丢失。");
                Record("background-host");
            }
            var replay = System.IO.Path.Combine(directory, "match.orcreplay");
            using (var recorder = new MatchReplayRecorder(replay, "player-view", "android-parity")) recorder.Record(active.CurrentView);
            if (!System.IO.File.Exists(replay)) throw new Exception("回放未保存。"); Record("replay");
            report["ok"] = true; GD.Print("ANDROID_PARITY_OK " + report.ToJsonString());
        }
        catch (Exception error) { report["ok"] = false; report["error"] = error.ToString(); GD.PushError("ANDROID_PARITY_FAILED " + error); }
        finally
        {
            try { MobilePlatform.SetHosting(false); }
            catch (Exception error) { report["serviceStopError"] = error.ToString(); report["ok"] = false; }
            System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("user://android-parity-report.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            WorkshopAuthoring.Configure(_workshopLibrary, _diy.Cards);
        }
    }
}
