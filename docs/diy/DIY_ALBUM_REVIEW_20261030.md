# DIY 相册评审（2026-10-30，第一批 3 张）

**评审口径**：不看「参数能不能写」，只看「打完这张牌，场上/手牌/总部会变成什么」。
每条结论都给出**判据出处**（哪个类型、哪一行、哪条测试），供复核。

**档位定义**

| 档 | 含义 | 谁来做 |
|---|---|---|
| **A** | 今天就能进包，不用改任何代码 | 写一个 `effects/*.prefab.json` + csx |
| **B** | 宿主生成器加一个分支就能做（引擎能力已有） | 我改 `DesktopNativeCodegen` |
| **C** | 引擎缺一个公开入口，宿主无论怎么写都做不到 | 提给引擎团队 |
| **D** | 结构性不可行（语义本身与引擎模型冲突） | 不做，说明原因 |

---

## 前置：一条必须先讲清的结构事实

宿主有**两条 DIY 路径**，能力差得很远：

| | 游戏内编辑器（玩家点选） | 卡包资产（手写 prefab + csx） |
|---|---|---|
| 触发时机 | **只有两个**：`unit.deployed` / `card.played` | 引擎 hook 表全部（`counter.triggered`、`card.died`、`turn.start`…） |
| 动作 | 161 个目录动作，62 个可执行 | `EffectRuntime` 28 个入口 + 任意 C# |
| 判据 | `DiyCardCompiler.Template()`：`var hook = type == "unit" ? "unit.deployed" : "card.played";`（**硬编码**，无参数可改） | `samples/demo-card-pack/effects/*.prefab.json` 的 `events[].csx` |

**相册里这三张卡，全部落在「手写卡包」这一侧**——不是能力不够，是游戏内编辑器的触发时机白名单里根本没有它们需要的时机。

### DIY 的真实天花板（已核实，不是估计）

`EffectRuntime` 公开的 28 个动作入口（`src/Orc.Game/Effects/EffectRuntime.cs` + `EffectRuntime.Board.cs`）：

```
AddToHand Battle Buff CostMod Damage DeclareAura DeployCostMod Draw FetchFromDeck
GainPointSlots GainPoints Grant JoinFrontline JoinSupportLine Kill LosePointSlots
LosePoints Move PickHandCards Pin RerunAuras Reveal Revoke Select SetKeywordValue
ShuffleIn Silence Upgrade
```

**没有**「激活反制」、**没有**「复制卡牌」。这是下面两张卡卡住的地方。

补充：引擎的结构化 DSL 只有约 20 个 op（`DslOpRegistry`：`addToHand attack aura buff costMod damage destroy draw gainPoint gainSlot grant losePoint loseSlot move pin reveal shuffleIn silence upgrade nested csx script`），另有 `csx` 作为逃生口。v4 已删除 `AuthoringRuntime`，csx 直接 `EffectRuntime.ResolveFor(card)` 取入口。

---

## 卡 1｜第12党卫军装甲师（5K/1，5/6，闪击）

> 敌方单位被消灭时，随机激活手牌中的 1 张反制。

**档位：C**

| 要素 | 状态 | 判据 |
|---|---|---|
| 触发时机「被消灭时」 | ✅ 引擎有 | `card.died` / `card.destroyed` 在 hook 表内 |
| 触发时机（游戏内） | ❌ | `DiyCardCompiler.Template()` 硬编码两个 hook |
| 只对**敌方**单位 | ✅ 可做 | csx 读载荷 `Card` + 比 `Owner` |
| **随机**取一张 | ✅ 可做 | `MatchRandomService.ResolveFor(card)` 公开，含 `PickOne<T>(IReadOnlyList<T>)` |
| 过滤出「反制」 | ✅ 可做 | `card is CounterCard`；手牌 `Player.Hand` 公开 |
| **激活反制** | ❌ **缺** | 见下 |

