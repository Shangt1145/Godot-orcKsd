using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyOrders()
    {
        try { await VerifyOrdersStageAsync(); GetTree().Quit(); }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
    private async Task VerifyOrdersStageAsync()
    {
        Show("battle");
        await StartRealMatchAsync(interactiveMulligan: false, hotseat: true, deckSeed: 7,
            verificationHand: ["USG/units/_4", "USG/commands/_7", "USG/commands/_11", "deran/command/_13", "USG/commands/_13"]);
        var runner = _runner ?? throw new Exception("Orders fixture did not start.");
        async Task Settle()
        {
            var until = Time.GetTicksMsec() + 10000;
            while (Time.GetTicksMsec() < until)
            {
                runner.Pump();
                if (!runner.IsSubmitting && !_battle.TargetChoiceVisible && _presentations!.Completion.IsCompleted)
                { await _presentations.Completion; await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); return; }
                await ToSignal(GetTree().CreateTimer(.02), SceneTreeTimer.SignalName.Timeout);
            }
            throw new Exception("Order presentation did not settle.");
        }
        async Task Round()
        {
            await runner.SubmitAsync(new EndTurn()); runner.SwitchSeat();
            await runner.SubmitAsync(new EndTurn()); runner.SwitchSeat(); await Settle();
        }
        UiCardView Hand(string id) => runner.CurrentView!.SelfHand.First(c => c.CardId == id);
        var unit = Hand("USG/units/_4"); var draw = Hand("USG/commands/_7");
        var damage = Hand("USG/commands/_11"); var choice = Hand("deran/command/_13"); var counter = Hand("USG/commands/_13");
        for (var i = 0; i < 5; i++) await Round();
        await _battle.DragAsync(unit.Uid, _battle.DropAtRowEdge(false, false)); await Settle();
        var deck = runner.CurrentView!.SelfDeckCount;
        await _battle.DragAsync(draw.Uid, new(640, 350), () =>
        {
            if (!_battle.Interaction.Contains("sel=" + draw.Uid[..Math.Min(6, draw.Uid.Length)]))
                throw new Exception($"Wrong draw gesture actor: {_battle.Interaction} {_battle.ChannelTrace}");
            return Task.CompletedTask;
        }); await Settle();
        if (runner.CurrentView!.SelfDeckCount != deck - 2 || runner.CurrentView.SelfHand.Any(c => c.Uid == draw.Uid))
            throw new Exception("Mouse draw order did not consume its card and draw two.");
        await Round(); // Seven credits can reserve this counter.
        var hp = runner.CurrentView!.EnemyHq!.Health;
        await _battle.DragAsync(counter.Uid, new(640, 350)); await Settle();
        if (!runner.CurrentView!.SelfHand.Single(c => c.Uid == counter.Uid).IsCounterArmed || runner.CurrentView.EnemyHq!.Health != hp)
            throw new Exception("Counter activation falsely executed its conditional effect.");
        await _battle.DragAsync(counter.Uid, new(640, 350)); await Settle();
        if (runner.CurrentView!.SelfHand.Single(c => c.Uid == counter.Uid).IsCounterArmed || runner.CurrentView.SelfKredits != 7)
            throw new Exception("Mouse counter cancellation did not refund its reservation.");
        await _battle.DragAsync(counter.Uid, new(640, 350)); await Settle(); await Round();
        await _battle.DragAsync(damage.Uid, new(640, 350));
        for (var i = 0; i < 180 && !_battle.TargetChoiceVisible; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!_battle.TargetChoiceVisible) throw new Exception("A targeted command did not open its target panel.");
        if (DisplayServer.GetName() != "headless")
            await ShotAsync(ProjectSettings.GlobalizePath("res://artifacts"), "stage6b2-targets");
        var credits = runner.CurrentView!.SelfKredits;
        _battle.CancelTargetChoice(); await Settle();
        if (!runner.CurrentView!.SelfHand.Any(c => c.Uid == damage.Uid) || runner.CurrentView.SelfKredits != credits)
            throw new Exception("Cancelling target selection consumed a card or credits.");
        await _battle.DragAsync(damage.Uid, new(640, 350));
        for (var i = 0; i < 180 && !_battle.TargetChoiceVisible; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await _battle.ClickTargetChoiceAsync(0); await Settle();
        if (runner.CurrentView!.SelfLine.Any(c => c.Uid == unit.Uid) || runner.CurrentView.EnemyHq!.Health != hp - 8
            || runner.CurrentView.SelfHand.Any(c => c.Uid == counter.Uid || c.Uid == damage.Uid))
            throw new Exception("Mouse damage and friendly-death counter did not consume once and deal the declared damage.");
        await _battle.DragAsync(choice.Uid, new(640, 350));
        for (var i = 0; i < 180 && !_battle.TargetChoiceVisible; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var selfHp = runner.CurrentView!.SelfHq!.Health;
        if (DisplayServer.GetName() != "headless")
            await ShotAsync(ProjectSettings.GlobalizePath("res://artifacts"), "stage6b2-choice");
        await _battle.ClickTargetChoiceAsync(0); await Settle();
        if (runner.CurrentView!.SelfHq!.Health != selfHp + 7 || runner.CurrentView.SelfHand.Any(c => c.Uid == choice.Uid))
            throw new Exception("Mouse choice did not execute the selected branch.");
        GD.Print("ORDERS_STAGE6B2_VERIFY_OK draw target cancel choice counter-arm counter-refund counter-death-trigger mouse-input");
        StopRealMatch(); _battle.ResetDemo();
    }
}
