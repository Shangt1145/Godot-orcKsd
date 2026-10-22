# 联机与匹配方案：虚拟局域网 + 组播发现 + 指令锁步（网络层已开源解耦）

> ## ⚠️ 同步层已被取代（2026-10-22）
>
> 本文的**网络层**（虚拟局域网，Radmin / ZeroTier / n2n 等均可）与**发现层**（组播宣告 + 本地撮合）仍然有效；
> **同步层从「指令锁步」改为「服务端权威」**——引擎在 `88c96bc` 这一版已经内建
> `Orc.Server` / `Orc.Client` / `Orc.Protocol`，一个 `Orc.Server` 实例＝一场对局，
> 房主客户端拉起它，其他人用 TCP/JSON-RPC 接进来。
>
> 改动原因（都是本文当时列为"风险"的那几条）：
>
> | 本文的判断 | 现在的事实 |
> |---|---|
> | "确定性尚未实测，V1 不过则方案作废" | 不再需要锁步，确定性问题自动消失 |
> | "锁步天然全知，作弊堵不住" | 视图按座位过滤，实测两端拿到不同总部/手牌 |
> | "断线＝本局作废" | `room.resume` + `seatToken` 重连 |
> | "要自己设计指令包格式" | 31 个 `tools/call` 就是命令面 |
>
> 落地形态见 **`docs/design/ROOM_SCREEN_20261022.md`**（已实现并双客户端实测通过）。
> 本文余下内容保留作为**历史推理与发现层设计依据**，不要照着实现同步层。

- 日期：2026-10-21（同步层于 2026-10-22 被取代）
- 状态：**发现层有效 · 同步层作废**
- 范围：1v1，桌面端 Windows，两人各自有 Radmin VPN
- 核心承诺：**不租服务器、不买 VPS、不运维任何常开进程**

---

## 1. 一页结论

整套东西是三层，每层的责任单一、边界清晰：

| 层 | 用什么 | 成本 | 谁提供 |
|---|---|---|---|
| **网络层** | Radmin VPN 虚拟局域网（`26.x.x.x`） | 免费 | Famatech（不归我们运维） |
| **发现层（＝匹配）** | UDP 组播宣告 + 客户端本地撮合 | 0 | 自己写，约 250 行 |
| **同步层** | ENet 可靠有序通道，只传 `UiCommand` | 0 | Godot 内置 |
| **规则层** | 引擎锁步（同 seed 各跑一份 `Match`） | 0 | **已有，一行不改** |

三句话概括：

1. **匹配不是"去问谁在等"，而是"谁想玩谁自己喊"**——每 1.5 秒往组播组发一个 64 字节的小包，收到即配对。
2. **IP 不用爬**——UDP 包自带源地址，`GetPacketIP()` 就是对方地址。
3. **对局不同步状态**——只同步指令，双方引擎各自算出同一个结果。

单局互发的数据量：一条指令几十字节，一局几十条。**总共几 KB。**

---

## 2. 为什么引擎能这么用（证据）

不是推测，逐条核过：

| 事实 | 位置 |
|---|---|
| 注释原文：「确定性 PRNG（自实现 SplitMix64…**为在线同步预留**）」「单流＋顺序确定消费…**P2 同步语义**」 | `Orc-KSD/src/Orc.Game/Match/RandomService.cs` 文件头 ② |
| `Match(... int? seed = null ...)`，`public int Seed { get; }` | `Match.cs:90` / `:203` |
| 前端建局 `CreateAsync(..., int seed, ...)` —— **seed 本就是必填参数** | `OrcMatchSession.cs:298` |
| 装配处直接 `seed: seed` | `OrcMatchSession.cs:367` |
| **先手由种子派生**：`openingPlayer = firstPlayerIndex ?? new MatchRandomService(seed).Next(2)` | `OrcMatchSession.cs:365` |
| 规则层零墙钟依赖（`Orc.Game`/`Orc.Core` 搜 `DateTime.Now`/`TickCount`/`Stopwatch` 零命中） | 全量核对 |
| `UiCommand` 12 个 `record`，字段仅 `string`/`int`/`bool`/集合 → **本身就是协议** | `proto/contracts/UiCommand.cs` |
| `SubmitAsync` 是 UI 触碰引擎的唯一入口 | `OrcMatchSession.cs:689` |

