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

## 尚未接线（后续阶段）

- 换牌的**交互**面板（识别 `TargetSlotKind.MulliganSelect` 后让玩家点选；当前是"保留全部"自动应答）
- 出牌 / 移动 / 攻击的命令映射与 `GetCommandAvailability` 高亮置黑
- 战斗结算演出与终局
