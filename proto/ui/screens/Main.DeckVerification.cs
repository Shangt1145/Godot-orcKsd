using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyDecks()
    {
        try { await VerifyDecksAsync(); StopRealMatch(); GetTree().Quit(); }
        catch (Exception e) { GD.PushError(e.ToString()); StopRealMatch(); GetTree().Quit(1); }
    }
    private async Task DeckClickAsync(Control control)
    {
        if (_deckPackPicker is not null && _deckPackPicker.Scroll.IsAncestorOf(control))
            _deckPackPicker.Scroll.EnsureControlVisible(control);
        for(var ancestor=control.GetParent();ancestor is not null;ancestor=ancestor.GetParent())
            if(ancestor is ScrollContainer scroll)scroll.EnsureControlVisible(control);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var at = control.GetGlobalTransformWithCanvas() * (control.Size / 2);
        if (control.GetWindow() != GetWindow()) at += control.GetWindow().Position;
        GetViewport().PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    private async Task DeckCaptureAsync(string name)
    {
        if (!OS.GetCmdlineUserArgs().Contains("--capture-decks")) return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var dir = ProjectSettings.GlobalizePath("res://artifacts/decks"); System.IO.Directory.CreateDirectory(dir);
        var picture = GetViewport().GetTexture().GetImage(); picture.SavePng($"{dir}/{name}-{picture.GetWidth()}.png");
    }
    private async Task VerifyDecksAsync()
    {
        await VerifyPackSelectionAsync();
        StopRealMatch(); Show("deck");
        _deckEditor.Load(_decks.ActiveDeck!);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await DeckCaptureAsync("builder");
        if (_deckEditor.StartButton.Disabled) throw new Exception("Starter deck cannot start.");
        var initial = _decks.ActiveId; var count = _decks.Decks.Count;
        _deckEditor.Load(new ConstructedDeck());
        await DeckClickAsync(_deckEditor.FreeNations);
        if (!_deckEditor.Draft.Options.UnrestrictedNations) throw new Exception("Free nation toggle did not apply.");
        await DeckClickAsync(_deckEditor.FreeSize);
        if (!_deckEditor.Draft.Options.UnrestrictedSize || _deckEditor.TotalCards.Editable) throw new Exception("Free size toggle did not apply.");
        _deckEditor.Search.Text = "av76/units/12";
        _deckEditor.Search.EmitSignal(LineEdit.SignalName.TextChanged, _deckEditor.Search.Text);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var face = _deckEditor.Grid.GetChild(0).GetChildren().OfType<CardControl>().Single();
        await DeckClickAsync(face);
        if (_deckEditor.Draft.CardIds.Length != 1) throw new Exception("Card click did not add.");
        var minus = _deckEditor.Rows.GetChildren().OfType<HBoxContainer>().Single().GetChildren().OfType<Button>().Single(b => b.Name == "RemoveCard");
        await DeckClickAsync(minus);
        if (_deckEditor.Draft.CardIds.Length != 0) throw new Exception("Minus did not remove.");
        _deckEditor.DeckName.Text = "鼠标测试草稿";
        _deckEditor.DeckName.EmitSignal(LineEdit.SignalName.TextChanged, _deckEditor.DeckName.Text);
        await DeckClickAsync(_deckEditor.SaveButton);
        if (_decks.Decks.Count != count + 1 || _decks.ActiveId != initial || !_deckEditor.StartButton.Disabled) throw new Exception("Draft save changed active deck or allowed empty deck to start.");
        var reloaded = new DeckLibrary(_deckPath);
        if (reloaded.Decks.Count != count + 1 || reloaded.ActiveId != initial) throw new Exception("Saved deck did not reload.");
        Show("decklib"); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await DeckCaptureAsync("library");
        await DeckClickAsync(_deckLibrary.TileButton(initial!));
        await DeckClickAsync(_deckLibrary.ActionButton("copy"));
        if (_decks.Decks.Count != count + 2 || _decks.ActiveId != initial) throw new Exception("Duplicate replaced active deck.");
        var copied = _decks.Decks.Single(d => d.Name.EndsWith(" 副本"));
        await DeckClickAsync(_deckLibrary.TileButton(copied.Id));
        await DeckClickAsync(_deckLibrary.ActionButton("delete"));
        var deletion = _deckLibrary.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Title == "删除卡组");
        await DeckClickAsync(deletion.GetOkButton());
        if (_decks.Decks.Count != count + 1 || _decks.ActiveId != initial) throw new Exception("Confirmed delete failed.");
        await DeckClickAsync(_deckLibrary.TileButton(_decks.ActiveId!));
        await DeckClickAsync(_deckLibrary.ActionButton("export"));
        var export = _deckLibrary.GetChildren().OfType<AcceptDialog>().Single(d => d.Title == "OrC-KSD 卡组代码");
        var code = export.GetChildren().OfType<VBoxContainer>().Single().GetChildren().OfType<TextEdit>().Single().Text;
        if (!DeckLibrary.Import(code).CardIds.SequenceEqual(_decks.ActiveDeck!.CardIds)) throw new Exception("Export dialog contains wrong list.");
        await DeckClickAsync(export.GetOkButton());
        await DeckClickAsync(_deckLibrary.ImportButton);
        var import = _deckLibrary.GetChildren().OfType<ConfirmationDialog>().Single(d => d.Title == "导入 OrC-KSD 卡组");
        var input = import.GetChildren().OfType<VBoxContainer>().Single().GetChildren().OfType<TextEdit>().Single();
        input.Text = "invalid"; await DeckClickAsync(import.GetOkButton());
        if (!import.Visible || _decks.Decks.Count != count + 1) throw new Exception("Invalid import closed dialog or saved a deck.");
        if (!import.GetChildren().OfType<VBoxContainer>().Single().GetChildren().OfType<Label>().Any(l => l.Text.Contains("ORK1:"))) throw new Exception("Invalid import did not show an error.");
        input.Text = code; await DeckClickAsync(import.GetOkButton());
        if (_decks.Decks.Count != count + 2 || _decks.ActiveId != initial || _active != "deck") throw new Exception("Import did not create a separate editable deck.");
        // Exercise both fixed-size and unrestricted-size decks through the actual start button.
        foreach (var size in new[] { 30, 39 })
        {
            Show("deck"); var deck = _deckService.Starter(size, 5) with { Name = $"自定义 {size + 1} 张", Options = new DeckOptions { UnrestrictedNations = true, TotalCards = size + 1, UnrestrictedSize = size == 30 } };
            _deckEditor.Load(deck); _deckEditor.Search.Text = "";
            await MenuSettledAsync();
            await MenuFrameAsync();
            if (_deckEditor.GetGlobalRect().End.X > GetViewportRect().End.X + 1)
                throw new Exception("Deck builder overflowed after returning from a full-screen match.");
            await DeckClickAsync(_deckEditor.StartButton);
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (!_battle.MulliganPanelVisible && DateTime.UtcNow < deadline) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!_battle.MulliganPanelVisible || _runner?.CurrentView is not { } opening)
                throw new Exception($"Constructed start did not open mulligan. size={size} page={_active} disabled={_deckEditor.StartButton.Disabled} phase={_runner?.CurrentView?.Phase} pending={_runner?.MulliganPending} errors={string.Join(";", _deckService.Validate(_deckEditor.Draft))}");
            // 先手随机：先手起手 4 张、后手 5 张，牌库数必须与之匹配。
            if (opening.SelfHand.Count is not (4 or 5) || opening.SelfDeckCount != size - opening.SelfHand.Count
                || opening.SelfHand.Any(c => !deck.CardIds.Contains(c.Definition.CardId))) throw new Exception("Wrong constructed deck was dealt.");
            _battle.ConfirmMulligan();
            deadline = DateTime.UtcNow.AddSeconds(12);
            while (_runner?.CurrentView?.Phase != "play" && DateTime.UtcNow < deadline) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_runner?.CurrentView?.Phase != "play") throw new Exception("Constructed mulligan did not settle.");
            StopRealMatch();
        }
        GD.Print("DECK_VERIFY_OK mouse-add-remove draft-save reload duplicate confirmed-delete export invalid-import import custom-count unrestricted-nations unrestricted-size exact-deal mulligan");
    }
}
