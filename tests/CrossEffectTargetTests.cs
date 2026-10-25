using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

/// <summary>
/// 「使 1 个友方单位获得 +3 攻击力。友方回合结束时，将其消灭。」
/// 后半段的「其」指前半段选中的那个单位。旧产物把第二段编成一次**新的**选靶请求：
/// 回合结束不是玩家在出牌，没人应答 → 空集 → 效果静默消失（玩家看到的就是「后半段被丢弃」）。
/// 这里必须真的打到「那个单位死了」，而不是「卡能打出」。
/// </summary>
[Collection("engine-host")]
public sealed class CrossEffectTargetTests
{
    private const string RepoRoot = @"H:\Working Folder\OrC-KSD.Godot";
    private const string PackBase = RepoRoot + @"\proto\data\kards";
    private const string FinalPush = "pack/builtin/final_push";
    private const string Host = "pack/builtin/4_pioneer_battalion";

    private readonly ITestOutputHelper _out;
    public CrossEffectTargetTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// 「部署：使 1 个友方单位获得 +1+1」——部署指向。玩家拖到场上时就已经指向了某个单位，
    /// 效果必须落在那个单位上；旧数据把「1 个」写成「所有」/让引擎自己挑一个，就是玩家说的「随机」。
    /// </summary>
    [Fact]
    public async Task 兵团的部署指向作用于玩家选中的那个单位()
    {
        const string Regiment = "pack/builtin/17th_infantry_regiment";
        OrcEngineHost.Reset();
        OrcEngineHost.Initialize(PackBase);
        OrcEngineHost.LoadPackCards(PackBase);
        var nations = Path.Combine(RepoRoot, "proto", "data", "nations");
        var catalog = new Kards.Ui.Core.CardCatalog();
        catalog.Load(nations, Kards.Ui.Core.CardCatalog.ResolveArtResource);
        var cards = new DeckConstructionService(catalog.Cards, nations).Cards;

        var session = await OrcMatchSession.CreateFromCatalogAsync(cards, seed: 20261025,
            matchId: "deploy-target", fallbackCardId: Host,
            openingHand: [Host, Regiment], sourceDirectory: nations, firstPlayerIndex: 0);
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            var hostCard = session.View.SelfHand.FirstOrDefault(c => c.CardId == Host)
                ?? throw new InvalidOperationException($"起手没有 {Host}");
            var gaps = session.Actions.DeploymentGaps;
            var deployed = await session.SubmitAsync(new PlayCard(hostCard.Uid, GapId: gaps.Last().GapId));
            session.Pump();
            Assert.Equal(UiSubmitOutcome.Applied, deployed);
            var host = session.View.SelfLine.FirstOrDefault(c => c.Uid == hostCard.Uid)
                ?? throw new InvalidOperationException("铺垫单位没能落场");
            var attackBefore = host.EffectiveAttack ?? 0;
            var defenseBefore = host.EffectiveDefense ?? 0;

            for (var turn = 0; turn < 8 && session.View.SelfKredits < 2; turn++)
            {
                await session.SubmitAsync(new EndTurn());
                session.Pump();
            }
            Assert.True(session.View.SelfKredits >= 2, "攒不出兵团部署的费用");

            var regiment = session.View.SelfHand.FirstOrDefault(c => c.CardId == Regiment)
                ?? throw new InvalidOperationException($"手牌里没有 {Regiment}");
            var slot = session.Actions.DeploymentGaps.Last();
            var ordered = await session.SubmitAsync(new PlayCard(regiment.Uid, GapId: slot.GapId, TargetUid: host.Uid));
            session.Pump();
            _out.WriteLine($"[regiment] outcome={ordered} reason={session.LastRejectionReason} pending={session.TargetPending}");
            Assert.Equal(UiSubmitOutcome.Applied, ordered);

            var after = session.View.SelfLine.FirstOrDefault(c => c.Uid == host.Uid);
            Assert.NotNull(after);
            _out.WriteLine($"[regiment] target attack={after!.EffectiveAttack} defense={after.EffectiveDefense} "
                + $"(was {attackBefore}/{defenseBefore})");
            Assert.Equal(attackBefore + 1, after.EffectiveAttack);
            Assert.Equal(defenseBefore + 1, after.EffectiveDefense);
        }
        finally
        {
            session.Dispose();
            OrcEngineHost.LoadPackCards(PackBase);
        }
    }

    [Fact]
    public async Task 最后一击的回合结束效果消灭的是打出时选中的那个单位()
    {
        OrcEngineHost.Reset();
        OrcEngineHost.Initialize(PackBase);
        OrcEngineHost.LoadPackCards(PackBase);
        var nations = Path.Combine(RepoRoot, "proto", "data", "nations");
        var catalog = new Kards.Ui.Core.CardCatalog();
        catalog.Load(nations, Kards.Ui.Core.CardCatalog.ResolveArtResource);
        var cards = new DeckConstructionService(catalog.Cards, nations).Cards;
        Assert.True(cards.Count > 0, "卡池为空");

        var session = await OrcMatchSession.CreateFromCatalogAsync(cards, seed: 20261025,
            matchId: "cross-effect-target", fallbackCardId: Host,
            openingHand: [Host, FinalPush], sourceDirectory: nations, firstPlayerIndex: 0);
        try
        {
            // 攒费阶段要让对手回合正常走完，否则资源不会增长；最后一步之前再关掉它，
            // 免得 AI 在我方回合结束前把靶子打掉，把「谁杀了它」搅混。
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            var hostCard = session.View.SelfHand.FirstOrDefault(c => c.CardId == Host)
                ?? throw new InvalidOperationException($"起手没有 {Host}：[{string.Join(' ', session.View.SelfHand.Select(c => c.CardId))}]");
            var deployed = await session.SubmitAsync(new PlayCard(hostCard.Uid, GapId: session.Actions.DeploymentGaps.Last().GapId));
            session.Pump();
            _out.WriteLine($"[deploy] outcome={deployed} reason={session.LastRejectionReason}");
            Assert.Equal(UiSubmitOutcome.Applied, deployed);
            var target = session.View.SelfLine.FirstOrDefault(c => c.Uid == hostCard.Uid)
                ?? throw new InvalidOperationException("单位没能落场");
            var attackBefore = target.EffectiveAttack ?? 0;
            _out.WriteLine($"[deploy] uid={target.Uid} attack={attackBefore}");

            for (var turn = 0; turn < 8 && session.View.SelfKredits < 1; turn++)
            {
                await session.SubmitAsync(new EndTurn());
                session.Pump();
            }
            Assert.True(session.View.SelfKredits >= 1, "攒不出最后一击的费用");

            var order = session.View.SelfHand.FirstOrDefault(c => c.CardId == FinalPush)
                ?? throw new InvalidOperationException($"手牌里没有 {FinalPush}：[{string.Join(' ', session.View.SelfHand.Select(c => c.CardId))}]");
            var played = await session.SubmitAsync(new PlayCard(order.Uid, TargetUid: target.Uid));
            session.Pump();
            _out.WriteLine($"[order] outcome={played} reason={session.LastRejectionReason} pending={session.TargetPending}");
            Assert.Equal(UiSubmitOutcome.Applied, played);

            var buffed = session.View.SelfLine.FirstOrDefault(c => c.Uid == target.Uid);
            Assert.NotNull(buffed);
            _out.WriteLine($"[buff] attack={buffed!.EffectiveAttack} (was {attackBefore})");
            Assert.Equal(attackBefore + 3, buffed.EffectiveAttack);

            // 己方回合结束 → 「将其消灭」必须作用在刚才那个单位上。
            session.DriveOpponent = false;
            await session.SubmitAsync(new EndTurn());
            session.Pump();
            var survivors = string.Join(' ', session.View.SelfLine.Where(c => !c.IsHq).Select(c => $"{c.CardId}@{c.SlotIndex}"));
            _out.WriteLine($"[turn-end] line=[{survivors}] phase={session.View.Phase}");
            Assert.DoesNotContain(session.View.SelfLine, c => c.Uid == target.Uid);
        }
        finally
        {
            session.Dispose();
            OrcEngineHost.LoadPackCards(PackBase);
        }
    }
}
