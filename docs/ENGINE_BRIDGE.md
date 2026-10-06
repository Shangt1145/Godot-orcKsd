# 引擎桥接（P0/P1）

日期：2026-10-05。将 Godot UI 接到 OrC 引擎的**真实对局**上。UI 仍只做呈现与输入，规则一律由引擎决定。

## 为什么需要这一层

引擎与 UI 说不同的话：

| 引擎给的是 | UI 需要的是 |
|---|---|
| 实体对象与 `Ref<Entity>`（销毁后读 `.Value` 会抛） | 稳定的字符串 uid |
| 每次动作的一段更新（`TakeSegments`） | 可播放的演出步骤 |
| 当前状态要"去问"（`Match` 各 Manager 读面） | 一整盘棋 `UiMatchView` |
| 卡牌对象（对手手牌也能读到） | 只含公开信息的投影 |

桥接层就是中间的翻译，并且是**唯一**挡隐藏信息的地方。

## 结构

`proto/bridge/Kards.Ui.OrcBridge/`（net8，不依赖 Godot，可独立编译与测试）

| 文件 | 职责 |
|---|---|
| `OrcRefs.cs` | `Entity.Id` ↔ uid；`Ref` 存活检查 |
| `OrcCardReader.cs` | 读一张卡：手牌走定义 / 场上走数据组件+修饰链 / HQ 走单独路径 |
| `OrcMatchReader.cs` | `Match` → `UiMatchView`，并对对手手牌只暴露张数 |
| `OrcUpdateTranslator.cs` | 信号 → `UiPresentationStep` |
| `OrcMatchHost.cs` | 生命周期：Initialize 前注册即时监听、帧循环取段、每段后重读全量 |
| `OrcTargeterBridge.cs` | `ITargeterBridge` 最小实现（当前为保守自动应答） |

`proto/ui/bridge/OrcMatchRunner.cs`（Godot 侧）负责建局、把桥接事件接到 `BattleScreen`。

## 三条来自引擎的硬约束

1. `OnImmediateUpdate` 必须在 `Initialize` **之前**注册，否则漏掉开局那一段（`card.load` / `deck.shuffled`）。
2. 段队列无上限、引擎不等 UI —— 帧循环必须持续 `Pump()`。
3. **换牌的补抽是静默的**（只发一条 `deck.shuffled`，不发 `card.drawn` / `card.hand.add`）—— 换完牌必须显式 `Refresh()`。

## 数据取值的分叉

`TryGetData<UnitStateData>(out _)` 为真即"已上场"：

- **手牌/卡组**：只有定义（`Definition`）、`FactionCostData`、`BattleStatsData`、`TagData`。对未上场单位调 `GetEffectiveValue(Attack/Defense/OperateCost)` 会抛异常；`DeployCost` 恒可用。
- **场上**：运行时数据组件 + 修饰链有效值；`Defense` 为剩余血量，`GetEffectiveDefenseCap()` 为有效上限。
- **HQ**：不是 `CardBase`，没有 `Definition`，只有 `Health`。

## 引擎侧没有、由 UI 自建的三样东西

- **卡图路径**：`CardDefinition` 无 ArtPath。以注册键 id（`CardLibrary.TryGetRegisteredId`）为 key 建 `id → res://…png` 映射。
- **可见性**：引擎无可见性 API，对手手牌在对象层完全可读；桥接按 `CardBase.Owner` 过滤，只出 `Hand.Count`。
- **阵营/稀有度中文名**：只有裸 enum，显示名表建在 UI 侧。

## 验证

```
dotnet test tests/Kards.Ui.Tests.csproj            # 36 项（含 8 项真实对局桥接断言）
& '.\tools\run.ps1' -Test -Verify                  # 追加 BRIDGE_VERIFY_OK
```

引擎源码不在本仓库内；引用路径由 `OrcEngineRoot` 属性控制，可覆盖：
`dotnet build -p:OrcEngineRoot=<path>\src\Orc.Game`

## 可玩闭环（P2）

命令映射（`OrcMatchRunner.SubmitAsync`），引擎入口一一对应：

