using Godot;
using Kards.Ui.Core;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private CosmeticLibrary _cosmetics=null!;
    private AppearanceScreen _appearanceScreen=null!;
    private ConstructedDeck? _appearanceDeck, _battleAppearanceDeck;
    private void InitializeAppearance()
    {
        _cosmetics=new CosmeticLibrary(ProjectSettings.GlobalizePath(_verificationRun?$"res://artifacts/appearance/{Guid.NewGuid():N}":"user://appearance"));
        OriginalCosmetics.Register(_cosmetics);
        AppearanceRuntime.Configure(_cosmetics);
    }
    private void AddAppearanceScreen(Control content)
    {
        _appearanceScreen=new AppearanceScreen();_appearanceScreen.Initialize(_cosmetics,_textures);AddScreen(content,"appearance",_appearanceScreen);
        _deckLibrary.CustomizeRequested+=deck=>OpenAppearance(deck);
        _appearanceScreen.BackRequested+=()=>Show("decklib");
        _cosmetics.SetContext(_decks.ActiveDeck?.Id,_decks.ActiveDeck?.Options.MainNation);
        _appearanceScreen.Equipped+=()=>{
            _battle.RefreshCosmetics();RefreshMenuData();
            var remote=_feed as RemoteMatchFeed??_room.Feed;
            if(remote is not null)_=PublishAppearanceAsync(remote);
        };
        _battle.EmoteRequested+=async id=>{
            try
            {
                if(_feed is RemoteMatchFeed remote)await remote.SendEmoteAsync(id);
                else _battle.ShowCosmeticEmote("self",id);
            }
            catch(Exception e){_battle.ShowHint(e.Message);}
        };
    }
    private void OpenAppearance(ConstructedDeck deck)
    {
        _appearanceDeck=deck.Snapshot();Show("appearance");
    }
    private void PrepareAppearanceScreen()
    {
        var deck=_appearanceDeck is {} selected?_decks.Decks.FirstOrDefault(d=>d.Id==selected.Id):_decks.ActiveDeck;
        _appearanceScreen.OpenDeck(deck);_appearanceDeck=null;
    }
    private async Task PublishAppearanceAsync(RemoteMatchFeed remote)
    {
        try{await remote.PublishCosmeticsAsync();}
        catch(Exception e){Callable.From(()=>{if(IsInsideTree())_appearanceScreen.SetStatus("外观暂未同步："+e.Message);}).CallDeferred();}
    }
    private void BindAppearance(IMatchFeed feed)
    {
        AppearanceRuntime.ResetRemote();
        _battle.ClearCosmeticEmotes();
        if(feed is not RemoteMatchFeed remote)
        {
            if(feed is ReplayMatchFeed){AppearanceRuntime.Observer=true;_battle.RefreshCosmetics();return;}
            var deck=_battleAppearanceDeck;
            AppearanceRuntime.LocalProfile=()=>_cosmetics.Resolve(deck?.Id,deck?.Options.MainNation);
            _battle.RefreshCosmetics();return;
        }
        remote.ConfigureCosmetics(_cosmetics);
        AppearanceRuntime.LocalProfile=()=>remote.LocalCosmetics;
        AppearanceRuntime.RemoteProfiles=()=>remote.CosmeticProfiles;AppearanceRuntime.ViewerSeat=()=>remote.Seat;AppearanceRuntime.Observer=remote.IsObserver;
        remote.CosmeticsChanged+=()=>Callable.From(()=>{if(ReferenceEquals(_feed,remote)&&IsInsideTree())_battle.RefreshCosmetics();}).CallDeferred();
        remote.EmoteReceived+=(seat,emote)=>Callable.From(()=>{if(ReferenceEquals(_feed,remote)&&IsInsideTree())_battle.ShowCosmeticEmote(seat==remote.Seat?"self":"enemy",emote);}).CallDeferred();
        remote.CosmeticStatus+=message=>Callable.From(()=>{if(ReferenceEquals(_feed,remote)&&IsInsideTree())_appearanceScreen.SetStatus(message);}).CallDeferred();
        _=PublishAppearanceAsync(remote);_battle.RefreshCosmetics();
    }
}
