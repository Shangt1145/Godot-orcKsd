using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    // Validate screen positions against the gesture's expected UID order, independently of
    // _frontOrder and engine slot numbering. Engine acceptance is covered by bridge tests.
    public async Task VerifyFrontPlacementAsync(Func<string, Task>? capture = null)
    {
        var checks = 0;
        foreach (var occupied in new[] { 0, 1, 2, 3, 4 })
        foreach (var anchor in new[] { "left", "middle", "right", "next" })
        {
            var definition = new UiCardDefinition { CardId = "placement", Name = "落点验证", CardType = "unit", UnitType = "tank" };
            UiCardView Card(string uid, string zone, int slot) => new()
            {
                Uid = uid, Definition = definition with { Name = uid }, Zone = zone, SlotIndex = slot,
                EffectiveAttack = uid == "mover" ? 9 : slot + 1, EffectiveDefense = 5, Health = 5, EffectiveOpCost = 0,
            };
            var front = Enumerable.Range(0, occupied).Select(i => Card($"front-{i}", "frontline", i + 1)).ToArray();
            var mover = Card("mover", "support", 0);
            var before = new UiMatchView
            {
                MatchId = $"placement-{occupied}-{anchor}", Phase = "play", FrontLineSlotCount = 5,
                SupportLineSlotCount = 5, SelfKredits = 5, SelfMaxKredits = 5,
                SelfLine = front.Append(mover).ToArray(),
                SelfHq = Card("hq", "support", 2) with { IsHq = true, Health = 20 },
            };
            var offeredSlot = occupied == 0 && anchor == "right" ? 4 : 0;
            ApplyProjection(before, new() { Moves = [new("mover", "frontline", offeredSlot)] });
            var drop = anchor switch
            {
                "left" => DropAtRowEdge(true, false),
                "right" => DropAtRowEdge(true, true),
                "next" when occupied > 0 => DropRightOf("front-0"),
                _ => DropAtRowMiddle(true),
            };
            var expected = front.Select(c => c.Uid).ToList();
            // Read actual card centres before the gesture, rather than the placement helper's answer.
            var expectedRank = front.Count(c => CardPoint(c.Uid).X <= drop.X);
            expected.Insert(expectedRank, "mover");
            UiCommand? sent = null;
            void OnCommand(UiCommand command) => sent = command;
            CommandRequested += OnCommand;
            try
            {
                await DragAsync("mover", drop, async () =>
                {
                    if (_slotHints.Count != occupied + 1 || !FrontBand.HasPoint(new Vector2(640, _slotHintY + FieldCardSize.Y / 2)))
                        throw new Exception($"Front markers are outside the line or omit an insertion position: {occupied}/{anchor}");
                    if (capture is not null) await capture($"front-{occupied}-{anchor}-held");
                });
            }
            finally { CommandRequested -= OnCommand; }
            if (sent is not CommandUnit { Uid: "mover", TargetUid: null } move || move.SlotIndex != offeredSlot)
                throw new Exception($"Front placement did not submit the available engine slot: {occupied}/{anchor}: {sent}");
            if (_state.SelfLine.Single(c => c.Uid == "mover").Zone != "support")
                throw new Exception("A placement gesture changed the board before engine acceptance.");
            var requestedRank = _frontPlacements.GetValueOrDefault("mover")?.Rank;
            var after = before with { SelfLine = front.Append(mover with { Zone = "frontline", SlotIndex = offeredSlot }).ToArray() };
            await PresentSequenceAsync(new(before.MatchId, [], after), new());
            var actual = RenderedFrontUids();
            if (!actual.SequenceEqual(expected))
                throw new Exception($"Front picture mismatch {occupied}/{anchor}: x={drop.X} rank={requestedRank} expectedRank={expectedRank} expected={string.Join(',', expected)} actual={string.Join(',', actual)}");
            if (capture is not null)
            {
                await ToSignal(GetTree().CreateTimer(.55), SceneTreeTimer.SignalName.Timeout);
                await capture($"front-{occupied}-{anchor}-settled");
            }
            checks++;
        }
        var blocked = _state.SelfLine[0] with { Uid = "blocked-mover", Zone = "support", SlotIndex = 0 };
        var full = _state with { MatchId = "placement-full", SelfLine = _state.SelfLine.Append(blocked).ToArray() };
        ApplyProjection(full, new());
        UiCommand? guarded = null;
        void CaptureCommand(UiCommand command) => guarded = command;
        CommandRequested += CaptureCommand;
        try
        {
            await DragFixtureAsync(blocked.Uid, DropAtRowMiddle(true));
            if (guarded is not null || _state.SelfLine.Single(c => c.Uid == blocked.Uid).Zone != "support")
                throw new Exception("Full front line accepted an unavailable movement gesture.");
            var existing = full.SelfLine.Single(c => c.SlotIndex == 1 && c.Zone == "frontline");
            var rejected = full with { MatchId = "placement-rejected", SelfLine = [existing, blocked] };
            ApplyProjection(rejected, new() { Moves = [new(blocked.Uid, "frontline", 4)] });
            await DragFixtureAsync(blocked.Uid, DropAtRowEdge(true, false));
            if (guarded is not CommandUnit { SlotIndex: 4 })
                throw new Exception("Movement ignored the engine's single offered slot.");
            ApplyProjection(rejected, new()); // engine rejection settles the unchanged board
            await PresentSequenceAsync(new(rejected.MatchId, [], rejected with
            { SelfLine = [existing, blocked with { Zone = "frontline", SlotIndex = 4 }] }), new());
            if (!RenderedFrontUids().SequenceEqual(new[] { existing.Uid, blocked.Uid }))
                throw new Exception("A rejected gesture changed a later front-line arrangement.");
        }
        finally { CommandRequested -= CaptureCommand; }
        GD.Print($"[placement] {checks} gesture-to-picture checks passed (0–4 units; left/middle/right/next; offered slot honoured)");
        GD.Print("[placement] full-line refusal and rejected-placement cleanup passed");
        ResetDemo();
    }

    public string[] RenderedFrontUids() => _cards.Values
        .Where(c => c.View?.Zone == "frontline" && c.Mode != BattleCardMode.Hand)
        .OrderBy(c => c.RestPosition.X).Select(c => c.View!.Uid).ToArray();
}
