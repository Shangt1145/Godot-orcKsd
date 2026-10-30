# Issue：`EffectRuntime` 缺「激活反制」入口，且反制激活的回合门禁与「敌方回合触发的效果」语义冲突

> 提交目标：`hitman-OLD6/OrC-KSD`（GitHub Issues）
> 依据版本：`origin/main` @ `a3d6678`（2026-10-30）
> 提交方：宿主 `OrC-KSD.Godot`（Godot 客户端）

## 一句话

DIY 卡需要「把手里的一张反制激活」，`EffectRuntime` 没有这个入口；而唯一能做这件事的
`PlayManager.UseCounterAsync` 在 csx 里**拿不到对象**。补入口不难，但它带着一个**规则问题**
需要先裁决，否则入口补了卡还是不能按卡面文字工作。

## 现象

宿主在评估玩家投稿的 DIY 卡，其中两张做不出来：

| 卡面效果 | 卡在哪 |
|---|---|
| 「敌方单位被消灭时，随机激活手牌中的 1 张反制」 | 缺激活入口 + 回合门禁冲突 |
| 「部署：随机复制手牌中的 1 张花费不大于 4 的反制，并将其激活」 | 只缺激活入口（其余全部现成） |

第二张除「激活」外的每一步都已有公开 API，可以证明这条请求**不是**在要一个新机制，
而是在要一个**已有机制的出口**：

| 要素 | 现有入口 | 位置 |
|---|---|---|
| 随机取一张 | `MatchRandomService.ResolveFor(card).PickOne<T>(...)` | `src/Orc.Game/Match/RandomService.cs:100,169` |
| 复制一张进手牌 | `MatchCardService.ResolveFor(card).CreateAndPlaceToHandAsync(definitionId, owner, ct)` | `src/Orc.Game/Match/MatchCardService.cs:457` |
| 打敌方总部 | `SelectAsync(new EffectSelector("all","enemy","hq"))` + `DamageAsync(hq, n)` | `EffectRuntime.cs` / `EffectRuntime.Board.cs` |
| **激活反制** | **无** | — |

## 为什么现在绕不过去

三条路都断了，不是我们没找：

1. **`EffectRuntime` 没有这个入口。** 目前公开 28 个动作入口
   （`AddToHand Battle Buff CostMod Damage DeclareAura DeployCostMod Draw FetchFromDeck
   GainPointSlots GainPoints Grant JoinFrontline JoinSupportLine Kill LosePointSlots
   LosePoints Move PickHandCards Pin RerunAuras Reveal Revoke Select SetKeywordValue
   ShuffleIn Silence Upgrade`），没有激活反制，也没有「复制卡牌」。
2. **`PlayManager` 从一张卡上取不到。** 唯一入口是
   `PlayManager.UseCounterAsync(CounterCard, ct)`（`src/Orc.Game/Managers/PlayManager.cs:529`），
   而 `Match.PlayManager` 虽然是 public（`src/Orc.Game/Match/Match.cs:287`），
   csx 手里只有 `Card`——`Player.EffectRuntime` 是 `internal`（`src/Orc.Game/Players/Player.cs:177`），
   `Player` 也不暴露 `Match`/`PlayManager`。
3. **`KeywordEffectActionFace` 不是更大的门面。** 它只有一个成员 `DeathAsync`
   （`src/Orc.Game/Cards/KeywordEffectActionFace.cs:31`），注释里说的「csx 侧统一走它」并不成立。

我们也考虑过用 `card.InvokeMemberAsync(CardProcesses.UseCounter, 自建 Context, ct)` 硬穿过去。
**放弃了**：`PlayManager.UseCounterAsync` 在调用成员之前做了终局门禁、`CounterUnavailable` 判定、
动作作用域、`EvaluateCounterUse` 前置判定和失败原因映射。绕过它等于自己重写一遍扣点与守卫，
一定漂移成两套口径——这正是 `DeckRules` 注释里警告过的那种病。

## 请求

### A. 补一个入口（必需）

```csharp
// EffectRuntime
public Task<bool> ActivateCounterAsync(Card counter, CancellationToken ct = default);
```

语义建议：**完全委托** `PlayManager.UseCounterAsync`，不另写判定。

- `counter` 不是 `CounterCard` ⇒ 返回 `false`（或抛出，你定；我们倾向 `false` + 不抛）。
- 沿用现有门禁：终局 / `CounterUnavailable`（已消耗、已销毁）/ `EvaluateCounterUse`（回合、点数）。
- 失败时**零副作用**：不扣点、不翻转状态、不发 `counter.triggered`。
- 返回值＝是否真的激活成功。

这样宿主侧一行 csx 就能写出来，且扣费与守卫只有一处实现。

### B. 一个需要你们裁决的规则问题（比 A 更重要）

`CounterUseJudicator` 在非己方回合直接拒（`src/Orc.Game/Judicators/CounterUseJudicator.cs:36,42`
⇒ `CounterUseRejection.NotOwnerTurn`）。

但「**敌方**单位被消灭时」这个触发时机，**大量发生在对手回合**（对手自己送掉单位、或我方反击致死）。
也就是说：即使 A 补好了，那张卡在对手回合也会**静默不激活**——玩家看到的是「卡面写了，但没反应」。

三条路，请你们选一条（我们都能接受，只要能定下来）：

1. **维持门禁**，宿主把卡面改成「**己方回合**敌方单位被消灭时」。
   代价：卡面变窄，和原设计不同。
2. **入口加一个显式的 `bypassTurnGate` 参数**（默认 false）。
   代价：等于允许「免费武装」，可能破坏反制的经济模型（激活本该花点数）。
3. **在引擎层定义「由效果驱动的激活」为独立语义**（不经过玩家回合门禁，但仍扣点）。
   代价：需要一条新规则，但语义最干净。

我们倾向 **3**，其次是 **1**。**2** 最省事但风险最大。

## 复现（最小）

```csharp
// 一张卡，hook = counter.triggered（或 card.died），csx 里想激活手里的一张反制
Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync =
    async (view, ctx, ct) =>
{
    var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(view.Host);
    if (runtime is null) return;

    var hand = view.Host.Owner!.Hand.OfType<Orc.Game.Cards.CounterCard>().ToArray();
    if (hand.Length == 0) return;

    var rng = Orc.Game.MatchRandomService.ResolveFor(view.Host)!;
    var target = rng.PickOne(hand);

    // ← 到这里为止全部可编译、可运行。
    // ← 下一步（激活 target）没有任何公开 API 可调。
};
```

预期：卡编译通过、能进对局、前面的随机与手牌读取都正常；
实际：最后一步无 API，只能整条效果降级成「什么都不做」。

## 我们不要求什么

- 不要求引擎为此开任何 `internal`。
- 不要求改动反制的现有语义（一次性消耗、武装窗口、激活↔取消可反复）。
- 不要求「复制卡牌」入口——那个我们用 `CreateAndPlaceToHandAsync` 已经能做，不占排期。

## 补充

宿主侧另有一处**触发时机白名单**问题（游戏内编辑器只支持 `unit.deployed` / `card.played`），
但那是我们自己的欠账，已记录，不进这个 issue。