结论：**联机不需要改规则代码**，只需要给引擎接一条字节管道。

---

## 3. 网络层：Radmin 的定位

Radmin VPN 装一块**虚拟网卡**，给每台机器一个 `26.x.x.x` 的**静态**虚拟 IP（重启不变）。对游戏而言对方就是"局域网里的另一台机器"。

| 能力 | 对我们的意义 |
|---|---|
| 封装 TCP / UDP / ICMP | ENet（UDP）能跑，这是前提 |
| UDP 打洞 + 对称 NAT 自动走中继 | **不用自己写 STUN/TURN** |
| **支持广播 / 多播** | 发现层成立的前提 |
| 静态 IP | 手动输一次 IP 可永久保存 |
| 单网络上限 100 台 | 相当于一个**免费的房间服务器** |

### 3.1 关键认知：Radmin 网络 = 免费房间服务器

所有玩家加入**同一个公开 Radmin 网络**（网络名 + 密码公开写在群里/官网上），就等于所有人进了同一个"大厅"。这是本方案能替代服务器的根本原因。

代价：上限 100 人；所有人的虚拟 IP 彼此可见；没有身份认证。

### 3.2 它不是什么

- 不是"零依赖"——Famatech 的中继挂了，对称 NAT 用户会连不上。
- 不是隐私工具——同网络成员能看到彼此虚拟 IP。
- **必须实测**：Radmin 存在"直连"与"**中继/TCP**"两种状态，中继模式下游戏 UDP 是否仍透传，见 §9.1。

---

## 4. 发现层：匹配协议（核心新增）

### 4.1 位置与端口

| 用途 | 地址 | 说明 |
|---|---|---|
| 组播发现 | `239.255.42.99:27016` | 与 `26.x.x.x` 的掩码无关 |
| ENet 对局 | `0.0.0.0:27015` | 避开 Radmin 自身隧道端口 UDP 17075 |

**为什么用组播而不是广播**：广播地址由子网掩码决定，而 `26.x.x.x` 的掩码未知，猜错就发不出去。组播地址与掩码无关，且 Radmin 明确支持多播转发。`255.255.255.255` 只作备选。

### 4.2 宣告包（固定 64 字节，小端）

固定长度二进制，不用 JSON——解析零分配，且包体小到不会触发分片。

| off | size | 字段 | 说明 |
|---|---|---|---|
| 0 | 4 | `magic` | `"ORCS"`（0x4F524353），脏包直接丢 |
| 4 | 1 | `protoVer` | 版本不匹配直接丢，不尝试兼容 |
| 5 | 1 | `state` | 0 = waiting，1 = in-game，2 = full |
| 6 | 2 | `port` | 对方 ENet 监听端口（大端） |
| 8 | 4 | `nonce` | 本会话随机数；同机多实例时用于去冲突 |
| 12 | 8 | `defHash` | `definitionsHash` 前 8 字节 |
| 20 | 8 | `deckHash` | 卡组指纹（可选匹配维度） |
| 28 | 1 | `faction` | 主国，用于"同阵营局" |
| 29 | 1 | `rating` | 自报水平档位（0–255），**软匹配** |
| 30 | 2 | `reserved` | 置 0 |
| 32 | 32 | `displayName` | UTF-8，超长截断，尾部补 0 |

### 4.3 宣告节奏

