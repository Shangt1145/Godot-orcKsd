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
│ 加入别人的房间         │  └───────────────────────────────────┘
│ [26.x.x.x:端口      ]  │
│ [加入  →            ]  │  座位齐备即可开局；远端玩家用上方地址自行加入。
│ ─────────────────      │                        [开始对局 →]
│ 拉入                   │
│ [＋ 引擎 AI]           │
│ [＋ 远端玩家]          │
│ [＋ Agent（未开放）]   │
└────────────────────────┘
```

两种身份共用这一个界面：

- **房主**（默认）：进屏即起本地 `Orc.Server`，占 `seat 0 / kind:local`，
  能放 AI、能开局。
- **客人**：在"加入别人的房间"填 `IP:端口`，连对方的服务端，占 `seat 1 / kind:remote`；
  不能放 AI、不能开局（按钮文案变"等待房主开局…"），房主一开局就自动切战场。

设计决定及其理由：

| 决定 | 理由 |
|---|---|
| 地址只列 3 条，虚拟局域网段优先（`AddressPriority`：Radmin `26.` / ZeroTier `28-30.` 等） | 本机常枚举出 7 张网卡，全列会把左栏撑满；"发给对方"实际只需一个可用地址 |
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
| 左栏"本机服务器" | `room.info` → `addresses` / `packPolicy` / `packFingerprint` | 地址由服务端枚举全部网卡，虚拟局域网地址自动在列 |
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
| 1 | 玩家卡组 id 映射缺失 | 只能用 `LobbyDeck` 现组，不能用自己构筑的卡组 |
| 2 | `notify/segment` 演出翻译未经真实双机验证 | 单机零回归已验证，联机演出只验证到"能收到视图" |
| 3 | Agent 控制面未开放 | 协议 2.0 预定稿里的 `seat.request` 等尚未实现 |
| 4 | 客人没有"断线重连"入口 | `seatToken` 已持久化在 feed 内，但没有 UI 让玩家重新接回 |
| 5 | `target.submit` 的 claimant 写死成 service | 宿主 UI 无法以 host 身份优先应答，只能与 agent 拼先到先服务 |

> 已解决：远端玩家的独立入口（房间屏左栏"加入别人的房间"）。
> 已解决：出牌按卡型分流（§5.1）。
> 已解决：选靶链路端到端打通（§5.2）——包括 16 张卡的效果脚本迁移（§5.3）。

## 5.1 出牌工具必须按卡型分流

`PlayCard` 不能一律映射成 `play.unit`：

| 卡型 | 工具 | 参数 |
|---|---|---|
| `unit` | `play.unit` | `{ref, owner, slot}` |
| `order` | `play.command` | `{ref}` |
| `counter` | `play.counter` | `{ref}` |

指令卡走 `play.unit` 会被服务端**静默忽略**——手牌不减、费用不扣、没有错误码。
实测症状是 `hand 6→6 kredits 3→3`，看起来像"没打出去"，很容易被误判成选靶链路的问题。
分流后鹰爪正常打出：`hand 6→5 kredits 3→0`。

## 5.2 选靶链路（**已端到端打通**）

| 环节 | 实现 |
|---|---|
| 收请求 | `notify/target.request` → `UiTargetRequest`，候选 `UiChoice.Id` 直接用序号（1 起） |
| 提交 | `target.submit{sessionId, picks:[序号]}` |
| 取消 | `target.cancel{sessionId}`；空选择（拖拽落空）也走这里 |
| **不要认领** | `target.submit` 的 claimant 在服务端写死成 `ClaimantService`；先 `target.claim{claimant:"host"}` 会让提交被判"会话与请求不符"。想要 host 优先得引擎先加参数 |

实测证据（`-VerifyTarget`）：

```
deck faction=soviet size=39
request selector=fieldUnit drag=True slots=1 candidates=1
submitted pick=1
after submit: ownHq 20->20 ownLine 1->0 pending=False
TARGET_VERIFY_OK request-received targeter-submitted effect-applied
```

## 5.3 csx 迁移：从 UI 层桥到引擎 targeter

**问题**：16 张卡的效果脚本调的是 `Kards.Ui.OrcBridge.DesktopTargetContext.SelectAsync`——
UI 层程序集，服务端进程里没有这个类型，效果**静默不执行**，永远不产生 pending。
（讽刺的是 `DesktopTargetContext.SelectCore` 内部走的也是 `_match.TargeterManager`，
即数据绕过正门抱了个 UI 壳。）

**迁移**（`tools/migrate-targeter-csx.py`，可重复执行）：

```
await Kards.Ui.OrcBridge.DesktopTargetContext.SelectAsync(orcSelf, "{json}", "{hash}", ct)
→ (await orcRuntime.BeginTargetSelectionAsync(orcSelf,
       new Orc.Game.Effects.EffectSelector("all", side, zone, Count:null), ct)).ToArray()
