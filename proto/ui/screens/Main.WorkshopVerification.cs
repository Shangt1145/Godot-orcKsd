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
        await VerifyWorkshopRoutesAsync();
        GD.Print("WORKSHOP_VERIFY_OK choice-animation unified-pack-routes");
    }
}