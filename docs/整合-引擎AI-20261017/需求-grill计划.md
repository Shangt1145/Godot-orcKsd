# 需求 Grill 计划 — 游戏本体 × 引擎架构整合

> 本次 grill 聚焦：用户说的「冲突」到底指什么、整合要交出什么可验收的结果。

## 已核实基线（代码取证，非用户陈述）

- 引擎分叉：本地 `HEAD=12d1aeb` 领先 3（`93bf794` 词条程序运行时 / `d2bc855` 效果触发战斗 / `12d1aeb` 诊断栈）；
  远端 `origin/main=9911d9c` 领先 6（`Orc.Ai` 启发式 AI、MCP 对局启动、`.sln` 纳入三工程）。共同祖先 `750e11e`。
- 远端 AI 形态：`Orc.Ai/AiPilot` 构造参数＝`McpService` + `IUiProjectionProvider`，单步 `StepAsync`，
  文档自述「服务层＝唯一门禁/唯一桥」。**不直接持有 `Match`**。
- 游戏本体现状：`OrcOpponentDriver` 直接调 `Match.PlayManager/CommandManager`，注释原文
  「the engine has no AI, so without this the enemy turn is a no-op」。与远端 AI 是两套互斥拓扑。
- API 面：远端把 `Card.Engine` 从 `public` 收成 `internal`（游戏本体未直接使用，暂不破）；
  本地 `Trigger.cs` 的 `data["stack"]` 远端没有；`tests/Orc.Game.Tests/KeywordProgramTests.cs`（316 行）远端没有。

## 未决问题

### Q1: 「冲突」的所指 已经完成
- 用户答复（2026-10-17 23:00）：「现在不需要改，他还没改完呢」。
- 裁定：**不指向任何具体代码改动**；整合被推迟到对方改完。本轮需求从「整合」降级为「待命准备」。

### Q2: 整合的目标形态 已经完成（转为整合日决定 D1）
- 用户未在准备期裁定 A/B；不影响本轮准备任务，写入 `整合预案.md` D1 留待整合日。

### Q3: 时机与验收 已经完成
- 用户答复：「他还没改完」→ 本轮不做任何合并/代码改动；只做准备（预案 + 监视）。

### Q4: 本地 3 个提交的取舍 已经完成
- 实测：合并后 `Orc.Game.Tests` 1122/1122 通过、`Kards.Ui.sln` 对合并树构建 0 错误 → **全保留**。

### Q5: 失效产物怎么处理 已经完成
- 用户选择「预案 + 远端监视」，本轮不发版；beta.2 留到 integ 通过后再发（预案 D3）。

### Q6: `Orc.Mcp/Tools` 缺文件谁补 待定
- `.gitignore:30 **/tools/` 把对方新增的 `src/Orc.Mcp/Tools/` 静默忽略了；远端与本机都没有这些文件。
- 无论选择 A 还是 B 方案，全量 `OrcEngine.sln` 构建都会挂 CS0234。需对方补提交，或我方 `git add -f`（前提是能拿到文件）。