```

映射：`side` any→both / enemy / friendly（缺省 both）；`zone` field→null（不限线）/ frontline / support。

🔴 **`.ToArray()` 不能省**：原返回值是 `Card[]`，脚本里有 `new Card[][]{orcV0, ...}` 这种嵌套。
换成 `IReadOnlyList<Card>` 会**编译失败 → 整段 csx 不执行 → 效果静默**，而且没有任何日志。
第一次迁移就踩了这个，表现和"没迁移"一模一样。

验证分层（缺一层就会误判）：

1. 卡打出去了吗？→ `hand 6→5 kredits 3→0`
2. 收到 pending 了吗？→ `request selector=fieldUnit candidates=N`
3. 效果真生效了吗？→ `ownLine 1→0`（单位被打死）

> 第 3 条要注意：脚本是"伤单位 + 伤总部"，单位死亡会在死亡结算处中断，总部那 1 点不一定落到。
> 所以断言要看"单位减少或总部掉血"，只盯总部会误报失败。

**没入库的部分**：`proto/data/kards/packs/` 是 `.gitignore` 的（卡数据产物不入库），
所以这 16 张卡的修改是本地状态。换机器/重装卡包后要重跑迁移脚本——
这就是把它做成工具而不是手改的原因。

## 5.4 指挥命令：移动 / 攻击 / 撤退（M1）

`RemoteMatchFeed.Map` 现在接 5 类命令（原来只有 PlayCard/EndTurn/Concede）：

| 前端命令 | 服务端工具 | 说明 |
|---|---|---|
| `CommandUnit` | `command.begin{ref}` | 发起指挥，之后走选靶 |
| `MoveUnit` | `command.move{ref}` | 发起移动，落点由选靶/引擎决定 |
| `AttackUnit` | `command.attack{ref}` | **只传攻击者**；目标由引擎裁定 |
| `RetreatUnit` | `command.leave{ref}` | 直接完成 |
| `PlayCard`（单位） | `play.unit` / `play.unit_pre` | 见下 |

### 🔴 三个"静默失败"级的坑

**① 出牌槽位不能由前端猜。**
`UiMatchView.SelfLine` 把**前线与支援线合并**，两条线的 `SlotIndex` 互相覆盖。
按它推"空槽"会算出已被占的位置，实测 **40+ 次 `TargetSlotOccupied`**，
症状是出牌永远失败（手牌不减、费用不扣、没有报错）。

正确做法：玩家指定了槽位（拖到具体位置）才走 `play.unit{ref,slot}`；
没指定就走 `play.unit_pre{ref}` 进部署选位交互，由 targeter 决定落点。

**② `targeting` 不是失败。**
指挥动作是两段式：工具只吃 `{ref}`，第一段结束服务端必回 `targeting`。
如果这时触发 `CommandRefused`，UI 会把刚发起的动作当场回滚——看起来像"点了没反应"。

**③ 指定目标不在候选里，必须主动 `target.cancel`。**
典型场景：敌方前线还有单位时想打 HQ，HQ 根本不在候选里。
把请求挂在服务端等超时会**死锁**：之后每条命令都吃
"当前有未应答的选靶请求"——连出牌都被堵死（实测出牌全失败就是这条）。

### AutoResolveTargets（无人值守）

```csharp
feed.AutoResolveTargets = true;   // 探针 / 自动对战
```

自动应答所有选靶（选第一个候选），且**不触发** `TargetRequested` 面板。
正常对局必须为 `false`。
存在的理由：部署类效果也会走选靶（`play.unit` 返回 `targeting`），
无人值守时不自动应答就会挂到超时。

### 现状与不足

实测（`-VerifyCombat`）：

```
attacks=37  enemyHq 20→16  turn=21
COMBAT_VERIFY_OK command-chain-attack-effective
```

历史上曾完整推到终局：`outcome=Lose turn=20 ownHq=0 enemyHq=7`。

**还没做到的**：

- `command.move` 发得出，但引擎多回 `NoActionAvailable`（单位当回合部署 / 本回合已行动），
  探针 `moves=0`。机制在，策略不够聪明。
- 移动目标是槽位、只能由面板应答；探针不抢应答权，所以移动链尚未端到端验证。
- 铺场因此受限：支援线塞满后一直吃 `PrePlayNoAvailableSlots`。

> 判「能打完」的标准仍是一局打到结算面板。现在**机制上通了**（跑到过一次 Lose），
> 但探针策略不足以稳定复现——那属于自动对战 AI 的活，不是命令链的活。

## 5.5 玩家卡组：不是"缺映射"，是两套卡毫无关联（M2）

原本的判断是"catalog id ↔ 应用层 id 缺一张映射表"。实际查完数据，结论完全不同：

**两套卡之间没有任何可用的关联。**

| 证据 | 结果 |
|---|---|
| 按「国籍 + 卡名」匹配 219 张包卡 | **只对上 2 张** |
| 按「卡名」单独匹配 | 2 张，且源卡有 7 组重名 |
| 包卡是否带溯源字段（src/art/sourceId） | **没有**，只有 `schemaVersion/id/name/components` |
| 国家代号能否互通 | **不能**：源卡是 `UN/USG/av76/deran`，包卡是 `germany/usa/britain/soviet`，没有映射表 |

所以"写张映射表"这条路不存在。真正能走的是：**联机就用卡包卡**。
好消息是这条路本来就已经铺好了 ——

```csharp
// DeckConstructionService.Rebuild
PlayableIds = _reviewedIds                       // 单机用的原始卡
    .Concat(_packCards.Select(c => c.CardId))    // pack/{包}/{局部id} ← 已经在可选池里
    ...