**为什么「激活」是缺的**：`EffectRuntime` 28 个入口里没有它。唯一的公开入口是 `PlayManager.UseCounterAsync(CounterCard, ct)`，而 `Match.PlayManager` 从一张卡上**取不到**——`Player.EffectRuntime` 是 `internal`，`Player` 也不暴露 `Match`/`PlayManager`。csx 里拿不到那个对象。

**还有一个规则冲突，比缺入口更麻烦**：`UseCounterAsync` 的前置判定是 `CardPlayGuards.EvaluateCounterUse`，拒绝类别含 **`NotOwnerTurn`（必须己方回合）** 与 `NotEnoughPoints`（扣点）。而「敌方单位被消灭」大量发生在**对手回合**——就算引擎补了入口，这张卡在对手回合也会**静默不激活**。这是规则层面的问题，需要产品裁决：

- 要么允许非己方回合激活（＝免费武装，破坏反制经济）
- 要么卡面改成「**己方回合**敌方单位被消灭时」

**怎么验**：`dotnet test` 里搜 `PlayChainCounterTests`（引擎侧，验证 `NotOwnerTurn` 拒绝路径）；宿主侧无判据，因为入口不存在。

---

## 卡 2｜第352步兵师（4K/1，4/4）

> 部署：随机复制手牌中的 1 张花费不大于 4 的反制，并将其激活。

**档位：C（但只差一个入口，且没有规则冲突）**

| 要素 | 状态 | 判据 |
|---|---|---|
| 触发「部署」 | ✅ | `unit.deployed`，游戏内编辑器就支持 |
| 过滤手牌（反制 ∧ 费用≤4） | ✅ | `Player.Hand` 公开；费用读 `DeployCost`（注意：读现值还是印刷值要定口径，`CostMod` 会改现值） |
| 随机 | ✅ | `MatchRandomService.PickOne` |
| **复制** | ✅ | `MatchCardService.ResolveFor(card).CreateAndPlaceToHandAsync(definitionId, owner, ct)` —— 公开静态解析 + 公开方法（`MatchCardService.cs:457`） |
| **激活** | ❌ 同卡 1 | 缺入口 |

**比卡 1 干净的地方**：部署发生在**己方回合**，`NotOwnerTurn` 不冲突；扣点与否只影响强度，不影响可行性。

**两个实现细节，做的时候要处理**：

1. **手牌上限 9**（`Player.HandLimit = 9`）：复制进满手牌会失败，需要决定是「爆牌弃置」还是「不触发」。
2. **费用口径**：「花费不大于 4」读印刷费还是现值？若读现值，`CostMod` 会让这张卡的选择面在局中漂移——玩家会觉得「明明写着 4 费却不能复制」。

**结论**：引擎补 `ActivateCounterAsync` 一个入口，这张卡就能做。**三张里最值得先推的**。

---

## 卡 3｜德军第6伞兵团（5K/1，4/5，奋战）

> 友方反制触发时，对敌方总部造成 2 点伤害。

**档位：A（手写卡包今天就能做）**

| 要素 | 状态 | 判据 |
|---|---|---|
| 触发「友方反制触发时」 | ✅ | hook `counter.triggered` 在引擎 hook 表；引擎模板 `counter_basic.tpl.json` 就是它，载荷 `{Card, Player}` |
| 打敌方总部 2 点 | ✅ 宿主已有 | 生成代码走 `SelectAsync(new EffectSelector("all","enemy","hq"))` → `DamageAsync(hq, 2)`；`damageHQ` 本身在 62 个 alpha 动作里 |
| 词条「奋战」 | ✅ | `KeywordIds.Fury = "奋战"` |
| 游戏内编辑器能不能选这个时机 | ❌ | 宿主 `Occasion()` 里有中文标签「反制触发后」，但**选择器里不给选**（编译器硬编码两个 hook） |

**注意**：这张卡的效果**不需要任何新能力**，唯一的门槛是「它不能通过游戏内编辑器做出来」。要让它进游戏内编辑器，改的是**触发时机白名单**（宿主侧改动，B 档），不是引擎。

