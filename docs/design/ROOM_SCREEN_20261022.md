# 作战房间（RoomScreen）设计与接线

> 落地日期：2026-10-22 · 提交：`98bffec`（房间屏）、`5698c70`（双客户端实测）
> 相关协议：引擎 `ProtocolInfo.ProtocolVersion = "1.0"`（**不是**交接文档里的 2.0 预定稿）

## 1. 一句话

进屏即**拉起一个 `Orc.Server` 实例**，在同一个界面上完成"起房 → 拉人 → 分配座位 → 开局"，
开局后把远端 feed 接到战场，和单机走同一条展示路径。

## 2. 形态

```
//  ROOM · 局域网对局
作战房间                                   [← 返回指挥部]
拉起服务器实例，分配座位，然后开局。

┌ 本机服务器 348 ────────┐  ┌ SEAT 0 ────────┐ ┌ SEAT 1 ────────┐
│ ● 就绪 · 端口 4717     │  │ 房主            │ │ 空座            │
│                        │  │ 玩家 · 已连接    │ │ 等待玩家接入，   │
│ 发给对方               │  │ ─────────────   │ │ 或直接放一个 AI  │
│ 26.109.28.81:4717      │  │ 卡组 39 张 ·     │ │ ─────────────   │
│ 198.18.0.1:4717        │  │ Germany         │ │ 卡组 —          │
│ 169.254.83.107:4717    │  │                 │ │                 │
│ +4 个其它网卡          │  │                 │ │ [＋ 放引擎 AI]  │
│ ─────────────────      │  └─────────────────┘ └─────────────────┘
│ 卡池 builtin · loaded  │
│ 房间 729f3e45          │  ┌ 开局检查 ─────────────────────────┐
│ ─────────────────      │  │ ✓ 座位 0 就位 ✓ 座位 1 就位 ✓ 卡池 │
│ 拉入                   │  └───────────────────────────────────┘
│ [＋ 引擎 AI]           │
│ [＋ 远端玩家]          │  座位齐备即可开局；远端玩家用上方地址自行加入。
│ [＋ Agent（未开放）]   │                        [开始对局 →]
└────────────────────────┘
```

设计决定及其理由：

| 决定 | 理由 |
|---|---|
| 地址只列 3 条，Radmin `26.x` 优先 | 本机常枚举出 7 张网卡，全列会把左栏撑满；"发给对方"实际只需一个可用地址 |
| 空座卡内直接给"放 AI" | 用户的心智是"把位置填上"，不是"先建一个 controller 再绑座位"；v1 的 `add_ai` 本身就要求指定座位 |
| 座位有 controller 用**金框**，空座用**暗框** | 沿用项目已有语言（实线＝生效 / 虚线＝未启用）的近似表达 |
| "开始对局"三绿才可点 | 与 `room.start` 的服务端前置条件一一对应（两座非空 + 卡池 loaded），失败原因在按钮变灰前就能看见 |
| 拉人区放左栏而非弹窗 | 房间是"待客"场景，可选项应当常驻可见，而不是藏进菜单 |

## 3. 状态机

```
未启动 ──Open()──> 组装卡组 ──> 起服务器 ──> 连接 ──> 占本座(seat 0, local)
                                                          │
                                     ┌────────────────────┘
                                     ▼
                              就绪（每 1.5s 轮询 room.info）
                                     │
                      ┌──────────────┼──────────────┐
                      ▼              ▼              ▼
                 放 AI(座位 N)  远端玩家 claim   开始对局
                      │              │              │
                      └──────────────┴──────┬───────┘
                                            ▼
                                   切战场（BindFeed + 首帧视图）
```

- `Open()` 可重复调用，已就绪时只刷新快照。
- `Close()` 释放 feed 与 server 进程——服务器不会自己退出，返回时必须显式关。
- 轮询 1.5s 是刻意的：v1 没有 `notify/room`，远端玩家接入只能靠拉。

## 4. 协议映射（v1 实际调用）

