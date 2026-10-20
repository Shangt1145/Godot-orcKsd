# 对战第五阶段（一）：起手换牌面板

日期：2026-10-06。版本：`0.0.0-alpha.10`。

## 目标

把桥接里"起手全保留"的保守自动应答换成真正的玩家决策：引擎挂起 `MulliganSelect` 请求，玩家在换牌面板点选后提交 `ChooseMulligan(KeepUids)`。这是交互式选择闭环的第一段（后续：指向性指令、部署带指向）。

交互样式依据 `KARDS_SELECTION_RESEARCH.md`（2026-10-06 完整对局录像逐帧核验）。

## 交付内容

| 项 | 说明 |
|---|---|
| 桥接挂起 | `OrcMatchRunner` 新增 `InteractiveMulligan`（init，默认 false）；`Present` 遇 `MulliganSelect` 槽位时**停泊请求**并触发 `MulliganRequested`，不再自动应答；引擎在 `Targeting()` 处等待，直到面板提交 |
| 换牌集合换算 | `OrcTargeterBridge.SelectReplace(allowed, keepUids)`：保留名单 → 退回牌库集合（纯 uid 映射，合法性仍由引擎校验） |
| 命令通路 | `SubmitAsync` 对 `ChooseMulligan` 绕开 Play 相位闸（换牌阶段唯一合法命令），完成后等待引擎完成替换+确认（双方确认 → 自动进入 play） |
| 换牌面板 | `BattleScreen.Stage5.cs`（新部分类）：暗化棋盘 + 横排大卡（`BattleCardMode.Inspect`）+ "选择要替换的卡牌"提示条 + **红 ✕ 印记**（`MulliganStamp`，点击切换）+ "确认"按钮；提交后显示"敌方正在选择起手牌"横带，play 相位到达即清除 |
| 状态收敛 | `ApplyProjection`/`ResetDemo` 接 `MulliganProjection(phase)`：等待横带随 play 清除；过期面板（对局已推进）自动关闭，不挡棋盘 |
| 对手侧 | 对手（驱动方）直接 `MulliganDone`（保留）；UI 不展示对手身份 |

## 明确不做（本阶段边界）

- 指向性指令拖拽打出、部署带指向自动弹箭头（原版实例待补证，见调研文档 §6）
- 多选一/检索选择面板（`PendingChoices` 仍走自动应答）
- 换牌补抽的逐卡飞入演出（面板收起即重排手牌；`BattleSequence.MulliganReturnAsync/EnemyAsync` 已备好，接入留待后续）

## 验证证据

| 证据 | 结果 |
|---|---|
| 单元测试 | **41/41 通过**（新增 `InteractiveMulliganReplacesOnlyTheUnkeptCards`、`KeepAllMulliganKeepsTheWholeOpeningHand`，均跑真引擎） |
| `--verify-ui` 端到端 | `MULLIGAN_VERIFY_OK panel marked=1 replaced keep-3 reach-play hand-size-stable`；既有 `BRIDGE_VERIFY_OK`、`UI_VERIFY_OK` 不变 |
| `--capture-ui` | `artifacts/battle-stage5-mulligan.png`、`battle-stage5-mulligan-marked.png`（红 ✕ 印记可见） |
| 非交互路径 | `StartRealMatchAsync(interactiveMulligan: false)` 保留自动全保留，`VerifyBridgeAsync` 与无头验证不受影响 |

### 已知边界说明

验证环境牌库为同名单卡：被换出的卡洗回牌库后**可能立即被抽回**（uid 相同是合法结果）。因此不变量为"手牌数不变 + 保留卡仍在 + 牌库数不变（洗回 +1 / 抽回 −1）"，不做"被换 uid 必不在场"断言。

## 涉及文件

`proto/ui/bridge/OrcMatchRunner.cs`、`proto/bridge/Kards.Ui.OrcBridge/OrcTargeterBridge.cs`、`proto/ui/screens/BattleScreen.Stage5.cs`（新）、`proto/ui/screens/BattleScreen.cs`、`proto/ui/screens/Main.cs`、`tests/OrcBridgeTests.cs`。
