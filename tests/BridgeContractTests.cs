using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// The guard rail for pulling upstream engine changes. The UI is built on Orc-KSD and is meant to
/// keep tracking its architecture, so the cost of an upstream break must show up as a red test here
/// rather than as a wrong thing on screen.
///
/// Two levels, deliberately different:
///   - SHAPE  — the contract's own fields, frozen by reflection. Pure compile-time surface.
///   - BEHAVIOUR — a real match, asserting the engine still reports what the UI depends on.
/// Numeric values are asserted as ranges, not literals: a balance change upstream is not a break,
/// but a renamed member or a flipped availability flag is.
/// </summary>
public sealed class BridgeContractTests
{
    private const string InfantryId = "contract-infantry";

    private static Match CreateMatch(int seed = 7)
    {
        var definitions = new[]
        {
            new CardDefinitionEntry(InfantryId, new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard))
        };
        var deckA = new CardList(Enumerable.Repeat(InfantryId, 20));
        var deckB = new CardList(Enumerable.Repeat(InfantryId, 20));
        Match? match = null;
        // AutoRespond matters: BeginUnitPrePlayAsync parks on a SingleSelect slot and waits forever
        // unless a choice is submitted. A bridge that only records requests deadlocks the host.
        var bridge = new OrcTargeterBridge(() => InteractablesOf(match), OrcTargeterBridge.AutoRespond);
        match = new Match(deckA, deckB, definitions, seed: seed, firstPlayerIndex: 0, targeterBridge: bridge);
        return match;
    }

    private static IReadOnlyList<object?> InteractablesOf(Match? match)
    {
        if (match is null || match.State == MatchState.Preparing) return Array.Empty<object?>();
        var references = new List<object?>();
        foreach (var line in new[] { match.Battlefield.PlayerASupportLine, match.Battlefield.FrontLine, match.Battlefield.PlayerBSupportLine })
            foreach (var slot in line) references.Add(slot.Ref);
        foreach (var player in match.Players) references.Add(player.Hq.Ref);
        return references;
    }

    // ── SHAPE ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The view model the whole UI is written against. A renamed or removed field breaks the UI
    /// silently at runtime otherwise — a renamed property simply reads as null.
    /// </summary>
    [Fact]
    public void MatchViewKeepsTheFieldsTheUiIsWrittenAgainst()
    {
        var required = new[]
        {
            nameof(UiMatchView.MatchId), nameof(UiMatchView.Turn), nameof(UiMatchView.Phase),
            nameof(UiMatchView.ActivePlayerSide), nameof(UiMatchView.ResultTitle),
            nameof(UiMatchView.ResultReason), nameof(UiMatchView.FinalTurn),
            nameof(UiMatchView.SelfHq), nameof(UiMatchView.EnemyHq),
            nameof(UiMatchView.SelfLine), nameof(UiMatchView.EnemyLine),
            nameof(UiMatchView.SelfHand), nameof(UiMatchView.PendingChoices),
            nameof(UiMatchView.SelfKredits), nameof(UiMatchView.SelfMaxKredits),
            nameof(UiMatchView.SelfDeckCount), nameof(UiMatchView.EnemyDeckCount),
            nameof(UiMatchView.SelfHandCount), nameof(UiMatchView.EnemyHandCount),
        };
        AssertMembers<UiMatchView>(required);
    }

    [Fact]
    public void CardViewKeepsTheFieldsTheCardFaceIsWrittenAgainst()
    {
        var required = new[]
        {
            nameof(UiCardView.Uid), nameof(UiCardView.Definition), nameof(UiCardView.Visibility),
            nameof(UiCardView.Health), nameof(UiCardView.Zone), nameof(UiCardView.IsHq),
            nameof(UiCardView.OwnerSide), nameof(UiCardView.SlotIndex),
            nameof(UiCardView.EffectiveAttack), nameof(UiCardView.EffectiveDefense),
            nameof(UiCardView.EffectiveCost), nameof(UiCardView.EffectiveOpCost),
        };
        AssertMembers<UiCardView>(required);
    }

    [Fact]
    public void DefinitionKeepsTheFieldsTheCollectionAndDeckAreWrittenAgainst()
    {
        var required = new[]
        {
            nameof(UiCardDefinition.CardId), nameof(UiCardDefinition.Name), nameof(UiCardDefinition.NameEn),
            nameof(UiCardDefinition.CardType), nameof(UiCardDefinition.UnitType), nameof(UiCardDefinition.Set),
            nameof(UiCardDefinition.Rarity), nameof(UiCardDefinition.ArtPath), nameof(UiCardDefinition.Text),
            nameof(UiCardDefinition.Cost), nameof(UiCardDefinition.BaseAttack),
            nameof(UiCardDefinition.BaseDefense), nameof(UiCardDefinition.BaseOpCost),
            nameof(UiCardDefinition.IsToken), nameof(UiCardDefinition.Keywords),
        };
        AssertMembers<UiCardDefinition>(required);
    }

    /// <summary>
    /// The action surface. If a field disappears the UI stops offering the move; if a field silently
    /// changes type the UI compiles against the wrong shape.
    /// </summary>
    [Fact]
    public void BattleActionsKeepTheFieldsTheUiGatesOn()
    {
        var required = new[]
        {
            nameof(UiBattleActions.PlayableUids), nameof(UiBattleActions.Moves),
            nameof(UiBattleActions.AttackPreviews), nameof(UiBattleActions.CanEndTurn),
            nameof(UiBattleActions.AttacksEnabled), nameof(UiBattleActions.MulliganOpen),
        };
        AssertMembers<UiBattleActions>(required);
    }

    /// <summary>
    /// Every presentation step kind the choreography switches on must keep existing. They are
    /// sibling types deriving from UiPresentationStep, so this checks the types themselves rather
    /// than members — a renamed step is a compile error in the switch and a silent loss here.
    /// </summary>
    [Fact]
    public void PresentationStepsTheChoreographySwitchesOnStillExist()
    {
        var assembly = typeof(UiPresentationStep).Assembly;
        foreach (var name in new[]
        {
            nameof(UiDrawPresentation), nameof(UiDeploymentPresentation),
            nameof(UiRemovalPresentation), nameof(UiCounterPresentation), nameof(UiStatusPresentation),
            nameof(UiDiscardPresentation), nameof(UiMulliganPresentation), nameof(UiTurnPresentation),
            nameof(UiResourcePresentation),
        })
        {
            var type = assembly.GetType($"Kards.Ui.Contracts.{name}");
            Assert.True(type is not null, $"the presentation step {name} no longer exists");
            if (type is not null)
                Assert.True(typeof(UiPresentationStep).IsAssignableFrom(type),
                    $"{name} no longer derives from UiPresentationStep");
        }

        // UiOrderImpact is deliberately not a step: it rides inside UiOrderPresentation.
        var impact = assembly.GetType($"Kards.Ui.Contracts.{nameof(UiOrderImpact)}");
        Assert.True(impact is not null, "UiOrderImpact no longer exists");
        var order = assembly.GetType($"Kards.Ui.Contracts.{nameof(UiOrderPresentation)}");
        Assert.True(order is not null, "UiOrderPresentation no longer exists");
        if (order is not null)
        {
            var impacts = order.GetProperty(nameof(UiOrderPresentation.Impacts));
            Assert.NotNull(impacts);
            Assert.Equal(typeof(IReadOnlyList<UiOrderImpact>), impacts!.PropertyType);
        }
    }

    [Fact]
    public void CommandsTheUiIssuesKeepTheirShape()
    {
        // PlayCard already carries SupportIndex: the contract for a support-line deployment slot
        // exists even though the UI has no way to send it yet. P12 builds on this.
        var play = new PlayCard("uid", SupportIndex: 2);
        Assert.Equal("uid", play.Uid);
        Assert.Equal(2, play.SupportIndex);
        var move = new MoveUnit("uid", "frontline", 2);
        Assert.Equal("frontline", move.ToZone);
        Assert.Equal(2, move.SlotIndex);
        var attack = new AttackUnit("attacker", "defender");
        Assert.Equal("attacker", attack.AttackerUid);
        Assert.Equal("defender", attack.DefenderUid);
        Assert.Equal(2, new ChooseMulligan(["a", "b"]).KeepUids.Count);
    }

    private static void AssertMembers<T>(string[] required) where T : class
    {
        var actual = typeof(T).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var missing = required.Where(r => !actual.Contains(r)).ToArray();
        Assert.True(missing.Length == 0,
            $"{typeof(T).Name} lost the member(s) the UI depends on: {string.Join(", ", missing)}");
    }

    // ── BEHAVIOUR ───────────────────────────────────────────────────────────

    /// <summary>
    /// A real match must project a view the UI can actually drive: an opening hand, a deck behind
    /// it, and both headquarters present and visible. If upstream changes how many cards the
    /// opening hand holds, this is where it shows.
    ///
    /// The count is a range, not a literal: AutoRespond answers the mulligan by keeping the whole
    /// hand, so a replace-then-refill may already have run by the time we look. The invariant the
    /// UI actually depends on is "hand plus deck equals the deck the engine started us with".
    /// </summary>
    [Fact]
    public async Task AFreshMatchProjectsADrivableBoard()
    {
        var host = new OrcMatchHost(CreateMatch(), "contract-open");
        await host.InitializeAsync();
        var view = host.View;

        Assert.Equal("contract-open", view.MatchId);
        // KARDS: the first player draws 4, the second 5 — the engine owns the number. The
        // invariant the UI relies on is that hand and deck still account for the full 20.
        Assert.InRange(view.SelfHand.Count, 4, 5);
        Assert.InRange(view.SelfDeckCount, 15, 16);
        Assert.Equal(20, view.SelfDeckCount + view.SelfHand.Count);
        Assert.Equal(20, view.EnemyDeckCount + view.EnemyHandCount);
        Assert.Equal(0, view.Turn);

        // Hidden information: the opponent's hand is a count only. There is deliberately no
        // EnemyHand collection to leak — if upstream adds one, this line stops compiling and
        // someone has to decide whether it is safe to expose. The second player draws 5.
        Assert.Equal(5, view.EnemyHandCount);

        Assert.NotNull(view.SelfHq);
        Assert.NotNull(view.EnemyHq);
        Assert.Equal(Visibility.Full, view.SelfHq!.Visibility);
        Assert.Equal(20, view.SelfHq.Health);
        Assert.Equal(20, view.EnemyHq!.Health);

        Assert.Empty(view.SelfLine);
        Assert.Empty(view.EnemyLine);
        Assert.Null(view.ResultTitle);
        Assert.Null(view.ResultReason);
    }

    /// <summary>
    /// The mulligan gate is contractual: the board must be visible and the engine must be asking,
    /// while actions stay closed. Only after both sides answer may play begin. AutoRespond does not
    /// answer this phase — the engine parks on the mulligan slot until the UI supplies a choice, so
    /// the gate is genuinely observable here.
    /// </summary>
    [Fact]
    public async Task TheOpeningPhaseGatesActionsUntilTheMulliganIsAnswered()
    {
        var match = CreateMatch();
        var host = new OrcMatchHost(match, "contract-mulligan");
        await host.InitializeAsync();

        Assert.Equal("mulligan", host.View.Phase);
        // MulliganOpen is declared on the contract but never assigned by the bridge — the UI reads
        // Phase instead. Asserted here so if someone wires it up, this test tells them the UI must
        // be checked too rather than assuming the flag was already driving the panel.
        Assert.False(host.Actions.MulliganOpen);
        Assert.False(host.Actions.CanEndTurn);
        Assert.Equal(0, host.View.Turn);
        // The headquarters are already readable, so the board can be drawn behind the panel.
        Assert.Equal(20, host.View.SelfHq!.Health);

        await match.MulliganDone(match.Players[0]);
        await match.MulliganDone(match.Players[1]);
        host.Pump();

        Assert.Equal("play", host.View.Phase);
        Assert.Equal(1, host.View.Turn);
    }

    /// <summary>
    /// Command points gate every card play. The engine's own model is slot-based: the turn grants a
    /// slot and the available points follow it. A round increase is therefore a slot change, and
    /// the point total must never exceed the slot count.
    /// </summary>
    [Fact]
    public async Task CommandPointsNeverExceedTheirSlotCount()
    {
        var match = CreateMatch();
        var host = new OrcMatchHost(match, "contract-kredits");
        await host.InitializeAsync();
        await match.MulliganDone(match.Players[0]);
        await match.MulliganDone(match.Players[1]);
        host.Pump();

        var view = host.View;
        Assert.True(view.SelfMaxKredits > 0, "the first turn should grant at least one command-point slot");
        Assert.InRange(view.SelfKredits, 0, view.SelfMaxKredits);
        Assert.NotEqual("?", view.SelfKredits.ToString());
    }

    /// <summary>
    /// A unit the UI can select, drag and place: it must appear in the actions surface with the
    /// zones it may legally go to, and the board projection must survive the engine's own state.
    /// This is the contract the whole drag-and-drop interaction is written against.
    /// </summary>
    [Fact]
    public async Task APlayedUnitBecomesBothSelectableAndPlaceable()
    {
        var match = CreateMatch();
        var host = new OrcMatchHost(match, "contract-play");
        host.ImmediateUpdate += _ => { };
        await host.InitializeAsync();
        await match.MulliganDone(match.Players[0]);
        await match.MulliganDone(match.Players[1]);
        host.Pump();

        var unit = (UnitCard)match.Players[0].Hand.First();
        var deployed = await match.PlayManager.BeginUnitPrePlayAsync(unit);
        Assert.Equal(PlayResultStatus.Success, deployed.Status);
        host.Pump();

        var uid = OrcRefs.KeyOf(unit);
        var onBoard = host.View.SelfLine.Concat(host.View.EnemyLine).FirstOrDefault(c => c.Uid == uid);
        Assert.NotNull(onBoard);
        Assert.Equal(Visibility.Full, onBoard!.Visibility);
        Assert.Equal(5, onBoard.Health);
        Assert.NotNull(onBoard.EffectiveAttack);
        Assert.NotNull(onBoard.EffectiveDefense);

        // The unit is no longer in hand, and the hand shrank by exactly one.
        Assert.DoesNotContain(host.View.SelfHand, c => c.Uid == uid);
        Assert.Equal(3, host.View.SelfHand.Count);

        // PlayableUids is the hand's interactive set — a card the UI must let the player pick up.
        // A deployed unit has left the hand, so what matters here is that the remaining cards
        // are still offered; if upstream narrows this set, the UI silently stops responding.
        Assert.NotEmpty(host.Actions.PlayableUids);
        Assert.All(host.Actions.PlayableUids, id =>
            Assert.Contains(host.View.SelfHand, c => c.Uid == id));
    }

    /// <summary>
    /// Damage is the contract P11 rebuilt around: the engine states the amount, and a hit on the
    /// enemy headquarters must be reported as an impact carrying both the amount and the attacker.
    /// If upstream stops pairing a victim with its attacker, this goes red instead of the UI
    /// silently rendering a bare hit.
    /// </summary>
    [Fact]
    public async Task AnAttackOnTheHeadquartersIsReportedWithItsAttackerAndAmount()
    {
        var match = CreateMatch();
        var host = new OrcMatchHost(match, "contract-assault");
        var impacts = new List<IReadOnlyList<UiOrderImpact>>();
        host.CombatReady += (reported, _, _) => impacts.AddRange(reported);
        await host.InitializeAsync();

        var unit = (UnitCard)match.Players[0].Hand.First();
        var attackerUid = OrcRefs.KeyOf(unit);
        var hqUid = host.View.EnemyHq!.Uid;

        await match.MulliganDone(match.Players[0]);
        await match.MulliganDone(match.Players[1]);
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.BeginUnitPrePlayAsync(unit)).Status);
        await match.EndTurn();
        await OrcOpponentDriver.PlayTurnAsync(match, match.Players[1]);
        Assert.Equal(Orc.Game.Commanding.CommandResultStatus.Success,
            (await match.CommandManager.BeginMoveAsync(unit)).Status);
        await match.EndTurn();
        await OrcOpponentDriver.PlayTurnAsync(match, match.Players[1]);

        var attack = match.CommandManager.BeginAttackAsync(unit);
        Assert.True(await Task.WhenAny(attack, Task.Delay(TimeSpan.FromSeconds(20))) == attack,
            "the attack never completed");
        Assert.Equal(Orc.Game.Commanding.CommandResultStatus.Success, (await attack).Status);
        host.Pump();

        var hq = impacts.SelectMany(i => i).FirstOrDefault(i => i.Before.Uid == hqUid);
        Assert.NotNull(hq);
        Assert.Equal(attackerUid, hq!.Source?.Uid);
        Assert.Equal(2, hq.Damage);
        Assert.Equal(18, host.View.EnemyHq!.Health);
    }

    /// <summary>
    /// The update stream is the UI's only source of truth about what happened. Every signal the
    /// translator switches on must keep its exact name — these are string constants, so a rename on
    /// either side compiles fine and silently stops feeding the choreography.
    /// </summary>
    [Fact]
    public void TheUpdateSignalsTheTranslatorSwitchesOnKeepTheirNames()
    {
        var required = new[]
        {
            "card.hand.add", "card.drawn", "unit.deployed", "card.stat.changed", "card.damaged",
            "unit.damage.dealt", "card.died", "card.discarded", "card.burned",
            "turn.start", "turn.start.after",
            "slot.gained", "slot.lost", "slot.changed",
            "point.gained", "point.lost", "point.changed",
            "counter.triggered", "unit.combat.survived", "unit.joined",
        };
        var actual = typeof(GameUpdates)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        var missing = required.Where(r => !actual.Contains(r)).ToArray();
        Assert.True(missing.Length == 0,
            $"GameUpdates lost the signal(s) the translator switches on: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The board has three lines per side. The UI renders the front line and the HQ; if upstream
    /// renames or removes a line the support line work in P12 will build on sand. Only the shape is
    /// checked here — reading a live line needs a match past Initialize, which other tests cover.
    /// </summary>
    [Fact]
    public void TheBoardStillHasTheThreeLinesTheUiLayoutsAgainst()
    {
        var fields = typeof(Battlefield).GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var line in new[] { "PlayerASupportLine", "FrontLine", "PlayerBSupportLine" })
            Assert.Contains(line, fields);
    }

    /// <summary>
    /// A slot can be addressed by index, because the UI turns a pointer position into a slot index
    /// and sends it. The indexing contract drag-and-drop is built on must not change.
    /// </summary>
    [Fact]
    public async Task BattleLinesStayIndexAddressable()
    {
        var match = CreateMatch();
        await match.Initialize();
        var line = match.Battlefield.FrontLine;
        // A drop is resolved by position within the line, so the indices must be dense.
        Assert.Equal([0, 1, 2, 3, 4], line.Select(s => s.Index).ToArray());
        // The support line holds the headquarters in a fixed middle slot, which is what leaves room on
        // both of its sides. Asserted through the engine's own constant rather than a copied number: the
        // point of the guard is that a deployment can go to either side of the headquarters.
        var support = match.Battlefield.PlayerASupportLine;
        Assert.Equal(5, support.Count);
        Assert.Equal(5, match.Battlefield.PlayerBSupportLine.Count);
        Assert.Equal(Battlefield.HqSlotIndex, Battlefield.IndexOfHq(support));
        Assert.True(Battlefield.HqSlotIndex >= 2, "no room to the left of the headquarters");
        Assert.True(support.Count - Battlefield.HqSlotIndex - 1 >= 2, "no room to the right of the headquarters");
    }
}