**怎么验**：`dotnet test --filter FullyQualifiedName~ComplexDiyTests`（已证明 `EffectRuntime` 在真对局里可调用）；hook 侧判据在引擎 `PlayChainCounterTests`（`counter.triggered` 在激活分支发出、取消分支不发）。

---

## 三张卡共同的、比上面所有问题都硬的闸门

**DIY 效果在开房/联机路径上是坏的**，与卡本身无关：

| 路径 | 表现 | 判据 |
|---|---|---|
| 本地对局（客户端自己跑 Match） | 效果**真的跑** | `ComplexDiyTests` 6 条全绿 |
| 独立服务端（真联机） | 卡能出，**效果静默不生效** | `AuthoredCardReachesBothPlayers…` |
| 内嵌服务端（单机开房 / 安卓房主） | **整局开不起来**，`match.start` 抛异常、message 含 "csx" | `AuthoredKeywordEffectMakesTheInProcessServerRefuseToStartTheMatch` |

根因：生成的 csx 依赖宿主程序集，引擎沙箱白名单里没有它。

**范围要讲准**：这两条用例验的都是**词条承载**的效果。**卡面 prefab 承载**的效果在服务端是否同样失效，**目前没有判据**——这是一个可以在半天内补掉的实验，补掉之前不宣称。

**所以「进游戏」要分三句话**：

1. **单人本地对战**：三张卡都能跑（卡 3 现在就能，卡 1/2 等引擎入口）。
2. **联机**：三张卡都只会有卡面、没有效果。
3. **手机开房**：带效果的 DIY 卡会让整局开不起来。

---

## 排序建议

| 顺序 | 做什么 | 收益 | 谁做 |
|---|---|---|---|
| 1 | 引擎加 `EffectRuntime.ActivateCounterAsync(CounterCard, ct)` | 一次性解锁「激活反制」这一类设计（卡 1、卡 2，以及相册里大概率还有的同族） | 引擎团队 |
| 2 | 宿主把触发时机白名单从 2 个放开到 hook 表 | 解锁卡 1、卡 3 在**游戏内编辑器**里可做 | 我 |
| 3 | 补「卡面 prefab 效果在服务端是否失效」的判据 | 把联机那句话从「没有判据」变成有结论 | 我 |
| 4 | 修 csx 依赖宿主程序集（沙箱白名单） | 联机 + 手机开房同时转绿 | 我（生成器侧） |

---

## 附：社区 DIY 的现状（网上取样，2026-10-30）

搜到的社区「DIY」几乎全部是**卡面 + 数值 + 一句效果文字**，工具是第三方卡牌生成器
（如「kardsDIY卡牌编辑器 v3.71」，3DM 收录）、贴吧设计帖、B 站自制卡视频。

**这意味着一件事，值得写下来**：社区的「DIY」和引擎的「DIY」是两个不同的东西——

- 社区做的是**看得见的卡**：图、费用、身材、词条名、效果文案。
- 引擎要的是**能执行的逻辑**：hook + 动作 + 参数，或者一段 csx。

两者之间没有自动通道。把相册里的一句话变成能跑的卡，中间那步**必须有人写效果逻辑**，
而且必须先回答「这句话的每个动词，引擎有没有对应入口」——这正是本评审在做的事。

顺带说明：引擎自带的中文效果文本解析器（`EffectParsing/Lexicon`）目前只认 **17 个动作词**
（造成/攻击/消灭/摧毁/抽/获得/具有/失去/移动/压制/抑制/加入手牌/洗入/升为老兵/揭示…）
和 **4 个具名触发**（部署/亡计/动员/抉择）。「**激活**」「**复制**」都不在里面，
所以这三张卡的效果文案**不能靠文本解析直接落地**。

---

*本评审基于 `OrC-KSD-github` `169e575`（已合并上游 Decks 子系统）与 `OrC-KSD.Godot` 工作树。*
