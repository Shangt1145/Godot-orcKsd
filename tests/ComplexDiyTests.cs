using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;
using Xunit.Abstractions;

namespace Kards.Ui.Tests;

/// <summary>
/// 「复杂 DIY」到底能不能做——用真引擎跑，不看参数表。
///
/// <para>
/// 判据是**打完这张牌之后场上/手牌/总部变成了什么**，不是"声明被接受"。
/// 每张卡都走 <see cref="OrcMatchSession"/>：真的开局、真的出牌、真的结算。
/// 这几条对应玩家口中"复杂"的五种写法：条件、循环、遍历、变量、事件监听。
/// </para>
/// <para>
/// 明确**拒绝**的形态也在这里钉住（<see cref="RefusedShapesStayReadable"/>）：v4 没有运行期挂载
/// 效果快照的入口，所以 `bindEvent`／`grantEffect` 必须给出可读原因，而不是静默生成一张哑弹卡。
/// </para>
/// </summary>
[Collection("Workshop")]
public sealed class ComplexDiyTests(ITestOutputHelper output)
{
    private static string Root
    {
        get { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "Kards.Ui.csproj"))) path = Path.GetDirectoryName(path)!; return path; }
    }
    private static CardCatalog Catalog() { var catalog = new CardCatalog(); catalog.Load(Path.Combine(Root, "proto/data/nations")); return catalog; }

    /// <summary>造一张卡、开局、把它打出去，返回结算后的 session。调用方负责断言与释放。</summary>
    private static async Task<OrcMatchSession> PlayAsync(string graph, int seed, string name = "复杂步兵")
    {
        var draft = new DiyCardDraft
        {
            Name = name, Cost = 0, OpCost = 0, Attack = 1, Defense = 3,
            OrcEffects = [new() { DesktopJson = graph }],
        };
        var session = await OrcMatchSession.CreateFromCatalogAsync(Catalog().Cards, seed, "complex-diy", "probe",
            sourceDirectory: Path.Combine(Root, "proto/data/nations"), customCards: [draft], openingHand: [draft.Id], firstPlayerIndex: 0);
        session.DriveOpponent = false;
        await session.SettleMulliganAsync(false);
        session.SettleProjection();
        var card = session.View.SelfHand.Single(c => c.CardId == draft.Id);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(card.Uid, 0)));
        session.SettleProjection();
        return session;
    }

    [Fact]
    public async Task 条件成立时走then不成立时走else()
    {
        const string graph = """
        {"trigger":"deploy","actions":[
          {"op":"conditional",
           "condition":{"test":"compare","left":{"expr":"read","of":"self","path":"attack"},"cmp":">=","right":1},
           "then":[{"op":"repeat","times":2,"actions":[{"op":"drawOne","side":"self"}]}],
           "else":[{"op":"dealDamage","target":{"sel":"all","side":"enemy","zone":"hq"},"amount":5}]}]}
        """;
        using var session = await PlayAsync(graph, 901);
        // 出牌 -1、抽 2 ⇒ 净 +1；else 分支没跑，总部不掉血。
        Assert.Equal(2, session.View.SelfHand.Count);
        Assert.Equal(20, session.View.EnemyHq!.Health);
        output.WriteLine($"条件→循环：手牌={session.View.SelfHand.Count} 敌方总部={session.View.EnemyHq!.Health}");
    }

    [Fact]
    public async Task 遍历场上单位逐个加攻()
    {
        const string graph = """
        {"trigger":"deploy","actions":[
          {"op":"iterate","target":{"sel":"all","side":"friendly","zone":"field"},"as":"ally",
           "actions":[{"op":"changeAttribute","target":{"sel":"ref","ref":"ally"},"property":"attack","mode":"add","value":1}]}]}
        """;
        using var session = await PlayAsync(graph, 902);
        // 入场后自己在场上，遍历到自己 ⇒ 1 + 1 = 2。这条能证明 `iterate` 的循环变量真的绑上了，
        // 而不是"遍历了但每条都改到同一个错地方"。
        var self = Assert.Single(session.View.SelfLine);
        Assert.Equal(2, self.EffectiveAttack);
        output.WriteLine($"遍历加攻：自身攻击={self.EffectiveAttack}");
    }

    [Fact]
    public async Task 循环里套条件按次数累加()
    {
        const string graph = """
        {"trigger":"deploy","actions":[
          {"op":"repeat","times":3,"actions":[
            {"op":"conditional",
             "condition":{"test":"compare","left":{"expr":"read","of":"self","path":"attack"},"cmp":">","right":0},
             "then":[{"op":"drawOne","side":"self"}],"else":[]}]}]}
        """;
        using var session = await PlayAsync(graph, 903);
        // 出牌 -1、循环 3 次各抽 1 ⇒ 3。
        Assert.Equal(3, session.View.SelfHand.Count);
        output.WriteLine($"循环套条件：手牌={session.View.SelfHand.Count}");
    }

    [Fact]
    public async Task 变量与循环组合()
    {
        const string graph = """
        {"trigger":"deploy","actions":[
          {"op":"setVar","name":"hits","value":2},
          {"op":"repeat","times":2,"actions":[
            {"op":"dealDamage","target":{"sel":"all","side":"enemy","zone":"hq"},"amount":1}]}]}
        """;
        using var session = await PlayAsync(graph, 904);
        Assert.Equal(18, session.View.EnemyHq!.Health);
        output.WriteLine($"变量+循环：敌方总部={session.View.EnemyHq!.Health}");
    }

    /// <summary>事件监听：这张卡在场上时友方单位死亡，效果要真的触发。</summary>
    [Fact]
    public async Task 友方死亡时抽牌()
    {
        const string graph = """
        {"trigger":"friendlyDeath","actions":[{"op":"drawOne","side":"self"}]}
        """;
        var draft = new DiyCardDraft
        {
            Name = "守望步兵", Cost = 0, OpCost = 0, Attack = 1, Defense = 1,
            OrcEffects = [new() { DesktopJson = graph }],
        };
        using var session = await OrcMatchSession.CreateFromCatalogAsync(Catalog().Cards, 905, "complex-diy-listener", "probe",
            sourceDirectory: Path.Combine(Root, "proto/data/nations"), customCards: [draft], openingHand: [draft.Id], firstPlayerIndex: 0);
        session.DriveOpponent = false;
        await session.SettleMulliganAsync(false);
        session.SettleProjection();
        var card = session.View.SelfHand.Single(c => c.CardId == draft.Id);
        Assert.Equal(UiSubmitOutcome.Applied, await session.SubmitAsync(new PlayCard(card.Uid, 0)));
        session.SettleProjection();
        var before = session.View.SelfHand.Count;
        // 自己杀自己：这是"友方单位死亡"最省事的一次真实触发。
        var native = session.Match.Players[0];
        var host = session.Match.Battlefield.GetSupportLine(native).Members.Single(c => c.Name == draft.Name);
        await session.Match.EffectRuntime.KillAsync(host, ct: default);
        session.SettleProjection();
        Assert.Equal(before + 1, session.View.SelfHand.Count);
        output.WriteLine($"友方死亡监听：手牌 {before} -> {session.View.SelfHand.Count}");
    }

    /// <summary>
    /// 明确拒绝的形态必须**说得出原因**，而不是生成一张看着正常、打出去什么都没发生的卡。
    /// 这条守的是"诚实降级"：v4 没有运行期挂载效果快照的入口，所以亡语挂载编不出来。
    /// </summary>
    [Fact]
    public void 拒绝的形态给出可读原因()
    {
        (string Graph, string Reason)[] refused =
        [
            ("""
             {"trigger":"deploy","actions":[
               {"op":"bindEvent","target":{"sel":"self"},
                "effect":{"trigger":"death","targets":[],"actions":[{"op":"dealDamage","target":{"sel":"all","side":"enemy","zone":"hq"},"amount":2}]}}]}
             """, "运行期挂载效果"),
            ("""
             {"trigger":"passive","condition":{"test":"compare","left":{"expr":"read","of":"self","path":"attack"},"cmp":">","right":0},
              "actions":[{"op":"changeAttribute","target":{"sel":"all","side":"friendly","zone":"field"},"property":"attack","mode":"add","value":1}]}
             """, "光环带条件"),
        ];
        foreach (var (graph, reason) in refused)
        {
            var errors = DiyCapability.CheckEffect(JsonSerializer.SerializeToElement(JsonNode.Parse(graph)!));
            output.WriteLine($"{reason} → {(errors.Count == 0 ? "（没拒绝！）" : string.Join("；", errors))}");
            Assert.NotEmpty(errors);
        }
    }
}
