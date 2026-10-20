using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyTargetDrag()
    {
        try
        {
            StopRealMatch(); Show("battle");
            await _battle.VerifyArrowChoicesAsync();
            await VerifyNativeDeploymentDragAsync();
            await VerifyNoTargetDragAsync();
            GD.Print("TARGET_DRAG_VERIFY_OK"); GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
        finally { WorkshopAuthoring.Configure(_workshopLibrary, _diy.Cards); }
    }

    private async Task VerifyNoTargetDragAsync()
    {
        var root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"OrC-no-target-"+Guid.NewGuid().ToString("N"));
        var library=new DiyWorkshopLibrary(System.IO.Path.Combine(root,"packs.json"));
        var keyword=new DiyKeywordDraft{Name="自动命中总部",ProgramJson=OrcDragVerification.NonTargetOrderProgram()};
        var pack=new DiyPackDraft{Keywords=[keyword]};library.Save(pack);
        var unit=new DiyCardDraft{PackId=pack.Id,Name="无目标单位",Cost=0};
        var order=new DiyCardDraft{PackId=pack.Id,Name="无目标指令",CardType="order",Cost=0,Keywords=[keyword.Id]};
        WorkshopAuthoring.Configure(library,[unit,order]);
        using var session=await OrcMatchSession.CreateFromCatalogAsync(_catalog.Cards,31,"no-target-drag","unused",
            sourceDirectory:RuntimeGameData.Directory("nations"),openingHand:[unit.Id,order.Id],customCards:[unit,order]);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);
        var calls=0;Task<UiSubmitOutcome>? submitted=null;
        void Request(UiTargetRequest request,IUiTargetResponder responder){calls++;responder.Cancel(request.RequestId);}
        void Submit(UiCommand command)=>submitted=session.SubmitAsync(command);
        session.TargetRequested+=Request;_battle.CommandRequested+=Submit;
        try
        {
            session.SettleProjection();_battle.ApplyProjection(session.View,session.Actions);
            var actor=session.View.SelfHand.First(c=>c.CardId==unit.Id);
            await _battle.DragAsync(actor.Uid,_battle.DeploymentGapPoint(0));
            if(submitted is null||await submitted!=UiSubmitOutcome.Applied)throw new Exception("Plain unit did not deploy.");
            session.SettleProjection();_battle.ApplyProjection(session.View,session.Actions);
            if(!session.View.SelfLine.Any(c=>c.Uid==actor.Uid)||calls!=0||_battle.TargetChoiceVisible||_battle.ChoicePanelVisibleForProbe)
                throw new Exception("Plain deployment entered target selection.");
            var command=session.View.SelfHand.First(c=>c.CardId==order.Id);var health=session.View.EnemyHq!.Health;
            if(session.Actions.OrderTargets.Any(t=>t.Uid==command.Uid))throw new Exception("Automatic order projected a target request.");
            submitted=null;await _battle.DragAsync(command.Uid,new(700,350));
            if(submitted is null||await submitted!=UiSubmitOutcome.Applied)throw new Exception("Automatic order did not play.");
            session.SettleProjection();
            if(session.View.EnemyHq!.Health!=health-1||calls!=0||_battle.TargetChoiceVisible||_battle.ChoicePanelVisibleForProbe)
                throw new Exception("Automatic order selected a target or failed to resolve.");
            GD.Print("NO_TARGET_DRAG_VERIFY_OK plain-unit automatic-order zero-target-requests zero-card-picker native-effects");
        }
        finally{session.TargetRequested-=Request;_battle.CommandRequested-=Submit;_battle.ResetDemo();}
    }

    private async Task VerifyNativeDeploymentDragAsync()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OrC-target-drag-" + Guid.NewGuid().ToString("N"));
        var library = new DiyWorkshopLibrary(System.IO.Path.Combine(root, "packs.json"));
        var keyword = new DiyKeywordDraft { Name = "部署支援", ProgramJson = OrcDragVerification.DeploymentProgram() };
        var orderKeyword = new DiyKeywordDraft { Name = "指向射击", ProgramJson = OrcDragVerification.OrderProgram() };
        var pack = new DiyPackDraft { Keywords = [keyword, orderKeyword] }; library.Save(pack);
        var target = new DiyCardDraft { PackId = pack.Id, Name = "支援对象", Cost = 0 };
        var source = new DiyCardDraft { PackId = pack.Id, Name = "指向部署测试", Cost = 1, Keywords = [keyword.Id] };
        var order = new DiyCardDraft { PackId = pack.Id, Name = "直接指向指令", CardType = "order", Cost = 0, Keywords = [orderKeyword.Id] };
        WorkshopAuthoring.Configure(library, [target, source, order]);
        using var session = await OrcMatchSession.CreateFromCatalogAsync(_catalog.Cards, 31, "native-target-drag", "unused",
            sourceDirectory: RuntimeGameData.Directory("nations"), openingHand: [target.Id, source.Id, order.Id], customCards: [target, source, order]);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        var victim = session.View.SelfHand.Single(c => c.CardId == target.Id);
        await session.SubmitAsync(new PlayCard(victim.Uid)); session.SettleProjection();
        var actor = session.View.SelfHand.Single(c => c.CardId == source.Id);
        _battle.ApplyProjection(session.View, session.Actions);
        session.InteractionStateReader = _battle.ReadInteractionState;
        Task<UiSubmitOutcome>? submitted = null;
        var promptCount = 0;
        void Submit(UiCommand command) => submitted = session.SubmitAsync(command);
        void Request(UiTargetRequest request, IUiTargetResponder responder)
        { promptCount++; Callable.From(() => _battle.ShowTargetChoice(request, responder)).CallDeferred(); }
        _battle.CommandRequested += Submit; session.TargetRequested += Request;
        try
        {
            async Task Wait(Func<bool> condition)
            {
                var until = Time.GetTicksMsec() + 8000;
                while (!condition() && Time.GetTicksMsec() < until)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!condition()) throw new Exception("Native deployment target drag timed out.");
            }
            var credits = session.View.SelfKredits;
            await _battle.DragAsync(actor.Uid, _battle.DeploymentGapPoint(session.Actions.DeploymentGaps.Count - 1));
            await Wait(() => _battle.TargetChoiceVisible);
            if (submitted is null || submitted.IsCompleted || session.View.SelfKredits != credits
                || _battle.ReadInteractionState().Phase != UiInteractionPhase.Choosing)
                throw new Exception("Deployment did not wait for its second gesture.");
            _battle.CancelChoiceWithMouseForVerification(true);
            await Wait(() => submitted.IsCompleted);
            if (await submitted != UiSubmitOutcome.Rejected) throw new Exception("Cancelled target unexpectedly played its unit.");
            session.SettleProjection(); _battle.RefuseCommand(new PlayCard(actor.Uid));
            if (session.View.SelfKredits != credits || !session.View.SelfHand.Any(c => c.Uid == actor.Uid))
                throw new Exception("Cancelled deployment spent its card or points.");
            _battle.UpdateInputProjection(session.View, session.Actions);
            await _battle.DragAsync(actor.Uid, _battle.DeploymentGapPoint(session.Actions.DeploymentGaps.Count - 1));
            await Wait(() => _battle.TargetChoiceVisible);
            await _battle.DragTargetChoiceAsync(_battle.CardPoint(victim.Uid), async () =>
            {
                if (submitted!.IsCompleted) throw new Exception("Deployment resolved before target release.");
                if (DisplayServer.GetName() != "headless")
                    await ShotAsync(ProjectSettings.GlobalizePath("res://artifacts"), "deployment-target-drag");
            });
            await Wait(() => submitted!.IsCompleted);
            if (await submitted! != UiSubmitOutcome.Applied) throw new Exception("Target release was not accepted.");
            session.SettleProjection();
            if (session.View.SelfKredits != credits - 1 || !session.View.SelfLine.Any(c => c.Uid == actor.Uid)
                || session.View.SelfLine.Single(c => c.Uid == victim.Uid).EffectiveAttack != target.Attack + 2)
                throw new Exception("Native target release did not deploy, pay and buff exactly once.");
            _battle.ApplyProjection(session.View, session.Actions);
            GD.Print("NATIVE_DEPLOYMENT_DRAG_VERIFY_OK gap-first target-second cancel-no-spend retry release-only custom-keyword");
            await ToSignal(GetTree().CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
            var orderCard = session.View.SelfHand.Single(c => c.CardId == order.Id);
            var health = session.View.SelfLine.Single(c => c.Uid == victim.Uid).EffectiveDefense;
            var promptsBeforeOrder = promptCount;
            submitted = null;
            await _battle.ClickAsync(_battle.GrabPoint(orderCard.Uid));
            if (submitted is not null || promptCount != promptsBeforeOrder)
                throw new Exception("Clicking an order played it without dragging.");
            await _battle.DragAsync(orderCard.Uid, _battle.CardPoint(victim.Uid), async () =>
            {
                _battle.VerifyOrderArrow(orderCard.Uid, victim.Uid);
                if (submitted is not null || _battle.TargetChoiceVisible || promptCount != promptsBeforeOrder)
                    throw new Exception("Targeted order began a play/second selection before release.");
                if (DisplayServer.GetName() != "headless")
                    await ShotAsync(ProjectSettings.GlobalizePath("res://artifacts"), "direct-order-arrow");
            });
            await Wait(() => submitted?.IsCompleted == true);
            if (await submitted! != UiSubmitOutcome.Applied || promptCount != promptsBeforeOrder || _battle.TargetChoiceVisible)
                throw new Exception("Direct order arrow requested a second target gesture.");
            session.SettleProjection();
            if (session.View.SelfHand.Any(c => c.Uid == orderCard.Uid)
                || session.View.SelfLine.Single(c => c.Uid == victim.Uid).EffectiveDefense != health - 1)
                throw new Exception("Direct order drop did not consume the card and hit its selected target.");
            GD.Print("DIRECT_ORDER_ARROW_VERIFY_OK card-stays-in-hand one-gesture release-plays no-second-arrow native-keyword-targets");
        }
        finally { _battle.CommandRequested -= Submit; session.TargetRequested -= Request; _battle.ResetDemo(); }
    }
}
