using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

/// <summary>
/// 「部署：使 1 个友方单位获得 -1 行动花费」是**两段式**：先把单位放到战线（位置），
/// 再让玩家选一个友方单位（效果目标）。位置和目标不能互相顶掉。
/// 这条用例专门盯第二段：预打出期间必须真的发出一次 fieldUnit 请求，而且效果落在玩家选的那个单位上。
/// </summary>
[Collection("engine-host")]
public sealed class DeployTargetingTests
{
    private const string RepoRoot = @"H:\Working Folder\OrC-KSD.Godot";
    private const string PackBase = RepoRoot + @"\proto\data\kards";
    private const string Host = "pack/builtin/4_pioneer_battalion";
    private const string Regiment = "pack/exp-bloodandiron/62_infantry_regiment";

    private readonly ITestOutputHelper _out;
    public DeployTargetingTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task 部署指向在落位之后再问玩家选哪个友方单位()
    {
        OrcEngineHost.Reset();
        OrcEngineHost.Initialize(PackBase);
        OrcEngineHost.LoadPackCards(PackBase);
        var nations = Path.Combine(RepoRoot, "proto", "data", "nations");
        var catalog = new Kards.Ui.Core.CardCatalog();
        catalog.Load(nations, Kards.Ui.Core.CardCatalog.ResolveArtResource);
        var cards = new DeckConstructionService(catalog.Cards, nations).Cards;

        var session = await OrcMatchSession.CreateFromCatalogAsync(cards, seed: 20261025,
            matchId: "deploy-targeting", fallbackCardId: Host,
            openingHand: [Host, Regiment], sourceDirectory: nations, firstPlayerIndex: 0);
        try
        {
            await session.SettleMulliganAsync(interactive: false);
            session.Pump();

            var hostCard = session.View.SelfHand.FirstOrDefault(c => c.CardId == Host)
                ?? throw new InvalidOperationException($"起手没有 {Host}");
            var placed = await session.SubmitAsync(new PlayCard(hostCard.Uid, GapId: session.Actions.DeploymentGaps.Last().GapId));
            session.Pump();
            Assert.Equal(UiSubmitOutcome.Applied, placed);
            var host = session.View.SelfLine.FirstOrDefault(c => c.Uid == hostCard.Uid)
                ?? throw new InvalidOperationException("铺垫单位没能落场");
            var opCostBefore = host.EffectiveOpCost;

            for (var turn = 0; turn < 8 && session.View.SelfKredits < 2; turn++)
            {
                await session.SubmitAsync(new EndTurn());
                session.Pump();
            }
            Assert.True(session.View.SelfKredits >= 2, "攒不出第 62 步兵团的费用");

            var regiment = session.View.SelfHand.FirstOrDefault(c => c.CardId == Regiment)
                ?? throw new InvalidOperationException($"手牌里没有 {Regiment}");

            var requests = new List<UiTargetRequest>();
            var answered = 0;
            session.TargetRequested += (request, responder) =>
            {
                requests.Add(request);
                // 明确选"先前铺好的那个单位"，而不是碰巧排在末尾的新单位自己。
                var slot = request.Slots[0];
                var pick = slot.Allowed.First(c => c.Id == host.Uid);
                _out.WriteLine($"[target] selector={request.SelectorName} slot={slot.Name} "
                    + $"candidates={slot.Allowed.Count} pick={pick.Id}");
                Assert.True(responder.Complete(request.RequestId,
                    new Dictionary<string, IReadOnlyList<string>> { [slot.Name] = [pick.Id] }));
                answered++;
            };

            var outcome = await session.SubmitAsync(new PlayCard(regiment.Uid,
                GapId: session.Actions.DeploymentGaps.Last().GapId));
            session.Pump();
            _out.WriteLine($"[deploy] outcome={outcome} reason={session.LastRejectionReason} "
                + $"requests={requests.Count} answered={answered} pending={session.TargetPending}");

            Assert.Equal(UiSubmitOutcome.Applied, outcome);
            Assert.True(requests.Count >= 1, "部署指向没有向玩家发起任何选靶请求（= 效果只能自动挑目标）");
            Assert.Equal("fieldUnit", requests[0].SelectorName);
            Assert.True(requests[0].Slots[0].Allowed.Count >= 1, "选靶请求的候选为空");

            var after = session.View.SelfLine.FirstOrDefault(c => c.Uid == host.Uid);
            Assert.NotNull(after);
            _out.WriteLine($"[result] opCost={opCostBefore} -> {after!.EffectiveOpCost}");
            Assert.Equal((opCostBefore ?? 0) - 1, after.EffectiveOpCost);
        }
        finally
        {
            session.Dispose();
            OrcEngineHost.LoadPackCards(PackBase);
        }
    }
}
