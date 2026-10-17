using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyWorkshop()
    {
        try{await VerifyWorkshopAsync();GetTree().Quit();}catch(Exception e){GD.PushError(e.ToString());StopRealMatch();GetTree().Quit(1);}
    }
    private async Task VerifyWorkshopAsync()
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        async Task Wait(Func<bool> condition)
        {var end=Time.GetTicksMsec()+10000;while(!condition()){if(Time.GetTicksMsec()>end)throw new Exception("Workshop UI timed out");await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}}
        async Task Capture(string name)
        {
            if(!OS.GetCmdlineUserArgs().Contains("--capture-workshop"))return;
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            var dir=ProjectSettings.GlobalizePath("res://artifacts/workshop");System.IO.Directory.CreateDirectory(dir);GetViewport().GetTexture().GetImage().SavePng(dir+"/"+name+".png");
        }
        Show("editor");await MenuSettledAsync();_workshop.ShowHome();await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await Capture("01-home");
        await DeckClickAsync(_workshop.PackChoice);await Wait(()=>GodotObject.IsInstanceValid(_workshop.CreateChoice)&&_workshop.CreateChoice.IsVisibleInTree());await Capture("02-choice");
        await DeckClickAsync(_workshop.CreateChoice);_workshop.PackName.Text="工坊界面验收卡包";
        await DeckClickAsync(_workshop.ConfirmPack);Check(_workshop.CurrentPack is {IsExpansion:false},"Base pack creation did not open a workspace");
        var pack=_workshop.CurrentPack!;Check(_workshop.Tabs.GetTabCount()==3,"Base pack must have keywords, cards and pool");
        _workshop.Tabs.CurrentTab=0;await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        _workshop.KeywordName.Text="界面强化";_workshop.KeywordText.Text="部署：获得 +1+1。";await DeckClickAsync(_workshop.SaveKeyword);
        var keyword=_workshopLibrary.Packs.Single(p=>p.Id==pack.Id).Keywords.Single();
        _workshop.Tabs.CurrentTab=1;await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        _diyEditor.CardName.Text="新国步兵";_diyEditor.Cost.Value=0;
        var box=_diyEditor.FindChildren("*","CheckBox",true,false).OfType<CheckBox>().Single(c=>c.Text==keyword.Name);box.ButtonPressed=true;
        await DiyClickAsync(_diyEditor.CreateNationButton);Check(_diyEditor.NationDialog is {Visible:true},"Nation editor did not open");
        var dialog=_diyEditor.NationDialog!;dialog.FindChildren("*","LineEdit",true,false).OfType<LineEdit>().First().Text="界面新国";
        var icon=Image.CreateEmpty(256,96,false,Image.Format.Rgba8);icon.Fill(new Color("b8493e"));var iconDir=ProjectSettings.GlobalizePath("res://artifacts/workshop");System.IO.Directory.CreateDirectory(iconDir);var iconPath=iconDir+"/flag.png";icon.SavePng(iconPath);
        _diyEditor.OnNationIconChosen(iconPath);await DeckClickAsync(dialog.GetOkButton());
        Check(_diyEditor.Draft.Nation.StartsWith("nation-",StringComparison.Ordinal),"Custom nation was not selected");
        var nation=WorkshopAuthoring.Nations.Single(n=>n.Id==_diyEditor.Draft.Nation);Check(nation.IconFile.Length>0&&WorkshopVisuals.Icon(nation.Id) is not null,"Nation icon was not decoded");
        var originalPath=iconDir+"/printed-card.png";_textures.Get("res://proto/art/kards/bloody_sickle.png")!.GetImage().SavePng(originalPath);_diyEditor.ArtInputKind.Select(0);_diyEditor.OnArtImported(originalPath);
        Check(_diyEditor.Draft.ArtImportMode=="card"&&_diyEditor.Draft.ArtSourceFile.Length>0,"Printed card was not adapted");
        var image=new Image();image.LoadPngFromBuffer(_diy.ReadArt(_diyEditor.Draft.ArtFile));var original=Image.LoadFromFile(originalPath);
        Check(image.GetHeight()<original.GetHeight()*.8f,"Printed rules or stats were retained in the illustration");
        Check(_diyEditor.Draft.ArtRegion.Y+_diyEditor.Draft.ArtRegion.Height<.64f,"Lower printed badge was not excluded from the extracted illustration");
        await Capture("03-adapted-card");await DiyClickAsync(_diyEditor.SaveButton);
        var saved=_diy.Cards.Single(c=>c.Id==_diyEditor.Draft.Id);Check(saved.PackId==pack.Id&&saved.Keywords.Contains(keyword.Id),"Authored card lost membership or keyword");
        Check(_deckService.PlayableIds.Contains(saved.Id),"Custom country/keyword card did not reach the native playable pool: "+_diyEditor.Notice);
        _workshop.Tabs.CurrentTab=2;await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await Capture("04-pool");
        _workshop.ShowHome();await DeckClickAsync(_workshop.ExpansionChoice);await Wait(()=>GodotObject.IsInstanceValid(_workshop.CreateChoice)&&_workshop.CreateChoice.IsVisibleInTree());await DeckClickAsync(_workshop.CreateChoice);
        Check(_workshop.ConfirmPack.Disabled,"Expansion creation did not require a base pack");
        DiyEditorScreen.Select(_workshop.BaseChoice,pack.Id);_workshop.BaseChoice.EmitSignal(OptionButton.SignalName.ItemSelected,(long)_workshop.BaseChoice.Selected);_workshop.PackName.Text="工坊界面验收扩展";
        await DeckClickAsync(_workshop.ConfirmPack);var expansion=_workshop.CurrentPack!;Check(expansion.BasePackId==pack.Id&&_workshop.Tabs.GetTabCount()==2,"Expansion workspace is not based on the selected pack");
        Check(WorkshopAuthoring.NationsFor(expansion.Id).Any(n=>n.Id==nation.Id)&&WorkshopAuthoring.KeywordsFor(expansion.Id).Any(k=>k.Id==keyword.Id),"Expansion did not inherit vocabulary");await Capture("05-expansion");
        _diyEditor.CardName.Text="扩展步兵";Check(_diyEditor.Save(),"Expansion card could not save");
        var child=_diyEditor.Draft;Check(DeckCardPacks.Of(child.Id)==pack.Id,"Expansion card did not join parent selection");
        _workshop.ShowHome();_workshop.OpenSelection(true,true);DiyEditorScreen.Select(_workshop.ModifyPick,expansion.Id);_workshop.ModifyPick.EmitSignal(OptionButton.SignalName.ItemSelected,(long)_workshop.ModifyPick.Selected);
        Check(DiyEditorScreen.Value(_workshop.BaseChoice)==pack.Id,"Modify did not retain its base pack");await DeckClickAsync(_workshop.ConfirmPack);Check(_diyEditor.AuthoredCards.Any(c=>c.Id==child.Id),"Modify workspace lost its own cards");
        using var native=await OrcMatchSession.CreateFromCatalogAsync(_catalog.Cards.Where(c=>!c.CardId.StartsWith("diy/",StringComparison.Ordinal)).ToArray(),31,"workshop-ui-native","unused",sourceDirectory:RuntimeGameData.Directory("nations"),openingHand:[saved.Id],customCards:_diy.Cards);
        native.DriveOpponent=false;await native.SettleMulliganAsync(false);var actor=native.View.SelfHand.Single(c=>c.CardId==saved.Id);
        Check(await native.SubmitAsync(new PlayCard(actor.Uid))==UiSubmitOutcome.Applied,"Workshop authored card could not deploy");native.SettleProjection();
        Check(native.View.SelfLine.Single(c=>c.Uid==actor.Uid).EffectiveAttack==saved.Attack+1,"Authored keyword did not execute in the real engine");
        GD.Print("WORKSHOP_VERIFY_OK choice-animation create-modify pack expansion-base-required inherited-vocabulary custom-nation imported-icon printed-card-crop editable-frame pool native-keyword-deploy");
    }
}