| 界面元素 | 协议 | 备注 |
|---|---|---|
| 进屏 | `ServerProcess` 起 `Orc.Server --packs ... --port 0` | 端口 0 ＝系统分配，避免撞车 |
| 左栏"本机服务器" | `room.info` → `addresses` / `packPolicy` / `packFingerprint` | 地址由服务端枚举全部网卡，Radmin `26.x` 自动在列 |
| 房主占座 | `room.claim {seat:0, kind:"local", deck:[全名]}` | **必须 `kind:"local"`**，否则 `HostSlot()` 不认，开局返回"房间没有房主" |
| 座位卡 | `room.info` → `seats[].controllerKind / displayName / connected` | `local`＝房主、`remote`＝玩家、`ai`＝引擎 |
| 放 AI | `room.add_ai {seat, deck:[全名]}` | 一次必须指定座位 |
| 开始对局 | `room.start {seatToken}` | **必须带本座令牌** |
| 战场视图 | `observe.view {owner}` ＋ `notify/view` 推送 | 按座位过滤，两端看到的不是同一份状态 |

## 5. 三条硬约束（踩过才知道）

1. **卡组 id 必须是应用层全名** `pack/{包id}/{局部id}`。
   房间填充走 `DeckRules.Validate`，直接拿 id 查定义表；MCP 建局那条路才会做"裸 id → 全名"的解析。
   写裸 id 的报错是"卡不在已装载卡池中"，很容易误判成"包没装上"。
2. **卡组必须 39 张、单一主国、同名上限按「卡名」累计**，
   且**缺 `effects` 组件的指令卡会被引擎静默丢弃**（builtin 里有 6 张，`diagnostics` 仍是 0）。
3. **视图枚举是字符串**。`ProtocolJson.Options` 没挂 `JsonStringEnumConverter`，
   直接反序列化 `UiMatchOutcome` 会抛 `could not be converted`；
   `RemoteMatchFeed.ViewJson` 单独补了一份带转换器的配置。

## 6. 线程模型

| 数据源 | 回调线程 | 处理 |
|---|---|---|
| 单机 `OrcMatchRunner` | Godot 主线程 | 同步执行，**保持验证时序逐帧不变** |
| 联机 `RemoteMatchFeed` | 协议线程 | `BindFeed` 内统一 `CallDeferred` 回主线程 |

远端模式下 `ProjectionReady` / `PresentationReady` / `CommandRefused` / `HintRequested`
都是协议线程回调；任何一个直接写控件都会触发 Godot 的
`can only be accessed from the main thread` 断言。

## 7. 卡组从哪来（临时通道）

玩家自建卡组存的是 catalog id（`av76/command/-1`），服务端要的是 `pack/builtin/...`，
**两套体系当前没有映射表**。所以房间里用 `LobbyDeck` 从卡包目录现场组一副合法卡组。

这不是最终形态，但它是可用的：能开局、能对战、规则合法。
真正的终点是补一张 catalog id ↔ 应用层 id 的映射，让玩家用自己构筑的卡组。

## 8. 已知缺口（按优先级）

| # | 缺口 | 影响 |
|---|---|---|
| 1 | **指令卡 / 移动 / 攻击缺 target responder** | 只能出单位牌；`command.begin` 之后无人应答 `target.request` |
| 2 | **远端玩家没有独立入口 UI** | 目前对方要用 `RemoteMatchFeed`/探针手动连；需要"加入房间"页（输地址 + 占座） |
| 3 | 玩家卡组 id 映射缺失 | 只能用 `LobbyDeck` 现组，不能用自己构筑的卡组 |
| 4 | `notify/segment` 演出翻译未经真实双机验证 | 单机零回归已验证，联机演出只验证到"能收到视图" |
| 5 | Agent 控制面未开放 | 协议 2.0 预定稿里的 `seat.request` 等尚未实现 |

## 9. 验证通道

```powershell
# 无头：起实例 → 占座 → 放 AI → 开局 → 切战场 → 换牌 → 出牌
tools\run.ps1 -VerifyRoom          # ROOM_VERIFY_OK

# 无头：两个真人客户端（seat 0 local ＋ seat 1 remote），验占座与按座位脱敏
tools\run.ps1 -VerifyRoomRemote    # ROOM_REMOTE_VERIFY_OK

# 图形：拍真实布局（等 seatToken 再拉 AI，主动刷快照后按快门）
tools\run.ps1 -CaptureRoom         # ROOM_CAPTURE_OK → artifacts/menu/room-1600.png
```

验收要同时看两类证据：`--verify-room` 证明**功能链**通，`--capture-room` 证明**画面**对。
只看其中一个都会误判——功能绿灯不代表布局没错，截图好看也不代表链路能跑。
