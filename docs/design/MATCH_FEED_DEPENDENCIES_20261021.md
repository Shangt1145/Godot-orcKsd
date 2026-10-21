# `IMatchFeed` 依赖清单与最小抽象

- 日期：2026-10-21
- 目的：在动手 M1 之前，把 `Main` 对本地 runner 的依赖列清楚，确认哪些远端给得了、哪些给不了
- 结论：**`BattleScreen` 一行都不用改**；改动全部集中在 `Main` 的三个方法和一个接口

---

## 1. 结论先行

| 发现 | 影响 |
|---|---|
| `BattleScreen` 已经有 `ApplyProjection(UiMatchView, UiBattleActions)` 和 `event Action<UiCommand> CommandRequested` | **它天然解耦，不认识 runner**。4426 行、20 个 partial 一行都不用动 |
| 全部接线集中在 `Main.cs` 的 `BindRunner`（408）、`ApplyRunnerProjection`（431）、`_Process`（447） | 改动面从"整个表现层"缩到"一个文件里的三个方法" |
| `Main` 对 runner 的依赖高度集中 | `CurrentView` 26 次、`CurrentActions` 13 次、`SubmitAsync` 8 次、`Pump` 2 次，其余全部 1–2 次 |
| 只有一个成员远端**给不了** | `Pump(delta)`——远端是推送驱动，没有"每帧推进"这个概念 |

**M1 的工作量因此从方案里的 2–3 天下调为 0.5–1 天。**

---

## 2. 现有接线（三处）

```csharp
// Main.cs:408  —— 订阅 7 个事件 + 注入输入门禁
private void BindRunner(OrcMatchRunner runner)
{
    _runner = runner;
    runner.InteractionStateReader = _battle.ReadInteractionState;
    runner.ProjectionReady += ...
    runner.MulliganRequested += ...
    runner.TargetRequested += ...
    runner.TargetClosed += ...
    runner.PresentationReady += ...
    runner.CommandRefused += ...
    runner.ErrorRaised += ...
    runner.HintRequested += ...
}

// Main.cs:431  —— 应用投影 + 重建 PresentationPlayer
private void ApplyRunnerProjection(OrcMatchRunner runner, UiMatchView view, UiBattleActions actions)

// Main.cs:447  —— 每帧推进 + 重新查询输入投影
public override void _Process(double delta)
{
    runner?.Pump(delta);
    _battle.UpdateInputProjection(view, runner.CurrentActions);
}
```

---

## 3. 依赖清单（逐成员）

| 成员 | 出现次数 | 用途 | 远端可行性 | 依据 |
|---|---|---|---|---|
| `CurrentView` | 26 | 当前整盘投影 | ✅ **直接可用** | `notify/view` / `observe.view` |
| `CurrentActions` | 13 | 可打出/可移动/可攻击 | ✅ **直接可用** | 推送载荷里的 `actions` |
| `SubmitAsync(UiCommand)` | 8 | 提交命令 | ✅ **直接可用** | `tools/call` 映射 |
| `Pump(delta)` | 2 | 每帧推进 | ❌ **不适用** | 服务端推送，无"推进"概念 |
| `ProjectionReady` | 1 | 全量投影事件 | ✅ | `notify/view` |
| `PresentationReady` | 1 | 演出段事件 | ✅ | `notify/segment` |
| `CombatReady` | 1 | 战斗结算 | ✅ | 应在 `notify/segment` 内 |
| `MulliganRequested` | 1 | 换牌面板 | ✅ | `mulligan.begin` |
| `TargetRequested` + responder | 1 | 选靶 | ✅ | `notify/target.request` + `target.submit/cancel` |
| `TargetClosed` | 1 | 关闭选靶 | ✅ | `notify/target.expired` |
| `CommandRefused` | 1 | 拒绝提示 | ✅ | 结果壳 `ok:false` + `code` |
| `ErrorRaised` | 1 | 错误上报 | ✅ | 错误分层（§6.1） |
| `HintRequested` | 1 | 提示文案 | ⚠️ 需映射 | 服务端没有等价物，可由 `code`/`message` 派生 |
| `MatchId` | 1 | 对局标识 | ✅ | `room.info.roomId` |
| `MulliganRequestId` | 1 | 换牌请求号 | ✅ | `mulligan.begin` 返回 |
| `MulliganPending` / `TargetPending` | 3 | 状态位 | ✅ | 由推送推导 |
| `InteractionStateReader` | 1 | **输入门禁** | ⚠️ **待确认** | 见 §4.1 |
| `KeywordSkills(uid)` | — | 词条技能面板 | ⚠️ 待确认 | 见 §4.2 |
| `CardPoolReport` | — | 卡池报告 | ⚠️ 待确认 | 见 §4.3 |
| `SwitchViewerForVerification` | — | 测试专用 | — | 不进接口 |

---

## 4. 三个真问题

### 4.1 `InteractionStateReader`（已确认可降级）

本地模式下，它由 `session.Host` 注入→引擎的 `action.input.allowed` 判定器，负责回答"现在能不能点"。

**但看了类型定义后发现它比想象的简单得多**（`proto/contracts/UiActionInteraction.cs:5`）：