- `state == waiting`：每 **1.5 秒** 一次，带 **±20% 抖动**（防所有机器同步碰撞）。
- 握手成功的第一件事：**立刻停发**并把 `state` 改成 `in-game`。否则别人会持续撞上你（§4.6）。
- 收到包 → 记入候选池 `(srcIp, nonce, 字段快照, 最后收到时间)`；**5 秒**未刷新则从池中移除。

带宽估算：100 台满员同时等待 → 每台收 100×64/1.5 ≈ 4.3 KB/s。实际几乎不会有 100 人同时排队，可忽略。

### 4.4 撮合规则（全部在客户端算）

```
1. 过滤：magic 错 / protoVer 不符 / defHash 不符 / state != waiting  → 丢弃
2. 排序：|rating − 本机 rating| 升序  →  同档内按"最近收到时间"降序（新来的优先）
3. 取第一名 → 进入配对
```

`defHash` 不一致必须丢弃：卡定义集不同必然导致状态分叉，宁可配不上也不能开一局错的。

`rating` 只是自报数字，无法验证，所以只能做"尽量凑得差不多"，**不能叫排位**。

### 4.5 角色去冲突（必须确定性的）

两边同时广播、同时决定"我连他" → 都当客户端 → 握手死锁。

规则：**比较 IP 的字节序，小的一方当房主**。

```
int cmp = CompareBytes(myIpV4, peerIpV4);   // 逐字节，不是字符串比较
if (cmp == 0) cmp = myNonce.CompareTo(peerNonce);
bool iAmHost = cmp < 0;
```

⚠️ **必须用字节序**。字符串比较下 `"26.10.0.1" < "26.9.0.1"` 会算错，而这类 bug 在两台机器上表现为"都当客户端、都超时"，查日志几乎看不出来。

### 4.6 竞态处理

| 竞态 | 处理 |
|---|---|
| A 已与 C 开局，B 仍在广播并连 A | A 停播后 B 收不到 → 自然消失；若 B 已发出连接，A 回 `MatchReject{reason="busy"}` |
| 双方同一帧决定配对 | §4.5 去冲突规则保证收敛到同一答案 |
| 收到自己的包（组播回环） | 按 `srcIp == 本机IP && nonce == 本机nonce` 丢弃 |
| 候选池里残留已下线的人 | 5 秒超时剔除；连接失败后把该 IP 拉黑 30 秒 |

### 4.7 降级通道

组播不通时（§9.1 探测失败），同一套 ENet 代码直接用**手动输 IP**：`SetupScreen` 里输入 `26.x.x.x` → `CreateClient`。**只损失"自动发现"，不损失"能对局"。**

---

## 5. 同步层：握手与指令流

### 5.1 握手（连接建立后第一条消息）

房主 → 客户端：

| 字段 | 说明 |
|---|---|
| `protoVer` | 二次校验 |
| `matchId` | 房主生成，双方必须相同 |
| `seed` | 决定洗切 + 先手，**先手不用单独传** |
| `deckA` / `deckB` | **座位 0 / 座位 1** 的卡 id 序列，顺序有意义 |
| `defHash` | 全量哈希（不只是前 8 字节） |
| `deckConfigA` / `deckConfigB` | `MainNation` / `AllyNation` |

客户端校验 `defHash` → 回 `MatchAccept` 或 `MatchReject{reason}`。

> **`deckA`/`deckB` 是座位顺序，不是"我的/你的"**。房主恒为座位 0，客户端恒为座位 1。搞反会导致双方镜像。

### 5.2 建局

```csharp
// 房主
CreateAsync(definitions, deckA, deckB, seed, matchId, viewerIndex: 0, ...)
// 客户端
CreateAsync(definitions, deckA, deckB, seed, matchId, viewerIndex: 1, ...)
```

`viewerIndex` 已经是现成参数（`OrcMatchHost.Viewer` / `SelectViewer(int)`）。**引擎侧无需改动。**

### 5.3 指令流

```
当前回合方 → SubmitAsync(cmd) 前 → 发送 {seq, side, cmd}
                                   ↓
                对方收到 → 校验 seq 连续 → SubmitAsync(cmd)
```

