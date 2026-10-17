using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using System.Security.Cryptography;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyDemoPackTargeting()
    {
        try { await VerifySavedDemoPackAsync(); GetTree().Quit(); }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
        finally { WorkshopAuthoring.Configure(_workshopLibrary, _diy.Cards); }
    }

    private async Task VerifySavedDemoPackAsync()
    {
        StopRealMatch(); Show("battle");
        var folder = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "Godot", "app_userdata", "OrC-KSD · 卡牌指挥部");
        var packsPath = System.IO.Path.Combine(folder, "diy-packs.json");
        var cardsPath = System.IO.Path.Combine(folder, "diy-cards.json");
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path)));
        var packsHash = Hash(packsPath); var cardsHash = Hash(cardsPath);
        var library = new DiyWorkshopLibrary(packsPath); var cards = new DiyCardLibrary(cardsPath);
        if (library.LoadError is not null || cards.LoadError is not null) throw new Exception("Cannot load saved workshop.");
        var pack = library.Packs.Single(p => p.Id == "pack/diy-demo-mechanics");
        var demo = cards.Cards.Where(c => c.PackId == pack.Id).ToArray();
        WorkshopAuthoring.Configure(library, cards.Cards);
        foreach (var draft in demo)
        {
            var result = DiyCardCompiler.Validate(draft, _catalog.Cards);
            if (!result.Success) throw new Exception(draft.Name + ": " + string.Join(";", result.Errors));
        }
        GD.Print($"SAVED_DEMO_PACK_LOADED cards={demo.Length} keywords={pack.Keywords.Length} packsSha256={packsHash} cardsSha256={cardsHash}");
        await VerifySavedRoutedDeploymentAsync(demo);
        using var session = await OrcMatchSession.CreateFromCatalogAsync(_catalog.Cards, 31, "saved-demo-pack-targeting", "unused",
            sourceDirectory: RuntimeGameData.Directory("nations"),
            openingHand: ["diy/demo-buqu", "diy/demo-zhuangtian", "diy/demo-chandou", "diy/demo-fashe"], customCards: demo);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        await OrcDragVerification.FundAsync(session);
        var enemyUid = await OrcDragVerification.JoinSupportAsync(session,1,"diy/demo-jidong");
        session.SettleProjection();
        var victim = session.View.SelfHand.Single(c => c.CardId == "diy/demo-buqu");
        await session.SubmitAsync(new PlayCard(victim.Uid)); session.SettleProjection();
        async Task Render()
        {
            _battle.ApplyProjection(session.View, session.Actions);
            await ToSignal(GetTree().CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
        }
        await Render(); session.InteractionStateReader = _battle.ReadInteractionState;
        Task<UiSubmitOutcome>? submitted = null; var prompts = 0;
        var findings = new List<string>();
        void Submit(UiCommand command) => submitted = session.SubmitAsync(command);
        void Request(UiTargetRequest request, IUiTargetResponder responder)
        { prompts++; Callable.From(() => _battle.ShowTargetChoice(request, responder)).CallDeferred(); }
        _battle.CommandRequested += Submit; session.TargetRequested += Request;
        try
        {
            async Task Wait(Func<bool> condition)
            {
                var until = Time.GetTicksMsec() + 8000;
                while (!condition() && Time.GetTicksMsec() < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!condition()) throw new Exception("Saved demo pack interaction timed out.");
            }
            async Task Direct(UiCardView order, string target, string capture)
            {
                submitted = null; var beforePrompts = prompts;
                await _battle.DragAsync(order.Uid, _battle.CardPoint(target), async () =>
                {
                    _battle.VerifyOrderArrow(order.Uid, target);
                    if (submitted is not null || prompts != beforePrompts || _battle.TargetChoiceVisible)
                        throw new Exception(order.Definition.Name + " submitted before release.");
                    if (DisplayServer.GetName() != "headless")
                        await ShotAsync(ProjectSettings.GlobalizePath("res://artifacts"), capture);
                });
                await Wait(() => submitted?.IsCompleted == true);
                if (await submitted! != UiSubmitOutcome.Applied || prompts != beforePrompts)
                    throw new Exception(order.Definition.Name + " did not play in a single arrow drag.");
                session.SettleProjection(); await Render();
            }
            var load = session.View.SelfHand.Single(c => c.CardId == "diy/demo-zhuangtian");
            var existingFire = session.View.SelfHand.Where(c => c.CardId == "diy/demo-fashe").Select(c => c.Uid).ToHashSet();
            var attack = session.View.SelfLine.Single(c => c.Uid == victim.Uid).EffectiveAttack;
            await Direct(load, victim.Uid, "demo-pack-load-arrow");
            var fire = session.View.SelfHand.Single(c => c.CardId == "diy/demo-fashe" && !existingFire.Contains(c.Uid));
            var recorded = OrcDragVerification.Recorded(session,fire.Uid,"damage");
            if (recorded != attack) throw new Exception("Generated launch did not carry captured attack.");
            var enemyHealth = session.View.EnemyLine.Single(c => c.Uid == enemyUid).EffectiveDefense;
            await Direct(fire, enemyUid, "demo-pack-fire-arrow");
            if (enemyHealth - session.View.EnemyLine.Single(c => c.Uid == enemyUid).EffectiveDefense != recorded)
                throw new Exception("Launch damage did not match its per-instance record.");
            GD.Print($"SAVED_DEMO_LOAD_FIRE_OK load-and-generated-fire direct-arrow release-only no-second-request damage={recorded}");
            var grapple = session.View.SelfHand.Single(c => c.CardId == "diy/demo-chandou");
            submitted = null; var beforePrompts = prompts;
            await _battle.DragAsync(grapple.Uid, _battle.DeploymentGapPoint(session.Actions.DeploymentGaps.Count - 1));
            await Wait(() => _battle.TargetChoiceVisible);
            if (submitted?.IsCompleted != false || prompts != beforePrompts + 1)
                throw new Exception("Grapple did not wait for the separate deployment target drag.");
            // After placement the arrow already follows the pointer. A fresh target press/release
            // must confirm it without requiring another drag from the source unit.
            await _battle.ClickAsync(_battle.CardPoint(enemyUid));
            await Wait(() => submitted?.IsCompleted == true);
            if (await submitted! != UiSubmitOutcome.Applied) throw new Exception("Grapple target release failed.");
            session.SettleProjection(); await Render();
            GD.Print("SAVED_DEMO_GRAPPLE_TARGETING_OK landing-first target-second native-link");
            int Health(string uid) => session.View.SelfLine.Concat(session.View.EnemyLine).FirstOrDefault(c => c.Uid == uid)?.EffectiveDefense ?? 0;
            var standalone = session.View.SelfHand.FirstOrDefault(c => existingFire.Contains(c.Uid));
            if (standalone is not null)
            {
                var pointsBefore = session.View.SelfKredits;
                var hpBefore = Health(enemyUid);
                GD.Print($"SAVED_DEMO_STANDALONE_FIRE hasRecord={OrcDragVerification.HasRecord(session,standalone.Uid)} playable={session.Actions.PlayableUids.Contains(standalone.Uid)}");
                try
                {
                    var outcome = await session.SubmitAsync(new PlayCard(standalone.Uid, TargetUid: enemyUid));
                    session.SettleProjection();
                    GD.Print($"SAVED_DEMO_STANDALONE_FIRE_RESULT outcome={outcome} pointsSpent={pointsBefore-session.View.SelfKredits} stillInHand={session.View.SelfHand.Any(c=>c.Uid==standalone.Uid)} damage={hpBefore-Health(enemyUid)}");
                    if (outcome == UiSubmitOutcome.Applied && hpBefore == Health(enemyUid))
                        findings.Add("Standalone launch has no captured record but is consumed and charged without damage.");
                }
                catch (Exception e) { findings.Add("Standalone launch fault: " + e.Message); GD.Print($"SAVED_DEMO_STANDALONE_FIRE_FAULT {e.GetType().Name}: {e.Message}"); }
            }
            var enemyBeforeTurn = Health(enemyUid);
            var grapplerBeforeTurn = Health(grapple.Uid);
            await OrcDragVerification.CycleTurnAsync(session); session.SettleProjection();
            GD.Print($"SAVED_DEMO_GRAPPLE_TICK enemyHpBefore={enemyBeforeTurn} enemyHpAfter={Health(enemyUid)} selfHpBefore={grapplerBeforeTurn} selfHpAfter={Health(grapple.Uid)}");
            if (enemyBeforeTurn == Health(enemyUid) && grapplerBeforeTurn == Health(grapple.Uid))
                findings.Add("Linked support-line units did not fight at the controller's next turn start.");
        }
        finally
        {
            _battle.CommandRequested -= Submit; session.TargetRequested -= Request; _battle.ResetDemo();
            if (Hash(packsPath) != packsHash || Hash(cardsPath) != cardsHash) throw new Exception("Workshop data changed during read-only verification.");
        }
        foreach (var finding in findings) GD.Print("SAVED_DEMO_PACK_FINDING " + finding);
        GD.Print($"SAVED_DEMO_PACK_AUDIT_COMPLETE targeting=pass findings={findings.Count} workshop-files-unchanged");
    }

    private async Task VerifySavedRoutedDeploymentAsync(DiyCardDraft[] demo)
    {
        using var session = await OrcMatchSession.CreateFromCatalogAsync(_catalog.Cards, 31, "saved-routed-deploy", "unused",
            sourceDirectory: RuntimeGameData.Directory("nations"),
            openingHand: ["diy/demo-kuijun", "diy/demo-zhuangtian", "diy/demo-fashe", "diy/demo-chandou"], customCards: demo);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false);
        foreach (var ownerIndex in new[]{0,1})
            await OrcDragVerification.JoinSupportAsync(session,ownerIndex,"diy/demo-jidong");
        session.SettleProjection();
        _battle.ApplyProjection(session.View, session.Actions);
        session.InteractionStateReader = _battle.ReadInteractionState;
        var actor = session.View.SelfHand.Single(c => c.CardId == "diy/demo-kuijun");
        var requests = new List<string>(); var errors = new List<string>(); Task<UiSubmitOutcome>? submitted = null;
        session.Host.ErrorRaised += errors.Add;
        void Request(UiTargetRequest request, IUiTargetResponder responder)
        { requests.Add(request.SelectorName + ":" + request.SourceCard?.Definition.Name); responder.Cancel(request.RequestId); }
        void Submit(UiCommand command)
        {
            if (command is not PlayCard play || play.Uid != actor.Uid) throw new Exception("Routed-unit gesture selected another hand card.");
            submitted = session.SubmitAsync(command);
        }
        session.TargetRequested += Request; _battle.CommandRequested += Submit;
        try
        {
            await _battle.DragAsync(actor.Uid, _battle.DeploymentGapPoint(0));
            if (submitted is null || await submitted.WaitAsync(TimeSpan.FromSeconds(5)) != UiSubmitOutcome.Applied || requests.Count != 0)
                throw new Exception("Routed deployment unexpectedly requested choices: " + string.Join(",", requests));
            session.SettleProjection();
            session.Pump();
            if (errors.Count != 0) throw new Exception("Routed deployment raised native errors: " + string.Join(";",errors));
            if (!session.View.SelfLine.Any(c => c.Uid == actor.Uid)) throw new Exception("Routed unit did not enter play.");
            GD.Print("SAVED_ROUTED_DEPLOY_OK actual-saved-card occupied-both-support-lines GUI-drag no-card-picker no-native-errors named-gap");
        }
        finally { session.TargetRequested -= Request; _battle.CommandRequested -= Submit; _battle.ResetDemo(); }
    }
}