```

`pack/...` 卡早就可以被构筑、被校验、被服务端接受。**缺的只是"房间根本没用玩家的卡组"** ——
它一直在用 `LobbyDeck` 现组一副机器牌。

### 改动

`RoomScreen` 新增 `ActiveDeckSource`（由 `Main` 接到 `DeckLibrary.ActiveDeck.CardIds`）：

```
组卡顺序：玩家构筑卡组（必须全为 pack/ 前缀且 39 张）
          → 不行就退回 LobbyDeck 自动组牌，并在卡池标签写明原因
```

有必带卡的验证探针（`-VerifyTarget` / `-VerifyCombat`）强制走自动组牌，不受影响。

卡池标签会显示 `自建` 或具体退回原因，例如：

```
卡池 player · …· 你的构筑卡组
卡池 builtin · … · 卡组含联机不支持的卡（av76/command/-1）
卡池 builtin · … · 卡组 12 张（联机要求 39 张）
```

### 玩家怎么组一套能联机的卡组

卡组编辑器的卡库同时列出「原始卡」和「卡包卡」两套。**联机必须选卡包卡**（`pack/` 前缀）。
目前没有 UI 层面的强区分，玩家混选就会看到上面那条"含联机不支持的卡"提示。

> 待办：卡库里给不可联机的原始卡加标记或过滤。当前靠提示语兜底——
> 这比"静默失败成一副打不出去的卡组"好，但还不够体面。

## 9. 验证通道

```powershell
# 无头：起实例 → 占座 → 放 AI → 开局 → 切战场 → 换牌 → 出牌
tools\run.ps1 -VerifyRoom          # ROOM_VERIFY_OK

# 无头：两个真人客户端（seat 0 local ＋ seat 1 remote），验占座与按座位脱敏
tools\run.ps1 -VerifyRoomRemote    # ROOM_REMOTE_VERIFY_OK

# 无头：走**界面路径**加入（第二个 RoomScreen 填地址点加入），验玩家真正会走的流程
tools\run.ps1 -VerifyRoomJoin      # ROOM_JOIN_VERIFY_OK

# 无头：选靶链路（收到 pending → 提交 → 效果生效）
tools\run.ps1 -VerifyTarget          # TARGET_VERIFY_OK

# 无头：指挥命令链（攻击真的造成伤害）
tools\run.ps1 -VerifyCombat          # COMBAT_VERIFY_OK command-chain-attack-effective

# 图形：拍真实布局（等 seatToken 再拉 AI，主动刷快照后按快门）
tools\run.ps1 -CaptureRoom         # ROOM_CAPTURE_OK → artifacts/menu/room-1600.png
```

验收要同时看两类证据：`--verify-room` 证明**功能链**通，`--capture-room` 证明**画面**对。
只看其中一个都会误判——功能绿灯不代表布局没错，截图好看也不代表链路能跑。