- 走 ENet **reliable ordered** 通道，不另做确认包（可靠传输已保证送达与顺序）。
- 回合制保证同一时刻只有一方能提交（`IsViewerOnTurn`），天然串行，无并发竞态。
- 对方回合误提交会被引擎以 `NotYourTurn` 拒掉，这是第二道防线。

**必须做**：联机时把 `OrcMatchSession.DriveOpponent` 置 **false**。它默认 `true`，会驱动 `AiPilot` 抢占对方回合——这是最容易漏的一处。

### 5.4 断线

`peer_disconnected` → 弹"对手已断开" → 本局作废，回菜单。

**不做重连**：锁步没有"中途加入"能力。将来的后路是指令回放（一局几十条 × 几十字节，重放成本几乎为零）。

---

## 6. 架构与改动落点

```
UI（SetupScreen / BattleScreen）        ← 加"快速匹配 / 建房 / 输 IP"与连接状态
├─ DiscoveryService  (新增)             ← 组播收发、候选池、撮合、去冲突   ~250 行
├─ NetChannel        (新增)             ← ENet 低层封装、握手与指令编解码    ~200 行
├─ OrcMatchSession.SubmitAsync          ← 加一层"先广播"钩子
└─ OrcMatchHost / Match                 ← 一行不改
```

### 6.1 用低层 `ENetConnection`，不用 `ENetMultiplayerPeer`

`ENetMultiplayerPeer` 会激活整个高层 API（`MultiplayerAPI` / `MultiplayerSpawner` / `MultiplayerSynchronizer` / `[RPC]`）并绑定场景树。我们只要一条字节管道，用 `ENetConnection`（`create_host` / `create_client` / `service` / `pop_packet` / `broadcast`）直接收发。

否则会把 `AnimationEngine` / `BattleSequence` / `SegmentPlayer` 全卷进网络时序，自找麻烦。

> 具体签名以实现时 Godot 4.7 文档为准，此处不写死。

### 6.2 接触点

| # | 位置 | 改什么 |
|---|---|---|
| 1 | 新增 `proto/net/DiscoveryService.cs` | 组播宣告、候选池、撮合、去冲突 |
| 2 | 新增 `proto/net/NetChannel.cs` | ENet 连接、握手、指令收发、断线检测 |
| 3 | `OrcMatchSession.SubmitAsync`（`:689`） | 钩子；`DriveOpponent = false` |
| 4 | `OrcMatchHost` | 不改（`viewerIndex` 已够用） |
| 5 | `SetupScreen.cs` | 对手栏从写死"内置对手"扩为三项 |
| 6 | `Main.cs` | 建局入口分流：本机种子 / 握手包种子 |

### 6.3 UI 落点（遵循既有菜单语言）

`SetupScreen` 对手栏扩为三项，视觉上沿用现有卡片列样式：

- `内置对手` —— 现状，不动
- `快速匹配` —— 进入等待态，显示"正在寻找对手（Radmin 网络内）· 已等待 Ns"，可取消
- `输入 IP` —— 降级通道，`26.x.x.x` + 端口

匹配成功 → 走既有 `MenuMotion` 幕布转场进战场（**不要新做转场**）。

---

## 7. 时序：动画不影响规则

规则在 `SubmitAsync` 被引擎应用的那一刻就定了；`AnimationEngine`/`BattleSequence` 只是播出已确定的 `UiPresentation`。两边演出快慢不同、一方关掉动画，规则结果一样。

**唯一要复核的**：`proto/core/TurnInputGate.cs` 如果依赖本机动画进度，会出现"一方能点、一方不能点"的窗口差异。联机下输入门禁**只允许依赖引擎状态**（`IsViewerOnTurn` / `Phase`）。

---

## 8. 候选池与匹配的可观测性

匹配是"看不见的"，出问题极难查。必须留痕：

