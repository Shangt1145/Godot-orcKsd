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
        while ((TargetChoiceVisible || _choiceRequest is not null || _choosing) && Time.GetTicksMsec() < until)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (TargetChoiceVisible || _choiceRequest is not null) throw new Exception("Choice did not complete its exit.");
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

    /// <summary>Battlefield choices confirm on fresh release, by source drag or direct target click.</summary>
    public async Task VerifyArrowChoicesAsync()
    {
        try
        {
            ResetDemo();
            var source = new UiCardView { Uid = "arrow-source", Definition = _demo.Cards.First(c => c.CardType == "order"),
                EffectiveCost = 2 };
            ApplyProjection(_state with { SelfHand = [source], SelfHandCount = 1 }, new() { CanEndTurn = true });
            var units = _state.SelfLine.Concat(_state.EnemyLine)
                .Where(c => c.Visibility == Visibility.Full && _cards.ContainsKey(c.Uid)).Take(2).ToArray();
            if (units.Length == 0) throw new Exception("Arrow-choice fixture lacks a visible board unit.");
            var request = new UiTargetRequest("arrow-choice", [new("pick", ChoiceKind.Reference, 1, 1,
                units.Select(u => new UiChoice(u.Uid, u.Definition.Name, ChoiceKind.Reference)).ToArray())])
            { SourceCard = source };
            var responder = new MockTargetResponder(request);
            ShowTargetChoice(request, responder);
            if (!TargetChoiceVisible || _choicePanel.Visible || _choicePanel.GetChildCount() != 0 || !_cards[source.Uid].Visible)
                throw new Exception("Arrow choice opened the paper panel or mishandled the source card.");
            async Task Aim(Vector2 local)
            {
                var point = _canvas.GetGlobalTransformWithCanvas() * local;
                GetViewport().PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point }, true);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            var target = _cards[units[0].Uid];
            await Aim(target.Position + target.Size / 2);
            if (!_aim.Visible || !_cards[units[0].Uid].Targeted
                || _aim.From.DistanceTo(ArrowSourceCenter()) > 1 || _aim.To.DistanceTo(target.Position + target.Size / 2) > 1)
                throw new Exception($"Hovering a unit did not draw a confirming arrow: aim={_aim.Visible} targeted={target.Targeted}.");
            await Aim(new(60, 690));
            if (_cards[units[0].Uid].Targeted || !_aim.Visible) throw new Exception("Empty hover kept a unit highlighted.");
            await ClickFixtureAsync(new(60, 690));
            if (responder.Calls != 0 || _arrowSlot is null) throw new Exception("An empty click committed or dismissed the arrow choice.");
            await DragTargetChoiceAsync(new(60, 690));
            if (responder.Calls != 0 || _arrowSlot is null) throw new Exception("Invalid release committed or closed the choice.");
            await DragTargetChoiceAsync(target.Position + target.Size / 2, () =>
            {
                if (responder.Calls != 0) throw new Exception("The target committed before mouse release.");
                return Task.CompletedTask;
            });
            await WaitChoiceClosedAsync();
            if (responder.Calls != 1 || !_cards[source.Uid].Visible || _arrowSlot is not null
                || _cards.Values.Any(c => c.Targeted) || _aim.Visible)
                throw new Exception($"Arrow choice click did not complete cleanly: calls={responder.Calls} visible={_cards[source.Uid].Visible}.");
            var clickRequest = request with { RequestId = "arrow-target-press-release" };
            var clickResponder = new MockTargetResponder(clickRequest);
            ShowTargetChoice(clickRequest, clickResponder);
            var targetPoint = _canvas.GetGlobalTransformWithCanvas() * CardPoint(units[0].Uid);
            GetViewport().PushInput(new InputEventMouseMotion { Position = targetPoint, GlobalPosition = targetPoint }, true);
            GetViewport().PushInput(new InputEventMouseButton { Position = targetPoint, GlobalPosition = targetPoint,
                ButtonIndex = MouseButton.Left, Pressed = true }, true);
            if (clickResponder.Calls != 0) throw new Exception("Target press confirmed before release.");
            GetViewport().PushInput(new InputEventMouseButton { Position = targetPoint, GlobalPosition = targetPoint,
                ButtonIndex = MouseButton.Left, Pressed = false }, true);
            if (clickResponder.Calls != 1 || TargetChoiceVisible || _choicePanel.Visible)
                throw new Exception("A fresh target click did not confirm on release.");
            clickResponder = new MockTargetResponder(clickRequest);
            ShowTargetChoice(clickRequest, clickResponder);
            ChooseTargetOption("pick",units[0].Uid);
            if(clickResponder.Calls!=0)throw new Exception("Programmatic paper selection submitted a board choice.");
            await DragTargetChoiceAsync(CardPoint(units[0].Uid));await WaitChoiceClosedAsync();
            if(clickResponder.Calls!=1)throw new Exception("Valid source drag was not submitted.");
            var twoSlots = clickRequest with { RequestId = "arrow-two-slots", Slots =
                [clickRequest.Slots[0], clickRequest.Slots[0] with { Name = "second" }] };
            var twoResponder = new MockTargetResponder(twoSlots);
            ShowTargetChoice(twoSlots, twoResponder);
            await DragTargetChoiceAsync(CardPoint(units[0].Uid));
            if (twoResponder.Calls != 0 || _choiceSlot?.Name != "second")
                throw new Exception("First target click did not advance exactly one choice slot.");
            GetViewport().PushInput(new InputEventMouseButton { Position = targetPoint, GlobalPosition = targetPoint,
                ButtonIndex = MouseButton.Left, Pressed = false }, true);
            if (twoResponder.Calls != 0) throw new Exception("An old release answered the next target slot.");
            await DragTargetChoiceAsync(CardPoint(units[0].Uid)); await WaitChoiceClosedAsync();
            if (twoResponder.Calls != 1) throw new Exception("Second target click did not submit the complete native choice.");
            var multi=request with{RequestId="arrow-multi",Slots=[request.Slots[0] with{Min=2,Max=2,BoardTargets=true}]};
            var multiResponder=new MockTargetResponder(multi);ShowTargetChoice(multi,multiResponder);
            if(_choicePanel.Visible)throw new Exception("Multiple board targets opened a card picker.");
            await DragTargetChoiceAsync(CardPoint(units[0].Uid));
            if(multiResponder.Calls!=0)throw new Exception("Multi-target choice submitted early.");
            await DragTargetChoiceAsync(CardPoint(units[1].Uid));await WaitChoiceClosedAsync();
            if(multiResponder.Calls!=1)throw new Exception("Multi-target drag did not complete.");
            var transient=request with{RequestId="arrow-transient",Slots=[request.Slots[0] with{BoardTargets=true,
                Allowed=request.Slots[0].Allowed.Append(new UiChoice("missing-actor","尚未显示的单位",ChoiceKind.Reference)).ToArray()}]};
            ShowTargetChoice(transient,new MockTargetResponder(transient));
            if(!ArrowChoiceActive||_choicePanel.Visible)throw new Exception("Missing board actor fell back to card picking.");
            CancelChoiceWithMouseForVerification(true);await WaitChoiceClosedAsync();
            var retry = request with { RequestId = "arrow-cancel" };
            var cancelResponder = new MockTargetResponder(retry);
            ShowTargetChoice(retry, cancelResponder);
            CancelChoiceWithMouseForVerification(right: true);
            await WaitChoiceClosedAsync();
            if (cancelResponder.Calls != 0 || !_cards[source.Uid].Visible || _arrowSlot is not null)
                throw new Exception("Right-click did not cancel the arrow choice and restore the hand.");
            // The unit stays at its confirmed landing position while choosing its deployment target.
            ResetDemo();
            source = _state.SelfHand.First(c => c.Definition.CardType == "unit");
            _deploymentOrigins[source.Uid] = DeploymentPose.From(_cards[source.Uid]);
            var center = new Vector2(940, SelfArea.Position.Y + FieldCardSize.Y / 2);
            _deploymentChoiceCenters[source.Uid] = center;
            var deployment = request with { RequestId = "deployment-arrow", SourceCard = source };
            var deploymentResponder = new MockTargetResponder(deployment);
            ShowTargetChoice(deployment, deploymentResponder);
            var expectedCenter = new Vector2(BoardSize.X / 2 +
                _cards.Values.Count(c => c.Uid != source.Uid && c.View?.OwnerSide == "self"
                    && (c.View.Zone == "support" || c.View.IsHq)) * SlotPitch / 2, center.Y);
            if (!_cards[source.Uid].Visible || _cards[source.Uid].Mode != BattleCardMode.Field
                || ArrowSourceCenter().DistanceTo(expectedCenter) > 1 || _choicePanel.Visible)
                throw new Exception("Deployment target arrow did not originate from the confirmed landing preview.");
            // A release left over from the placement gesture cannot answer the new target request.
            var stray = _canvas.GetGlobalTransformWithCanvas() * CardPoint(units[0].Uid);
            GetViewport().PushInput(new InputEventMouseButton { Position = stray, ButtonIndex = MouseButton.Left, Pressed = false }, true);
            if (deploymentResponder.Calls != 0) throw new Exception("Placement release also committed the deployment target.");
            CancelChoiceWithMouseForVerification(right: true); await WaitChoiceClosedAsync();
            if (_cards[source.Uid].Mode != BattleCardMode.Hand || _deploymentChoiceCenters.Count != 0
                || _cards[source.Uid].Position != _cards[source.Uid].RestPosition)
                throw new Exception("Cancelled deployment targeting did not restore the hand.");
            // References to hand/pile cards are genuine card picking, not battlefield targeting.
            foreach (var (selector, zone) in new[] { ("hand", "hand"), ("cardPickerReference", "deck"), ("unknown-card-picker", "discard") })
            {
                ResetDemo();
                var candidate = _state.SelfHand[0] with { Uid = "pile-reference", Zone = zone };
                var pick = new UiTargetRequest("pile-" + zone,
                    [new("pick", ChoiceKind.Reference, 1, 1,
                        [new UiChoice(candidate.Uid, candidate.Definition.Name, ChoiceKind.Reference, candidate)])])
                    { SelectorName = selector };
                var picked = new MockTargetResponder(pick);
                ShowTargetChoice(pick, picked);
                if (ArrowChoiceActive || !_choicePanel.Visible)
                    throw new Exception($"A {zone} card choice incorrectly became battlefield targeting.");
                await ClickTargetChoiceAsync(0); await WaitChoiceClosedAsync();
                if (picked.Calls != 1) throw new Exception($"The {zone} card picker did not accept its mouse choice.");
            }
            GD.Print("ARROW_CHOICE_VERIFY_OK no-panel source-drag-or-target-click release-only paper-blocked invalid-release-retry two-slots-no-click-through landing-preview no-placement-click-through right-cancel hand-deck-discard-pickers-preserved");
        }
        finally { ResetDemo(); }
    }

    public async Task DragTargetChoiceAsync(Vector2 target, Func<Task>? onHeld = null)
    {
        var start = _canvas.GetGlobalTransformWithCanvas() * ArrowSourceCenter();
        var end = _canvas.GetGlobalTransformWithCanvas() * target;
        GetViewport().PushInput(new InputEventMouseButton { Position = start, GlobalPosition = start,
            ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
        if(start.DistanceTo(end)<9)
            GetViewport().PushInput(new InputEventMouseMotion { Position=start+new Vector2(40,0),GlobalPosition=start+new Vector2(40,0),
                Relative=new Vector2(40,0),ButtonMask=MouseButtonMask.Left},true);
        GetViewport().PushInput(new InputEventMouseMotion { Position = end, GlobalPosition = end,
            Relative = end - start, ButtonMask = MouseButtonMask.Left }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (onHeld is not null) await onHeld();
        GetViewport().PushInput(new InputEventMouseButton { Position = end, GlobalPosition = end,
            ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
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
