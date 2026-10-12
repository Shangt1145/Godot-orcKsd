using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    private sealed class ChainedChoiceResponder(UiTargetRequest request, Action next) : IUiTargetResponder
    {
        private readonly MockTargetResponder _inner = new(request);
        public bool Complete(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> selections)
        { if (!_inner.Complete(requestId, selections)) return false; next(); return true; }
        public bool Cancel(string requestId) => _inner.Cancel(requestId);
    }
    private UiTargetRequest ChoiceFixture(int count, bool definitions, UiCardView? source) => new("choice-visual",
        [new("pick", definitions ? ChoiceKind.CardDefinition : ChoiceKind.Option, 1, 1,
            Enumerable.Range(0, count).Select(i => definitions
                ? new UiChoice(i.ToString(), _demo.Cards[i].Name, ChoiceKind.CardDefinition,
                    new UiCardView { Uid = "candidate-" + i, Definition = _demo.Cards[i], EffectiveCost = _demo.Cards[i].Cost })
                : new UiChoice(i.ToString(), i == 0 ? "使友方总部获得+7防御力" : i == 1 ? "抽两张牌" : "获得一个指挥点槽", ChoiceKind.Option)).ToArray())])
        { SourceCard = source };

    private async Task WaitChoiceClosedAsync()
    {
        var until = Time.GetTicksMsec() + 3000;
        while (TargetChoiceVisible && Time.GetTicksMsec() < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (TargetChoiceVisible) throw new Exception("Choice did not complete its exit.");
    }
    public void CancelChoiceWithMouseForVerification(bool right)
    {
        var at = _canvas.GetGlobalTransformWithCanvas() * new Vector2(1040, 380);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at,
            ButtonIndex = right ? MouseButton.Right : MouseButton.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at,
            ButtonIndex = right ? MouseButton.Right : MouseButton.Left, Pressed = false }, true);
    }
    public async Task VerifyChoiceVisualsAsync()
    {
        var speed = _clock.Current; var reduced = _clock.ReducedMotion; var samples = 0;
        try
        {
            foreach (var current in Enum.GetValues<AnimClock.Speed>())
            foreach (var quiet in new[] { false, true })
            foreach (var count in new[] { 2, 3 })
            foreach (var definitions in new[] { false, true })
            {
                ResetDemo(); _clock.SetSpeed(current); _clock.SetReducedMotion(quiet);
                var source = new UiCardView { Uid = "choice-source", Definition = _demo.Cards.First(c => c.CardId == "deran/command/_13"),
                    EffectiveCost = 2 };
                ApplyProjection(_state with { SelfHand = [source], SelfHandCount = 1 }, new() { CanEndTurn = true, PlayableUids = [source.Uid] });
                _orderOrigins[source.Uid] = new(new(732, 242), new(144, 202), Vector2.One, 0); RestorePendingDeploymentPoses();
                var request = ChoiceFixture(count, definitions, source);
                var responder = new MockTargetResponder(request); ShowTargetChoice(request, responder);
                while (_choicePanel.IsEntering) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (_cards[source.Uid].Visible || _choicePanel.Papers.Count != count || _choicePanel.Papers.Any(c => c.Mode == BattleCardMode.Hidden)
                    || (definitions ? _choicePanel.Papers.Any(c => c.ChoiceCaption is not null)
                        : _choicePanel.Papers.Select(c => c.ChoiceCaption).SequenceEqual(request.Slots[0].Allowed.Select(c => c.Label)) == false))
                    throw new Exception("Paper choices did not show the engine-supplied faces/branch descriptions.");
                ChooseTargetOption("pick", "invalid");
                if (responder.Calls != 0 || !TargetChoiceVisible) throw new Exception("An unavailable choice was committed.");
                await ClickTargetChoiceAsync(count - 1); await WaitChoiceClosedAsync();
                if (responder.Calls != 1 || !_cards[source.Uid].Visible || !_orderOrigins.TryGetValue(source.Uid, out var chosen)
                    || chosen.Center.DistanceTo(new(420, 456)) > 1 || _choicePanel.GetChildCount() != 0)
                    throw new Exception($"Chosen paper transfer failed {current}/{quiet}/{count}/{definitions}: calls={responder.Calls} visible={_cards[source.Uid].Visible} pose={_orderOrigins.GetValueOrDefault(source.Uid)} children={_choicePanel.GetChildCount()}.");
                var faceMatched = false;
                void OnOrder(string phase)
                {
                    if (phase != "order-start") return;
                    var actor = _sequence.GetChildren().OfType<BattleCard>().Single(c => c.Uid == source.Uid);
                    var option = request.Slots[0].Allowed[count - 1];
                    faceMatched = actor.View?.CardId == (definitions ? option.Card!.CardId : source.CardId)
                        && actor.ChoiceCaption == (definitions ? null : option.Label);
                }
                _sequence.PhaseChanged += OnOrder;
                var after = _state with { SelfHand = [], SelfHandCount = 0 };
                try { await PresentSequenceAsync(new(_state.MatchId, [new UiOrderPresentation(source, [])], after), new()); }
                finally { _sequence.PhaseChanged -= OnOrder; }
                if (!faceMatched || _orderChoiceFaces.Count != 0 || _orderChoiceCaptions.Count != 0 || _sequence.LiveCardCount != 0)
                    throw new Exception("Chosen face/branch changed during handoff or retained a temporary paper.");
                samples++;
            }
            // Blank-table and right-button cancellations, and a rejected response followed by retry.
            foreach (var right in new[] { false, true })
            {
                ResetDemo(); var source = _state.SelfHand[0] with { Definition = _demo.Cards.First(c => c.CardType == "order") };
                ApplyProjection(_state with { SelfHand = [source], SelfHandCount = 1 }, new() { CanEndTurn = true });
                _orderOrigins[source.Uid] = new(new(732, 242), new(144, 202), Vector2.One, 0); RestorePendingDeploymentPoses();
                var request = ChoiceFixture(2, false, source); var responder = new MockTargetResponder(request);
                ShowTargetChoice(request, responder);
                CancelChoiceWithMouseForVerification(right);
                await WaitChoiceClosedAsync();
                if (responder.Calls != 0 || _orderOrigins.Count != 0 || !_cards[source.Uid].Visible
                    || _cards[source.Uid].Position != _cards[source.Uid].RestPosition)
                    throw new Exception("Cancelled paper choice did not restore the unplayed hand card.");
            }
            ResetDemo(); var retry = ChoiceFixture(2, false, null); var reject = new MockTargetResponder(retry, true);
            ShowTargetChoice(retry, reject); await ClickTargetChoiceAsync(1);
            while (_choosing) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!TargetChoiceVisible || reject.Calls != 1) throw new Exception("Rejected choice discarded the live request.");
            await ClickTargetChoiceAsync(0); await WaitChoiceClosedAsync();
            if (reject.Calls != 2) throw new Exception("Choice retry was not forwarded.");
            ResetDemo(); var first = ChoiceFixture(3, true, null) with { RequestId = "first-three" };
            var second = ChoiceFixture(3, true, null) with { RequestId = "second-three" };
            var secondResponder = new MockTargetResponder(second);
            ShowTargetChoice(first, new ChainedChoiceResponder(first, () => ShowTargetChoice(second, secondResponder)));
            await ClickTargetChoiceAsync(1);
            while (_choiceRequest?.RequestId == first.RequestId) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!TargetChoiceVisible || _choiceRequest?.RequestId != second.RequestId)
                throw new Exception("The first three-choice close discarded the second request.");
            await ClickTargetChoiceAsync(2); await WaitChoiceClosedAsync();
            if (secondResponder.Calls != 1) throw new Exception("The second three-choice was not completed once.");
            ResetDemo(); ShowTargetChoice(ChoiceFixture(3, true, null), new MockTargetResponder(ChoiceFixture(3, true, null)));
            ResetDemo(); if (TargetChoiceVisible || _choicePanel.GetChildCount() != 0) throw new Exception("Reset retained a choice overlay.");
            GD.Print($"CHOICE_MOTION_VERIFY_OK samples={samples} two-three-paper-options candidate-flips source-hidden chosen-face-handoff mouse-select blank-right-cancel rejected-retry consecutive-three-choices reset-cleanup");
        }
        finally { ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced); }
    }

    public async Task VerifyCounterReturnsAsync()
    {
        var speed = _clock.Current; var reduced = _clock.ReducedMotion; var samples = 0;
        try
        {
            foreach (var current in Enum.GetValues<AnimClock.Speed>())
            foreach (var quiet in new[] { false, true })
            foreach (var kind in new[] { "counter-arm", "counter-disarm" })
            {
                ResetDemo(); _clock.SetSpeed(current); _clock.SetReducedMotion(quiet);
                var fixture = _demo.CreatePresentationScenario(kind); ApplyProjection(fixture.Before, fixture.Actions);
                var view = ((UiCounterPresentation)fixture.Resolution.Steps[0]).Card!;
                var origin = new DeploymentPose(new(788, 258), new(144, 202), Vector2.One * 1.05f, -.08f);
                _orderOrigins[view.Uid] = origin; RestorePendingDeploymentPoses(); var start = false; var returned = false;
                void Phase(string name)
                {
                    var paper = _sequence.GetChildren().OfType<BattleCard>().FirstOrDefault(c => c.Uid == view.Uid);
                    if (name == "counter-return-start") start = paper is not null && DeploymentPose.From(paper) == origin
                        && VisibleCopies(view.Uid) == 1 && IsFriendlyTurn;
                    if (name == "counter-returned") returned = paper is not null && paper.Position.DistanceTo(_cards[view.Uid].RestPosition) < 1;
                }
                _sequence.PhaseChanged += Phase;
                try { await PresentSequenceAsync(fixture.Resolution, fixture.Actions); }
                finally { _sequence.PhaseChanged -= Phase; }
                if (!start || !returned || _orderOrigins.Count != 0 || _sequence.LiveCardCount != 0 || !MatchesProjection(fixture.Resolution.After)
                    || _cards[view.Uid].Mode != BattleCardMode.Hand || !_cards[view.Uid].Visible)
                    throw new Exception($"Counter return failed {current}/{quiet}/{kind}: start={start} returned={returned}.");
                samples++;
            }
            GD.Print($"COUNTER_RETURN_VERIFY_OK samples={samples} exact-release-origin single-actor stays-in-hand notice reservation-refund-projection cleanup");
        }
        finally { ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced); }
    }

    public async Task CaptureOrderExtrasAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir); var frame = 0;
        var speed = _clock.Current; var reduced = _clock.ReducedMotion;
        var sequenceFrames = _sequence.StepFrames; var choiceFrames = _choicePanel.StepFrames;
        _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false); _sequence.StepFrames = _choicePanel.StepFrames = 1;
        async Task Tick()
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SaveJpg($"{dir}/extra_{frame++:D4}.jpg", .95f);
        }
        try
        {
            foreach (var kind in new[] { "counter-arm", "counter-disarm" })
            {
                ResetDemo(); var fixture = _demo.CreatePresentationScenario(kind);
                var card = ((UiCounterPresentation)fixture.Resolution.Steps[0]).Card!;
                ApplyProjection(fixture.Before, new() { CanEndTurn = true, PlayableUids = [card.Uid] });
                for (var i = 0; i < 12; i++) await Tick();
                await DragAsync(card.Uid, new(830, 350));
                if (!_orderOrigins.ContainsKey(card.Uid)) throw new Exception("Counter capture did not perform a legal mouse release.");
                var playback = PresentSequenceAsync(fixture.Resolution, fixture.Actions);
                while (!playback.IsCompleted) await Tick(); await playback;
                for (var i = 0; i < 18; i++) await Tick();
            }
            foreach (var definitions in new[] { false, true })
            {
                ResetDemo(); var definition = _demo.Cards.First(c => c.CardId == "deran/command/_13");
                var source = new UiCardView { Uid = "film-choice", Definition = definition, EffectiveCost = definition.Cost };
                var before = _state with { SelfHand = [source], SelfHandCount = 1 };
                ApplyProjection(before, new() { CanEndTurn = true, PlayableUids = [source.Uid] });
                for (var i = 0; i < 12; i++) await Tick(); await DragAsync(source.Uid, new(830, 350));
                var request = ChoiceFixture(definitions ? 3 : 2, definitions, source);
                ShowTargetChoice(request, new MockTargetResponder(request));
                for (var i = 0; i < 48; i++) await Tick();
                var click = ClickTargetChoiceAsync(definitions ? 2 : 1);
                while (TargetChoiceVisible) await Tick(); await click;
                var playback = PresentSequenceAsync(new(before.MatchId, [new UiOrderPresentation(source, [])],
                    before with { SelfHand = [], SelfHandCount = 0 }), new());
                while (!playback.IsCompleted) await Tick(); await playback;
                for (var i = 0; i < 18; i++) await Tick();
            }
            GD.Print($"ORDER_EXTRAS_FILM_OK frames={frame} dir={dir}");
        }
        finally
        {
            _sequence.StepFrames = sequenceFrames; _choicePanel.StepFrames = choiceFrames;
            ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced);
        }
    }
}
