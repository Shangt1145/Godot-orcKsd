using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

/// <summary>Explicit native mechanism fixture; never admitted into the product card pool.</summary>
public sealed record NativeStateFeedback(UiMatchView Initial, IReadOnlyList<UiPresentationResolution> Resolutions, UiMatchView Final);

public static class OrcStateFeedbackVerification
{
    public static async Task<NativeStateFeedback> RunHqZeroAsync(CancellationToken ct = default)
    {
        const string id = "verify/lethal-bomber";
        var definitions = new[] { new CardDefinitionEntry(id, new CardDefinition("终局核验轰炸机", 0, 0, 20, 5,
            keywords: [new(KeywordIds.Blitz)], unitTypes: [UnitType.Bomber], faction: Faction.Germany, rarity: Rarity.Standard)) };
        var deck = Enumerable.Repeat(id, 24).ToArray();
        using var session = await OrcMatchSession.CreateAsync(definitions, deck, deck, 17,
            "native-hq-zero-" + Guid.NewGuid().ToString("N"), ct: ct);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false, ct);
        var uid = session.View.SelfHand[0].Uid;
        if (await session.SubmitAsync(new PlayCard(uid), ct) != UiSubmitOutcome.Applied)
            throw new InvalidOperationException("Lethal bomber deployment failed.");
        session.SettleProjection(); var initial = UiSnapshots.Freeze(session.View);
        var resolutions = new List<UiPresentationResolution>(); session.Host.PresentationReady += (r, _) => resolutions.Add(r);
        if (await session.SubmitAsync(new AttackUnit(uid, session.View.EnemyHq!.Uid), ct) != UiSubmitOutcome.Applied)
            throw new InvalidOperationException("Native lethal attack failed.");
        session.Pump(); session.Pump();
        if (session.View.Phase != "over" || session.View.ResultTitle != "胜利" || session.View.ResultReason != "HqZero")
            throw new InvalidOperationException("Native lethal attack did not finish the match.");
        return new(initial, resolutions.AsReadOnly(), UiSnapshots.Freeze(session.View));
    }

    public static async Task<NativeStateFeedback> RunAsync(CancellationToken ct = default)
    {
        const string basic = "verify/feedback-unit", veteran = "verify/feedback-veteran";
        var definitions = new[]
        {
            new CardDefinitionEntry(basic, new CardDefinition("状态核验单位", 0, 1, 2, 5,
                keywords: [new(KeywordIds.Covert), new(KeywordIds.Armor, 2)], unitTypes: [UnitType.Infantry],
                faction: Faction.Germany, rarity: Rarity.Standard, becomesVeteran: veteran)),
            new CardDefinitionEntry(veteran, new CardDefinition("状态核验老兵", 0, 1, 4, 7,
                keywords: [new(KeywordIds.Blitz)], unitTypes: [UnitType.Infantry],
                faction: Faction.Germany, rarity: Rarity.Standard, veteranOf: basic))
        };
        var deck = Enumerable.Repeat(basic, 24).ToArray();
        using var session = await OrcMatchSession.CreateAsync(definitions, deck, deck, 17,
            "native-feedback-" + Guid.NewGuid().ToString("N"), ct: ct);
        session.DriveOpponent = false; await session.SettleMulliganAsync(false, ct);
        var uid = session.View.SelfHand[0].Uid;
        if (await session.SubmitAsync(new PlayCard(uid), ct) != UiSubmitOutcome.Applied)
            throw new InvalidOperationException("Native feedback deployment failed.");
        session.SettleProjection();
        var initial = UiSnapshots.Freeze(session.View);
        var unit = (UnitCard)session.Host.Index[uid]; var match = session.Host.Match;
        var resolutions = new List<UiPresentationResolution>();
        session.Host.PresentationReady += (r, _) => resolutions.Add(r);
        async Task Action(Func<Task> change)
        {
            using (match.Engine.BeginAction()) await change();
            session.Host.CaptureCompletedActions(); session.Pump();
        }
        await Action(async () => { await CovertRules.RevealAsync(unit, ct); });
        await Action(async () =>
        {
            var result = await unit.InvokeVeteranTriggerAsync(ct);
            if (!result.IsPromoted) throw new InvalidOperationException("Native veteran promotion failed.");
        });
        // Native no-ops must not produce a second reveal/promotion.
        await Action(async () => { await CovertRules.RevealAsync(unit, ct); await unit.InvokeVeteranTriggerAsync(ct); });
        await Action(async () => { await SuppressRules.ApplyAsync(unit); });
        await Action(async () => { await SuppressRules.ExtendAsync(unit); });
        await Action(async () => { await unit.Keywords.RevokeAsync(KeywordIds.Suppressed); });
        await Action(async () => { await unit.AddUnitTypeAsync(UnitType.Bomber, ct); });
        await Action(async () => { await InhibitRules.ApplyAsync(unit, ct); });
        await Action(async () => { await unit.Keywords.GrantAsync(KeywordIds.SmokeScreen); });
        await Action(() => match.Players[0].Hq.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.HqHealth, 3, unit), ct));
        await Action(() => match.ShuffleDeckAsync(match.Players[0], ct));
        await Action(() => match.ShuffleDeckAsync(match.Players[1], ct));
        return new(initial, resolutions.AsReadOnly(), UiSnapshots.Freeze(session.View));
    }
}
