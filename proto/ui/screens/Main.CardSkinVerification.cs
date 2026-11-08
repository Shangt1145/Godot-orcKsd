using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyCardSkins()
    {
        try
        {
            _clock.SetReducedMotion(true);
            var directory = ProjectSettings.GlobalizePath("res://artifacts/card-skins-workshop"); Directory.CreateDirectory(directory);
            var image = Image.CreateEmpty(500, 701, false, Image.Format.Rgba8); image.Fill(new("17394F"));
            image.FillRect(new(10, 10, 480, 681), new("54706D")); image.FillRect(new(22, 22, 456, 657), new("17394F"));
            image.FillRect(new(40, 120, 420, 390), new("A56C46")); image.FillRect(new(100, 200, 90, 310), new("D6C591"));
            image.FillRect(new(225, 165, 155, 345), new("4B6D55")); image.FillRect(new(22, 540, 456, 15), new("83C8CE"));
            image.SavePng(directory + "/complete-card.png");
            Show("editor"); _workshop.OpenNativePack("orc.bulwark"); await MenuSettledAsync();
            var tile = _workshop.FindChildren("*", "Control", true, false).OfType<CardControl>().First(c=>c.IsVisibleInTree() && c.Definition.CardType == "unit");
            var card = tile.Definition; await DeckClickAsync(tile); await MenuFrameAsync();
            var asset = _nativeWorkshop.Assets().Single(a=>a.AppId==card.CardId);
            var original = _nativeWorkshop.Read(asset.Path);
            var tabs = _workshop.FindChildren("*", "TabContainer", true, false).OfType<TabContainer>().Single(t=>t.IsVisibleInTree());
            tabs.CurrentTab = 2; await MenuFrameAsync(); await DeckClickAsync(_workshop.NativeSkinButton); await MenuFrameAsync();
            var editor = _workshop.FindChildren("*", "Control", true, false).OfType<CardSkinEditor>().Single();
            editor.ImportImage(directory + "/complete-card.png");
            editor.SetVerificationPosition(0, new(.72f, .84f, .18f, .10f));
            await MenuFrameAsync();
            var positioner = editor.FindChildren("*", "Control", true, false).OfType<SkinPositioner>().Single();
            var cost = editor.Draft.CostBox; var from = new Vector2((cost.X+cost.Width/2)*positioner.Size.X,(cost.Y+cost.Height/2)*positioner.Size.Y);
            // Drag through the control's actual input handler; verify the normalized position is written back.
            positioner._GuiInput(new InputEventMouseButton { ButtonIndex=MouseButton.Left,Pressed=true,Position=from });
            positioner._GuiInput(new InputEventMouseMotion { Position=from-new Vector2(35,35) });
            positioner._GuiInput(new InputEventMouseButton { ButtonIndex=MouseButton.Left,Pressed=false,Position=from-new Vector2(35,35) });
            Require(editor.Draft.CostBox.X < .7f, "拖动数值区域没有更新位置");
            await DeckClickAsync(editor.SaveButton);
            var skin = _nativeWorkshop.PreviewSkin(asset.Id);
            Require(skin is not null && skin.CostBox.X < .7f, "完整异画和手动数值位置未保存："+editor.Status);
            Require(_nativeWorkshop.Read(asset.Path)==original, "异画改动了规则定义");
            Require(!editor.FindChildren("*", "Button", true, false).OfType<Button>().Any(b=>b.Text.Contains("卡框")||b.Text.Contains("插画")||b.Text.Contains("装备")), "异画页仍有分层编辑或卡组装备");
            if (DisplayServer.GetName() != "headless") { await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw); GetViewport().GetTexture().GetImage().SavePng(directory+"/editor.png"); }
            editor.GetWindow().QueueFree(); await MenuFrameAsync();
            var archive = directory+"/workshop-skin.zip"; _nativeWorkshop.Export("orc.bulwark", archive, true);
            _nativeWorkshop.Promote(); ReloadPacks();
            Require(AppearanceRuntime.Skin(card.CardId) is not null, "工坊应用修改后异画未进入展示层");
            Show("home"); await MenuSettledAsync();
            var panel = new PanelContainer { Position=new(210,100),Size=new(1150,650) }; AddChild(panel);
            panel.AddThemeStyleboxOverride("panel",MenuStyles.Box(new("18232C"),MenuStyles.Gold,4,20));
            var row = new HBoxContainer { Alignment=BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation",42); panel.AddChild(row);
            var catalogColumn=new VBoxContainer {SizeFlagsVertical=Control.SizeFlags.ShrinkCenter}; row.AddChild(catalogColumn); catalogColumn.AddChild(MenuStyles.Label("完整异画 · 定义数值",17));
            var catalog=new CardControl {SizeFlagsVertical=Control.SizeFlags.ShrinkCenter}; catalogColumn.AddChild(catalog); catalog.Bind(card,_textures,260);
            Require(catalog.Visual.GetChildren().OfType<AlternateCardFace>().Any(),"收藏未显示完整异画");
            var live=new UiCardView { Uid="skin-test",Definition=card,EffectiveCost=7,EffectiveAttack=9,EffectiveDefense=8,Health=2,EffectiveOpCost=3,OwnerSide="self" };
            foreach(var mode in new[]{BattleCardMode.Hand,BattleCardMode.Field})
            {
                var column=new VBoxContainer {SizeFlagsVertical=Control.SizeFlags.ShrinkCenter}; row.AddChild(column); column.AddChild(MenuStyles.Label(mode==BattleCardMode.Hand?"手牌 · 动态数值":"场上 · 动态数值",17));
                var battle=new BattleCard { CustomMinimumSize=new(260,365),Size=new(260,365),SizeFlagsVertical=Control.SizeFlags.ShrinkCenter }; column.AddChild(battle); battle.Bind(live,mode,_textures); Require(!battle.UsesGeneratedFace,"异画仍经过旧模板");
            }
            var hidden=new BattleCard {Size=new(150,210)}; AddChild(hidden); hidden.Bind(live with{Visibility=Visibility.Hidden},BattleCardMode.Hand,_textures); Require(hidden.Mode==BattleCardMode.Hidden,"隐藏卡牌泄露异画"); hidden.QueueFree();
            await MenuFrameAsync();
            if(DisplayServer.GetName()!="headless") {await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);GetViewport().GetTexture().GetImage().SavePng(directory+"/surfaces.png");}
            GD.Print("CARD_SKINS_VERIFY_OK workshop-entry complete-card-only numeric-coordinates drag draft promote export catalogue hand field hidden rules-unchanged"); GetTree().Quit();
        }
        catch(Exception error) {GD.PushError(error.ToString());GetTree().Quit(1);}
    }
}
