using Godot;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
namespace Kards.Ui;
public partial class Main
{
    private async void VerifySetupSelection()
    {
        try
        {
            // Isolated verification deck library only: exercise multiple rows and real selection.
            foreach(var nation in _deckService.PackCards.Where(c=>c.CardId.StartsWith("pack/builtin/",StringComparison.Ordinal) && c.CardType=="unit").GroupBy(c=>c.Set,StringComparer.OrdinalIgnoreCase).Take(5))
            {
                var built = LobbyDeck.Build(System.IO.Path.Combine(RuntimeGameData.Directory("kards"),"packs"), requiredLocalId:nation.First().CardId.Split('/').Last());
                var main=built.Faction.ToLowerInvariant();
                _decks.Save(new ConstructedDeck { Name=SetupDeckBack.NationLabel(main)+" · 验证卡组", CardIds=built.CardIds.ToArray(),
                    Options=new DeckOptions { MainNation=main, AllyNation=main=="britain"?"soviet":"britain" } });
            }
            Show("setup"); await MenuSettledAsync(); _setup.Refresh();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(_setup.StartButton.IsVisibleInTree(), "准备页没有出击按钮");
            // Select a visible first-row item through the actual button.
            var tile=_setup.FindChildren("*","Button",true,false).OfType<Button>().First(b=>b.TooltipText.Length>0 && b.TooltipText!=_decks.ActiveDeck?.Name);
            var expected=tile.TooltipText; await DeckClickAsync(tile);
            Require(_decks.ActiveDeck?.Name==expected,"卡背点击未切换当前卡组");
            var folder = ProjectSettings.GlobalizePath("res://artifacts/setup-selection-20261022"); System.IO.Directory.CreateDirectory(folder);
            async Task Capture(string name) { if(DisplayServer.GetName()=="headless")return; await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw); GetViewport().GetTexture().GetImage().SavePng(folder+"/"+name+".png"); }
            await Capture("practice-"+GetViewport().GetVisibleRect().Size.X);
            var friends = _setup.FindChildren("*", "Button", true, false).OfType<Button>().Single(b=>b.Text=="好友对战  /  局域网");
            await DeckClickAsync(friends);
            Require(_setup.RoomButton.IsVisibleInTree(), "好友模式没有建房入口");
            Require(_setup.StartButton.Text.Contains("房间") || _setup.StartButton.Text.Contains("卡组"), "好友模式主操作错误");
            await Capture("friends-"+GetViewport().GetVisibleRect().Size.X);
            GD.Print("SETUP_SELECTION_VERIFY_OK deck-gallery practice friends discovery " + folder); GetTree().Quit();
        }
        catch(Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
    }
}