- 开发期在 `artifacts/net/` 落一份 `discovery-<时间>.log`：每个收到的包（时间、源 IP、state、defHash 前 4 字节）+ 每次撮合决策（为什么选它、去冲突结果）。
- 提供 `--net-probe` 入口：只跑发现层，控制台打印候选池快照，不建局。**这是验证组播是否通的唯一手段**（§9.1）。

---

## 9. 风险与缓解

### 9.1 【唯一的技术未知】Radmin 中继模式下 UDP／组播是否透传

Radmin 帮助明确区分"直连"与"**中继/TCP**"。中继模式下链路转 TCP，UDP 是否仍被封装转发、组播是否仍扩散，**必须实测**，不能假设。

**探测方法**（`--net-probe`，30 分钟）：
1. 两端加入同一 Radmin 网络，`ping 26.x.x.x` 互通。
2. 一端 `JoinMulticastGroup(239.255.42.99)` 并监听 `27016`；另一端每 1.5 秒发一次宣告包。
3. 在 Radmin 窗口确认当前是「直连」还是「中继/TCP」，**两种状态各测一次**。

**判定**：
- 组播通 → 走完整方案（自动匹配）。
- 组播不通、UDP 通 → 自动发现降级为"手动输 IP"（§4.7），其余不变。
- **UDP 也不通** → ENet 换 `WebSocketMultiplayerPeer`（TCP）。卡牌是低频小包，队头阻塞几乎无感，代价可接受。

### 9.2 【最大业务风险】卡定义集不一致 → 静默分叉

`definitions` 来自 `OrcEngineHost.Snapshot()` + 卡包 + DIY 卡 + 导入卡 + 转换效果。任一项不同，两边算出的结果就不同，**且不会报错**——只是画面慢慢对不上。

缓解：
1. 宣告包带 `defHash` 前 8 字节**先过滤**（不配=不连），握手里再校验全量哈希（不一致直接 reject）。
2. **M1/M2 阶段联机只允许内置卡池**。DIY 卡必须先同步给对方，否则对方建局直接抛 `The deck references undefined card(s)`。
3. `matchId` 双方必须一致（引擎投影/日志按它索引）。

### 9.3 锁步天然全知 → 作弊堵不住

双方本地都有完整 `Match`，内存里能读到对方手牌。`IUiProjectionProvider` 只裁剪投影，挡不住直接读内存。

**立场：接受**。真要防就得有服务端裁判，那就回到租服务器了。UI 与说明里如实标注"仅限熟人/社区局"。

### 9.4 自报分数不可信

`rating` 由客户端自己写，改得了也没人验。所以匹配只能是"尽力凑"，**不叫排位、不记分、不做赛季**。

### 9.5 Windows 防火墙

- 游戏进程需**入站 UDP** 放行（27015 对局 + 27016 组播接收）。
- 规则绑定到 **Radmin 虚拟网卡**，不要暴露在物理网卡上。
- Radmin 自身隧道端口是 UDP 17075，避开。

### 9.6 广播域上限

Radmin 单网络 100 台。满员时：组播流量上升、人人可见彼此、名字可能重名。**1v1 场景实际不会有这个问题**，但公开网络需要一间"大厅网络"，人多了得分池。

---

## 10. 前置验证（写代码之前必须完成）

| # | 验证项 | 通过标准 | 需要网络？ |
|---|---|---|---|
| **V1** | **确定性验证** | 同进程建两个 session，同 seed / 同卡组，回放同一指令序列，逐步比对 `UiMatchView`（手牌、场面、资源、回合）+ `Engine.RootStream` 错误条目 → **全等** | ❌ 不需要 |
| **V2** | **Radmin 组播/UDP 探测** | `--net-probe` 在「直连」与「中继」两种模式下均能收到对方宣告 | ✅ 需要 |
| **V3** | **卡定义一致性** | 两台机器算出的 `defHash` 完全相同 | ✅ 需要 |

