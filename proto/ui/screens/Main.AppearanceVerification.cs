using Godot;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyAppearance()
    {
        RemoteMatchFeed? guest=null;
        try
        {
            var folder=ProjectSettings.GlobalizePath("res://artifacts/appearance-verification");Directory.CreateDirectory(folder);
            var roomDeck=LobbyDeck.Build(Path.Combine(RuntimeGameData.Directory("kards"),"packs"));
            var nation=roomDeck.Faction.ToLowerInvariant();
            var fixtureDeck=new ConstructedDeck {Name="外观联机验证",CardIds=roomDeck.CardIds.ToArray(),PackIds=["pack/"+roomDeck.PackageId],
                Options=new(){MainNation=nation,AllyNation=nation=="japan"?"germany":"japan"}};
            _decks.Save(fixtureDeck);_decks.Activate(fixtureDeck.Id);RefreshMenuData();
            byte[] Png(Color color)
            {
                var image=Image.CreateEmpty(128,176,false,Image.Format.Rgba8);image.Fill(color);
                image.FillRect(new Rect2I(6,6,116,164),color.Darkened(.22f));image.FillRect(new Rect2I(48,40,32,96),new Color("e5d8ac"));
                return image.SavePngToBuffer();
            }
            foreach(var (kind,color) in new[]{("back","9d632d"),("decoration","347d62"),("emote","557ba4"),("hq","83624f")})_cosmetics.Import(kind,"验证·"+kind,Png(new(color)));
            Show("decklib");await ToSignal(GetTree().CreateTimer(.5),SceneTreeTimer.SignalName.Timeout);
            await DeckClickAsync(_deckLibrary.TileButton(fixtureDeck.Id));
            await DeckClickAsync(_deckLibrary.ActionButton("customize"));
            Require(_active=="appearance","卡组详情未进入外观编辑器");
            await ToSignal(GetTree().CreateTimer(.5),SceneTreeTimer.SignalName.Timeout);
            await _appearanceScreen.VerifyEquipmentAsync();

            var reloaded=new CosmeticLibrary(Path.GetDirectoryName(_cosmetics.ImagePath(_cosmetics.Profile.CardBack))!+"/..");
            reloaded.SetContext(_decks.ActiveDeck?.Id,_decks.ActiveDeck?.Options.MainNation);
            Require(reloaded.Profile.HeadquartersName=="山城指挥所"&&reloaded.Profile.CardBack==_cosmetics.Profile.CardBack,"外观保存重载失败");
            var originalBack=_cosmetics.Assets.First(a=>a.BuiltIn&&a.Kind=="back"&&a.Name=="BasicGermanA");
            var originalHq=OriginalCosmetics.DefaultHq("japan");
            var special=OriginalCosmetics.Emotes.First(e=>e.Style=="special"&&e.EnemyImage.Length>0&&e.Nations.Contains(nation)).Emote;
            var originals=new[]{special}.Concat(OriginalCosmetics.Defaults(nation)).DistinctBy(e=>e.Id).Take(8).ToArray();
            _cosmetics.Save(_cosmetics.Profile with{CardBack=originalBack.Image,Headquarters=originalHq,HeadquartersName="",Decoration=_cosmetics.Assets.First(a=>a.BuiltIn&&a.Kind=="decoration"&&a.Name=="AN-M8_SmokeGrenade").Image,Emotes=originals});
            Require(OriginalCosmetics.Emotes.Count>=274&&((FontFile)OriginalCosmetics.Font()).Data.Length>1000000,"原版表情或字体未载入");
            _appearanceScreen.OpenDeck(fixtureDeck);
            await _appearanceScreen.VerifyOriginalGalleryAsync();
            await ToSignal(GetTree().CreateTimer(.3),SceneTreeTimer.SignalName.Timeout);
            if(DisplayServer.GetName()!="headless")await ShotAsync(folder,"appearance-editor-original");
            GD.Print("ORIGINAL_ASSETS_VERIFY_OK original-cardback raw-hq special-emote font native-icons");
            Show("room");await _room.OpenAsync();var host=_room.Feed??throw new Exception("房间未创建");
            Require(host.SupportsCosmetics,"房间服务器未包含外观通道");
            await host.PublishCosmeticsAsync();
            var guestLibrary=new CosmeticLibrary(Path.Combine(folder,Guid.NewGuid().ToString("N")));
            var guestBack=_cosmetics.Assets.First(a=>a.BuiltIn&&a.Kind=="back"&&a.Name=="JapanBasicA");
            var other=guestLibrary.Import("back","原版卡背",_cosmetics.ReadImage(guestBack.Image)!);
            var guestSpecial=OriginalCosmetics.Emotes.First(e=>e.OriginalId=="emote_fall_seven").Emote;
            guestLibrary.CacheImage(_cosmetics.ReadImage(originalHq)!);guestLibrary.CacheImage(_cosmetics.ReadImage(guestSpecial.Image)!);guestLibrary.CacheImage(_cosmetics.ReadImage(guestSpecial.EnemyImage)!);
            guestLibrary.Save(new(){CardBack=other.Image,Decoration="",Headquarters=originalHq,HeadquartersName="",Emotes=[guestSpecial with{Id="hello"}]});
            guest=await RemoteMatchFeed.ConnectAsync("127.0.0.1",_room.Port,seat:1);guest.ConfigureCosmetics(guestLibrary);
            Require(await guest.ClaimSeatAsync(1,"remote","远端玩家",_room.Deck!.CardIds) is not null,"远端占座失败");
            Require((await _room.AdmitForVerificationAsync(guest.ConnectionId)).Ok&&await guest.WaitForAdmissionAsync(),"批准远端玩家失败");
            await guest.PublishCosmeticsAsync();await guest.RefreshCosmeticsAsync();await host.RefreshCosmeticsAsync();
            async Task Wait(Func<bool> check,string message)
            {
                var until=Time.GetTicksMsec()+12000;
                while(!check()&&Time.GetTicksMsec()<until)await ToSignal(GetTree().CreateTimer(.05),SceneTreeTimer.SignalName.Timeout);
                Require(check(),message);
            }
            await Wait(()=>host.CosmeticProfiles.Count==2&&guest.CosmeticProfiles.Count==2,"双方外观未同步");
            Require(host.CosmeticProfiles[1].HeadquartersName==""&&guest.CosmeticProfiles[0].HeadquartersName=="","总部外观视角错误");
            Require(_cosmetics.HasImage(other.Image)&&guestLibrary.HasImage(_cosmetics.Profile.CardBack),"图片未传输到对方缓存");
            Require(guestLibrary.Assets.Count==1,"接收缓存不应自动加入本地导入列表");
            GD.Print("COSMETICS_NETWORK_VERIFY_OK two-approved-players independent-profiles images-transferred cache-reused");
            Require(await _room.StartForVerificationAsync(),"真实对局未开始");
            await host.RefreshViewAsync();await guest.RefreshViewAsync();
            Require(await host.MulliganDoneAsync(0)&&await guest.MulliganDoneAsync(1),"结束换牌失败");
            await host.RefreshViewAsync();await guest.RefreshViewAsync();
            await Wait(()=>host.CurrentView?.Phase=="play"&&guest.CurrentView?.Phase=="play","换牌后未进入对局");
            if(!ReferenceEquals(_feed,host))BindFeed(host);
            Show("battle");_battle.ApplyProjection(host.CurrentView!,host.CurrentActions);_battle.RefreshCosmetics();
            await ToSignal(GetTree().CreateTimer(.5),SceneTreeTimer.SignalName.Timeout);
            var originalHealth=host.CurrentView!.SelfHq!.Health;var originalName=host.CurrentView.SelfHq.Definition.Name;
            var gotGuestEmote=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.EmoteReceived+=(seat,emote)=>{if(seat==1&&emote.Id=="hello")gotGuestEmote.TrySetResult();};
            var gotHostEmote=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostEmoteId=host.LocalCosmetics.Emotes[0].Id;
            Require(guest.CosmeticProfiles[0].Emotes[0].Style==special.Style&&guest.CosmeticProfiles[0].Emotes[0].Tint==special.Tint&&guestLibrary.HasImage(special.Image)&&guestLibrary.HasImage(special.EnemyImage),"原版表情背景或样式未传输");
            guest.EmoteReceived+=(seat,emote)=>{if(seat==0&&emote.Id==hostEmoteId)gotHostEmote.TrySetResult();};
            await _battle.VerifySendEmoteAsync(hostEmoteId);await gotHostEmote.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await guest.SendEmoteAsync("hello");await gotGuestEmote.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ToSignal(GetTree().CreateTimer(.25),SceneTreeTimer.SignalName.Timeout);
            if(DisplayServer.GetName()!="headless")await ShotAsync(folder,"battle-cosmetics-emotes");
            Require(host.CurrentView.SelfHq.Health==originalHealth&&host.CurrentView.SelfHq.Definition.Name==originalName,"外观更改了总部规则数据");
            GD.Print("EMOTE_NETWORK_VERIFY_OK both-directions sender-seat-mapped hq-rules-unchanged");
            await _battle.VerifyCosmeticMenuAsync(async()=>{if(DisplayServer.GetName()!="headless")await ShotAsync(folder,"battle-emote-menu");});
            var currentDeck=_decks.ActiveDeck!;
            var otherDeck=currentDeck with{Id=Guid.NewGuid().ToString("N"),Name="外观隔离验证"};
            _cosmetics.SaveFor(otherDeck.Id,otherDeck.Options.MainNation,_cosmetics.Resolve(otherDeck.Id,otherDeck.Options.MainNation) with{HeadquartersName="别的卡组"});
            _cosmetics.SetContext(otherDeck.Id,otherDeck.Options.MainNation);
            await host.PublishCosmeticsAsync();await guest.RefreshCosmeticsAsync();
            Require(guest.CosmeticProfiles[0].HeadquartersName==""&&AppearanceRuntime.Profile("self").HeadquartersName=="","浏览其他卡组改变了对局外观");
            _cosmetics.SetContext(currentDeck.Id,currentDeck.Options.MainNation);
            GD.Print("COSMETICS_SCOPE_VERIFY_OK deck-entry per-deck-hq room-deck-stable-while-browsing");
            var token=guest.SeatToken;await guest.DisposeAsync();
            guest=await RemoteMatchFeed.ConnectAsync("127.0.0.1",_room.Port,seat:1);guest.ConfigureCosmetics(guestLibrary);
            Require(await guest.ResumeSeatAsync(token),"远端重连失败");await guest.RefreshCosmeticsAsync();
            await Wait(()=>guest.CosmeticProfiles.ContainsKey(0),"重连未恢复对方外观");
            Require(guest.CosmeticProfiles[0].CardBack==_cosmetics.Profile.CardBack,"重连外观不一致");
            GD.Print("COSMETICS_RECONNECT_VERIFY_OK own-seat-resumed peer-profile-and-images-restored");
            await guest.DisposeAsync();guest=null;StopRealMatch();await _room.CloseAsync();GetTree().Quit();
        }
        catch(Exception e){GD.PushError(e.ToString());if(guest is not null)await guest.DisposeAsync();StopRealMatch();await _room.CloseAsync();GetTree().Quit(1);}
    }
}
