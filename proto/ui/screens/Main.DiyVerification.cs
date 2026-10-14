using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyDiy()
    {
        try { await VerifyDiyAsync(); GetTree().Quit(); }
        catch (Exception e) { GD.PushError(e.ToString()); StopRealMatch(); GetTree().Quit(1); }
    }
    private async Task DiyClickAsync(Control control)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_diyEditor.EditorScroll.IsAncestorOf(control))
        {
            _diyEditor.EditorScroll.EnsureControlVisible(control);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        await DeckClickAsync(control);
    }
    private async Task DiyCaptureAsync(string name)
    {
        if (!OS.GetCmdlineUserArgs().Contains("--capture-diy")) return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var directory = ProjectSettings.GlobalizePath("res://artifacts/diy");
        System.IO.Directory.CreateDirectory(directory);
        var image = GetViewport().GetTexture().GetImage();
        image.SavePng($"{directory}/{name}-{image.GetWidth()}.png");
    }
    private async Task VerifyDiyAsync()
    {
        var originalCards = _diy.Cards.Select(c => c.Id).ToHashSet();
        var originalDecks = _decks.Decks.Select(d => d.Id).ToHashSet(); var active = _decks.ActiveId;
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        _clock.SetSpeed(Enum.GetValues<AnimClock.Speed>().Last()); _clock.SetReducedMotion(false);
        StopRealMatch(); Show("editor");
        async Task Until(Func<bool> done)
        {
            var deadline = Time.GetTicksMsec() + 20_000;
            while (!done())
            {
                if (Time.GetTicksMsec() > deadline) throw new Exception("DIY UI verification timed out.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        try
        {
            _diyEditor.Load(new DiyCardDraft());
            _diyEditor.CardName.Text = "DIY 补给步兵"; _diyEditor.Cost.Value = 0; _diyEditor.OpCost.Value = 0;
            DiyEditorScreen.Select(_diyEditor.Art, "USG/units/_4");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await DiyCaptureAsync("initial");
            await DiyClickAsync(_diyEditor.AddEffectButton);
            Check(_diyEditor.EffectRows.Count == 1, "Add effect mouse click failed.");
            _diyEditor.EffectRows[0].Amount.Value = 2;
            await DiyClickAsync(_diyEditor.SaveButton);
            var unit = _diyEditor.Draft;
            Check(_diy.Cards.Any(c => c.Id == unit.Id) && !_diyEditor.IsDirty, "DIY unit was not saved: " + _diyEditor.Notice);
            Check(new DiyCardLibrary(_diyPath).Cards.Any(c => c.Id == unit.Id), "DIY unit did not survive reload.");
            Check(_deckService.PlayableIds.Contains(unit.Id) && _catalog.Cards.Any(c => c.CardId == unit.Id), "Saved DIY did not reach catalog/deck pool.");
            _diyEditor.Attack.Value = 5; await DiyClickAsync(_diyEditor.SaveButton);
            unit = _diyEditor.Draft; Check(_diy.Cards.Single(c => c.Id == unit.Id).Attack == 5, "DIY edit did not overwrite its own identity.");
            _diyEditor.EditorScroll.ScrollVertical = 0;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await DiyCaptureAsync("editor-unit");
            Check(_diyEditor.GetGlobalRect().Encloses(_diyEditor.PreviewPanel.GetGlobalRect()), "DIY preview overflowed editor.");

            // Dirty selection is guarded, then copying produces an independent identity.
            _diyEditor.CardName.Text = "未保存修改";
            _diyEditor.CardName.EmitSignal(LineEdit.SignalName.TextChanged, _diyEditor.CardName.Text);
            await DiyClickAsync(_diyEditor.NewButton);
            Check(_diyEditor.DiscardDialog.Visible && _diyEditor.Draft.Id == unit.Id, "New card discarded changes silently.");
            await DeckClickAsync(_diyEditor.DiscardDialog.GetCancelButton());
            _diyEditor.Load(unit);
            await DiyClickAsync(_diyEditor.CopyButton); await DiyClickAsync(_diyEditor.SaveButton);
            var copied = _diyEditor.Draft; Check(copied.Id != unit.Id, "Copy reused original ID.");
            await DiyClickAsync(_diyEditor.DeleteButton);
            Check(_diy.Cards.Any(c => c.Id == copied.Id), "Delete occurred before confirmation.");
            await DeckClickAsync(_diyEditor.DeleteDialog.GetOkButton());
            Check(!_diy.Cards.Any(c => c.Id == copied.Id) && _diy.Cards.Any(c => c.Id == unit.Id), "Copy deletion damaged original.");

            _diyEditor.Load(unit); await DiyClickAsync(_diyEditor.ExportButton);
            var code = _diyEditor.LastExportCode;
            Check(DiyCardLibrary.Import(code).Name == unit.Name, "Export code has wrong card.");
            await DeckClickAsync(_diyEditor.ActiveExport!.GetOkButton());
            await DiyClickAsync(_diyEditor.ImportButton);
            _diyEditor.ImportText!.Text = "invalid";
            await DeckClickAsync(_diyEditor.ActiveImport!.GetOkButton());
            Check(_diyEditor.ActiveImport!.Visible, "Invalid import closed the dialog.");
            _diyEditor.ImportText.Text = code; await DeckClickAsync(_diyEditor.ActiveImport.GetOkButton());
            Check(_diyEditor.ActiveImport is null && _diyEditor.Draft.Id != unit.Id, "Valid import did not create a new ID.");

            _diyEditor.Load(new DiyCardDraft { CardType = "order", Name = "DIY 战地补给", Cost = 0,
                Effects = [new() { Op = "draw", Amount = 1 }, new() { Op = "buff", Target = "friendlyUnits", Attack = 1, Defense = 2, Until = "turnEnd" }] });
            var buffRow = _diyEditor.EffectRows[1];
            await DiyClickAsync(buffRow.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "↑"));
            Check(_diyEditor.Draft.Effects[0].Op == "buff", "Effect up arrow did not reorder authored operations.");
            await DiyClickAsync(buffRow.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "↓"));
            Check(_diyEditor.Draft.Effects[0].Op == "draw", "Effect down arrow did not restore authored operations.");
            await DiyClickAsync(_diyEditor.SaveButton); var order = _diyEditor.Draft;
            _diyEditor.EditorScroll.ScrollVertical = 0;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await DiyCaptureAsync("editor-order");
            _diyEditor.EditorScroll.EnsureControlVisible(_diyEditor.EffectRows[^1]);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await DiyCaptureAsync("editor-effects");

            // A corrupt but structurally readable saved draft is quarantined, including its original op.
            var invalid = new DiyCardDraft { Id = "diy/invalid-" + Guid.NewGuid().ToString("N"), Effects = [new() { Op = "unknown" }] };
            _diy.Save(invalid); RefreshDiy(); _diyEditor.Load(invalid);
            Check(_diyEditor.SaveButton.Disabled && _diyEditor.Draft.Effects[0].Op == "unknown"
                && !_deckService.PlayableIds.Contains(invalid.Id), "Invalid stored effects were silently repaired/admitted.");
            _diy.Delete(invalid.Id); RefreshDiy();

            // The native mouse card control adds a DIY card to the existing builder and persists exact IDs.
            var baseDeck = _deckService.Starter(5);
            _deckEditor.Load(baseDeck with { Name = "DIY 验收卡组", CardIds = baseDeck.CardIds.Take(4).ToArray() });
            _diyEditor.Load(unit); await DiyClickAsync(_diyEditor.ConstructButton);
            Check(_active == "deck", "DIY construct button did not open builder.");
            _deckEditor.Search.Text = unit.Id;
            _deckEditor.Search.EmitSignal(LineEdit.SignalName.TextChanged, unit.Id);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var face = _deckEditor.Grid.GetChild(0).GetChildren().OfType<CardControl>().Single();
            await DeckClickAsync(face); await DeckClickAsync(_deckEditor.SaveButton);
            Check(_deckEditor.Draft.CardIds.Contains(unit.Id) && _deckService.Validate(_deckEditor.Draft).Count == 0, "DIY builder card was not admitted.");
            await DiyCaptureAsync("builder");

            // Start the ordinary constructed-deck path with the exact saved list.
            await StartRealMatchAsync(false, manualOpponentForVerification: true, deckSeed: 37,
                trialDeck: _deckEditor.Draft);
            Check(_runner?.IsRunning == true && _runner.CardPoolReport!.Verified == 24, "DIY exact constructed match failed.");
            await AwaitIdleAsync(20); StopRealMatch();

            // Then exercise the user-facing trial button, real mulligan and deployment.
            Show("editor"); _diyEditor.Load(unit);
            await DiyClickAsync(_diyEditor.TrialButton);
            await Until(() => _runner?.MulliganPending == true);
            var runner = _runner!; var trialActive = _decks.ActiveId;
            var errors = new List<string>(); runner.ErrorRaised += errors.Add;
            Check(_active == "battle" && _trialDeck!.CardIds.Length == 12 && _trialDeck.CardIds.Count(id => id == unit.Id) == 2, "Trial did not create a small legal deck.");
            await runner.SubmitAsync(new ChooseMulligan(runner.CurrentView!.SelfHand.Select(c => c.Uid).ToArray(), runner.MulliganRequestId));
            runner.Pump(); await AwaitIdleAsync(20);
            var inHand = runner.CurrentView!.SelfHand.First(c => c.CardId == unit.Id);
            Check(inHand.EffectiveAttack == 5 && inHand.Definition.Name == unit.Name, "Trial did not use latest saved definition.");
            await _battle.DragAsync(inHand.Uid, _battle.DropAtRowEdge(false, false));
            await WaitForInputCommitAsync(runner, v => v.SelfLine.Any(c => c.Uid == inHand.Uid));
            await AwaitIdleAsync(20);
            Check(errors.Count == 0, "Native trial effect failed: " + string.Join(";", errors));
            Check(_battle.MatchesProjection(runner.CurrentView!) && _decks.ActiveId == trialActive, "Trial altered active deck or failed to settle.");
            await DiyCaptureAsync("trial");
            StopRealMatch();
            Show("editor"); _diyEditor.Load(order);
            GD.Print("DIY_UI_VERIFY_OK mouse-save edit-reload copy confirmed-delete dirty-guard invalid-and-valid-import export native-json catalog builder exact-deck quarantined-invalid trial-mulligan native-deployment overlay-effects=0");
        }
        finally
        {
            StopRealMatch(); _trialDeck = null; _battle.ResetDemo();
            foreach (var card in _diy.Cards.Where(c => !originalCards.Contains(c.Id))) _diy.Delete(card.Id);
            foreach (var deck in _decks.Decks.Where(d => !originalDecks.Contains(d.Id))) _decks.Delete(deck.Id);
            if (active is not null) _decks.Activate(active);
            RefreshDiy(); _diyEditor.Load(_diy.Cards.FirstOrDefault() ?? new());
            _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet);
        }
    }
}
