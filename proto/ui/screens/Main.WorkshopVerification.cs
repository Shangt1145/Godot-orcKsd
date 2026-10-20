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
        Show("editor");await MenuSettledAsync();_workshop.ShowHome();
        if (!_clock.ReducedMotion)
        {
            await ToSignal(GetTree().CreateTimer(.07),SceneTreeTimer.SignalName.Timeout);
            Check(_workshop.ChoicePhase=="entering","Home did not spread from a stack");
            await Capture("00-home-spreading");
        }
        await Wait(()=>_workshop.ChoicePhase=="ready");
        Check(_workshop.ChoiceCards.Count==2&&_workshop.ChoiceCards.All(c=>c.Size.Y>c.Size.X),"Workshop home must show two portrait paper cards");await Capture("01-home");
        var pair=_workshop.ChoiceCards.ToArray();var before=pair.Select(c=>c.Position).ToArray();
        var spacing=Math.Abs(before[1].X-before[0].X);
        await DeckClickAsync(_workshop.PackChoice);
        if(!_clock.ReducedMotion)
        {
            Check(_workshop.ChoicePhase=="selecting","Choice did not play its selection animation");
            await ToSignal(GetTree().CreateTimer(.04),SceneTreeTimer.SignalName.Timeout);
            Check(pair.All(c=>!c.FaceDown&&c.Scale==Vector2.One&&c.Rotation==0&&c.Modulate.A==1),"Choice must keep both faces full size and opaque");
            Check(Math.Abs(pair[1].Position.X-pair[0].Position.X)<spacing,"Choice cards did not converge");
            Check(pair.Zip(before).All(x=>Math.Abs(x.First.Position.Y-x.Second.Y)<.1f),"Choice moved vertically during collapse");
            await Capture("01a-choice-motion");
            await Wait(()=>_workshop.ChoicePhase=="stacked");
            Check(pair[0].Position.DistanceTo(pair[1].Position)<.1f&&pair[0].ZIndex>pair[1].ZIndex,"Selected face must cover the central stack");
            await Capture("01b-choice-stacked");
        }
        await Wait(()=>GodotObject.IsInstanceValid(_workshop.CreateChoice)&&_workshop.CreateChoice.IsVisibleInTree()&&_workshop.ChoicePhase=="ready");await Capture("02-choice");
        await DeckClickAsync(_workshop.CreateChoice);await Wait(()=>GodotObject.IsInstanceValid(_workshop.PackName)&&_workshop.PackName.IsVisibleInTree());_workshop.PackName.Text="工坊界面验收卡包";
        await DeckClickAsync(_workshop.ConfirmPack);Check(_workshop.CurrentPack is {IsExpansion:false},"Base pack creation did not open a workspace");
        var pack=_workshop.CurrentPack!;
        Check(Enumerable.Range(0,_diyEditor.Nation.ItemCount).Any(i=>_diyEditor.Nation.GetItemMetadata(i).AsString()=="japan"),"Backend countries were truncated to four");
        var priorNation=_diyEditor.Draft.Nation;DiyEditorScreen.Select(_diyEditor.Nation,"japan");_diyEditor.Nation.EmitSignal(OptionButton.SignalName.ItemSelected,(long)_diyEditor.Nation.Selected);
        Check(_diyEditor.Draft.Nation=="japan"&&DiyCardCompiler.Validate(_diyEditor.Draft,_catalog.Cards).Success,"Native country selection did not reach the compiler");
        DiyEditorScreen.Select(_diyEditor.Nation,priorNation);_diyEditor.Nation.EmitSignal(OptionButton.SignalName.ItemSelected,(long)_diyEditor.Nation.Selected);Check(_workshop.Tabs.GetTabCount()==3,"Base pack must have keywords, cards and pool");
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
        _workshop.ShowHome();await Wait(()=>_workshop.ChoicePhase=="ready");await DeckClickAsync(_workshop.ExpansionChoice);await Wait(()=>GodotObject.IsInstanceValid(_workshop.CreateChoice)&&_workshop.CreateChoice.IsVisibleInTree()&&_workshop.ChoicePhase=="ready");await DeckClickAsync(_workshop.CreateChoice);await Wait(()=>GodotObject.IsInstanceValid(_workshop.ConfirmPack)&&_workshop.ConfirmPack.IsVisibleInTree());
        Check(_workshop.ConfirmPack.Disabled,"Expansion creation did not require a base pack");
        DiyEditorScreen.Select(_workshop.BaseChoice,pack.Id);_workshop.BaseChoice.EmitSignal(OptionButton.SignalName.ItemSelected,(long)_workshop.BaseChoice.Selected);_workshop.PackName.Text="工坊界面验收扩展";
        await DeckClickAsync(_workshop.ConfirmPack);var expansion=_workshop.CurrentPack!;Check(expansion.BasePackId==pack.Id&&_workshop.Tabs.GetTabCount()==2,"Expansion workspace is not based on the selected pack");
        Check(WorkshopAuthoring.NationsFor(expansion.Id).Any(n=>n.Id==nation.Id)&&WorkshopAuthoring.KeywordsFor(expansion.Id).Any(k=>k.Id==keyword.Id),"Expansion did not inherit vocabulary");await Capture("05-expansion");
        _diyEditor.CardName.Text="扩展步兵";Check(_diyEditor.Save(),"Expansion card could not save");
        var child=_diyEditor.Draft;Check(DeckCardPacks.Of(child.Id)==pack.Id,"Expansion card did not join parent selection");
        _workshop.ShowHome();_workshop.OpenSelection(true,true);await Wait(()=>_workshop.ChoicePhase=="ready");
        await DeckClickAsync(_workshop.ChoiceCards.Single(c=>c.Caption==expansion.Name));await Wait(()=>_workshop.ChoicePhase=="selected");
        Check(_workshop.BaseChoice.IsVisibleInTree(),"Expansion editor hid its base pack selector");await Capture("06-modify-expansion-cards");
        Check(DiyEditorScreen.Value(_workshop.BaseChoice)==pack.Id,"Modify did not retain its base pack");await DeckClickAsync(_workshop.ConfirmPack);Check(_diyEditor.AuthoredCards.Any(c=>c.Id==child.Id),"Modify workspace lost its own cards");
        _workshop.OpenSelection(false,true);await Wait(()=>_workshop.ChoicePhase=="ready");
        await DeckClickAsync(_workshop.ChoiceCards.Single(c=>c.Caption==pack.Name));await Wait(()=>_workshop.ChoicePhase=="selected");
        _workshop.PackName.Text="已修改的工坊卡包";await Capture("07-modify-pack-cards");await DeckClickAsync(_workshop.ConfirmPack);
        var reloadedPack=_workshopLibrary.Packs.Single(p=>p.Id==pack.Id);
        Check(reloadedPack.Name=="已修改的工坊卡包"&&reloadedPack.Nations.Any(n=>n.Id==nation.Id)&&reloadedPack.Keywords.Any(k=>k.Id==keyword.Id),"Editing metadata lost pack vocabulary");
        Check(_deckService.Packs.Any(p=>p.Id==pack.Id&&p.Name==reloadedPack.Name),"Edited pack name did not reach the backend construction catalog");
        var quiet=_clock.ReducedMotion;
        try{_clock.SetReducedMotion(true);_workshop.ShowHome();await Wait(()=>_workshop.ChoicePhase=="ready");await DeckClickAsync(_workshop.PackChoice);await Wait(()=>_workshop.ChoicePhase=="ready");Check(_workshop.ChoiceCards.All(c=>c.Scale==Vector2.One&&!c.FaceDown),"Reduced motion left cards flipped");}
        finally{_clock.SetReducedMotion(quiet);}
        using var native=await OrcMatchSession.CreateFromCatalogAsync(_catalog.Cards.Where(c=>!c.CardId.StartsWith("diy/",StringComparison.Ordinal)).ToArray(),31,"workshop-ui-native","unused",sourceDirectory:RuntimeGameData.Directory("nations"),openingHand:[saved.Id],customCards:_diy.Cards);
        native.DriveOpponent=false;await native.SettleMulliganAsync(false);var actor=native.View.SelfHand.Single(c=>c.CardId==saved.Id);
        Check(await native.SubmitAsync(new PlayCard(actor.Uid))==UiSubmitOutcome.Applied,"Workshop authored card could not deploy");native.SettleProjection();
        Check(native.View.SelfLine.Single(c=>c.Uid==actor.Uid).EffectiveAttack==saved.Attack+1,"Authored keyword did not execute in the real engine");
        async Task EditIncluded(bool isExpansion,string packId,string cardId,string label,string shot)
        {
            _workshop.OpenSelection(isExpansion,true);await Wait(()=>_workshop.ChoicePhase=="ready");
            var descriptor=BuiltInWorkshopCatalog.Packs(_workshopLibrary).Single(p=>p.Id==packId);
            await DeckClickAsync(_workshop.ChoiceCards.Single(c=>c.Caption==descriptor.Name));
            await Wait(()=>_workshop.ChoicePhase=="selected");await Capture(shot+"-list");
            _workshop.PackName.Text=label+"卡包";await DeckClickAsync(_workshop.ConfirmPack);
            Check(_workshop.CurrentPack?.Id==packId,"Included pack lost its identity in modify flow");
            Check(_diyEditor.Cards.ItemCount>0,"Included cards did not load in the editor list");
            var index=Enumerable.Range(0,_diyEditor.Cards.ItemCount).Single(i=>_diyEditor.Cards.GetItemTooltip(i)==cardId);
            _diyEditor.Cards.Select(index);_diyEditor.Cards.EmitSignal(ItemList.SignalName.ItemSelected,(long)index);
            Check(_diyEditor.Draft.SourceCardId==cardId,"Original card was not editable");
            _diyEditor.CardName.Text=label;_diyEditor.Cost.Value=0;_diyEditor.Attack.Value=7;
            Check(_diyEditor.Save(),"Included card failed to save: "+_diyEditor.Notice);
            Check(!_deckService.CustomErrors.ContainsKey(_diyEditor.Draft.Id),"Included card did not compile: "+_diyEditor.Notice);
            Check(_catalog.Cards.Single(c=>c.CardId==cardId).Name==label&&_deckService.Cards.Single(c=>c.CardId==cardId).BaseAttack==7,"Local edit did not reach catalog and construction");
            await Capture(shot+"-editor");
            var edit=_diyEditor.Draft;
            _workshop.OpenPack(_workshopLibrary.Packs.Single(p=>p.Id==packId));
            Check(_diyEditor.Draft.Id==edit.Id&&_diyEditor.Draft.Name==label,"Reopening included pack lost its saved edit");
            _workshop.Tabs.CurrentTab=isExpansion?1:2;await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await Capture(shot+"-pool");
        }
        await EditIncluded(false,"source","av76/units/12","已修改的随附卡牌","08-included");
        await EditIncluded(true,"pack/exp-winterwar","pack/exp-winterwar/432_infantry_regiment","已修改的内置卡牌","09-builtin");
        using(var editedMatch=await OrcMatchSession.CreateFromCatalogAsync(_catalog.Cards,31,"workshop-included-native","unused",
            sourceDirectory:RuntimeGameData.Directory("nations"),openingHand:["pack/exp-winterwar/432_infantry_regiment"],customCards:_diy.Cards))
        {
            editedMatch.DriveOpponent=false;await editedMatch.SettleMulliganAsync(false);
            var unit=editedMatch.View.SelfHand.Single(c=>c.CardId=="pack/exp-winterwar/432_infantry_regiment");
            Check(await editedMatch.SubmitAsync(new PlayCard(unit.Uid))==UiSubmitOutcome.Applied,"Edited included unit could not deploy");
            editedMatch.SettleProjection();Check(editedMatch.View.SelfLine.Single(c=>c.Uid==unit.Uid).EffectiveAttack==7,"Native deployment used original stats");
        }
        _workshop.Tabs.CurrentTab=0;await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        Check(_diyEditor.DeleteButton.Text=="恢复原版","Included edit did not expose restore original");
        await DeckClickAsync(_diyEditor.DeleteButton);await DeckClickAsync(_diyEditor.DeleteDialog.GetOkButton());
        Check(!_diy.Cards.Any(c=>c.SourceCardId=="pack/exp-winterwar/432_infantry_regiment")
            &&_catalog.Cards.Single(c=>c.CardId=="pack/exp-winterwar/432_infantry_regiment").BaseAttack==1,"Restore original did not remove local override");
        await Capture("10-restored-original");
        GD.Print("WORKSHOP_VERIFY_OK choice-animation create-modify pack expansion-base-required inherited-vocabulary custom-nation imported-icon printed-card-crop editable-frame pool native-keyword-deploy included-native-edit-reopen-deploy");
    }
}
