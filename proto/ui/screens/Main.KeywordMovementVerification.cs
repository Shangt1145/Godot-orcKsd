using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyKeywordMovement()
    {
        try { await VerifyKeywordMovementAsync(); GD.Print("KEYWORD_DRAG_VERIFY_OK"); GetTree().Quit(); }
        catch(Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
        finally { WorkshopAuthoring.Configure(_workshopLibrary,_diy.Cards); }
    }

    private async Task VerifyKeywordMovementAsync()
    {
        StopRealMatch();Show("battle");
        var folder=System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),"Godot","app_userdata","OrC-KSD · 卡牌指挥部");
        var packs=new DiyWorkshopLibrary(System.IO.Path.Combine(folder,"diy-packs.json"));
        var cards=new DiyCardLibrary(System.IO.Path.Combine(folder,"diy-cards.json"));
        WorkshopAuthoring.Configure(packs,cards.Cards);
        var demo=cards.Cards.Where(c=>c.PackId=="pack/diy-demo-mechanics").ToArray();
        using var session=await OrcMatchSession.CreateFromCatalogAsync(_catalog.Cards,31,"keyword-drag","unused",
            sourceDirectory:RuntimeGameData.Directory("nations"),openingHand:["diy/demo-kuijun","diy/demo-jidong"],customCards:demo);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);
        var (routedUid,mobileUid)=await OrcDragVerification.PrepareMovementAsync(session);
        var presentations=new Queue<(UiPresentationResolution Resolution,UiBattleActions Actions)>();
        session.Host.PresentationReady+=(r,a)=>presentations.Enqueue((r,a));
        Task<UiSubmitOutcome>? submitted=null;var requests=0;var errors=new List<string>();
        session.Host.ErrorRaised+=errors.Add;
        void Submit(UiCommand command)=>submitted=session.SubmitAsync(command);
        void Request(UiTargetRequest request,IUiTargetResponder responder) {requests++;responder.Cancel(request.RequestId);}
        _battle.CommandRequested+=Submit;session.TargetRequested+=Request;
        async Task Render() {session.SettleProjection();_battle.ApplyProjection(session.View,session.Actions);_battle.UpdateInputProjection(session.View,session.Actions);await ToSignal(GetTree().CreateTimer(.5),SceneTreeTimer.SignalName.Timeout);}
        async Task Drag(string uid,Vector2 target)
        {
            submitted=null;await _battle.DragAsync(uid,target);
            if(submitted is null||await submitted.WaitAsync(TimeSpan.FromSeconds(6))!=UiSubmitOutcome.Applied)
                throw new Exception("Drag rejected: "+session.LastRejectionReason+" submitted="+(submitted is not null)+" trace="+_battle.ChannelTrace+" "+_battle.Interaction+" hints="+_battle.LastDragHints);
            session.Pump();
            while(presentations.TryDequeue(out var item))
            {
                _battle.ObservePresentationTurn(item.Resolution);
                await _battle.PresentSequenceAsync(item.Resolution,item.Actions);
            }
            _battle.UpdateInputProjection(session.View,session.Actions);
            if(_battle.ReadInteractionState().PendingActorUids.Contains(uid))throw new Exception("Presentation did not release the actor for the next drag.");
        }
        try
        {
            await Render();session.InteractionStateReader=_battle.ReadInteractionState;
            if(!session.Actions.Moves.Any(m=>m.Uid==routedUid&&m.ToZone=="support"))throw new Exception("No native retreat candidates.");
            var points=OrcDragVerification.Points(session);
            await Drag(routedUid,new(700,555));
            OrcDragVerification.AssertRetreated(session,routedUid,points);
            GD.Print("SAVED_RETREAT_DRAG_OK front-to-support normal-cost normal-opportunity no-picker");
            var target=session.View.EnemyHq!.Uid;var health=session.View.EnemyHq.Health;
            await Drag(mobileUid,_battle.CardPoint(target));
            if(DisplayServer.GetName()!="headless")await ShotAsync(ProjectSettings.GlobalizePath("res://artifacts"),"mobility-after-first");
            if(!session.Actions.AttackPreviews.Any(p=>p.AttackerUid==mobileUid))throw new Exception("Second attack unavailable.");
            await Drag(mobileUid,_battle.CardPoint(target));
            if(session.Actions.AttackPreviews.Any(p=>p.AttackerUid==mobileUid)||OrcDragVerification.HasProgram(session,mobileUid))throw new Exception("Mobility not consumed after second attack.");
            if(session.View.EnemyHq!.Health!=health-6||OrcDragVerification.Points(session)!=points-3)throw new Exception("Two normal attacks did not deal/pay twice.");
            submitted=null;
            await _battle.DragAsync(mobileUid,_battle.CardPoint(target));
            if(submitted is not null||OrcDragVerification.Points(session)!=points-3||session.View.EnemyHq.Health!=health-6)
                throw new Exception("An unavailable third attack was submitted.");
            if(requests!=0||errors.Count!=0)throw new Exception("Unexpected selection/errors: "+string.Join(";",errors));
            GD.Print("SAVED_MOBILITY_DRAG_OK two-normal-attacks keyword-removed no-third-attack no-buttons no-picker");
        }
        finally {_battle.CommandRequested-=Submit;session.TargetRequested-=Request;_battle.ResetDemo();}
    }
}