```csharp
public sealed record UiActionInteractionState(UiInteractionPhase Phase, IReadOnlyList<string> PendingActorUids)
{
    public bool AllowsActions => Phase is UiInteractionPhase.Ready or UiInteractionPhase.Dragging;
}
```

**只有两个字段**：一个相位枚举 + 一个"正在等待的 actor"列表。它不是"这张卡能不能打"的细粒度判断。

远端推导方式：

| 字段 | 远端来源 |
|---|---|
| `Phase` | 由 `view.phase`（`play`/`mulligan`/`over`）+ `view.activePlayerSide`（`self`/`enemy`）推出 |
| `PendingActorUids` | 远端为空列表（没有本地引擎的挂起动作；选靶由 `notify/target.*` 单独表达） |

**所以远端可以保守构造 `UiActionInteractionState(Ready, [])`，不需要服务端额外提供工具，也不需要 §4.1 原先担心的 `play.can_play` 拼接。**

代价：远端不知道"点数不够"这类细化拒绝，玩家点下去才被服务端拒（`ok:false` + `code`）。**不产生错误状态，只是多一次往返**——可接受。

### 4.2 `KeywordSkills(uid)`

词条技能面板的数据源。指南里没有直接对应工具。可能是 `play.command` 的变体，或需要服务端补。

**M2 阶段先不做**：联机时不显示词条技能面板，比显示错的强。

### 4.3 `CardPoolReport`

本地"可用 N 种卡"的提示，属于构筑期信息。联机时卡组由 `room.claim` 提交，服务端校验，**不需要这个报告**。

---

## 5. `IMatchFeed` 最小设计

```csharp
/// <summary>对局数据源：单机（进程内引擎）与联机（远端服务端）共用同一套接线。</summary>
public interface IMatchFeed
{
    string MatchId { get; }
    UiMatchView CurrentView { get; }
    UiBattleActions CurrentActions { get; }
    bool IsSubmitting { get; }
    bool MulliganPending { get; }
    bool TargetPending { get; }
    string? MulliganRequestId { get; }

    Func<UiActionInteractionState>? InteractionStateReader { get; set; }

    Task<UiSubmitOutcome> SubmitAsync(UiCommand command, CancellationToken ct = default);

    /// <summary>单机：推进引擎帧。联机：空实现（由推送驱动）。</summary>
    void Advance(double delta);

    event Action<UiMatchView, UiBattleActions>? ProjectionReady;
    event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;
    event Action<IReadOnlyList<UiOrderImpact>, UiMatchView, UiBattleActions>? CombatReady;
    event Action? MulliganRequested;
    event Action<Action<UiTargetRequest>, IUiTargetResponder>? TargetRequested;
    event Action? TargetClosed;
    event Action<UiCommand>? CommandRefused;
    event Action<string>? ErrorRaised;
    event Action<string>? HintRequested;
}
```

- `OrcMatchRunner` 实现它：现有成员几乎是原样，`Pump` 改名 `Advance`（或保留 `Pump` 并在接口里叫 `Advance`，内部转发）
- `RemoteMatchFeed` 实现它：`Advance` 空实现，事件由 `notify/*` 驱动
- `Main.BindRunner` → `Main.BindFeed(IMatchFeed)`，`ApplyRunnerProjection` 同理
- `_Process` 里 `runner?.Pump(delta)` → `_runner?.Advance(delta)`

**`BattleScreen` 不在接口里，也不需要改。**

---

## 6. 修正后的 M1 步骤

| # | 动作 | 验收 |
|---|---|---|
| 1 | 新增 `proto/net/IMatchFeed.cs` | 编译通过 |
| 2 | `OrcMatchRunner : IMatchFeed`（补 `Advance` 转发 `Pump`） | 编译通过 |
| 3 | `Main.BindRunner` → `BindFeed(IMatchFeed)`、`ApplyRunnerProjection` 参数类型改 | 编译通过 |
| 4 | `_Process` 改调 `Advance` | 编译通过 |
| 5 | 全量回归 | **`--verify-ui` 全绿、243 单测全绿、`--verify-export` 通过** |

**风险点**：第 2 步 `OrcMatchRunner` 是 `public sealed class`，加接口不改行为；但 `CurrentView` 在它上面可能是抛异常的实现（未初始化），接口化后要保证异常语义不变。

---

## 7. 待确认（开工前 30 分钟能查完）

| # | 问题 | 怎么查 | 状态 |
|---|---|---|---|
| 1 | `UiActionInteractionState` 的字段面 | `proto/contracts/UiActionInteraction.cs` | ✅ **已确认**：仅 `Phase` + `PendingActorUids`，远端可本地构造 |
| 2 | `notify/view` 载荷里是否已含 `actions`（还是需要单独请求） | 接入指南 §6.3 或探针实测 | 待查 |
| 3 | `notify/segment` 是否覆盖 `CombatReady` 的三种载荷 | 接入指南 §6.4 | 待查 |

**剩下两条都不阻断 M1**（M1 只做接口与本地实现，不碰远端），M2 开工前查完即可。
