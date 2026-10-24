using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    private async Task CosmeticClickAsync(Control control)
    {
        var point=control.GetGlobalRect().GetCenter();
        GetViewport().PushInput(new InputEventMouseMotion{Position=point,GlobalPosition=point},true);
        GetViewport().PushInput(new InputEventMouseButton{Position=point,GlobalPosition=point,ButtonIndex=MouseButton.Left,Pressed=true},true);
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        GetViewport().PushInput(new InputEventMouseButton{Position=point,GlobalPosition=point,ButtonIndex=MouseButton.Left,Pressed=false},true);
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
    }
    internal async Task VerifySendEmoteAsync(string id)
    {
        await CosmeticClickAsync(_emoteMenu);
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        if(!_emotePanel.Visible)throw new Exception("Emote icon did not open the menu.");
        var index=Array.FindIndex(_equippedEmotes,e=>e.Id==id);
        if(index<0)throw new Exception("Requested emote is not equipped.");
        await CosmeticClickAsync(_emoteChoices.GetChildren().OfType<Button>().ElementAt(index));
        if(_emotePanel.Visible)throw new Exception("Sending an emote did not close its menu.");
    }
    internal async Task VerifyCosmeticMenuAsync(Func<Task> screenshot)
    {
        void Require(bool value,string message){if(!value)throw new Exception(message);}
        await CosmeticClickAsync(_emoteMenu);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        Require(_emotePanel.Visible,"Icon failed to open menu.");
        Require(_emoteChoices.GetChildren().OfType<Button>().Count()==_equippedEmotes.Length+1,"Missing equipped emotes or communication action.");
        await screenshot();
        await CosmeticClickAsync(_emoteChoices.GetChildren().OfType<Button>().Last());
        Require(_communicationsCut,"Communications were not cut.");
        Require(!_canvas.GetChildren().OfType<BattleEmoteBubble>().Any(),"Cut communications retained a visible emote.");
        ShowCosmeticEmote("enemy",new CosmeticEmote("test","不应显示"));ShowCosmeticEmote("self",new CosmeticEmote("test","不应显示"));
        Require(!_canvas.GetChildren().OfType<BattleEmoteBubble>().Any(),"Muted side still displayed emotes.");
        await CosmeticClickAsync(_emoteMenu);
        Require(_emoteChoices.GetChildren().OfType<Button>().SkipLast(1).All(b=>b.Disabled),"Muted menu still permits sending.");
        await CosmeticClickAsync(_emoteChoices.GetChildren().OfType<Button>().Last());
        Require(!_communicationsCut,"Communications were not restored.");
        ShowCosmeticEmote("enemy",new CosmeticEmote("test","通讯恢复"));
        Require(_canvas.GetChildren().OfType<BattleEmoteBubble>().Count()==1,"Restored communications still hide emotes.");
        ClearCosmeticEmotes();
        await CosmeticClickAsync(_emoteMenu);
        GetViewport().PushInput(new InputEventKey{Keycode=Key.Escape,Pressed=true},true);
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        Require(!_emotePanel.Visible,"Escape failed to close menu.");
        GD.Print("EMOTE_MENU_VERIFY_OK native-icon-click eight-rows cut-hides-both disable-send restore escape-close");
    }
}
