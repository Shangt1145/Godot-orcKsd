using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    public bool HasLocalTwoPlayerEntry => Enumerable.Range(0, _menu.ItemCount).Any(i =>
        _menu.GetItemId(i) == 26 || _menu.GetItemText(i).Contains("同屏") || _menu.GetItemText(i).Contains("双人"));
    private sealed class RecordingChoiceResponder(UiTargetRequest request, bool rejectFirst = false) : IUiTargetResponder
    {
        private readonly MockTargetResponder _inner = new(request, rejectFirst);
        public IReadOnlyDictionary<string, IReadOnlyList<string>>? Answer { get; private set; }
        public int Calls => _inner.Calls;
        public int CancelCalls { get; private set; }
        public bool Complete(string id, IReadOnlyDictionary<string, IReadOnlyList<string>> selections)
        {
            if (!_inner.Complete(id, selections)) return false;
            Answer = selections.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.ToArray()); return true;
        }
        public bool Cancel(string id) { CancelCalls++; return _inner.Cancel(id); }
    }
    private async Task ClickControlAsync(Control control)
    {
        var at = control.GetGlobalTransformWithCanvas() * (control.Size / 2);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    public async Task VerifyMultiChoicesAsync()
    {
        var reduced = _clock.ReducedMotion;
        var speed = _clock.Current;
        async Task Capture(string name)
        {
            if (!OS.GetCmdlineUserArgs().Contains("--capture-stage6c2")) return;
            var dir = ProjectSettings.GlobalizePath("res://artifacts/stage6c2"); System.IO.Directory.CreateDirectory(dir);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng($"{dir}/{name}.png");
        }
        try
        {
            foreach (var current in Enum.GetValues<AnimClock.Speed>())
            foreach (var quiet in new[] { false, true })
            foreach (var min in new[] { 0, 2 })
            {
                _clock.SetSpeed(current); _clock.SetReducedMotion(quiet);
                ResetDemo();
                var source = _state.SelfHand[0] with { Definition = _demo.Cards.First(c => c.CardType == "order") };
                ApplyProjection(_state with { SelfHand = [source], SelfHandCount = 1 }, new() { CanEndTurn = true });
                var allowed = Enumerable.Range(0, 7).Select(i => new UiChoice(i.ToString(), "候选 " + i, ChoiceKind.CardDefinition,
                    new UiCardView { Uid = "candidate-" + i, Definition = _demo.Cards[i] })).ToArray();
                var request = new UiTargetRequest("multi-" + min, [new("pick", ChoiceKind.CardDefinition, min, 2, allowed)]) { SourceCard = source };
                var responder = new RecordingChoiceResponder(request, rejectFirst: min == 2);
                ShowTargetChoice(request, responder);
                if (_choicePanel.Papers.Count != 5 || _choicePanel.GetChildren().OfType<Button>().Count() != 2
                    || _choicePanel.GetChildren().OfType<Label>().Single().Text != "0/2")
                    throw new Exception("The choice footer must contain only pagination and selected/required.");
                await ClickTargetChoiceAsync(0);
                if (responder.Calls != 0 || !_choicePanel.SelectedIds.SequenceEqual(new[] { "0" })
                    || !_choicePanel.Papers[0].GetChildren().OfType<MulliganStamp>().Single().Marked)
                    throw new Exception("First click committed early or did not use the mulligan stamp.");
                foreach (var key in new[] { Key.Enter, Key.KpEnter, Key.Escape, Key.Space })
                {
                    GetViewport().PushInput(new InputEventKey { Keycode = key, Pressed = true }, true);
                    GetViewport().PushInput(new InputEventKey { Keycode = key, Pressed = false }, true);
                }
                ChooseTargetOptions("pick", []); ChooseTargetOptions("pick", ["0"]);
                if (responder.Calls != 0 || responder.CancelCalls != 0 || !TargetChoiceVisible
                    || _choicePanel.GetChildren().OfType<Label>().Single().Text != "1/2")
                    throw new Exception("A partial/empty/keyboard answer escaped the exact-count mouse policy.");
                await ClickTargetChoiceAsync(0);
                if (_choicePanel.SelectedIds.Count != 0 || _choicePanel.Papers[0].GetChildren().OfType<MulliganStamp>().Single().Marked)
                    throw new Exception("Clicking the marked card did not deselect it.");
                await ClickTargetChoiceAsync(0);
                if (min == 2 && quiet && current == AnimClock.Speed.Normal) await Capture("multi-page-one");
                await ClickControlAsync(_choicePanel.NextPageButton!);
                if (min == 2 && quiet && current == AnimClock.Speed.Normal) await Capture("multi-page-two");
                await ClickTargetChoiceAsync(1); // The second card reaches Max and automatically submits.
                if (min == 2)
                {
                    while (_choosing) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    if (!TargetChoiceVisible || !_choicePanel.SelectedIds.SequenceEqual(new[] { "0", "6" }))
                        throw new Exception("Rejected multi-choice lost its selections.");
                    await ClickControlAsync(_choicePanel.NextPageButton!);
                    await ClickTargetChoiceAsync(1); await ClickTargetChoiceAsync(1); // Toggle off/on to retry once.
                }
                await WaitChoiceClosedAsync();
                if (!responder.Answer!["pick"].SequenceEqual(new[] { "0", "6" }) || _orderChoiceFaces.Count != 0
                    || !_cards[source.Uid].Visible || _orderOrigins[source.Uid].Center.DistanceTo(new(420, 456)) > 1)
                    throw new Exception($"Multi-choice source handoff incorrect: min={min} ids={string.Join(",", responder.Answer!["pick"])} faces={_orderChoiceFaces.Count} visible={_cards[source.Uid].Visible} center={_orderOrigins[source.Uid].Center}.");
                ShowTargetChoice(request, responder = new(request));
                await ClickTargetChoiceAsync(0); CancelChoiceWithMouseForVerification(true); await WaitChoiceClosedAsync();
                if (responder.Answer is not null || _orderOrigins.Count != 0) throw new Exception("Multi cancel committed an answer.");
            }
            ResetDemo();
            var emptyRequest = new UiTargetRequest("zero-maximum", [new("empty", ChoiceKind.Reference, 0, 0, [])]);
            var emptyResponder = new RecordingChoiceResponder(emptyRequest);
            ShowTargetChoice(emptyRequest, emptyResponder);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            ChooseTargetOptions("empty", []);
            GetViewport().PushInput(new InputEventKey { Keycode = Key.Enter, Pressed = true }, true);
            GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, Pressed = true }, true);
            if (emptyResponder.Calls != 0 || emptyResponder.CancelCalls != 0 || !TargetChoiceVisible)
                throw new Exception("An empty request submitted itself or accepted keyboard input.");
            CancelChoiceWithMouseForVerification(true); await WaitChoiceClosedAsync();
            if (emptyResponder.CancelCalls != 1) throw new Exception("Right-click did not release an empty request.");
            ResetDemo();
            GD.Print("MULTI_CHOICE_VERIFY_OK samples=12 real-mouse min-max toggle-off mulligan-red-stamp paging-selected-count-only full-count-auto-submit cross-page-selection rejection-retains-selection no-partial-no-empty no-keyboard-submit-cancel right-cancel source-face-handoff");
        }
        finally { ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced); }
    }
}
