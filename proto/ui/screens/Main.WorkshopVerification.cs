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
        async Task Capture(string name)
        {
            if(!OS.GetCmdlineUserArgs().Contains("--capture-workshop"))return;
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            var dir=ProjectSettings.GlobalizePath("res://artifacts/workshop");System.IO.Directory.CreateDirectory(dir);GetViewport().GetTexture().GetImage().SavePng(dir+"/"+name+".png");
        }
        Show("editor");await MenuSettledAsync();_workshop.ShowHome();
        Check(_workshop.PackChoice.IsVisibleInTree() && _workshop.ProjectOpenButtons.Count > 0, "Workshop dashboard must expose create and open actions");
        await Capture("01-home");
        await DeckClickAsync(_workshop.PackChoice);
        Check(_workshop.ConfirmPack.Disabled, "An empty project name must not be accepted");
        await Capture("02-create");
        _workshop.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Title == "创建卡包").QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await VerifyWorkshopRoutesAsync();
        GD.Print("WORKSHOP_VERIFY_OK project-dashboard unified-pack-routes");
    }
}
