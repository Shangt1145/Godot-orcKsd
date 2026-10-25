using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyDebugDeployment()
    {
        try
        {
            await VerifyDebugDeploymentAsync();GD.Print("DEBUG_DEPLOYMENT_VERIFY_OK");
            if(MobilePlatform.IsAndroid)System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("user://debug-deployment-report.json"),"{\"ok\":true,\"graphicalDebug\":true,\"directTarget\":true,\"emptyDrop\":true,\"cancelWithoutPayment\":true}");
            GetTree().Quit();
        }
        catch(Exception error)
        {
            if(MobilePlatform.IsAndroid)System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("user://debug-deployment-report.json"),System.Text.Json.JsonSerializer.Serialize(new{ok=false,error=error.ToString()}));
            GD.PushError(error.ToString());GetTree().Quit(1);
        }
        finally{StopRealMatch();}
    }
    private async Task VerifyDebugDeploymentAsync()
    {
        const string hostId="pack/builtin/4_pioneer_battalion",deployId="pack/builtin/17th_infantry_regiment";
        Show("battle");await StartRealMatchAsync(interactiveMulligan:false,manualOpponentForVerification:true,deckSeed:7,verificationHand:[deployId]);
        var runner=_runner??throw new Exception("No runner.");
        async Task Frame(){runner.Pump();await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
        async Task Until(Func<bool> condition,string failure)
        {var end=Time.GetTicksMsec()+12000;while(!condition()&&Time.GetTicksMsec()<end)await Frame();if(!condition())throw new Exception(failure);}
        async Task Debug(MatchDebugCommand command)
        {var result=await runner.DebugAsync(command);if(!result.Success)throw new Exception(result.Message);await Frame();}
        await _console!.VerifyGraphicalDebugAsync();
        await Debug(new(MatchDebugAction.GainSlots,10));await Debug(new(MatchDebugAction.GainKredits,10));
        await Debug(new(MatchDebugAction.Summon,CardId:hostId));await Debug(new(MatchDebugAction.Summon,CardId:hostId));
        var hosts=runner.CurrentView!.SelfLine.Where(c=>c.CardId==hostId).ToArray();if(hosts.Length!=2)throw new Exception("Need two distinct targets.");
        var first=hosts[0];var second=hosts[1];var requests=0;
        runner.TargetRequested+=(_,_)=>requests++;
        var commands=new List<PlayCard>();void OnCommand(UiCommand c){if(c is PlayCard p)commands.Add(p);}
        _battle.CommandRequested+=OnCommand;
        try
        {
            // Direct arrow: the second candidate receives the effect, independent of deployment gap.
            var actor=runner.CurrentView.SelfHand.Single(c=>c.CardId==deployId);
            await _battle.DragAsync(actor.Uid,_battle.CardPoint(second.Uid));
            await Until(()=>!runner.IsSubmitting&&!runner.TargetPending&&runner.CurrentView!.SelfLine.Any(c=>c.Uid==actor.Uid),"Direct targeted deployment stalled.");
            var submitted=commands.Last();if(submitted.TargetUid!=second.Uid||submitted.GapId is null)throw new Exception("UI dropped the independent target or gap.");
            if(runner.CurrentView!.SelfLine.Single(c=>c.Uid==second.Uid).EffectiveAttack!=second.EffectiveAttack+1||runner.CurrentView.SelfLine.Single(c=>c.Uid==first.Uid).EffectiveAttack!=first.EffectiveAttack)throw new Exception("Deployment effect hit the wrong candidate.");
            if(requests!=0)throw new Exception("Direct arrow unnecessarily requested a second target.");
            GD.Print("DEPLOY_DIRECT_TARGET_OK second-candidate=true gap-and-target=true");
            // Empty drop: no TargetUid, real board arrow answers deferred native pre-play selection.
            await Debug(new(MatchDebugAction.CreateHand,CardId:deployId));
            actor=runner.CurrentView.SelfHand.Single(c=>c.CardId==deployId);
            await _battle.DragAsync(actor.Uid,_battle.DropAtRowEdge(false,true));
            await Until(()=>_battle.ArrowChoiceActive,"Empty-space deployment never opened interactive targeting.");
            if(commands.Last().TargetUid is not null)throw new Exception("Empty-space test accidentally supplied a target.");
            await _battle.DragTargetChoiceAsync(_battle.CardPoint(second.Uid));
            await Until(()=>!runner.IsSubmitting&&!runner.TargetPending&&runner.CurrentView!.SelfLine.Any(c=>c.Uid==actor.Uid),"Empty-space deployment did not settle.");
            if(runner.CurrentView!.SelfLine.Single(c=>c.Uid==second.Uid).EffectiveAttack!=second.EffectiveAttack+2)throw new Exception("Native arrow ignored the selected second unit.");
            GD.Print("DEPLOY_EMPTY_TARGET_OK targetUid=omitted actual-board-arrow=true");
            // Cancel through real right mouse input, before payment or deployment.
            await Debug(new(MatchDebugAction.Destroy,TargetUid:actor.Uid));
            await Debug(new(MatchDebugAction.CreateHand,CardId:deployId));
            actor=runner.CurrentView.SelfHand.Single(c=>c.CardId==deployId);var credits=runner.CurrentView.SelfKredits;var line=runner.CurrentView.SelfLine.Count;
            await _battle.DragAsync(actor.Uid,_battle.DropAtRowEdge(false,false));
            await Until(()=>_battle.ArrowChoiceActive,"Cancel test did not open targeting.");
            GetViewport().PushInput(new InputEventMouseButton{ButtonIndex=MouseButton.Right,Pressed=true,Position=new(20,20),GlobalPosition=new(20,20)},true);
            GetViewport().PushInput(new InputEventMouseButton{ButtonIndex=MouseButton.Right,Pressed=false,Position=new(20,20),GlobalPosition=new(20,20)},true);
            await Until(()=>!runner.IsSubmitting&&!runner.TargetPending,"Cancelled deployment remained parked.");
            if(runner.CurrentView.SelfKredits!=credits||runner.CurrentView.SelfLine.Count!=line||!runner.CurrentView.SelfHand.Any(c=>c.Uid==actor.Uid))throw new Exception("Cancel consumed resources or partially deployed.");
            GD.Print("DEPLOY_CANCEL_TARGET_OK hand=retained resources=unchanged effects=none");
            if(_presentations is { } presentations)await Task.WhenAny(presentations.Completion,Task.Delay(5000));
            await Frame();
            var directory=ProjectSettings.GlobalizePath(MobilePlatform.IsAndroid?"user://debug-deployment":"res://artifacts/debug-deployment");System.IO.Directory.CreateDirectory(directory);
            await _console.CaptureDebugAsync(System.IO.Path.Combine(directory,MobileSim.Enabled?"debug-mobile.png":"debug-desktop.png"));
        }
        finally{_battle.CommandRequested-=OnCommand;}
    }
}
