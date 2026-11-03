# 选项弹窗：什么时候弹、弹出来长什么样（2026-11-03）

> 背景：引擎 `9b62d965`（R38）把 `choice` 接通了，`choice-failed[...]` 取代了 `NotSupportedException`。
> 我们这侧原本以为「弹窗还没做」，翻代码才发现**面板一直都在**（`ChoiceKind.Option` 那套），
> 缺的是两件事：**提示语没传**、**AI 提交路径不认识标识集**。本文记触发条件、改了什么、怎么验证的。

---

## 1 触发条件：五道闸，缺一道都不弹

| 闸 | 条件 | 不过会怎样 |
|---|---|---|
| 1 | 在**真对局**里跑（不是编辑器） | 编辑器只校验／生成，不执行 |
| 2 | 这张牌的效果图里真有 `choice` 节点 | 没有就没有。**今天 `samples/` 与 `proto/` 里 `"op": "choice"` 出现 0 次** |
| 3 | 执行流真的走到它 | 前面的 `branch`／`while` 可能跳过；`choice` 不许进循环（`ask-in-loop`） |
| 4 | 有桥：`owner.TargeterManager.Bridge` 非空 | 本地对局恒有（`OrcMatchSession` 装配 `OrcTargeterBridge`）；MCP 无桥 ⇒ `choice-failed[bridge-not-assembled]`，整张牌打不出 |
| 5 | 不是 AI 链、不是自动模式 | 见 §2 的三条分支 |

## 2 过了五道闸，三条分支

| 谁在打 | 实际发生什么 |
|---|---|
| **本地玩家** | `AnswerParked` → `Identifiers is not null` → `PresentTargetChoice` → 面板弹出（每个选项一张纸牌，`ChoiceCaption` ＝ 选项文案）。点一下 ⇒ `PickEvent(identifiers: …)` ⇒ 引擎回 0 起下标 |
| **对手／AI** | `BeginAiChain` 挂起 → `SubmitAiPick`。**改之前**：只按 `candidates` 解序号 ⇒ 选项请求恒空 ⇒ `CancelEvent` ⇒ `choice-failed[cancelled]`，那张牌打不出来 |
| **自动模式**（验证脚本、对手回合兜底） | `AutoRespond` 直接选第 0 项，静默 |

另外 `ShowTargetChoice` 开头有一句 `if (!IsFriendlyTurn) { responder.Cancel(...); }`——
就算请求漏到非己方回合，界面也会主动取消，不会让玩家替对手做选择。

---

## 3 改了什么

| 位置 | 改动 |
|---|---|
| `UiTargetRequest` | 新增 `Prompt`（缺省空串）。引擎只在 `choice` 的 `pending` 里带这句，`ask` 的输出逐条不变 |
| `OrcMatchSession.TargetChoices` | `Prompt = data.Prompt ?? ""`（R38 的 `SelectorPresentation.Prompt`） |
| `RemoteMatchFeed` | ① 解析 `prompt`；② `candidates` 空、`identifiers` 非空 ⇒ 按 **1 起序号** 造 `ChoiceKind.Option` 候选（`_picksByChoiceId[id] = i+1`，提交路径一行没改）；③ 单槽类别补 `Option` 分支 |
| `OrcTargeterBridge.SubmitAiPick` | 候选为空时按 **1 起序号** 解 `identifiers`；越界**不静默改选第 1 项**，`Min>0` 时按取消收场 |
| `BattleChoices` | 选项请求在牌面上方多一条提示条（620×48，暗底金边）；空串时整条不出现——选靶界面一个像素都不变。新增 `PromptText` 供验证读 |
| `BattleScreen.ChoiceVerification` | 夹具给选项请求带上提示语，验证里断言「提示语到得了桌面，且卡牌定义选择那条路径仍然没有」 |

**为什么 AI 那条以前是错的**：`AiPilot.ChoosePickAsync` 对标识类回的是 `1`（1 起，与 `target.submit` 同口径），
而 `SubmitAiPick` 拿它当 `candidates` 的 0 起下标用——候选为空，怎么算都是空，于是「AI 死活不出带 `choice` 的牌」。

---

## 4 验证

- `SelectorMigrationTests` 9 → 12：`AiOptionPicksAreOneBasedIdentifiers`（1 起序号取到第二个选项）、
  `AiOutOfRangeOptionCancelsInsteadOfPickingTheFirst`（越界不许替 AI 决定）、`PromptSurvivesTheSnapshotFreeze`
  （`UiSnapshots.Freeze` 是 `record with`，新字段必须跟着过去）。
- 全量 **750 通过 / 2 失败**（`CrossEffectTargetTests`、`MatchDebugTests`，既有引擎漂移）。
- 游戏内：`--verify-orders` → `ORDERS_STAGE6B2_VERIFY_OK … paper-choice …`；
  `-Verify` 全轮 → `UI_VERIFY_OK screens=13 cards=534`。

## 5 顺手修的一条测试隔离问题

`AssaultPresentationTests` 直接 `new Match(...)`，却没登记动作实现集，靠「别的测试类先跑过
`FoundationActions.Register()`」的运气过关。加三条测试换了执行顺序，它就翻脸
（`未注册动作实现：请先注册默认实现集`）。已加静态构造登记——`SetDefault` 可重复调用，不再赌顺序。

## 6 今天还看不到真弹窗的原因

`choice` 要出现在一张真卡里，得先有「handler DSL 文档 → 卡效果」的落盘链路
（`AssetEditor.Commit` 那条，仍在待办）。所以现在能证明的是：**面板、提示语、提交、AI 兜底四条路径都对**，
缺的只是「一张带 `choice` 的牌」。合成请求已经在游戏内验证过同一条渲染路径。
