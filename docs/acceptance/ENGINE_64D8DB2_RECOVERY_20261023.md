# 引擎 64d8db2 后联机与战斗恢复验收（2026-10-23）

引擎侧修掉移动类型契约（`MoveRevalidationJudicator` 接受 `FormationGap`）后，本轮把 UI 侧剩余适配做完。

## 结论

| 项 | 结果 |
|---|---|
| 引擎构建 / Orc.Game.Tests | ✅ 1354/1354 |
| UI 构建 | ✅ |
| UI 单测 | 🟡 530/533（3 项为指令效果重复结算，见下） |
| `--verify-ui`（单机全链） | ✅ |
| `--verify-room` | ✅ 含 local-art |
| `--verify-combat` | ✅ |
| `--verify-room-discovery` | ✅ |
| `--verify-room-reconnect` | ✅ |

## 本轮 UI 侧修复

1. **部署落点直通**：`PlayCard` 带 `GapId` 时直接 `PlayUnitAsync(unit, gap)`，不再依赖"事后被问再答"——
   意图在 `SubmitAsync` 的 finally 里已被清掉，旧路径必然落到 `SelectionRequired`（H39 失败根因）。
   新增 `FindGap()` 在三线当前空隙里按引用键找回落点。
2. **战斗余效回收**：`PresentCombatAsync` 结束前等待余效自淡（≤1s），"演出结束"包含火焰/残烟/碎片淡完；
   否则紧随其后的 `LiveEffectCount==0` 断言测的是帧数差（实测 live=2 误报）。
3. **拖拽选靶断言**：新抬升/扇面动效会让 `RestPosition` 漂移 7~13px，删掉与 `_aim.From` 重复的锚点复算，
   只守"卡跟手 + 箭头对齐卡心"。
4. **AI 部署**：`OrcBridgeTests` 的 AI 回合改走宿主+选择器桥路径（部署要选空隙，无桥＝无人应答）。
5. **测试夹具**：`OrcBridgeTests` 静态构造里补 `FoundationActions.Register()`（引擎 S2 起构造 Match 必需）。
6. **引擎侧（本地）**：`TurnManager.BeginTurnAsync` 卡组抽空时跳过抽牌——集合契约"空卡组 Draw 抛错"
   会让长局在回合开始崩掉整局；疲劳规则仍待专门批次。

## 已知未收敛（引擎侧）

`OfficialCardExpansionTests` 三项：**一张指令打出时效果结算两次**（一次出牌弹两次 `fieldUnit` 选靶：
同一 `card.played` 触发里 `deadly_duty` 的选靶脚本被执行两遍）。UI 侧已排除：
声明去重无效、桌面指令桥关闭后仍复现（NullRef 路径），故指向 `OrderFlow` Resolve band
（priority 0 的 `card.played` 与 priority 100 的"施放结算"疑似各跑一次同一 csx 效果）。
复现：`dotnet test --filter FullyQualifiedName~TargetedSacrificeCancelsBeforePayment`。