| UI 命令 | 引擎入口 |
|---|---|
| `PlayCard` | `PlayManager.BeginUnitPrePlayAsync` / `BeginCommandPrePlayAsync` |
| `MoveUnit` | `CommandManager.BeginMoveAsync` |
| `AttackUnit` | `CommandManager.BeginAttackAsync`（玩家点击的目标作为选择回放给引擎） |
| `EndTurn` | `Match.EndTurn` |

- 可用行动投影 `OrcActionReader`：移动/攻击来自 `GetCommandAvailability`（引擎纯查询）；**引擎没有"能否出牌"查询面**，所以手牌列为可尝试，合法性由引擎在提交时判定，其拒绝原因（`PlayFailureReason` / `CommandFailureReason`）原样显示给玩家。
- 伤害预览**不编造**：`AttackPreviews` 只带候选目标，不带伤害数字。
- 目标选择：`OrcTargeterBridge` 优先用玩家意图（已点击的目标），换牌保留全部，其余取首个允许候选 —— 这是交互面板接入前的过渡策略。
- 入口：战斗页齿轮菜单「真实对局（接入引擎）」。

## 新架构适配（P6，引擎 32299c5）

- **`card.burned`（第 18 条信号）**：满手爆牌不再发 `card.discarded`。桥接直接消费该信号 → `UiDiscardKind.Burn`；
  "抽到未进手牌"的推断保留为兜底。端到端测试真实触发满手爆牌并断言信号与手牌/牌库计数。
- **结果原因词表**：`Reason()` 按引擎新枚举全量映射
  （`CommandFailureReason` 9 项 / `CommandBlockReason` 7 项 / `PlayFailureReason` 新词表——
  `PrePlayPointShortage` / `PrePlayNoAvailableSlots` / `TargetSlotOccupied` / `UnitAlreadyUnitized` / `Counter*` 等）。
- **卡牌数据组件化格式**（引擎侧已定稿）：`{ schemaVersion:1, id, name, components:[{component:"factionCost"|"battleStats"|"tagData"|"typeCategory"|"keywords"|"effects"}] }`；
  `CardDefinition` 保留旧参数构造 → 桥接读面零改动。将来 UI 卡池导入可直接消费该 JSON。
- `Match` 构造签名未变；`GetCommandAvailability` 未变；`AllowFirstTurnDraw`（先手首回合抽牌，缺省关）对 UI 透明。
- 仍未解决：`damage-flow` 无结构化载荷 —— 攻击方弹道轨迹继续等待上游。

## 完整对局闭环（P7）

引擎没有 AI，敌方回合原本是空转。新增 `OrcOpponentDriver`（桥接层、net8、可单测）：

- 策略刻意朴素：出最便宜的单位 → 前进 → 攻击首个合法目标 → 交回合；全部经引擎**同一套动作入口与
  可用性查询**（与玩家侧完全对称），最多 12 个动作防失控。
- 我方 `EndTurn` 后自动接管敌方回合，打完交回；终局（HQ 归零 / 认输）投影为 `Phase=over` + `ResultTitle`。
- 结算面板「再来一局」对真实对局同样生效（重启 runner）。
- 测试：对手回合真实部署并交回；认输投影 `失败`；爆牌信号。共 39 项单测。

## 受击编排（P5）

引擎的攻击/伤害**不发更新信号**（`damage-flow` 只写日志条目，且条目不带结构化载荷——没有攻击方身份、
金额只嵌在文本里），所以攻击方的弹道轨迹目前无法忠实还原；这是对上游的接口缺口。

能还原的是**受击方**：`card.stat.changed`（防御/HQ 血量下降，带精确卡引用）→ 命中光斑、红色伤害数字
（前后差值）、死亡爆碎，经 `PresentImpactsAsync` 播放；死亡不再重复播 `card.died` 的移除演出。
同一分段里受击编排优先于其它步骤，其余状态由编排结束的整盘渲染收敛。

**给上游的提议**：为伤害结算补一条结构化更新（载荷＝`{ Source, Target, Amount }`），
UI 即可播放完整的武器轨迹与命中时序。

## 尚未接线（后续阶段）

- 交互式换牌面板（识别 `TargetSlotKind.MulliganSelect` 后让玩家点选替换）
- 部署槽位 / 攻击目标的**玩家点选**面板（当前为自动应答）
- 伤害数字预览（需引擎查询面）
- 战斗结算演出、状态变化与终局面板
- 卡图 `id → res://…png` 映射（当前为文字卡面兜底）