**V1 不通过，整个方案作废** → 转「传状态 + 权威裁判」。所以 V1 必须第一个做，而它恰好**不需要任何网络代码**。

V1 做成 `--verify-lockstep` 探针，与现有 `--verify-opponent-turn` 同风格。同进程双 session 不受网络干扰，问题定位最干净。

---

## 11. 分阶段实施

### M1 — 同机双实例（0.5–1 天）
不碰网络，用内存队列当通道。目标：同 seed、同指令序列下两边状态逐回合全等。交付 `--verify-lockstep` + 结论。
> 这一步就是 V1，把"引擎能不能锁步"这个根本问题单独隔离出来。

### M2 — Radmin 下真对局（1–2 天）
接入 `ENetConnection` + 握手包 + 指令流；`SetupScreen` 加"输入 IP"。目标：两台机器打完一整局无分叉。
> 先做手动 IP，不做发现——**把"能对局"和"能互相找到"拆开验证**。

### M3 — 自动匹配（1–2 天）
`DiscoveryService`：组播宣告 + 候选池 + 撮合 + 去冲突；`SetupScreen` 加"快速匹配"与等待态 UI。
> 发现层可以独立于对局验证：`--net-probe` 两边能互相看见就算 M3 的核心成立。

### M4 — 可用性（按需）
自动发现失败后的重试与拉黑、名字显示、阵营/卡组过滤、观战（引擎已有 `_observer`）、断线后的指令回放重连。

---

## 12. 明确不做

| 不做 | 原因 |
|---|---|
| 租赁任何服务器 / VPS / 常开进程 | 本方案的硬约束 |
| 专用权威服务器 | 同上 |
| `MultiplayerSpawner` / `MultiplayerSynchronizer` 同步卡牌节点 | 回合制不需要逐帧同步，会把演出时序卷进来 |
| 帧同步 / 预测回滚 | 卡牌无实时性要求 |
| 排位 / 赛季 / 战绩 | 无服务器 = 分数不可信 |
| 跨平台（手机 / Web） | Radmin 只覆盖桌面 Windows；Web 导出不支持 UDP |
| 断线续玩（M1–M3） | 锁步不支持中途加入 |

---

## 13. 验收标准

### M1
1. 同 seed、同指令序列，两边 `UiMatchView` 每一步全等。
2. 全程 `Engine.RootStream` 无新增 `Error`。
3. 换 seed 后结果随之变化（证明真的在用种子，而不是碰巧一致）。

### M2
1. 两台机器经 Radmin 输 IP 连接，30 秒内进入战场。
2. 完整打完一局，**双方结算一致**（胜负 + 原因）。
3. 任一方断开，另一方 5 秒内收到明确提示，不卡死。
4. `defHash` 不一致时拒绝开局并给出可读原因。

### M3
1. 双方各自点"快速匹配"，**3 秒内**自动配对进入战场。
2. 只在一方等待时，不产生任何配对（不自己跟自己开）。
3. 三方同时等待时，能正确配成一对，落单的一方继续等，不出现"两边都当房主"。
4. 任一方进对局后，其宣告立即停止，别人不再匹配到他。

---

## 14. 参考

- 引擎确定性：`Orc-KSD/src/Orc.Game/Match/RandomService.cs`（文件头 ②）
- 种子与先手：`Match.cs:90/203`；`OrcMatchSession.cs:365-367`
- 命令契约：`proto/contracts/UiCommand.cs`
- 唯一入口：`OrcMatchSession.cs:689`
- 视角切换：`OrcMatchHost.cs:89/198`
- 对手驱动（**联机需关**）：`OrcOpponentDriver.cs`
- 既有探针风格：`proto/ui/screens/Main.*Verification.cs`
- Radmin 帮助（中继/TCP 说明）：<https://radmin-vpn.sourceforge.io/cn/help/index.html>
