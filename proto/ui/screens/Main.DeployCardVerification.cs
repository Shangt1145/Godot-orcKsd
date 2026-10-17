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
        Show("battle");
        await StartRealMatchAsync(interactiveMulligan: false, manualOpponentForVerification: true,
            deckSeed: 7, verificationHand: [id]);
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
                        GD.Print($"DEPLOY_TARGET_HOLD submitting={runner.IsSubmitting} targetPending={runner.TargetPending}");
                    }
                    var picks = pending.Slots.ToDictionary(s => s.Name, s => (IReadOnlyList<string>)
                        s.Allowed.Take(Math.Max(1, s.Min)).Select(c => c.Id).ToList());
                    var ok = responder.Complete(pending.RequestId, picks);
                    answered = true;
                    GD.Print($"DEPLOY_TARGET_ANSWER ok={ok} picks=[{string.Join("; ", picks.Select(p => p.Key + "=" + string.Join(',', p.Value)))}]");
                    pending = null;
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
            if (!settled) throw new Exception($"deployment never settled: targets={requests.Count} targetPending={runner.TargetPending}");
            if (!landed) throw new Exception("card left the hand but is not on the line");
            GD.Print("DEPLOY_CARD_VERIFY_OK");
        }
        finally
        {
            runner.TargetRequested -= OnTarget;
            StopRealMatch(); _battle.ResetDemo();
        }
    }
}
