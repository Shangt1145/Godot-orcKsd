# 真人联机换牌与单位部署修复（2026-10-24）

## 问题与根因

- 一方完成换牌而另一方仍在选择时，Main 的每次投影对账会再次打开本方换牌面板。若投影在退牌动画中到达，还会取消动画及其结束时才发送的命令。
- RemoteMatchFeed 原先只按全局 mulligan 相位判定本方待换牌；服务端 mulliganOpen 也未区分座位是否已确认。
- 服务端 actions.deploymentGaps 发送的是 `{uid,gaps:[ref]}`，而 UiBattleActions 消费的是 `{gapId,ordinal,leftUid,rightUid}`。界面拖牌缺少有效落点；旧房间验收直接发送 SupportIndex，绕过了实际拖牌路径。

## 修复

- BattleScreen 在确认瞬间记录本方已提交，投影不能重开面板；收到明确命令拒绝后恢复选择，换局清理状态。
- 服务端 mulliganOpen 使用该座位的 MulliganManager.IsConfirmed；远端 feed 读取此字段，重连刷新也能保留本方完成状态。
- 服务端发送 UI 契约要求的扁平部署空隙，编号及左右邻牌均来自引擎当前阵线快照。
- 真人验收增加退牌期间刷新、等待另一位玩家、双方完成后实际拖牌、服务端手牌移除及场上单位出现的断言。等待可打单位时不再误把可打指令当成单位就绪。

## 验证

- UI 全量单测：565/565。
- Orc.Mcp 全量单测：121/121，含新增投影回归。部署结构测试修复前失败、修复后通过；双方分别先确认的状态测试均通过。
- Godot headless 双真人连接：替换一张、保留全部两种场景均通过，包含实际输入事件拖牌部署；标记 `ROOM_REMOTE_VERIFY_OK ... actual-drag-deploy`。
- Godot headless 房主 + AI：替换一张、进入对局及 AI 回合返回通过。该场首回合没有可负担牌，未用它作为出牌通过证据；出牌证据来自上面的真人测试。
- 构建成功；相关文件 diff 空白检查通过。

运行日志在源码工作区 artifacts/：`room-ui-tests.log`、`room-mcp-all-tests.log`、`projection-tests-before-fix.log`、`projection-tests-after-fix.log`、`room-remote-replace-after-fix.log`、`room-remote-keep-after-fix.log`、`room-ai-after-fix.log`。

## 交付范围

引擎源码改动位于 `H:/Working Folder/OrC-KSD-github/src/Orc.Mcp/McpService.Projection.cs`，回归在同仓库 `tests/Orc.Mcp.Tests/ProjectionTests.cs`。对应补丁保存在 `docs/engine-patches/remote-mulligan-deployment-20261024.patch`，更新引擎时须保留。

运行副本 `H:/g/kards` 及开发服务端已重建。两端客户端与房主服务端须同时使用修复后的构建。未重新打包发行 ZIP/离线补丁，未提交或推送。本轮验证是本机两条真人连接及 UI 输入事件，未做跨机器组网或完整长局验收。
