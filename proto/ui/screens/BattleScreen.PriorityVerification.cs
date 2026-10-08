using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class BattleScreen
{
    public async Task VerifyTurnAndPriorityAsync()
    {
        var speed = _clock.Current; var reduced = _clock.ReducedMotion;
        _clock.SetSpeed(AnimClock.Speed.Faster); _clock.SetReducedMotion(false);
        ResetDemo();
        var initial = UiSnapshots.Freeze(_demo.State with { MatchId = "priority-ui" });
        var actions = UiSnapshots.Freeze(_demo.Actions);
        var commands = 0;
        void OnCommand(UiCommand _) => commands++;
        CommandRequested += OnCommand;
        try
        {
            ApplyProjection(initial, actions); UpdateInputProjection(initial, actions);
            Color ButtonColor() => ((StyleBoxFlat)_endTurn.GetThemeStylebox(_endTurn.Disabled ? "disabled" : "normal")).BgColor;
            if (!EndTurnEnabled || ButtonColor() != new Color("d98632")) throw new Exception("Friendly turn button is not orange and enabled.");
            var uid = initial.SelfHand.First(c => actions.PlayableUids.Contains(c.Uid)).Uid;
            var enemy = initial with { ActivePlayerSide = "enemy" };
            // Deliberately supply stale legal actions: ownership must override every entry point.
            UpdateInputProjection(enemy, actions);
            SelectCard(uid); TryDrop(DropAtRowEdge(false, false)); Submit(new PlayCard(uid)); EndTurnClicked();
            var unitUid = initial.SelfLine.First().Uid;
            SelectCard(unitUid);
            Submit(new MoveUnit(unitUid, "frontline"));
            Submit(new AttackUnit(unitUid, initial.EnemyLine.First().Uid));
            Submit(new CommandUnit(unitUid, initial.EnemyLine.First().Uid));
            await DragAsync(uid, DropAtRowEdge(false, false)); await ClickEndTurnAsync();
            if (commands != 0 || _pressed is not null || _selected is not null || EndTurnEnabled
                || InteractionActions.PlayableUids.Count != 0 || InteractionActions.Moves.Count != 0
                || InteractionActions.AttackPreviews.Count != 0 || ButtonColor() != new Color("666760"))
                throw new Exception("Enemy turn allowed a gesture/command or retained the friendly button color.");
            UpdateInputProjection(initial, actions);
            await DragAsync(uid, DropAtRowEdge(false, false), () =>
            {
                UpdateInputProjection(enemy, actions);
                if (_pressed is not null || _selected is not null) throw new Exception("Turn change did not cancel a held gesture.");
                return Task.CompletedTask;
            });
            if (commands != 0) throw new Exception("A gesture released after turn change submitted a command.");
            UpdateInputProjection(initial, actions with { CanEndTurn = false });
            if (EndTurnEnabled || ButtonColor() != new Color("d98632")) throw new Exception("Button color followed command availability rather than turn ownership.");
            GD.Print("TURN_INPUT_VERIFY_OK enemy-stale-actions blocked-drag-play-move-attack-endturn held-gesture-cancel orange-self gray-enemy");

            ApplyProjection(initial, actions);
            var firstDraw = initial.SelfHand[0] with { Uid = "priority-draw-1", SlotIndex = initial.SelfHand.Count };
            var secondDraw = firstDraw with { Uid = "priority-draw-2", SlotIndex = firstDraw.SlotIndex + 1 };
            var drawn1 = initial with { SelfHand = initial.SelfHand.Append(firstDraw).ToArray(), SelfHandCount = initial.SelfHandCount + 1 };
            var drawn2 = drawn1 with { SelfHand = drawn1.SelfHand.Append(secondDraw).ToArray(), SelfHandCount = drawn1.SelfHandCount + 1 };
            var removed = initial.EnemyLine[0];
            var killed = drawn2 with { EnemyLine = drawn2.EnemyLine.Where(c => c.Uid != removed.Uid).ToArray() };
            var slot = Enumerable.Range(0, initial.SupportLineSlotCount).Except(SupportOccupancy()).First();
            var deployed = initial.SelfHand.First(c => c.Uid == uid) with { Zone = "support", SlotIndex = slot };
            var afterHand = killed.SelfHand.Where(c => c.Uid != uid).Select((c, i) => c with { SlotIndex = i }).ToArray();
            var final = killed with { SelfHand = afterHand, SelfHandCount = afterHand.Length, SelfLine = killed.SelfLine.Append(deployed).ToArray() };
            var played = new List<long>();
            using var player = new PresentationPlayer(initial.MatchId, (r, a, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (r.Sequence == 4 && r.After.SelfHand.Any(c => c.Uid == secondDraw.Uid))
                    throw new Exception("A waiting draw appeared before its animation because a direct action reindexed the hand.");
                played.Add(r.Sequence); return PresentSequenceAsync(r, a);
            }, initial);
            _ = player.Enqueue(new(initial.MatchId, [new UiDrawPresentation("self", firstDraw.SlotIndex, firstDraw)], drawn1) { Sequence = 1 }, actions);
            _ = player.Enqueue(new(initial.MatchId, [new UiDrawPresentation("self", secondDraw.SlotIndex, secondDraw)], drawn2) { Sequence = 2 }, actions);
            _ = player.Enqueue(new(initial.MatchId, [new UiRemovalPresentation(removed)], killed) { Sequence = 3 }, actions);
            _ = player.Enqueue(new(initial.MatchId, [new UiDeploymentPresentation(deployed, final)], final) { Sequence = 4 }, actions);
            UpdateInputProjection(final, actions);
            await player.Completion;
            if (!played.SequenceEqual(new long[] { 1, 4, 2, 3 }) || !MatchesProjection(final)
                || _state.SelfHand.Count != final.SelfHand.Count || _cards.ContainsKey(removed.Uid)
                || _cards[uid].Mode != BattleCardMode.Field || _sequence.LiveCardCount != 0 || _combat.LiveEffectCount != 0)
                throw new Exception($"Priority playback lost order or the final projection: {string.Join(',', played)}.");
            GD.Print("PRESENTATION_PRIORITY_UI_VERIFY_OK active-draw-finishes deployment-overtakes pending-draw-removal FIFO-feedback no-board-rollback effects=0");

            // A defeated unit's slot is immediately available while its disappearance waits.
            // The retained picture must not participate in the next deployment's row layout.
            ApplyProjection(initial, actions);
            var victim = initial.SelfLine.First(c => c.Zone == "support");
            var fallen = initial with { SelfLine = initial.SelfLine.Where(c => c.Uid != victim.Uid).ToArray() };
            var replacement = initial.SelfHand.First(c => c.Uid == uid) with { Zone = "support", SlotIndex = victim.SlotIndex };
            var replacementHand = fallen.SelfHand.Where(c => c.Uid != uid).Select((c, i) => c with { SlotIndex = i }).ToArray();
            var replaced = fallen with { SelfHand = replacementHand, SelfHandCount = replacementHand.Length, SelfLine = fallen.SelfLine.Append(replacement).ToArray() };
            played.Clear(); var checkedSlot = false;
            void OnLand(string phase)
            {
                if (phase != "deployment-landed") return;
                checkedSlot = !_cards.ContainsKey(victim.Uid) && _removalActors.ContainsKey(victim.Uid)
                    && _cards.Values.Count(c => c.View?.OwnerSide == "self" && c.View.Zone == "support"
                        && c.View.SlotIndex == victim.SlotIndex) == 1;
            }
            _sequence.PhaseChanged += OnLand;
            try
            {
                using var deathQueue = new PresentationPlayer(initial.MatchId, (r, a, ct) =>
                { ct.ThrowIfCancellationRequested(); played.Add(r.Sequence); return PresentSequenceAsync(r, a); }, initial);
                _ = deathQueue.Enqueue(new(initial.MatchId,
                    [new UiBoardImpactsPresentation([new(victim, null, victim.Health ?? 3, initial.EnemyLine.First())])], fallen) { Sequence = 1 }, actions);
                _ = deathQueue.Enqueue(new(initial.MatchId, [new UiDeploymentPresentation(replacement, replaced)], replaced) { Sequence = 2 }, actions);
                UpdateInputProjection(replaced, actions); await deathQueue.Completion;
            }
            finally { _sequence.PhaseChanged -= OnLand; }
            if (!checkedSlot || !played.SequenceEqual(new long[] { 1, 2, 1 }) || !MatchesProjection(replaced)
                || _removalActors.Count != 0 || _combat.LiveEffectCount != 0)
                throw new Exception("Deferred death occupied the replacement slot or leaked a retained actor.");
            GD.Print("DEFERRED_REMOVAL_UI_VERIFY_OK attack deployment removal freed-slot actor-cleanup");
        }
        finally
        {
            CommandRequested -= OnCommand; ResetDemo();
            _clock.SetSpeed(speed); _clock.SetReducedMotion(reduced);
        }
    }
}
