# 交接文档 — 对战 UI（OrC-KSD.Godot）

交接日期：2026-10-06。当前版本：`0.0.0-alpha.11`。

> **本次交接状态**：拍桌部署动作已按原版逐帧核验重做并交付（见 `BATTLE_SLAM_REWORK.md`）。
> P9（受击接真实伤害 + 攻击弹道）的改动**已从工作区剥离、未提交**，原因见§3。

## 1. 项目是什么

`H:\Working Folder\OrC-KSD.Godot`：Godot 4.7 .NET (C#) 的 KARDS 风格对战 UI。引擎在 `H:\Working Folder\OrC-KSD`（S1145 维护），UI 通过桥接层消费引擎真值——**UI 不算规则、不猜状态、不泄露隐藏信息**。

- 构建副本：`H:\g\kards`（`tools/run.ps1` 自动同步源码过去构建）
- 引擎干净引用副本：`H:\Working Folder\OrC-KSD-github`（worktree，detached；`Kards.Ui.OrcBridge.csproj` 的 `OrcEngineRoot` 指向它）
- ⚠️ 主引擎仓库 `OrC-KSD` 工作区有 S1145 的 330 个未提交改动，**绝对不要动**；以 GitHub 为准时用干净副本

## 2. 已完成

| 提交 | 内容 |
|---|---|
| `（本次）` alpha.11 | **部署拍桌按原版逐帧核验重做**：`SlamStyle(defense)` 成为单一真源（防御力=身材，唯一输入）；删除兵种/家族分支（空军 ×1.25、舰船水花）与旧"跳起→砸下"曲线；新增 `--capture-slam` 录制钩子 + 5 支核验工具。详见 `BATTLE_SLAM_REWORK.md` |
| `601ae1a` alpha.10 | **P8 换牌面板**：`MulliganSelect` 请求停泊等 UI、`ChooseMulligan` 命令通路、红 ✕ 印记面板（`BattleScreen.Stage5.cs`）、"敌方正在选择起手牌"横带；headless 保留自动应答（`InteractiveMulligan=false`） |
| `c787d44` | `run.ps1` 补齐引擎 Roslyn 运行时闭包：Godot 只拷项目程序集，引擎 a24ef2d 起需要 `Microsoft.CodeAnalysis.*`（csx 效果脚本），用 `dotnet publish` 解析闭包后补拷到 `.godot\mono\temp\bin\Debug` |
| 更早 | P0–P7 桥接七步（详见 `docs/ENGINE_BRIDGE.md`）：真实对局可读、命令映射、拖拽手势、拍桌、受击编排（diff 兜底版）、对手 AI 驱动、认输/终局、最近空槽回放 |

文档索引：`ENGINE_BRIDGE.md`（桥接全程+引擎缺口）、`KARDS_SELECTION_RESEARCH.md`（原版交互调研，含 2026-10-06 完整对局录像逐帧核验：换牌面板、瞄准灰化、回合横幅等）、`BATTLE_STAGE5.md`（换牌面板交付）、`VERSIONING.md`。

## 3.暂缓：P9 受击接真实伤害 + 攻击演出（改动已剥离，未提交）

### 结论：不是死锁，是 testhost 崩溃

2026-10-06 复现并定位（原交接文档的判断有误，更正如下）：

- **现象**：`dotnet test --filter ~AttackImpacts` 报"测试主机进程崩溃"，不是挂起。
  `--blame --blame-hang-timeout60s` 产出166MB 转储（`tests/TestResults/`，已 gitignore）。
- **面包屑推翻旧结论**：旧文档称"连第一行 `Mark("test entered")` 都没出现，
  推断卡在 `new Match(...)` 构造"。实测面包屑**完整走到 `mulligan done`**，
  说明 `new Match`、`InitializeAsync`、读手牌、`MulliganDone` 全部正常完成。
- **实际卡点**：崩在下一行 `match.PlayManager.BeginUnitPrePlayAsync(unit)`（部署）。
  `OrcOpponentDriver` 尚未被调用，不是嫌疑。转储分析未取到托管栈（进程直接死，
  疑似栈溢出级），指向引擎侧 EffectParsing/EffectRuntime 装配。
- **`seed` 不是原因**：`seed: 11` 改`seed: 7`（与通过的Mulligan 测试一致）后仍崩溃。
- **引擎侧待查**：`Orc.Game` 中 `PlayManager.BeginUnitPrePlayAsync` 的执行路径，
  与 a24ef2d 新增的效果解析流水线的关系。此项属引擎范畴，不阻塞 UI 发版。

### 已剥离的改动（保存在 `artifacts/p9-worktree-backup.patch`，695 行）

| 文件 | 内容 |
|---|---|
| `proto/contracts/UiPresentation.cs` | `UiOrderImpact.Source`（攻击者视图，null=无弹道） |
| `proto/core/UiSnapshots.cs` | `UiSnapshots.FreezeImpact` 冻结 Source |
| `proto/bridge/.../OrcUpdateTranslator.cs` | `card.damaged`（用引擎 `Amount`，不再 diff 倒推）、`unit.acted`、`PairActor` 段内配对 |
| `proto/ui/anim/BattleCombat.cs` | `PresentAssaultAsync`：按兵种弹道（机枪连射/坦克炮弹/火炮抛物线/投弹）逐目标开火 + 命中红字 |
| `proto/ui/screens/BattleScreen.cs` | 按 Source 分组、按段序播放 |
| `tests/OrcBridgeTests.cs` | `AttackImpactsCarryTheEngineAmountAndThePairedAttacker`（真引擎全链路断言） |

恢复方式：`git apply artifacts/p9-worktree-backup.patch`（需先解决与拍桌改动的重叠）。

> 该测试还应加一道保险：`await parked.Task` 换成
> `Task.WhenAny(parked.Task, Task.Delay(20s))` 并在超时时抛 `TimeoutException`，
> 避免请求未park 时永久挂起整个测试进程。

### P9 完成标准（引擎侧修复后再继续）

1. 引擎 `BeginUnitPrePlayAsync` 的崩溃修复后，42/42 测试全绿
2. `VerifyBridgeAsync` 补断言：攻击 HQ 的 impact 有 `Source` 且 `Damage == 20 - hqHealth`
3. `tools/run.ps1 -Verify` 全绿（MULLIGAN/BRIDGE/UI_VERIFY_OK）
4. 可选：`-Capture` 加攻击弹道帧
5. 另开阶段发版（`alpha.11` 已用于拍桌重做）

## 4. 引擎新能力（a24ef2d，UI 尚未消费的部分）

- **新增 8 条信号**（`GameUpdates` 共 26 条）：`card.damaged {Card, Amount}`、`unit.acted {Unit}`（P9 已实现但暂缓，见 §3）；`slot.gained/lost/changed`、`point.gained/lost/changed`（E1-25：槽/点分离，回合开始递增只发 `slot.changed`，打牌扣费现在也走 `point.changed`——**翻译器后续要按来源处理，避免演出重复**）
- 效果解析流水线（词表 JSON+模板+csx）：纯引擎内部，UI 无 API 面
- 待办遗留（按优先级）：**交互式选择第二段**（指向性指令拖拽打出、部署带指向自动弹箭头——原版实例待补证，见调研文档 §6）、多选候选面板（`PendingChoices` 仍自动应答）、资源条动画接 `point/slot.changed`、换牌补抽飞入演出（`MulliganReturnAsync/EnemyAsync` 已写好未接）、卡图映射

## 5. 操作手册

```powershell
# 全套（同步源码到 H:\g\kards + 构建 + 可选测试 + Godot 运行）
powershell -File tools\run.ps1 -Test -Verify        # 端到端验证（headless）
powershell -File tools\run.ps1 -Capture             # 截图到 H:\g\kards\artifacts
powershell -File tools\run.ps1 -Editor              # 打开编辑器
# 只跑单测
dotnet test tests\Kards.Ui.Tests.csproj -v q --nologo
# 引擎切换
dotnet build -p:OrcEngineRoot='H:\...你自己的副本\src\Orc.Game'
```

- 提交身份：`git -c user.name='OrC-KSD UI' -c user.email='ui@local' commit ...`（本机无全局身份）
- 网络：github.com:443 时通时断（SSH 不通）；推送失败就稍后重试；raw.githubusercontent.com 的 API 只读取回可用
- 验收基线（全绿标志）：`MULLIGAN_VERIFY_OK ...`、`BRIDGE_VERIFY_OK ...`、`UI_VERIFY_OK screens=7 cards=315 texture_cache=64/64`、`已通过 41`
- 同名单卡牌库注意：换出去的牌可能立刻被抽回（uid 合法重现），不变量断言用手牌数/牌库数，不用 uid 缺席
