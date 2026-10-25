using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class Main
{
    /// <summary>Real-match deployment of a single named card through the mouse path, with target requests recorded.</summary>
    private async void VerifyDeployCard()
    {
        try { await VerifyDeployCardAsync(); GetTree().Quit(); }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    private async Task VerifyDeployCardAsync()
    {
        var id = OS.GetEnvironment("DEPLOY_CARD_ID") is { Length: > 0 } env ? env : "pack/exp-winterwar/h39_swg";
        // 部署指向是两段式：先落位、再由部署效果问"选谁"。要验第二段，场面必须先有可作用的对象，
        // 所以允许先铺一张垫场单位（DEPLOY_HOST_CARD）。
        var hostId = OS.GetEnvironment("DEPLOY_HOST_CARD") is { Length: > 0 } host ? host : null;
        var hand = hostId is null ? new[] { id } : new[] { hostId, id };
        Show("battle");
        await StartRealMatchAsync(interactiveMulligan: false, manualOpponentForVerification: true,
            deckSeed: 7, verificationHand: hand);
        var runner = _runner!;
        var opening = runner.CurrentView ?? throw new Exception("no projection after start");
        var card = opening.SelfHand.FirstOrDefault(c => c.CardId == id)
            ?? throw new Exception($"verification hand lacks {id}: [{string.Join(' ', opening.SelfHand.Select(c => c.CardId))}]");
        GD.Print($"DEPLOY_CARD_START id={id} uid={card.Uid} hand=[{string.Join(' ', opening.SelfHand.Select(c => c.CardId))}]");

        var requests = new List<string>();
        UiTargetRequest? pending = null; IUiTargetResponder? responder = null;
        void OnTarget(UiTargetRequest request, IUiTargetResponder resp)
        {
            pending = request; responder = resp;
            var slots = string.Join("; ", request.Slots.Select(s => $"{s.Name} {s.Kind} {s.Min}..{s.Max} of {s.Allowed.Count}"));
            requests.Add($"{request.SelectorName}[{slots}]");
            GD.Print($"DEPLOY_TARGET_REQUEST selector={request.SelectorName} drag={request.IsDrag} slots={slots}");
        }
        runner.TargetRequested += OnTarget;
        var errors=new List<string>();
        void OnError(string error)=>errors.Add(error);
        runner.ErrorRaised+=OnError;

        try
        {
            var cost = card.EffectiveCost ?? card.Definition.Cost ?? 4;
            // 攒够这张卡的费用：起始只有 1 点，逐回合推进。对手席是手动的，切过去替它结束回合。
            async Task SettleAsync()
            {
                var until = Time.GetTicksMsec() + 6000;
                while (Time.GetTicksMsec() < until && (runner.IsSubmitting || runner.TargetPending))
                { runner.Pump(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            }
            // 两段式的第一段：趁开局 1 费先把垫场单位正常落位（它自己不带指向）。
            if (hostId is not null && runner.CurrentView!.SelfKredits >= 1)
            {
                var hostCard = runner.CurrentView!.SelfHand.FirstOrDefault(c => c.CardId == hostId)
                    ?? throw new Exception($"verification hand lacks host {hostId}");
                await _battle.DragAsync(hostCard.Uid, _battle.DropAtRowEdge(false, false));
                await SettleAsync();
                var hostLanded = runner.CurrentView!.SelfLine.Any(c => c.Uid == hostCard.Uid);
                GD.Print($"DEPLOY_HOST_RESULT id={hostId} landed={hostLanded} line=[{string.Join(' ', runner.CurrentView!.SelfLine.Select(c => $"{c.CardId}@{c.SlotIndex}"))}]");
                if (!hostLanded) throw new Exception("垫场单位没能落位，第二段无从验证");
            }
            for (var turn = 0; turn < 8 && runner.CurrentView!.SelfKredits < cost; turn++)
            {
                GD.Print($"DEPLOY_CARD_WAIT_FUNDS turn={turn} credits={runner.CurrentView!.SelfKredits} cost={cost} seat={runner.ActiveSeat}");
                await runner.SubmitAsync(new EndTurn());
                await SettleAsync();
                runner.SwitchViewerForVerification();
                await SettleAsync();
                await runner.SubmitAsync(new EndTurn());
                await SettleAsync();
                runner.SwitchViewerForVerification();
                await SettleAsync();
            }
            GD.Print($"DEPLOY_CARD_FUNDED credits={runner.CurrentView!.SelfKredits} cost={cost} seat={runner.ActiveSeat}");

            // 炮兵只能落支援线：front=false 即己方支援带。
            await _battle.DragAsync(card.Uid, _battle.DropAtRowEdge(false, false));
            var deadline = Time.GetTicksMsec() + 12000;
            var answered = false; var settled = false; var waitedForTarget = 0;
            while (Time.GetTicksMsec() < deadline)
            {
                runner.Pump();
                if (pending is not null && responder is not null)
                {
                    if (!answered)
                    {
                        waitedForTarget = 1;
                        if(waitedForTarget==1)GD.Print($"DEPLOY_TARGET_HOLD submitting={runner.IsSubmitting} targetPending={runner.TargetPending}");
                        waitedForTarget=2;
                    }
                    if(_battle.ChoicePanelVisibleForProbe)throw new Exception("Unit target opened card-picking UI.");
                    if(_battle.ArrowChoiceActive)
                    {
                        var request=pending;
                        var target=request.Slots.Where(s=>s.BoardTargets).SelectMany(s=>s.Allowed).FirstOrDefault(c=>_battle.HasCard(c.Id));
                        if(target is not null)
                        {
                            await _battle.DragTargetChoiceAsync(_battle.CardPoint(target.Id));answered=true;
                            GD.Print("DEPLOY_TARGET_MOUSE_DRAG "+target.Id);
                            if(pending==request)pending=null;
                        }
                    }
                }
                var view = runner.CurrentView!;
                if (!runner.IsSubmitting && !runner.TargetPending
                    && (view.SelfLine.Any(c => c.Uid == card.Uid) || !view.SelfHand.Any(c => c.Uid == card.Uid)))
                { settled = true; break; }
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            // 演出是异步的：等它播完再比对投影，否则会把"还没播到扣费"误判成投影不一致。
            if (_presentations is { } presentations) await Task.WhenAny(presentations.Completion, Task.Delay(5000));
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var final = runner.CurrentView!;
            var landed = final.SelfLine.Any(c => c.Uid == card.Uid);
            GD.Print($"DEPLOY_CARD_RESULT id={id} landed={landed} inHand={final.SelfHand.Any(c => c.Uid == card.Uid)} "
                + $"settled={settled} submitting={runner.IsSubmitting} targetPending={runner.TargetPending} "
                + $"targets={requests.Count} held={waitedForTarget} line=[{string.Join(' ', final.SelfLine.Select(c => $"{c.CardId}@{c.SlotIndex}"))}] "
                + $"diff=[{_battle.ProjectionDiff(final)}]");
            // 选靶到底走没走、被谁取消——日志比结果行更能说明问题（这张卡是"先落位再选靶"的两段式）。
            foreach (var entry in Kards.Ui.Core.GameLog.Snapshot()
                         .Where(e => e.Category is "target" or "submit").TakeLast(24))
                GD.Print($"DEPLOY_LOG {entry}");
            if(errors.Count!=0)throw new Exception("Deployment raised native errors: "+string.Join(";",errors));
            if(_battle.ProjectionDiff(final).Any())throw new Exception("Deployment projection did not settle.");
            if (!settled) throw new Exception($"deployment never settled: targets={requests.Count} targetPending={runner.TargetPending}");
            if (!landed) throw new Exception("card left the hand but is not on the line");
            GD.Print("DEPLOY_CARD_VERIFY_OK");
        }
        finally
        {
            runner.TargetRequested -= OnTarget;runner.ErrorRaised-=OnError;
            StopRealMatch(); _battle.ResetDemo();
        }
    }
}
