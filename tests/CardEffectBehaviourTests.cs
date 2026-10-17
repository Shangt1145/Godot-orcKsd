using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

/// <summary>These tests rebuild the process-wide engine host, so they must not overlap with others.</summary>
[CollectionDefinition("engine-host", DisableParallelization = true)]
public sealed class EngineHostCollection;

/// <summary>
/// H39 sWG reads 「抑制被本单位攻击的单位」 (EN: Suppress units attacked by this unit). That is a
/// self-event, so the suppression has to ride on the damage this unit deals — nothing may be asked
/// of the player while the unit is being placed.
/// </summary>
[Collection("engine-host")]
public sealed class CardEffectBehaviourTests
{
    private const string RepoRoot = @"H:\Working Folder\OrC-KSD.Godot";
    private const string PackBase = RepoRoot + @"\proto\data\kards";
    private const string H39 = "pack/exp-winterwar/h39_swg";

    private readonly ITestOutputHelper _out;
    public CardEffectBehaviourTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task H39_suppresses_the_unit_it_attacks()
    {
        // 宿主是进程级静态面：别的测试可能先按「无卡包」暖机过，这里重建一次拿到卡包宿主。
        // 本类独占一个非并行集合，重建不会打扰并发用例；收尾不做 Reset（会清空卡包家族表）。
        OrcEngineHost.Reset();
        OrcEngineHost.Initialize(PackBase);
        OrcEngineHost.LoadPackCards(PackBase);
        var nations = Path.Combine(RepoRoot, "proto", "data", "nations");
        var catalog = new Kards.Ui.Core.CardCatalog();
        catalog.Load(nations, Kards.Ui.Core.CardCatalog.ResolveArtResource);
        var cards = new DeckConstructionService(catalog.Cards, nations).Cards;
        Assert.True(cards.Count > 0, "卡池为空");

        var session = await OrcMatchSession.CreateFromCatalogAsync(cards, seed: 20261007,
            matchId: "h39-combat", fallbackCardId: H39, openingHand: [H39], sourceDirectory: nations);
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            var card = session.View.SelfHand.FirstOrDefault(c => c.CardId == H39)
                ?? throw new InvalidOperationException(
                    $"起手没有 {H39}：[{string.Join(' ', session.View.SelfHand.Select(c => c.CardId))}]");
            var cost = card.Definition.Cost ?? 4;

            for (var turn = 0; turn < 10 && session.View.SelfKredits < cost; turn++)
            {
                await session.SubmitAsync(new EndTurn());
                session.Pump();
            }
            _out.WriteLine($"[fund] kredits={session.View.SelfKredits} cost={cost}");

            var placed = await session.SubmitAsync(new PlayCard(card.Uid, 4));
            session.Pump();
            _out.WriteLine($"[deploy] outcome={placed} reason={session.LastRejectionReason} support=[{string.Join(' ',
                session.View.SelfLine.Where(c => !c.IsHq).Select(c => $"{c.CardId}@{c.SlotIndex}"))}]");
            Assert.Equal(UiSubmitOutcome.Applied, placed);
            Assert.Contains(session.View.SelfLine, c => c.CardId == H39);

            // 打出这一步不许向玩家要任何东西：这张卡没有部署能力。
            Assert.False(session.TargetPending, "H39 打出时不该弹选靶");

            string? victimUid = null;
            var struck = false;
            for (var turn = 0; turn < 16 && !struck; turn++)
            {
                var view = session.View;
                if (view.Phase == "over") break;
                if (view.ActivePlayerSide != "self")
                {
                    await session.SubmitAsync(new EndTurn());
                    session.Pump();
                    continue;
                }
                var preview = session.Actions.AttackPreviews.FirstOrDefault(p =>
                    p.AttackerUid == card.Uid && view.EnemyLine.Any(e => e.Uid == p.DefenderUid && !e.IsHq));
                if (preview is null)
                {
                    await session.SubmitAsync(new EndTurn());
                    session.Pump();
                    continue;
                }
                _out.WriteLine($"[attack] turn={view.Turn} h39 -> {preview.DefenderUid}");
                var outcome = await session.SubmitAsync(new AttackUnit(preview.AttackerUid, preview.DefenderUid));
                session.Pump();
                _out.WriteLine($"[attack] outcome={outcome}");
                if (outcome == UiSubmitOutcome.Applied) { struck = true; victimUid = preview.DefenderUid; }
            }

            Assert.True(struck, "H39 整局都没能对被攻击的单位出手——攻击资格或阵线口径有问题");

            var victim = session.View.EnemyLine.FirstOrDefault(c => c.Uid == victimUid);
            Assert.NotNull(victim);
            _out.WriteLine($"[result] victim={victim!.CardId} silenced={victim.IsSilenced} suppressed={victim.IsSuppressed}");
            Assert.True(victim.IsSilenced, $"被 H39 打过的单位没有被抑制（silenced={victim.IsSilenced}）");
        }
        finally
        {
            session.Dispose();
            // 不 Reset：Reset 会清空 DeckCardPacks 的家族表，其他测试（DeckPackTests）正读它。
            // 把卡包家族重新装回来，等价于应用启动后的常态。
            OrcEngineHost.LoadPackCards(PackBase);
        }
    }
}
