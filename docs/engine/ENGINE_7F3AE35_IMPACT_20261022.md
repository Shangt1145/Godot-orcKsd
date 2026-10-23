# 引擎 7f3ae35 更新核对

日期：2026-10-22。当前引擎从 `e48d0fe` 快进至远端 `7f3ae35`，共 4 个提交、317 个文件变化。

## 已确认

- 新增 Orc.Manager 长期生命周期管理器、session.attach/detach、管理员批准、按角色分发投影与事件段。
- 无 seat 的 `roles:["viewer"]` 先进入 pending，经管理员 conn.admit 批准后成为 omniscient。绑定 seat 的 viewer 是另一种受限视角。
- omniscient 接收 `perspective.mode=omniscient`、两份 `views` 和全量事件段；不能据此读取双方未公开的构筑声明。
- 线协议常量仍为 `1.0`，不是新接入指南部分示例里的 `2.0`。
- 旧 room.claim、room.resume、room.start 等接口已移除，不提供旧协议兼容层。
- 棋盘改为新的 BattleLine／FormationGap 等位置模型，不能继续把战线当作 Slot 容器枚举。

## 实测状态

`dotnet test tests/Orc.Server.Tests/Orc.Server.Tests.csproj`，筛选：

- ViewerDistribution_SeatVsOmniscient_FramesUsePerspectiveMode
- ViewerOnly_Omniscient_CannotSeeOpponentDeclarations

结果：2 通过、0 失败。证据：`artifacts/engine-update-20261022/viewer-contract.trx`、`viewer-tests.log`。

UI 桥接项目重新编译：19 个错误、6 个警告。主要涉及 OrcActionReader、OrcDragVerification、OrcAiGateway、OrcMatchHost、SourceEffectAssembly 等消费旧棋盘类型的代码。证据：同目录 `bridge-build.log`。旧网络方法以字符串调用，编译报错数没有覆盖全部协议适配量；消除这 19 个错误不等于联机恢复。

因此，拉取后当前源码暂时不能完成 UI 构建。此前已有运行包与截图仍是历史基线，不作为此次引擎兼容证据。

## 本地改动保全

EffectRuntime.cs 原有 5 行选靶诊断与上游涉及同一文件，直接 pull 被 Git 拒绝。已先保存补丁、对该文件单独 stash、快进更新，再成功恢复。当前仍只保留原有 5 行修改，没有冲突。

备份位于 `artifacts/engine-update-20261022/EffectRuntime.local.patch`，保留的 stash 编号记录于 `stash-id.txt`；未删除 stash，未提交引擎源码。

## 后续适配顺序

1. 更新本地投影／行动读取与验证夹具对新棋盘模型的消费，恢复 UI 编译和单机回归，不重做战场布局。
2. 更新服务端启动管理、连接申请、批准、拒绝／撤销、重连与房间轮换，替代旧 room.* 流程。
3. 实现无 seat viewer 的只读入口、双视角切换及全知帧消费；覆盖 pending 无数据、管理员拒绝、断开不占座不判负、轮换后重新申请。
4. 回放单独设计记录、版本、播放与拖动定位。新引擎文档仍将观战回放列为非本轮实现范围，不能把观战投影直接等同于可用回放功能。

此次完成更新核查及专项验证，未实现上述 UI 适配、观战或回放，不可打包发布当前源码。

## 追加：a25466c 拉取后的复验（2026-10-23）

引擎侧 a25466c 是**测试适配**（58 个 Orc.Game.Tests 用例对齐 BoardPosition/FormationGap 新契约），
规则未变。复验结果：

| 项 | 结果 |
|---|---|
| 引擎构建 | ✅ |
| Orc.Game.Tests | ❌ 128 / 1354 红（上一轮 158） |
| UI 构建 | ✅ |
| UI 单测 | ❌ 11 / 533 红（全是攻击/总部伤害/移动落点） |
| `--verify-room` | ✅ 含 `local-art`（AI 座位卡图已修） |
| `--verify-room-discovery` | ✅ |
| `--verify-ui` | ❌ 卡在“没有任何单位能攻击任何合法目标” |

### 阻塞根因（引擎侧，一处类型契约）

`CommandManager.BeginMoveAsync` 把选中的移动候选（`FormationGap`）原样作为 `NewPosition` 交给
`DispatchMoveAsync`，而 `MoveRevalidationJudicator.ValidateLogic` 要求
`refs[2].Value is BoardPosition`——`FormationGap : Entity` **不是** `BoardPosition`，
于是每一次“支援线→前线”推进都必然 `ValidationRejected`（UI 侧表现为 `ExecutionRejected`）。

铁证：引擎自己的 `SandboxPrimitiveTests.Move_SupportToFrontline_Allowed` 同样红。

连带效应：单位上不了前线 → 射程矩阵（支援线只能打敌前线）无目标 → `AttackPreviews` 恒为空
→ 双方全员支援线互瞪，对局软锁。UI 侧无解，必须引擎修（dispatch 前 gap→BoardPosition 映射，
或复验接受 FormationGap）。

### UI 侧待办（引擎修好后按序）

1. 复跑 `--verify-ui` / `--verify-combat` / `--verify-room-remote` / `--verify-room-segments`；
2. 11 项 UI 单测按新语义对齐（多数应随引擎修复自动转绿，`MovementLandsWhereThePlayerAsked` 需复核）；
3. M4 剩余：匹配语义（自动配对/密码房）；M5 剩余：观战（viewer 角色已有引擎支持）、回放（会话日志已有）。
