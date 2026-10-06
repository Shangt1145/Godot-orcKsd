using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>Local interaction fixture, not a KARDS rules engine. Production adapters replace its state and options.</summary>
public sealed class BattleDemoAdapter
{
    private readonly IReadOnlyList<UiCardDefinition> _catalog;
    public UiMatchView State { get; private set; } = null!;
    public UiBattleActions Actions { get; private set; } = new();
    public BattleDemoAdapter(IReadOnlyList<UiCardDefinition> catalog) { _catalog = catalog; Reset(); }

    /// <summary>The catalog backing the fixtures, so callers can audit the real definitions.</summary>
    public IReadOnlyList<UiCardDefinition> Cards => _catalog;
    public void Reset()
    {
        var self = Units("UN/");
        var enemy = Units("deran/");
        UiCardView Unit(UiCardDefinition d, string uid, string side, string zone, int slot) => new()
        {
            Uid = uid, Definition = d, OwnerSide = side, Zone = zone, SlotIndex = slot,
            EffectiveCost = d.Cost, EffectiveAttack = d.BaseAttack, EffectiveDefense = d.BaseDefense,
            EffectiveOpCost = d.BaseOpCost ?? 1, Health = d.BaseDefense, Keywords = d.Keywords,
            KeywordValues = d.KeywordValues, CanPlayCard = zone == "hand", CanAttack = side == "self" && zone != "hand",
            CanMoveAndAttack = side == "self" && zone == "support", CanBeTargeted = side == "enemy"
        };
        State = new()
        {
            MatchId = "battle-demo", Turn = 4, Phase = "play", ActivePlayerSide = "self",
            SelfPlayerName = "联合指挥官", EnemyPlayerName = "德兰指挥官",
            SelfKredits = 8, SelfMaxKredits = 8, EnemyKredits = 5, EnemyMaxKredits = 7,
            SelfDeckCount = 24, EnemyDeckCount = 26, EnemyHandCount = 5, SelfHandCount = 5,
            SelfHq = Hq("self", "联合总部", "UN", 20), EnemyHq = Hq("enemy", "德兰总部", "deran", 12),
            SelfHand = Enumerable.Range(0, 5).Select(i => Unit(self[(i + 3) % self.Count], $"hand-{i}", "self", "hand", i)).ToArray(),
            SelfLine = new[] { Unit(self[0], "self-0", "self", "support", 0), Unit(self[1], "self-1", "self", "support", 1), Unit(self[2], "self-front", "self", "frontline", 0) with { Health = Math.Min(2, self[2].BaseDefense ?? 2) } },
            EnemyLine = Enumerable.Range(0, 3).Select(i =>
            {
                var card = Unit(enemy[i], $"enemy-{i}", "enemy", "support", i);
                return i == 2 ? card with { Health = 1 } : card;
            }).ToArray()
        };
        RefreshActions();
    }
    private List<UiCardDefinition> Units(string prefix)
    {
        var units = _catalog.Where(c => c.CardId.StartsWith(prefix, StringComparison.Ordinal) && c.CardType == "unit" && c.ArtPath.Length > 0
                && c.UnitType is "infantry" or "tank" or "artillery" or "fighter" or "bomber")
            .OrderBy(c => c.Cost).ThenBy(c => c.CardId, StringComparer.Ordinal).Take(12).ToList();
        if (units.Count < 4)
            units = _catalog.Where(c => c.CardType == "unit").Take(12).ToList();
        if (units.Count < 4) throw new InvalidOperationException("Battle demo requires at least four unit definitions.");
        return units;
    }
    private static UiCardView Hq(string side, string name, string set, int health) => new()
    {
        Uid = side + "-hq", Definition = new() { CardId = side + "-hq", Name = name, Set = set, CardType = "hq" },
        IsHq = true, OwnerSide = side, Zone = "support", SlotIndex = 1, Health = health, EffectiveDefense = health, CanBeTargeted = side == "enemy"
    };
    private static UiCardView[] ReindexLine(IEnumerable<UiCardView> cards)
    {
        var row = cards.ToArray();
        var slots = row.GroupBy(c => c.Zone).SelectMany(g => g.OrderBy(c => c.SlotIndex).Select((c, i) => (c.Uid, Index: i)))
            .ToDictionary(p => p.Uid, p => p.Index);
        return row.Select(c => c with { SlotIndex = slots[c.Uid] }).ToArray();
    }
    private void RefreshActions()
    {
        if (State.ActivePlayerSide != "self" || State.Phase == "over") { Actions = new(); return; }
        var supportCount = State.SelfLine.Count(c => c.Zone == "support");
        var frontCount = State.SelfLine.Concat(State.EnemyLine).Count(c => c.Zone == "frontline");
        var enemies = State.EnemyLine.Concat(State.EnemyHq is { } hq ? new[] { hq } : Array.Empty<UiCardView>()).ToArray();
        Actions = new()
        {
            CanEndTurn = true,
            AttacksEnabled = true,
            PlayableUids = supportCount < 4 ? State.SelfHand.Where(c => c.CanPlayCard && c.EffectiveCost <= State.SelfKredits).Select(c => c.Uid).ToArray() : [],
            Moves = frontCount < 5 && !State.EnemyLine.Any(c => c.Zone == "frontline")
                ? State.SelfLine.Where(c => c.Zone == "support" && c.CanMoveAndAttack && c.EffectiveOpCost <= State.SelfKredits).Select(c => new UiMoveOption(c.Uid, "frontline")).ToArray() : [],
            // Demonstration rules only. Production forecasts come from the engine, including every keyword/modifier.
            AttackPreviews = State.SelfLine.Where(c => c.CanAttack && c.EffectiveAttack > 0 && c.EffectiveOpCost <= State.SelfKredits)
                .SelectMany(c => enemies.Where(e => c.Zone == "frontline" || e.Zone == "frontline" || c.Definition.UnitType is "artillery" or "fighter" or "bomber")
                    .Select(e =>
                    {
                        var counter = e.IsHq || c.Definition.UnitType is "artillery" or "bomber" ? 0 : e.EffectiveAttack;
                        return new UiAttackPreview(c.Uid, e.Uid, c.EffectiveAttack, counter, e.Health <= c.EffectiveAttack, c.Health <= counter);
                    })).ToArray()
        };
    }
    public bool TrySubmit(UiCommand command, out string message)
    {
        message = "当前演示不支持该行动";
        switch (command)
        {
            case AttackUnit attack:
                return TryResolveAttack(attack, out _, out message);
            case PlayCard play when Actions.PlayableUids.Contains(play.Uid):
                var card = State.SelfHand.First(c => c.Uid == play.Uid);
                var hand = State.SelfHand.Where(c => c.Uid != play.Uid).Select((c, i) => c with { SlotIndex = i }).ToArray();
                var support = State.SelfLine.Where(c => c.Zone == "support").OrderBy(c => c.SlotIndex).ToList();
                if (State.SelfHq is { } headquarters) support.Insert(Math.Clamp(headquarters.SlotIndex, 0, support.Count), headquarters);
                support.Insert(Math.Clamp(play.SupportIndex ?? support.Count, 0, support.Count), card with
                    { Zone = "support", CanPlayCard = false, CanAttack = false, CanMoveAndAttack = true });
                var supportUnits = support.Where(c => !c.IsHq).Select((c, i) => c with { SlotIndex = i }).ToArray();
                State = State with
                {
                    SelfHand = hand, SelfHandCount = hand.Length, SelfKredits = State.SelfKredits - card.EffectiveCost!.Value,
                    SelfLine = State.SelfLine.Where(c => c.Zone != "support").Concat(supportUnits).ToArray(),
                    SelfHq = State.SelfHq is null ? null : State.SelfHq with { SlotIndex = support.FindIndex(c => c.IsHq) }
                };
                message = $"已部署 {card.Definition.Name}";
                break;
            case MoveUnit move when Actions.Moves.Any(m => m.Uid == move.Uid && m.ToZone == move.ToZone):
                var moving = State.SelfLine.First(c => c.Uid == move.Uid);
                var entered = moving with { Zone = move.ToZone, SlotIndex = move.SlotIndex ?? State.SelfLine.Count(c => c.Zone == move.ToZone), CanMoveAndAttack = false };
                // The front line is shared by both sides, so inserting at an index renumbers both rows.
                var frontRow = State.SelfLine.Concat(State.EnemyLine).Where(c => c.Zone == "frontline")
                    .OrderBy(c => c.SlotIndex).ToList();
                frontRow.Insert(Math.Clamp(entered.SlotIndex, 0, frontRow.Count), entered);
                var frontOrder = frontRow.Select((c, i) => (c.Uid, Index: i)).ToDictionary(p => p.Uid, p => p.Index);
                // The moving unit is still "support" in the old state, so it must not stay in that row.
                var supportRow = State.SelfLine.Where(c => c.Zone == "support" && c.Uid != move.Uid)
                    .OrderBy(c => c.SlotIndex).Select((c, i) => c with { SlotIndex = i });
                var selfFront = frontRow.Where(c => c.OwnerSide == "self").Select(c => c with { SlotIndex = frontOrder[c.Uid] });
                State = State with
                {
                    SelfKredits = State.SelfKredits - moving.EffectiveOpCost!.Value,
                    SelfLine = move.ToZone == "frontline"
                        ? supportRow.Concat(selfFront).ToArray()
                        : ReindexLine(State.SelfLine.Select(c => c.Uid == move.Uid ? entered : c)),
                    EnemyLine = move.ToZone == "frontline"
                        ? State.EnemyLine.Select(c => c.Zone == "frontline" ? c with { SlotIndex = frontOrder[c.Uid] } : c).ToArray()
                        : State.EnemyLine,
                    SelfHq = State.SelfHq is { } selfHq && moving.Zone == "support" && moving.SlotIndex < selfHq.SlotIndex
                        ? selfHq with { SlotIndex = Math.Max(0, selfHq.SlotIndex - 1) } : State.SelfHq
                };
                message = $"{moving.Definition.Name} 进入前线";
                break;
            case EndTurn when Actions.CanEndTurn:
                State = State with { ActivePlayerSide = "enemy" };
                message = "对手回合";
                break;
            default:
                return false;
        }
        RefreshActions();
        return true;
    }
    public void BeginNextTurn()
    {
        if (State.ActivePlayerSide != "enemy") return;
        State = State with
        {
            Turn = State.Turn + 1, ActivePlayerSide = "self", SelfMaxKredits = Math.Min(12, State.SelfMaxKredits + 1),
            SelfKredits = Math.Min(12, State.SelfMaxKredits + 1),
            SelfLine = State.SelfLine.Select(c => c with { CanAttack = true, CanMoveAndAttack = c.Zone == "support" }).ToArray()
        };
        RefreshActions();
    }
    public bool TryResolveAttack(AttackUnit command, out UiCombatResolution? resolution, out string message)
    {
        resolution = null; message = "无法攻击该目标";
        var forecast = Actions.AttacksEnabled ? Actions.AttackPreviews.FirstOrDefault(p => p.AttackerUid == command.AttackerUid && p.DefenderUid == command.DefenderUid) : null;
        if (forecast?.DamageToDefender is not { } outgoing || forecast.DamageToAttacker is not { } incoming) return false;
        var attacker = State.SelfLine.FirstOrDefault(c => c.Uid == command.AttackerUid);
        var defender = State.EnemyLine.FirstOrDefault(c => c.Uid == command.DefenderUid) ?? (State.EnemyHq?.Uid == command.DefenderUid ? State.EnemyHq : null);
        if (attacker is null || defender is null || attacker.Health is null || defender.Health is null) return false;
        var attackerAfter = attacker.Health <= incoming ? null : attacker with { Health = attacker.Health - incoming, CanAttack = false, CanMoveAndAttack = false };
        var defenderAfter = defender.Health <= outgoing ? null : defender with { Health = defender.Health - outgoing };
        var after = State with
        {
            SelfKredits = State.SelfKredits - (attacker.EffectiveOpCost ?? 0),
            SelfLine = ReindexLine(State.SelfLine.Where(c => c.Uid != attacker.Uid || attackerAfter is not null).Select(c => c.Uid == attacker.Uid ? attackerAfter! : c)),
            EnemyLine = ReindexLine(State.EnemyLine.Where(c => c.Uid != defender.Uid || defenderAfter is not null).Select(c => c.Uid == defender.Uid ? defenderAfter! : c)),
            EnemyHq = defender.IsHq ? defenderAfter
                : defenderAfter is null && defender.Zone == "support" && State.EnemyHq is { } enemyHq && defender.SlotIndex < enemyHq.SlotIndex
                    ? enemyHq with { SlotIndex = Math.Max(0, enemyHq.SlotIndex - 1) } : State.EnemyHq,
            SelfHq = attackerAfter is null && attacker.Zone == "support" && State.SelfHq is { } selfHq && attacker.SlotIndex < selfHq.SlotIndex
                ? selfHq with { SlotIndex = Math.Max(0, selfHq.SlotIndex - 1) } : State.SelfHq,
            Phase = defender.IsHq && defenderAfter is null ? "over" : State.Phase,
            ResultTitle = defender.IsHq && defenderAfter is null ? "胜利" : State.ResultTitle
        };
        resolution = UiSnapshots.Freeze(new UiCombatResolution(State.MatchId, attacker, defender, outgoing, incoming, attackerAfter, defenderAfter, after));
        State = after; RefreshActions();
        message = defender.IsHq && defenderAfter is null ? "敌方总部已摧毁" : $"{attacker.Definition.Name} 发动攻击";
        return true;
    }
    public void ResetCombatScenario(string unitType, bool hq = false, string? targetType = null)
    {
        Reset();
        var definition = _catalog.FirstOrDefault(c => c.CardType == "unit" && c.UnitType == unitType && c.ArtPath.Length > 0)
            ?? _catalog.First(c => c.CardType == "unit") with { UnitType = unitType };
        var attacker = State.SelfLine.First(c => c.Zone == "frontline") with
        {
            Definition = definition, EffectiveAttack = 3, EffectiveDefense = 4, Health = 4, EffectiveOpCost = 1
        };
        var defender = State.EnemyLine[0] with { Health = 3, EffectiveAttack = 1 };
        if (targetType is not null)
            defender = defender with { Definition = _catalog.FirstOrDefault(c => c.CardType == "unit" && c.UnitType == targetType && c.ArtPath.Length > 0)
                ?? defender.Definition with { UnitType = targetType } };
        State = State with
        {
            SelfLine = State.SelfLine.Where(c => c.Zone != "frontline").Append(attacker).ToArray(),
            EnemyLine = State.EnemyLine.Select(c => c.Uid == defender.Uid ? defender : c).ToArray(),
            EnemyHq = hq ? State.EnemyHq! with { Health = 3, EffectiveDefense = 3 } : State.EnemyHq
        };
        RefreshActions();
    }
    public (UiMatchView Before, UiPresentationResolution Resolution, UiBattleActions Actions) CreatePresentationScenario(string kind)
    {
        if (kind.StartsWith("counter-", StringComparison.Ordinal) || kind.StartsWith("status-", StringComparison.Ordinal)) return CreateFeedbackScenario(kind);
        Reset();
        var steps = new List<UiPresentationStep>();
        if (kind is "draw" or "draw-two")
        {
            var count = kind == "draw-two" ? 2 : 1;
            var before = State;
            var draws = Enumerable.Range(0, count).Select(i => State.SelfHand[i] with
                { Uid = $"draw-{i}", SlotIndex = State.SelfHand.Count + i }).ToArray();
            steps.AddRange(draws.Select(c => new UiDrawPresentation("self", c.SlotIndex, c)));
            State = State with { SelfHand = State.SelfHand.Concat(draws).ToArray(), SelfHandCount = State.SelfHand.Count + count,
                SelfDeckCount = State.SelfDeckCount - count };
            RefreshActions();
            return (UiSnapshots.Freeze(before), UiSnapshots.Freeze(new UiPresentationResolution(State.MatchId, steps, State)), UiSnapshots.Freeze(Actions));
        }
        if (kind == "enemy-draw")
        {
            var before = State;
            steps.Add(new UiDrawPresentation("enemy", State.EnemyHandCount ?? 0));
            State = State with { EnemyHandCount = (State.EnemyHandCount ?? 0) + 1, EnemyDeckCount = (State.EnemyDeckCount ?? 0) - 1 };
            RefreshActions();
            return (UiSnapshots.Freeze(before), UiSnapshots.Freeze(new UiPresentationResolution(State.MatchId, steps, State)), UiSnapshots.Freeze(Actions));
        }
        var enemy = kind == "enemy-order";
        var demoCardId = kind == "multi-order" ? "custom/new-mucr2iux-1nv" : enemy ? "deran/command/_12" : "USG/commands/_11";
        var definition = _catalog.FirstOrDefault(c => c.CardId == demoCardId && c.CardType == "order" && c.ArtPath.Length > 0)
            ?? throw new InvalidOperationException("Presentation fixture requires an order definition.");
        var order = new UiCardView { Uid = "demo-order", Definition = definition, OwnerSide = enemy ? "enemy" : "self",
            Zone = "hand", SlotIndex = 4, EffectiveCost = definition.Cost };
        State = State with
        {
            SelfHand = enemy ? State.SelfHand : State.SelfHand.Take(4).Append(order).ToArray(),
            EnemyLine = State.EnemyLine.Select((c, i) => c with { Health = i == 0 ? 5 : i == 1 ? 3 : 1 }).ToArray(),
            SelfLine = enemy ? State.SelfLine.Select((c, i) => i == 0 ? c with { Health = 5 } : c).ToArray() : State.SelfLine
        };
        var initial = UiSnapshots.Freeze(State);
        var targets = enemy ? State.SelfLine.Take(1).ToArray() : State.EnemyLine.Take(kind == "multi-order" ? 3 : 1).ToArray();
        var impacts = targets.Select((c, i) => new UiOrderImpact(c, i == 0 ? c with { Health = 2 } : null, 3)).ToArray();
        if (kind == "multi-order" && State.EnemyHq is { } headquarters)
            impacts = impacts.Append(new UiOrderImpact(headquarters, headquarters with { Health = 9 }, 3)).ToArray();
        steps.Add(new UiOrderPresentation(order, impacts));
        var afterByUid = impacts.ToDictionary(i => i.Before.Uid, i => i.After);
        IReadOnlyList<UiCardView> Update(IReadOnlyList<UiCardView> row) => row.Where(c => !afterByUid.ContainsKey(c.Uid) || afterByUid[c.Uid] is not null)
            .Select(c => afterByUid.GetValueOrDefault(c.Uid, c)!).ToArray();
        State = State with
        {
            SelfHand = enemy ? State.SelfHand : State.SelfHand.Where(c => c.Uid != order.Uid).ToArray(), SelfHandCount = enemy ? 5 : 4,
            SelfKredits = enemy ? State.SelfKredits : Math.Max(0, State.SelfKredits - (order.EffectiveCost ?? 0)),
            EnemyHandCount = enemy ? (State.EnemyHandCount ?? 5) - 1 : State.EnemyHandCount,
            EnemyKredits = enemy ? Math.Max(0, (State.EnemyKredits ?? 5) - (order.EffectiveCost ?? 0)) : State.EnemyKredits,
            SelfLine = Update(State.SelfLine), EnemyLine = Update(State.EnemyLine),
            EnemyHq = State.EnemyHq is { } hq && afterByUid.TryGetValue(hq.Uid, out var hqAfter) ? hqAfter : State.EnemyHq
        };
        RefreshActions();
        return (initial, UiSnapshots.Freeze(new UiPresentationResolution(State.MatchId, steps, State)), UiSnapshots.Freeze(Actions));
    }
    private (UiMatchView Before, UiPresentationResolution Resolution, UiBattleActions Actions) CreateFeedbackScenario(string kind)
    {
        Reset(); var steps = new List<UiPresentationStep>();
        UiMatchView before;
        if (kind.StartsWith("counter-", StringComparison.Ordinal))
        {
            var enemy = kind == "counter-hidden";
            var definition = _catalog.First(c => c.CardId == "av76/command/-14");
            var card = new UiCardView { Uid = "demo-counter", Definition = definition, OwnerSide = enemy ? "enemy" : "self",
                EffectiveCost = definition.Cost, Zone = "hand", SlotIndex = 4, IsCounterArmed = kind is "counter-disarm" or "counter-trigger" };
            if (!enemy) State = State with { SelfHand = State.SelfHand.Take(4).Append(card).ToArray(), SelfCounterCount = card.IsCounterArmed ? 1 : 0,
                SelfKredits = card.IsCounterArmed ? 3 : 8 };
            before = UiSnapshots.Freeze(State);
            var stage = kind == "counter-disarm" ? UiCounterStage.Disarmed : kind == "counter-trigger" ? UiCounterStage.Triggered : UiCounterStage.Armed;
            var afterCard = card with { IsCounterArmed = stage == UiCounterStage.Armed };
            UiCardView? deployed = null;
            if (stage == UiCounterStage.Triggered)
            {
                deployed = State.EnemyLine[0] with { Uid = "deployed-enemy", Zone = "support", SlotIndex = State.EnemyLine.Count };
                var placed = State with { EnemyLine = State.EnemyLine.Append(deployed).ToArray(), EnemyHandCount = 4 };
                steps.Add(new UiDeploymentPresentation(deployed, placed));
            }
            steps.Add(new UiCounterPresentation(enemy ? "enemy" : "self", stage, afterCard));
            if (deployed is not null) steps.Add(new UiRemovalPresentation(deployed));
            if (!enemy) State = State with
            {
                SelfHand = stage == UiCounterStage.Triggered ? State.SelfHand.Where(c => c.Uid != card.Uid).ToArray()
                    : State.SelfHand.Select(c => c.Uid == card.Uid ? afterCard : c).ToArray(),
                SelfHandCount = stage == UiCounterStage.Triggered ? 4 : 5, SelfCounterCount = afterCard.IsCounterArmed ? 1 : 0,
                SelfKredits = stage == UiCounterStage.Armed ? 3 : stage == UiCounterStage.Disarmed ? 8 : State.SelfKredits,
                EnemyHandCount = stage == UiCounterStage.Triggered ? 4 : State.EnemyHandCount
            };
        }
        else
        {
            var target = kind == "status-cost" ? State.SelfHand[0] : State.SelfLine[0];
            if (kind == "status-heal")
            {
                target = target with { Health = 1, EffectiveDefense = 4 };
                State = State with { SelfLine = State.SelfLine.Select(c => c.Uid == target.Uid ? target : c).ToArray() };
            }
            before = UiSnapshots.Freeze(State);
            var status = kind switch { "status-heal" => UiStatusKind.Heal, "status-buff" => UiStatusKind.Buff,
                "status-suppressed" => UiStatusKind.Suppressed, _ => UiStatusKind.CostChanged };
            var after = status switch
            {
                UiStatusKind.Heal => target with { Health = 4 },
                UiStatusKind.Buff => target with { EffectiveAttack = 4, EffectiveDefense = 5, Health = 5 },
                UiStatusKind.Suppressed => target with { IsSuppressed = true, IsSilenced = true },
                _ => target with { EffectiveCost = 0 }
            };
            steps.Add(new UiStatusPresentation(target, after, status));
            if (status == UiStatusKind.Suppressed)
            {
                var clear = after with { IsSuppressed = false, IsSilenced = false };
                steps.Add(new UiStatusPresentation(after, clear, UiStatusKind.Cleared)); after = clear;
            }
            State = State with { SelfLine = State.SelfLine.Select(c => c.Uid == target.Uid ? after : c).ToArray(),
                SelfHand = State.SelfHand.Select(c => c.Uid == target.Uid ? after : c).ToArray() };
        }
        RefreshActions();
        return (before, UiSnapshots.Freeze(new UiPresentationResolution(State.MatchId, steps, State)), UiSnapshots.Freeze(Actions));
    }
}
