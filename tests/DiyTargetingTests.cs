using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// DIY 卡的「部署：对 1 个敌方单位造成 N 点伤害」是不是真的把选靶交给玩家。
///
/// <para>
/// 这条专门盯**两条不同的编译入口**，因为它们在引擎里落到不同的选靶 API：
/// 结构化 Ops 走原生 op 模板（`runtime.SelectAsync`，无头），完整声明图走 `DesktopTargetContext`（交互式）。
/// 官方卡包那批有脚本和声明图，所以 <c>DeployTargetingTests</c> 绿了不能证明 DIY 能用。
/// </para>
///
/// <para>
/// 判据分两层：① 引擎有没有向 UI 发 target 请求；② 伤害有没有落在**玩家点的那个**（不是候选第一个）。
/// 只断言 ① 会漏掉"发了请求但答案被丢掉"，只断言 ② 在单候选时会误判。
/// </para>
/// </summary>
[Collection("engine-host")]
public sealed class DiyTargetingTests
{
    private static string Root
    {
        get { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "Kards.Ui.csproj"))) path = Path.GetDirectoryName(path)!; return path; }
    }

    private static CardCatalog Catalog()
    {
        var catalog = new CardCatalog();
        catalog.Load(Path.Combine(Root, "proto/data/nations"));
        return catalog;
    }

    private sealed record Stage(OrcMatchSession Session, DiyCardDraft Shooter, DiyCardDraft First, DiyCardDraft Second, string FirstUid, string SecondUid);

    /// <summary>建局，把两个稻草人放到敌方支援线（两个候选才分得清"玩家选的"和"自动取第一个"）。</summary>
    private static async Task<Stage> PrepareAsync(int seed, DiyCardDraft shooter)
    {
        var catalog = Catalog();
        var first = new DiyCardDraft { Name = "敌方稻草人甲", Cost = 0, OpCost = 0, Attack = 1, Defense = 5 };
        var second = new DiyCardDraft { Name = "敌方稻草人乙", Cost = 0, OpCost = 0, Attack = 1, Defense = 5 };
        var session = await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards, seed, "diy-targeting", "probe",
            sourceDirectory: Path.Combine(Root, "proto/data/nations"),
            customCards: [shooter, first, second], openingHand: [shooter.Id], firstPlayerIndex: 0);
        session.DriveOpponent = false;
        await session.SettleMulliganAsync(interactive: false);
        session.SettleProjection();

        var enabled = await session.DebugAsync(new MatchDebugCommand(MatchDebugAction.Enable));
        Assert.True(enabled.Success, enabled.Message);
        foreach (var dummy in new[] { first, second })
        {
            var summoned = await session.DebugAsync(new MatchDebugCommand(MatchDebugAction.Summon, Side: "enemy", CardId: dummy.Id));
            Assert.True(summoned.Success, summoned.Message);
        }
        session.SettleProjection();
        Assert.Equal(2, session.View.EnemyLine.Count);

        return new Stage(session, shooter, first, second,
            session.View.EnemyLine.Single(c => c.CardId == first.Id).Uid,
            session.View.EnemyLine.Single(c => c.CardId == second.Id).Uid);
    }

    /// <summary>把选靶面板一律答成"候选里的最后一个"，并记下问了几次、点了谁。</summary>
    private sealed class LastAnswer
    {
        public int Prompts;
        public string? Picked;
        public void Attach(OrcMatchSession session) => session.TargetRequested += (request, responder) =>
        {
            Prompts++;
            var last = request.Slots[0].Allowed.Last();
            Picked = session.View.EnemyLine.FirstOrDefault(c => c.Uid == last.Id)?.Uid ?? last.Id;
            Assert.True(responder.Complete(request.RequestId,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    [request.Slots[0].Name] = [last.Id],
                }));
        };
    }

    /// <summary>部署，并把选靶面板一律答成"候选里的最后一个"。</summary>
    private static async Task<(int Prompts, string? PickedUid)> DeployChoosingLastAsync(OrcMatchSession session, string cardId)
    {
        var answer = new LastAnswer();
        answer.Attach(session);
        var card = session.View.SelfHand.Single(c => c.CardId == cardId);
        var gap = session.Actions.DeploymentGaps.Last().GapId;
        var outcome = await session.SubmitAsync(new PlayCard(card.Uid, GapId: gap));
        session.SettleProjection();
        Assert.Equal(UiSubmitOutcome.Applied, outcome);
        return (answer.Prompts, answer.Picked);
    }

    private static void AssertPlayerChose(Stage stage, int prompts, string? pickedUid)
    {
        var board = string.Join("，", stage.Session.View.EnemyLine.Select(c => $"{c.Definition.Name}=防{c.EffectiveDefense}"));
        Assert.True(prompts == 1,
            $"「部署：对 1 个敌方单位造成伤害」没有弹选靶（prompts={prompts}，rejection={stage.Session.LastRejectionReason}，" +
            $"盘面：{board}）。玩家实际感知就是随机命中或没反应。");

        // 只有玩家点的那个掉 2 点防；第一个候选必须毫毛无伤，否则说明是"自动取第一个"。
        var hit = stage.Session.View.EnemyLine.Single(c => c.Uid == pickedUid);
        var untouched = stage.Session.View.EnemyLine.Single(c => c.Uid != pickedUid);
        Assert.Equal(3, hit.EffectiveDefense);
        Assert.Equal(5, untouched.EffectiveDefense);
    }

    /// <summary>
    /// 结构化 Ops 的 `Sel="one"` 曾经走引擎 op 模板（`runtime.SelectAsync`＝无头），
    /// 不向 UI 发 target 请求，直接命中候选第一个（2026-10-26 实测：部署后"乙方"变 3、"甲方"仍 5）。
    /// 现在这类 op 在编译期转成声明图（OrcStructuredGraph），走预打出捕获 + 交互选靶。
    /// </summary>
    [Fact]
    public async Task 结构化Ops的部署单体伤害应当弹选靶()
    {
        var shooter = new DiyCardDraft
        {
            Name = "结构点名兵", Cost = 0, OpCost = 0, Attack = 1, Defense = 3,
            UseStructuredEffects = true,
            OrcEffects =
            [
                new OrcEffectDraft
                {
                    Template = "deploy_basic",
                    Ops = [new OrcOpDraft { Op = "damage", Sel = "one", Side = "enemy", Amount = 2 }],
                },
            ],
        };
        var stage = await PrepareAsync(1201, shooter);
        using var session = stage.Session;

        var (prompts, picked) = await DeployChoosingLastAsync(session, shooter.Id);

        AssertPlayerChose(stage, prompts, picked);
    }

    [Fact]
    public async Task 文本解析的部署单体伤害应当弹选靶()
    {
        var catalog = Catalog();
        var shooter = new DiyCardDraft
        {
            Name = "文本点名兵", Cost = 0, OpCost = 0, Attack = 1, Defense = 3,
            EffectText = "部署：对一个敌方单位造成2点伤害。",
        };
        var parsed = DiyTextAuthoring.Parse(shooter, shooter.EffectText, catalog.Cards);
        Assert.True(parsed.Success, string.Join("；", parsed.Problems));
        shooter = parsed.Draft!;

        var stage = await PrepareAsync(1202, shooter);
        using var session = stage.Session;

        var (prompts, picked) = await DeployChoosingLastAsync(session, shooter.Id);

        AssertPlayerChose(stage, prompts, picked);
    }

    /// <summary>结构化面里所有"默认单目标"的 op（Inspector 的 Seed 就是 Sel=one / Side=enemy）。</summary>
    private static readonly string[] SingleTargetOpIds =
    [
        "damage", "buff", "destroy", "pin", "silence", "costMod", "grant", "move", "reveal", "upgrade",
    ];

    public static TheoryData<string> SingleTargetOps
    {
        get { var data = new TheoryData<string>(); foreach (var op in SingleTargetOpIds) data.Add(op); return data; }
    }

    /// <summary>
    /// 每一个默认单目标 op 都必须**由玩家点选**目标，而不是引擎自己取候选第一个。
    /// <para>
    /// 判据两层：① 选靶面板恰好弹一次；② 效果只落在玩家点的那个单位上——只断言 ① 会漏掉
    /// "问了但答案被丢掉"，只断言 ② 在单候选时无法区分。
    /// </para>
    /// <para>
    /// reveal / upgrade 在稻草人身上本身是空操作（DIY 卡没有隐蔽标记，也没有老兵形态），
    /// 这两条只验"选靶通道"，不验揭示/晋升语义——所以断言是"问了一次、且谁都没被误伤"。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(SingleTargetOps))]
    public async Task 结构化单目标动作都由玩家点选目标(string op)
    {
        var shooter = new DiyCardDraft
        {
            Name = "结构" + op, Cost = 0, OpCost = 0, Attack = 1, Defense = 3,
            UseStructuredEffects = true,
            OrcEffects = [new OrcEffectDraft { Template = "deploy_basic", Ops = [Structured(op)] }],
        };
        var stage = await PrepareAsync(1300 + op.Length, shooter);
        using var session = stage.Session;

        var (prompts, picked) = await DeployChoosingLastAsync(session, shooter.Id);

        var board = string.Join("，", session.View.EnemyLine.Select(c =>
            $"{c.Definition.Name}=防{c.EffectiveDefense}/攻{c.EffectiveAttack}/区{c.Zone}"));
        Assert.True(prompts == 1,
            $"结构化 op「{op}」的部署单目标没有弹选靶（prompts={prompts}，rejection={session.LastRejectionReason}，盘面：{board}）。");

        var hit = session.View.EnemyLine.FirstOrDefault(c => c.Uid == picked);
        var other = session.View.EnemyLine.FirstOrDefault(c => c.Uid != picked);
        switch (op)
        {
            case "damage":
                Assert.NotNull(hit); Assert.NotNull(other);
                Assert.Equal(3, hit!.EffectiveDefense);
                Assert.Equal(5, other!.EffectiveDefense);
                break;
            case "buff":
                Assert.NotNull(hit); Assert.NotNull(other);
                Assert.Equal(2, hit!.EffectiveAttack);
                Assert.Equal(6, hit.EffectiveDefense);
                Assert.Equal(1, other!.EffectiveAttack);
                Assert.Equal(5, other.EffectiveDefense);
                break;
            case "destroy":
                Assert.Null(hit);
                Assert.NotNull(other);
                break;
            case "pin":
                Assert.NotNull(hit); Assert.NotNull(other);
                Assert.True(hit!.IsSuppressed, "玩家点的单位没有被压制。");
                Assert.False(other!.IsSuppressed, "没点到的单位也被压制了。");
                break;
            case "silence":
                Assert.NotNull(hit); Assert.NotNull(other);
                Assert.True(hit!.IsSilenced, "玩家点的单位没有被抑制。");
                Assert.False(other!.IsSilenced, "没点到的单位也被抑制了。");
                break;
            case "costMod":
                Assert.NotNull(hit); Assert.NotNull(other);
                Assert.Equal(1, hit!.EffectiveOpCost);
                Assert.Equal(0, other!.EffectiveOpCost);
                break;
            case "grant":
            {
                Assert.NotNull(hit); Assert.NotNull(other);
                // 视图里的词条是引擎标识（"blitz"），编辑器给的是中文标签（"闪击"）——不绑字面量，
                // 直接验"多出来的词条只在玩家点的那个身上"。
                var gained = hit!.Keywords.Except(other!.Keywords).ToArray();
                Assert.Single(gained);
                Assert.Empty(other.Keywords);
                break;
            }
            case "move":
                Assert.NotNull(hit); Assert.NotNull(other);
                Assert.Equal("frontline", hit!.Zone);
                Assert.NotEqual("frontline", other!.Zone);
                break;
            case "reveal":
            case "upgrade":
                Assert.NotNull(hit); Assert.NotNull(other);
                Assert.Equal(5, hit!.EffectiveDefense);
                Assert.Equal(5, other!.EffectiveDefense);
                break;
            default:
                Assert.Fail("用例表里出现了没有断言的动作：" + op);
                break;
        }
    }

    /// <summary>
    /// 一个效果里混排"要选目标"和"不需要选目标"的动作时，两条通道必须各走各的：
    /// 单目标走声明图（玩家点），抽牌仍走 op 模板（无需应答）。整条效果若因为其中一个 op
    /// 需要选靶就整体改道，抽牌这类动作的已验证语义会跟着漂。
    /// </summary>
    [Fact]
    public async Task 结构化效果混排时单目标仍由玩家点选()
    {
        var shooter = new DiyCardDraft
        {
            Name = "结构混排兵", Cost = 0, OpCost = 0, Attack = 1, Defense = 3,
            UseStructuredEffects = true,
            OrcEffects =
            [
                new OrcEffectDraft
                {
                    Template = "deploy_basic",
                    Ops =
                    [
                        new OrcOpDraft { Op = "damage", Sel = "one", Side = "enemy", Amount = 2 },
                        new OrcOpDraft { Op = "draw", Count = 1 },
                    ],
                },
            ],
        };
        var stage = await PrepareAsync(1311, shooter);
        using var session = stage.Session;

        var (prompts, picked) = await DeployChoosingLastAsync(session, shooter.Id);

        AssertPlayerChose(stage, prompts, picked);
    }

    /// <summary>指令走的是 <c>played_basic</c>（声明图 trigger=play），这条确认它也在"打出前"被问到。</summary>
    [Fact]
    public async Task 结构化指令的单目标伤害由玩家点选()
    {
        var shooter = new DiyCardDraft
        {
            Name = "结构点名令", CardType = "order", Cost = 0,
            UseStructuredEffects = true,
            OrcEffects =
            [
                new OrcEffectDraft
                {
                    // played_basic 的合法守卫是 SelfCard（SelfUnit 只配 deploy_basic）。
                    Template = "played_basic", Guard = "SelfCard",
                    Ops = [new OrcOpDraft { Op = "damage", Sel = "one", Side = "enemy", Amount = 2 }],
                },
            ],
        };
        var stage = await PrepareAsync(1312, shooter);
        using var session = stage.Session;

        var answer = new LastAnswer();
        answer.Attach(session);
        var card = session.View.SelfHand.Single(c => c.CardId == shooter.Id);
        var outcome = await session.SubmitAsync(new PlayCard(card.Uid));
        session.SettleProjection();

        Assert.Equal(UiSubmitOutcome.Applied, outcome);
        AssertPlayerChose(stage, answer.Prompts, answer.Picked);
    }

    /// <summary>
    /// 声明图的作用域＝图文本的哈希。联机工坊导出时会把图塞进 <c>desktop-effects.json</c> 并
    /// 经 <c>JsonNode</c> 往返一次；只要往返后的文本变一个字节，对方客户端捕获的键就和卡里
    /// 编译好的 <c>SelectAsync(..., scope)</c> 对不上——他的卡会退化成"执行时才去问目标"。
    /// </summary>
    [Fact]
    public void 声明图经导出往返后文本不变()
    {
        foreach (var op in SingleTargetOpIds)
        {
            var effect = new OrcEffectDraft { Template = "deploy_basic", Ops = [Structured(op)] };
            var graph = OrcStructuredGraph.Graph(effect, effect.Ops[0]);
            Assert.NotNull(graph);
            var roundTrip = System.Text.Json.Nodes.JsonNode.Parse(graph!)!.ToJsonString();
            Assert.Equal(graph, roundTrip);
        }
    }

    private static OrcOpDraft Structured(string op) => op switch
    {
        "damage" => new() { Op = "damage", Sel = "one", Side = "enemy", Amount = 2 },
        "buff" => new() { Op = "buff", Sel = "one", Side = "enemy", Attack = 1, Defense = 1 },
        "destroy" => new() { Op = "destroy", Sel = "one", Side = "enemy" },
        "pin" => new() { Op = "pin", Sel = "one", Side = "enemy", Amount = 1 },
        "silence" => new() { Op = "silence", Sel = "one", Side = "enemy" },
        "costMod" => new() { Op = "costMod", Sel = "one", Side = "enemy", Amount = 1 },
        "grant" => new()
        {
            Op = "grant", Sel = "one", Side = "enemy",
            Keyword = OrcEffectEditing.KeywordChoices[0].Id, Value = 1,
        },
        "move" => new() { Op = "move", Sel = "one", Side = "enemy", OpZone = "frontline" },
        "reveal" => new() { Op = "reveal", Sel = "one", Side = "enemy" },
        "upgrade" => new() { Op = "upgrade", Sel = "one", Side = "enemy" },
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "未知的单目标动作"),
    };
}
