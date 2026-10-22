# 联机 AI 开局修复与作战准备页

日期：2026-10-22。基线 UI `1fc69cc`。本轮未修改 AI 策略或引擎源码。

## 故障原因与修复

- 远端视图只含服务端投影，缺少客户端卡图位置。RemoteMatchFeed 现在通过只读的 CardId→本地卡图映射补齐可见卡牌的 Definition.ArtPath，覆盖手牌、战线、总部与候选；不改变卡牌规则或揭示对手暗牌。
- 换牌面板发送 ChooseMulligan，但远端 Map 没有处理，最终显示“联机模式下暂不支持该操作”。现在保留全部走 mulligan.done；替换牌走 mulligan.begin，再按服务端 mulligan 候选中的 ref 对应 picks。实际换牌仍由引擎执行。
- 初始换牌投影可能先于战场事件订阅到达。进入远端战场后显式检查当前换牌状态，确保面板能出现。
- 既有验收直接调用 MulliganDoneAsync，绕过玩家按钮，所以没发现断链。现验证改为实际面板 ConfirmMulligan，并可标记一张替换，检查该卡离开手牌。

## 作战准备页

移除旧双方面朝下卡背和底部技术简报，改为地图桌面上的卡组封面陈列、选中标识、卡组名称张数，以及侧边作战方式／当前卡组／主出击操作。卡组来自现有卡组库，封面取实际卡牌；可直接切换当前部队。

保留我们的地图、纸张与金色视觉语言，加入训练、好友对战、房间发现和工坊卡组管理提示。模式选择用选中态；好友列表按原监听机制更新。卡组不满足当前联机入口范围时引导更换，创建与列表加入均检查，避免从准备页默默换成另一副牌。房间内部验证用的自动组牌通道保留。

参考：[KARDS 官方 Steam UI 更新说明](https://store.steampowered.com/news/posts/?appids=544810&enddate=1678187811&feed=steam_community_announcements) 中的模式与卡组选择层级；这是本项目的实现，不宣称复刻全部原版页面。

## 验证

- 编译通过，现存 4 项警告未在本轮处理；不宣称零警告。
- `artifacts/room-ai-fix-keep.log`：保留全部→正常对局→出牌→结束回合→AI 行动并交回回合，`ROOM_VERIFY_OK`。
- `artifacts/room-ai-fix-replace.log`：同上，另检查选择替换的一张卡确实离开手牌，检查全部初始手牌的本地纹理能加载；最终以实际渲染进程执行。
- `artifacts/room-ai-fix-join.log`：`ROOM_JOIN_VERIFY_OK`，真实服务器与两个客户端加入、独立座位及视角；同机双端，不冒充两台物理电脑测试。
- `artifacts/setup-selection-1600.log`、`setup-selection-1280.log`：两种请求窗口尺寸通过准备页模式切换与入口验证，并检查渲染截图。Godot 的逻辑视口仍为 1600，截图按窗口缩放输出。
- 截图保存在 `artifacts/setup-selection-20261022/`：practice、friends 的两种尺寸及 `room-ai-fixed.png`。

没有运行完整发行门禁，没有重新打包／推送；当前是源码及本地调试构建更新。对战布局未改，联机 UI 消费引擎快照与候选，不自行驱动 AI 或决定行动规则。
