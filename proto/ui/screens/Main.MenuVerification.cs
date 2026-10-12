using Godot;
using Kards.Ui.Core;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class Main
{
    private sealed class MenuChoiceProbe : IUiTargetResponder
    {
        public bool Cancelled, Completed;
        public bool Complete(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> selections)
        { Completed = selections.TryGetValue("pick", out var ids) && ids.SequenceEqual(new[] { "first" }); return Completed; }
        public bool Cancel(string requestId) { Cancelled = true; return true; }
    }
    private void VerifySplashFadeInput()
    {
        var button = _nav["collection"];
        var at = button.GetGlobalTransformWithCanvas() * (button.Size / 2);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        if (_active != "collection") throw new Exception("Ready splash intercepted input during its exit.");
        _splashFadeInputVerified = true; Show("home");
    }
    private async void VerifyMenu()
    {
        try { await VerifyMenuAsync(); GetTree().Quit(); }
        catch (Exception error) { GD.PushError(error.ToString()); StopRealMatch(); GetTree().Quit(1); }
    }
    private async Task MenuFrameAsync()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    private async Task MenuCaptureAsync(string name)
    {
        await MenuSettledAsync();
        await MenuFrameAsync();
        var viewport = GetViewport().GetVisibleRect();
        var screen = _screens[_active];
        var rect = screen.GetGlobalRect();
        if (rect.End.X > viewport.End.X + 1 || rect.End.Y > viewport.End.Y + 1)
        {
            if (OS.GetCmdlineUserArgs().Contains("--capture-menu")) SaveMenuImage("overflow");
            foreach (var child in screen.GetChildren().OfType<Control>())
            {
                GD.Print($"LAYOUT {child.GetType().Name} size={child.Size} min={child.GetCombinedMinimumSize()}");
                foreach (var inner in child.GetChildren().OfType<Control>()) GD.Print($"  {inner.GetType().Name} size={inner.Size} min={inner.GetCombinedMinimumSize()}");
            }
            throw new Exception($"Menu overflow: {_active}, {rect}, viewport {viewport}.");
        }
        if (!OS.GetCmdlineUserArgs().Contains("--capture-menu")) return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        SaveMenuImage(name);
    }
    private async Task MenuSettledAsync()
    {
        var deadline = Time.GetTicksMsec() + 2000;
        while ((_menuTransition?.IsRunning() == true || (_active == "home" && _home.Hero.IsAnimating)) && Time.GetTicksMsec() < deadline)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_menuTransition?.IsRunning() == true || (_active == "home" && _home.Hero.IsAnimating)) throw new Exception("Menu motion did not settle.");
    }
    private async Task CaptureMenuMotionAsync(string name)
    {
        if (!OS.GetCmdlineUserArgs().Contains("--capture-menu")) return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw); SaveMenuImage(name);
    }
    private async Task VerifyMenuAsync()
    {
        void Check(bool valid, string reason) { if (!valid) throw new Exception(reason); }
        Check(_active == "home" && _runner is null, "Startup must show the home screen without starting a match.");
        Check(_splash is null && _startupStages == 4 && _splashFadeInputVerified, "Startup did not report its real stages or release input when ready.");
        var active = _decks.ActiveId!;
        var reduced = _clock.ReducedMotion;
        var fixtureIds = new List<string>();
        try
        {
            await MenuCaptureAsync("home");
            _clock.SetReducedMotion(false);
            Show("collection");
            Check(_menuTransition?.IsRunning() == true && _collection.Modulate.A < 1, "Page entrance did not start.");
            await CaptureMenuMotionAsync("transition-start");
            await DeckClickAsync(_nav["decklib"]);
            Check(_active == "decklib" && !_deckLibrary.PlaySelection && _runner is null, "Navigation during a transition was blocked.");
            for (var i = 0; i < 12; i++) { Show("home"); Show("collection"); Show("settings"); }
            Show("home"); await CaptureMenuMotionAsync("hero-enter");
            _clock.SetReducedMotion(true);
            Check(_menuTransition is null && _home.Hero.Reveal == 1 && _home.Position == Vector2.Zero && _home.Modulate == Colors.White,
                "Reduced motion did not cancel and restore active motion.");
            Show("collection"); Check(_collection.Position == Vector2.Zero && _collection.Modulate == Colors.White, "Reduced motion still animated page entrance.");
            _clock.SetReducedMotion(false);
            var hoverButton = _nav["settings"];
            var hoverRect = hoverButton.GetGlobalRect();
            var hoverAt = hoverButton.GetGlobalTransformWithCanvas() * (hoverButton.Size / 2);
            GetViewport().PushInput(new InputEventMouseMotion { Position = hoverAt, GlobalPosition = hoverAt }, true);
            await ToSignal(GetTree().CreateTimer(.18), SceneTreeTimer.SignalName.Timeout);
            Check(hoverButton.GetChildren().OfType<MenuHoverAccent>().Single().Amount > .95f && hoverButton.GetGlobalRect() == hoverRect,
                "Button hover did not animate without moving its hit area.");
            await CaptureMenuMotionAsync("button-hover");
            await DeckClickAsync(_nav["decklib"]);
            var copy = _decks.ActiveDeck! with { Id = Guid.NewGuid().ToString("N"), Name = "空中支援" };
            var second = copy with { Id = Guid.NewGuid().ToString("N"), Name = "装甲纵队" };
            var draft = new ConstructedDeck { Name = "未完成的卡组" };
            foreach (var deck in new[] { copy, second, draft }) { _decks.Save(deck); fixtureIds.Add(deck.Id); }
            _deckLibrary.Refresh();
            await DeckClickAsync(_deckLibrary.TileButton(draft.Id));
            Check(_deckLibrary.SelectedDeckId == draft.Id && _decks.ActiveId == active && _deckLibrary.ActionButton("start").Disabled,
                "Selecting a draft must not activate it or permit a match.");
            await MenuCaptureAsync("deck-draft");
            await DeckClickAsync(_deckLibrary.TileButton(copy.Id));
            Check(_deckLibrary.SelectedDeckId == copy.Id && _decks.ActiveId == active, "Browsing decks must not change the active deck.");
            await MenuCaptureAsync("deck-selection");
            await DeckClickAsync(_deckLibrary.ActionButton("activate"));
            Check(_decks.ActiveId == copy.Id, "Explicit activation did not select the deck.");
            await DeckClickAsync(_nav["home"]);
            Check(_active == "home" && _runner is null, "Brand did not return home.");
            await DeckClickAsync(_nav["collection"]);
            Check(_active == "collection" && _nav["collection"].ButtonPressed, "Collection navigation did not select the page.");
            await MenuCaptureAsync("collection");
            await VerifyCollectionScrollAsync();
            var filter = _collection.GetChildren().OfType<HBoxContainer>().First().GetChildren().OfType<Button>().Single(b => b.Text == "筛选");
            await DeckClickAsync(filter); await MenuCaptureAsync("collection-filters");
            await DeckClickAsync(filter);
            await DeckClickAsync(_nav["decklib"]); await DeckClickAsync(_deckLibrary.TileButton(copy.Id)); await DeckClickAsync(_deckLibrary.ActionButton("edit"));
            Check(_active == "deck" && _nav["decklib"].ButtonPressed, "Editing did not retain its card-library context.");
            await MenuCaptureAsync("builder");
            await DeckClickAsync(_nav["editor"]);
            Check(_active == "editor", "DIY navigation did not open the editor."); await MenuCaptureAsync("diy");
            await DeckClickAsync(_nav["settings"]); await MenuCaptureAsync("settings");
            await DeckClickAsync(_nav["decklib"]);
            Check(_active == "decklib" && !_deckLibrary.PlaySelection, "Deck navigation did not open management.");
            await MenuCaptureAsync("decks");
            await DeckClickAsync(_deckLibrary.TileButton(copy.Id));
            await DeckClickAsync(_deckLibrary.ActionButton("start"));
            var deadline = Time.GetTicksMsec() + 20_000;
            while (!_battle.MulliganPanelVisible && Time.GetTicksMsec() < deadline) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(_active == "battle" && _battle.MulliganPanelVisible && _runner?.CurrentView is { } opening
                && opening.SelfHand.All(c => copy.CardIds.Contains(c.Definition.CardId)), "Selected deck did not enter its native match.");
            var runner = _runner;
            Show("home"); await MenuFrameAsync();
            Check(_resume.Visible, "Running match did not expose resume navigation.");
            await DeckClickAsync(_resume);
            Check(_active == "battle" && _runner == runner && _battle.MulliganPanelVisible, "Resume replaced the current match.");
            Check(!_shellBar.Visible && !_menuBackdrop.Visible, "Menu chrome leaked into battle.");
            StopRealMatch(); _battle.ResetDemo(); Show("home"); await MenuSettledAsync();
            await DeckClickAsync(_home.PlayButton);
            deadline = Time.GetTicksMsec() + 20_000;
            while (!_battle.MulliganPanelVisible && Time.GetTicksMsec() < deadline) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(_active == "battle" && _battle.MulliganPanelVisible && _runner?.CurrentView is { } homeOpening
                && homeOpening.SelfHand.All(c => copy.CardIds.Contains(c.Definition.CardId)), "Hero action did not start the saved home deck.");
            // Hidden battle choices must retain their answer without intercepting menu input.
            StopRealMatch(); _battle.ResetDemo();
            var request = new UiTargetRequest("menu-hidden-choice", [new("pick", ChoiceKind.Option, 1, 1,
                [new("first", "第一项", ChoiceKind.Option), new("second", "第二项", ChoiceKind.Option)])]);
            var probe = new MenuChoiceProbe(); _battle.ShowTargetChoice(request, probe);
            Show("collection"); await MenuFrameAsync();
            var at = new Vector2(300, 300);
            GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Right, Pressed = true }, true);
            GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Right, Pressed = false }, true);
            await ToSignal(GetTree().CreateTimer(.8), SceneTreeTimer.SignalName.Timeout);
            Check(!probe.Cancelled && _battle.TargetChoiceVisible, "Menu right click cancelled a hidden battle choice.");
            GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, Pressed = true }, true);
            GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, Pressed = false }, true);
            await MenuFrameAsync(); Check(_active == "home", "Hidden battle choice intercepted menu Escape.");
            Show("battle"); await _battle.ClickTargetChoiceAsync(0);
            deadline = Time.GetTicksMsec() + 3000;
            while (!probe.Completed && Time.GetTicksMsec() < deadline) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(probe.Completed && !probe.Cancelled, "Returning to battle did not preserve its choice.");
            _decks.Save(copy with { Name = new string('长', 64) }); Show("home"); await MenuCaptureAsync("home-long-name");
            GD.Print("MENU_VERIFY_OK actual-startup-stages splash-fade-input single-navigation interruptible-transition hero-start reduced-motion continuous-collection bounded-actors selected-versus-active draft-block native-selected-deck resume-current-match hidden-choice-no-input-leak layout-bounds");
        }
        finally
        {
            StopRealMatch(); _decks.Activate(active);
            foreach (var id in fixtureIds) _decks.Delete(id);
            _clock.SetReducedMotion(reduced);
            Show("home");
        }
    }
    private async Task VerifyCollectionScrollAsync()
    {
        void Check(bool valid, string reason) { if (!valid) throw new Exception(reason); }
        var scroll = _collection.CardScroll;
        var at = scroll.GetGlobalTransformWithCanvas() * new Vector2(60, 80);
        GetViewport().PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.WheelDown, Factor = 4, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.WheelDown, Pressed = false }, true);
        await MenuFrameAsync(); Check(scroll.ScrollVertical > 0, "Native collection mouse wheel did not scroll.");
        var seen = new HashSet<string>(); var maxActors = 0;
        for (var y = 0; y <= scroll.GetVScrollBar().MaxValue; y += 256)
        {
            scroll.ScrollVertical = y; await MenuFrameAsync();
            var actors = _collection.VisibleCards.ToArray(); maxActors = Math.Max(maxActors, actors.Length);
            Check(actors.Select(c => c.Definition.CardId).Distinct().Count() == actors.Length, "A collection window duplicated a card.");
            foreach (var actor in actors) seen.Add(actor.Definition.CardId);
        }
        Check(seen.SetEquals(_collection.Results.Select(c => c.CardId)), "Continuous scrolling omitted or duplicated collection results.");
        Check(maxActors <= 48, $"Collection retained too many card actors: {maxActors}.");
        var last = _collection.VisibleCards.Single(c => c.Definition.CardId == _collection.Results.Last().CardId);
        await DeckClickAsync(last); Check(_collection.SelectedCardId == last.Definition.CardId, "Last collection card was not selectable.");
        await MenuCaptureAsync("collection-bottom");
        var chosen = last.Definition.CardId;
        await DeckClickAsync(_nav["settings"]); await DeckClickAsync(_nav["collection"]); await MenuSettledAsync();
        Check(scroll.ScrollVertical > 0 && _collection.SelectedCardId == chosen, "Collection lost its scroll position or detail on navigation.");
        _collection.Search.Text = chosen; _collection.Search.EmitSignal(LineEdit.SignalName.TextChanged, chosen);
        await MenuFrameAsync();
        Check(scroll.ScrollVertical == 0 && _collection.Results.Count == 1 && _collection.VisibleCards.Single().Definition.CardId == chosen,
            "Filtering did not reset the continuous scroll or retain the matching card.");
        await MenuCaptureAsync("collection-search");
        _collection.Search.Text = "no-card-fixture-4582"; _collection.Search.EmitSignal(LineEdit.SignalName.TextChanged, _collection.Search.Text);
        await MenuFrameAsync(); Check(_collection.Results.Count == 0 && !_collection.VisibleCards.Any(), "Empty search retained obsolete actors.");
        _collection.Search.Text = ""; _collection.Search.EmitSignal(LineEdit.SignalName.TextChanged, "");
        await MenuFrameAsync();
        Check(_collection.Results.Count == _catalog.Cards.Count && scroll.ScrollVertical == 0, "Clearing search did not restore the full collection.");
        GD.Print($"COLLECTION_SCROLL_VERIFY_OK all={seen.Count} max_actors={maxActors} wheel last-card search-reset empty-search preserved-position");
    }
}
