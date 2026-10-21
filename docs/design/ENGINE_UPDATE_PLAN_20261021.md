# 引擎更新评估与接入方案（`6d72eac` → `88c96bc`）

- 日期：2026-10-21
- 状态：**待评审**
- 前置事实：Beta.4 已用 `88c96bc` 编译、243 单测、`--verify-ui`、导出、真实离线升级**全绿**（`dist/Orc-KSD-0.0.0-beta.4-*.zip`）
- 本文回答两件事：**引擎这轮改了什么**、**前端该怎么跟**

---

## 0. 一页结论

| 问题 | 答案 |
|---|---|
| 规模 | 11 个 commit，263 文件，+34889 / −480 |
| 有破坏性变更吗 | **没有**。Beta.4 的发布实证了这一点（编译 0 error、单测全绿、导出与升级验证通过） |
| 唯一需要 UI 适配的 | 反制语义（`45f50cc`）——**已完成**，见 §2 |
| 真正的变化 | 不是"引擎改了接口"，而是**引擎给了新能力**：内建的服务端/客户端分离多人架构 |
| 建议路线 | **C：混合**——单机维持进程内引擎，联机走 `Orc.Server`（§5） |
| 对昨天联机方案的影响 | 网络层（Radmin）保留，**同步层从"锁步"升级为"服务端权威"**（§4.2） |

一句话：这轮更新**不需要抢救代码**，它打开了一扇门——而门后面正好是我们昨天花了半天在手工搭建的东西。

---

## 1. 逐 commit 影响清单

| # | commit | 主题 | 对 UI 的实际影响 |
|---|---|---|---|
| 1 | `8f85020` | 解决方案纳入 `Orc.Protocol`／`Orc.Client`／`Orc.Server`（含测试） | 无（纯新增工程） |
| 2 | `4d62ab2` | 客户端/服务端/协议三层与多人房间，太空战争示例卡包 | **无即时影响，但这是本轮最重要的一条** |
| 3 | `21e537e` | 多人支持客户端接入指南、前端适配说明 | 无（文档） |
| 4 | `3dada1d` | fix：多人房间推送与座位作用域、资产编辑版本规则 | 无（服务端内部） |
| 5 | `a35e0b9` | docs：审查报告与缺陷修复方案 | 无 |
| 6 | `b22b69d` | 解决方案纳入 `Orc.Mcp.Bridge` | 无 |
| 7 | **`45f50cc`** | **无头沙盒原语与 MCP Bridge、反制效果装配** | **有**——反制状态机语义变了，已适配 |
| 8 | `bc78509` | docs：沙盒、调试原语、反制装配、mcp-run | 无 |
| 9 | `ff4fb64` | chore：修复 `**/tools/` 误吞 MCP 源码 | 无 |
| 10 | `eb9186e` | fix：补回 17 个 MCP 工具源码 | 无 |
| 11 | `88c96bc` | docs：交接说明补记忽略规则事故 | 无 |

**结论**：11 条里只有第 7 条碰到 UI，且已经在 Beta.4 修完。

---

## 2. 已适配项（实证，不需要再做）

### 2.1 反制语义（`45f50cc`）

引擎把反制从"被动挂事件总线"改成**装配进 `CounterCard` 的 handler 集**（激活时进 `OrderFlow` 的 Counter 段）。新的窗口语义（`tests/Orc.Game.Tests/CounterArmingWindowTests.cs` 类注释原文）：

1. 己方指令不被己方反制拦截（归属过滤）
2. **只在对手回合生效**（回合门禁）
3. **己方回合开始自动解除**（`counter.disarmed`、不退点、不消耗、卡留手牌）
4. 解除后可重新激活
5. 主动取消仍退点且不发解除信号

`MatchOptions` 只新增了 `SandboxAuthorized`，**没有反制窗口开关**——语义不可配置。

UI 侧已完成的改动（`0c9ae8c`）：

| 文件 | 改动 |
|---|---|
| `Main.OrderVerification.cs` | 验收剧本按新语义重写：预热→取消→**重新预热**→跨回合自动解除→己方指令不触发 |
| `DiyEditorScreen.cs` | 移除卡图同时清草稿 `ArtFile`（`Load` 会用 `draft.ArtFile` 把 `_importedArt` 读回） |
| `DiyWorkshopScreen.Compatible.cs` | 新增 `OpenCompatibleCardForVerification`（工坊 `OpenPack` 只到卡包列表） |
| `Main.cs` | 旧编辑器 UI 夹具摘出 `--verify-ui`（18 处坐标点击全部落空） |

### 2.2 反制在 UI 上的表达缺口（**待办，非阻断**）

引擎现在有明确的"武装 → 解除 → 重新武装"三态，但 UI 只用了 `IsCounterArmed` 一个布尔值。缺的是：

| 缺什么 | 为什么重要 |
|---|---|
| 解除提示 | 玩家在己方回合开始时看到了卡"突然不亮了"，但没有任何说明；引擎有明确原因（回合门禁），应给出一次性提示 |
| 解除与取消的区分 | 两者都让 `IsCounterArmed` 变 false，但一个**不退点**、一个**退点**，玩家看不出差别 |
| 可重新激活的暗示 | 解除后仍可再点，需要视觉上允许再操作 |

**这一条建议放进 Beta.5**，因为它不影响正确性，只影响可理解性。

---

## 3. 新增能力盘点（未接入）

### 3.1 多人：`Orc.Protocol` / `Orc.Client` / `Orc.Server`（重点）

#### 3.1.1 架构

```
房主客户端 ──拉起──> Orc.Server 进程（一个实例 = 一场对局）
   │                    │
   │   房间态：座位 + 控制器（local / remote / ai）
   │   对局态：引擎 + 串行执行 + 按座位投影
   │
   └── TCP/NDJSON（JSON-RPC 2.0）──> 其他客户端
```

| 谁 | 负责 | 不负责 |
|---|---|---|
| 服务端 | 规则裁决、投影、选靶归属、卡组校验、AI 驱动、脱敏 | 美术、卡面文本、输入、动画 |
| 客户端 | 渲染、输入、把意图变成工具调用 | **任何规则判断** |

三种控制器：`local`（房主，唯一能开局/踢人/加 AI）／`remote`（凭 `seatToken` 绑定座位）／`ai`（服务端进程内 `AiPilot`，**完全不经过协议**但与人类同权）。

服务端只服务**一局**：房间先于对局存在，终局后不可回到房间态。

#### 3.1.2 协议

- 传输：**TCP / NDJSON**，JSON-RPC 2.0
- 方法只有四个：`initialize`、`ping`、`tools/list`、`tools/call`
- 业务全在工具里。**M0 实测共 31 个**（`tools/list` 返回，运行时自描述，schema 以它为准）：

| 组 | 工具 |
|---|---|
| service | `service.info`、`service.tools` |
| docs | `docs.list`、`docs.read` |
| room | `room.info`、`room.claim`、`room.release`、`room.kick`、`room.add_ai`、`room.start`、`room.resume` |
| match | `match.concede`、`match.end_turn` |
| mulligan | `mulligan.begin`、`mulligan.done` |
| play | `play.can_play`、`play.unit`、`play.unit_pre`、`play.join`、`play.command`、`play.command_pre`、`play.counter` |
| command | `command.begin`、`command.attack`、`command.move`、`command.leave`、`command.availability` |
| target | `target.submit`、`target.cancel`、`target.claim`（已退役，仅兼容） |
| observe | `observe.view` |

**这 31 个工具与 `UiCommand` 的 12 个 record 一一对应**，§5.2 的 `IMatchFeed` 抽象成立。
- 结果壳：`{ ok, code, message, details }`；`code == "targeting"` **不是错误**（动作已挂起等选靶），`code == "cancelled"` 也是成功态

**座位作用域（授权层）**——未登记的工具一律 `unauthorized`，默认拒绝：

| 类别 | 工具 | 门禁 |
|---|---|---|
| 绑定 | `room.claim`、`room.resume` | 成功后写会话座位；`kind:"local"` 只接受回环来源 |
| 归属校验 | `match.concede`、`mulligan.*`、`play.unit/join` | 参数 `owner` 必须＝会话座位，缺省也拒 |
| 仅要求已绑定 | `match.end_turn`、`play.*_pre`、`play.command/counter`、`command.*` | 未绑定 → `unauthorized` |
| 与座位无关 | `room.info/release/kick/add_ai/start`、`service.*` | 不看座位，且不改绑 |

`observe.view`：已绑定 → `seat` 只能省略或等于自己；未绑定 → 放行（单机诊断）。
**绑定不变量**：一个座位最多一个活动会话；同 `seatToken` 的 `room.resume` 会**顶掉**旧连接。

#### 3.1.3 心跳与断线

- 服务端：**45 秒无任何入站请求**即断开（TCP 半开不产生 FIN，只能靠应用层心跳）
- 客户端（`OrcClient` 内置）：静默 ≥30 秒主动 `ping`；发出后 15 秒无入站帧 → 判死
- 判死后果一致：关连接、**所有挂起请求立即以 `IOException` 失败**、`IsConnected = false`
- 重连：`room.resume` + `observe.view` 拉全量；`seatToken` 是唯一凭据，**必须持久化**

#### 3.1.4 单机也能用（关键）

`ServerProcess.StartAsync` 可以拉起本地服务端进程，`--port 0` 由系统分配，stdout 首行 `{"ready":true,"port":N}` 即就绪。服务端会自动带 `--parent-pid`，客户端崩溃时随之退出。

**这意味着**：即使不联机，也可以走"本地服务端"路径——代价是多一个进程、多一层 TCP，收益是单机/联机**同一条代码路径**。

#### 3.1.5 对照：接入指南要求删掉的东西

| 现有组件 | 指南要求 | 我们的现状 |
|---|---|---|
| `OrcEngineHost`（进程级引擎单例） | 对局路径停用 | **在用**，全部对局都靠它 |
| `OrcMatchSession`（持 `internal Match`） | 删除 | **在用**，命令唯一入口 |
| `OrcMatchHost.Pump()` 每帧投影 | 删除，改 `notify/view` 推送 | **在用** |
| `OrcMatchHost.Index`（暴露 `Card` 字典） | 删除（绕过投影的旁路） | **在用** |
| `IUiTargetResponder.Complete/Cancel`（同步 bool） | 删除，协议是异步请求-应答 | **在用** |
| `Match._Process` 同步排空 | 删除，主线程只渲染 | **在用** |

**所以"跟随引擎"不是加功能，是把对局层整个换掉。** 这是本方案最重要的判断。

### 3.2 MCP：`Orc.Mcp` / `Orc.Mcp.Bridge`

`McpService` 是**协议无关门面**：持有工具目录、唯一活跃对局与执行器、沙盒模式门控；**不实现传输**（stdio/JSON-RPC 由宿主适配）。

对 UI 的潜在价值：
- 同一套工具面可以喂给 AI Agent 打完整对局（沙盒指南里有"Agent 每回合循环"）
- 可以把"做一张卡 → 验证 → 对局"的流程自动化

**但这不是游戏功能**，优先级低于多人。建议只做调研不做接入。

### 3.3 无头沙盒与调试原语

- `Orc.Game/Sandbox`：`SandboxPrimitives`／`SandboxResult`／`SandboxViews`
- 无头对战环境：可以跑完整对局、输出事件流（含完整因果子树）、验证"指令/反制/新卡"三条链
- 由 `MatchOptions.SandboxAuthorized` 门控，**`play` 路径不开启**

对 UI 的价值：**能大幅缩短"改一个词条 → 验证"的循环**——不必起 Godot、不必点 UI。建议作为工具链优化独立评估。

### 3.4 资产编辑器：`Orc.Packages/Editing/AssetEditor`

已有：会话 / 提交 / 建资产 / 设版本 / 预览锁 / 重载（临时包 `.tmp/`、测试版本追加段、转正去段递增）。

DIY 目前是**前端自持**（`DiyWorkshopLibrary.Save`，保存/版本/依赖全在前端）。引擎给的是长期归宿，但迁移成本高，且当前 DIY 刚重构完。

**建议：暂不迁移，但让导出/导入的卡包符合 v2 manifest + sidecar + lock**，否则无法被 `EngineHost` 装载。

### 3.5 新卡包：太空战争

`samples` 里的示例卡包 + 设计文档。属于内容侧，UI 只需确认它能被现有卡包路由正确列出。

---

## 4. 三条路线

### 4.1 路线对比

| | **A. 维持进程内 + Radmin 锁步** | **B. 全面服务端化** | **C. 混合（推荐）** |
|---|---|---|---|
| 单机玩法 | 不动 | 改为本地服务端进程 | **不动** |
| 联机 | 自建组播发现 + ENet 锁步 | 官方协议 | 官方协议 |
| 要删的组件 | 0 | 6 个（§3.1.5） | 0 |
| 新增代码 | 发现层 ~250 行 + 传输层 ~200 行 | 协议客户端 + 6 个 UI 面 | 远端数据源适配层 |
| 工作量 | 3–5 天 | 2–4 周 | **1–2 周** |
| 反作弊 | 无（锁步全知） | 有（服务端脱敏） | 有（联机侧） |
| 断线重连 | 无 | 有（`room.resume`） | 有（联机侧） |
| 引擎更新成本 | 每次都要手工适配 | 自动 | 单机侧手工，联机侧自动 |
| 与引擎方向 | 逆行 | 一致 | 一致 |

### 4.2 对昨天方案的修正

昨天定的是 **Radmin + 组播发现 + ENet 指令锁步**。现在要改的是**同步层**：

| 层 | 昨天 | 现在 |
|---|---|---|
| 网络打通 | Radmin 虚拟局域网 | **不变**（仍然需要，因为房主要被访问） |
| 发现/匹配 | 组播 `239.255.42.99:27016` 宣告 | **不变**（组播找房依然是最便宜的方案） |
| 传输 | ENet（UDP）+ 自建握手/指令包 | **换** `Orc.Client`（TCP/NDJSON） |
| 同步 | 指令锁步（双方各跑一份 `Match`） | **换** 服务端权威（房主本地 `Orc.Server`） |
| 作弊面 | 全知，堵不住 | 服务端脱敏，**天然解决** |
| 断线 | 本局作废 | `room.resume` 重连 |

**为什么值得换**：昨天方案里我最担心的三件事——确定性验证（V1）、手牌全知、断线作废——**引擎这轮全给解决了**。而且不用自己写指令包格式、不用验证引擎锁步一致性。

**保留什么**：Radmin 打通网络 + 组播找房。这两块引擎不提供，也没打算提供。

### 4.3 为什么选 C 而不是 B

- B 要动**所有**对局路径，包括现在跑得好好的单机。Beta.4 刚发出去，立刻重构整条命脉，风险与收益不成比例。
- C 只加一条**并行**的联机路径，单机零回归。
- C 的代价是要维护两套数据源，需要一层抽象（§5.2）——但这层抽象本来就有价值（可测试性）。

---

## 5. 路线 C 的详细设计

### 5.1 边界

```
┌─────────────────────────────────────────────┐
│ BattleScreen / 所有表现层                    │  ← 不区分单机/联机
├─────────────────────────────────────────────┤
│ 新增：IMatchFeed（对局数据源抽象）           │  ← 唯一新增的核心抽象
│   ├─ LocalMatchFeed  → OrcMatchHost（现有）  │  ← 单机，零改动
│   └─ RemoteMatchFeed → OrcClient + notify/*  │  ← 联机
├─────────────────────────────────────────────┤
│ 单机：OrcEngineHost + OrcMatchSession        │  ← 不动
│ 联机：Orc.Server 进程（房主机器上）          │  ← 新增
└─────────────────────────────────────────────┘
```

### 5.2 `IMatchFeed` 是这次改动的关键

现有 `BattleScreen` 直接吃 `OrcMatchHost.View`（`UiMatchView`）和 `OrcMatchHost.Actions`（`UiBattleActions`），并调用 `Pump()` 每帧推进。

远端模式下：
- `View` 由 `notify/view` 推送，不是每帧读
- `Actions` 在推送载荷里
- 没有 `Pump()`

**所以抽象面应该只有四个**：

```csharp
interface IMatchFeed {
    UiMatchView View { get; }
    UiBattleActions Actions { get; }
    event Action? Changed;                    // 本地 Pump 或远端推送触发
    Task<UiSubmitOutcome> SubmitAsync(UiCommand cmd, CancellationToken ct);
}
```

`UiCommand` 是现成的（`proto/contracts/UiCommand.cs`），**联机与单机共用同一套命令**——这是这次方案能成立的前提，因为引擎的 `play.*` 工具与 `UiCommand` 语义同构。

> 实现时要逐个核对 12 个 `UiCommand` 与 `play.*` 工具的映射（`PlayCard`→`play.unit/join`、`EndTurn`→`match.end_turn`、`ChooseTarget`→`target.submit` 等）。映射不上的命令，在远端模式下应显式拒绝而不是静默丢弃。

### 5.3 单机路径（零改动）

`LocalMatchFeed` 直接转发现有的 `OrcMatchHost`，`Pump()` 仍在 `_Process` 里调用。**Beta.4 的行为逐字节不变。**

### 5.4 联机路径

1. 房主：`ServerProcess.StartAsync` 拉起本地 `Orc.Server`（`--packs <卡包目录> --port 0`）
2. 房主：`OrcClient.ConnectAsync`（回环）→ `initialize` → `room.claim{seat:0, kind:"local"}` → `room.start`
3. 客户端：通过组播发现房主的 Radmin IP → `OrcClient.ConnectAsync(房主IP, 端口)` → `initialize` → `room.claim{seat:1, deck:[...]}`（需要房主发的 `seatToken` 或邀请凭据）
4. 双方：`notify/view` 推送到就刷界面；输入经 `tools/call` 下发

**`room.start` 前的座位面板**是新增 UI：两个座椅、控制器类型、连接状态、房主可"添加 AI / 踢出"。

> 注意：`room.claim` 的 `kind:"local"` **只接受回环来源**——所以远端的房主必须是真正的本机 `local`，外部玩家只能是 `remote`。这与"房主拖 AI 陪练"的设计吻合。

---

## 6. 分阶段实施

### M0 · 只读评估（**✅ 已完成，2026-10-21 22:26**）

探针：`artifacts/net-probe/probe_server.py`（Python 标准库，不引第三方，不改任何项目代码）。

实测结果（`card-pack-demo` 卡包）：

| 验收项 | 结果 |
|---|---|
| 服务端构建 | ✅ 0 error（3 个既有 nullable 警告） |
| 进程启动 | ✅ stdout 首行 `{"ready":true,"port":3581}` |
| `initialize` | ✅ `protocolVersion "1.0"`、`schemaVersion 1`、`capabilities ["room","push","targeting"]` |
| `tools/list` | ✅ **31 个工具**（清单见 §3.1.2） |
| `room.info` | ✅ `state:"lobby"`、两个空座位（`controllerKind:""`、`connected:false`）、`packPolicy:"loaded"`、`packFingerprint:"db53ff711f611ba3"` |

**两个重要发现：**

1. **协议细节**：`RpcRequest.Id` 是 **string**，不是整数。发整数会让反序列化失败、服务端回 `id=null` 的错误，客户端等不到匹配 id 而超时。
2. 🔴 **`room.info.addresses` 自动列出了 Radmin 虚拟 IP**（`26.109.28.81`），
   同时还列出了 `198.18.0.1`、`192.168.0.104` 等共 7 个地址。
   **引擎自己就会枚举所有网卡，Radmin 打通网络后无需任何额外配置**——这把"网络层"的成本进一步压低了。

**结论**：路线 C 的前提成立，`Orc.Server` 在本机可直接运行。

### M1 · 抽象数据源（2–3 天）

- 引入 `IMatchFeed`，把 `BattleScreen` 对 `OrcMatchHost` 的直接依赖收进 `LocalMatchFeed`
- **目标：行为零变化**，全部现有 verify 照旧通过
- 这是纯重构，风险在主战场（`BattleScreen` 改错会伤到发版功能），所以必须逐项跑 `--verify-ui`

### M2 · 联机最小闭环（3–4 天）

- `RemoteMatchFeed` + 开房面板（端口/地址/复制）
- 目标：两台机器经 Radmin 打完一整局
- 验收：终局面板以 `view.outcome` 为准；断线有遮罩；重连能恢复

### M3 · 席位与匹配（2–3 天）

- 座位面板（控制器类型/连接状态/踢人/加 AI）
- 组播找房（复用昨天定的 `239.255.42.99:27016` 方案，只把"连上"从 ENet 换成 `OrcClient`）
- 验收：双方各自点"快速匹配"，3 秒内配对

### M4 · 收尾（按需）

- 反制的解除提示（§2.2）
- 观战（`observe.view` 未绑定会话即可放行，天然支持）
- 断线重连打磨

---

## 7. 逐文件改动清单（M1）

| 文件 | 改动 | 风险 |
|---|---|---|
| 新增 `proto/net/IMatchFeed.cs` | 四成员接口 | 低 |
| 新增 `proto/net/LocalMatchFeed.cs` | 包 `OrcMatchHost`，`Pump` 转发 `Changed` | 低 |
| `BattleScreen.cs` | 把 `_host` 换成 `IMatchFeed`（成员很多，需逐个替换） | **中** |
| `BattleScreen.Presentation.cs` | 同上 | **中** |
| `Main.cs` | 建局后注入 `LocalMatchFeed` | 低 |
| 新增 `proto/net/RemoteMatchFeed.cs` | M2 才写 | — |
| `Kards.Ui.OrcBridge.csproj` | M2 加 `Orc.Client` 引用 | 低 |
| `proto/net/ServerLauncher.cs` | M2 包 `ServerProcess` | 低 |

---

## 8. 风险登记

| # | 风险 | 等级 | 缓解 |
|---|---|---|---|
| 1 | `IMatchFeed` 抽象泄漏（远端提供不了本地能提供的东西，如 `Index` 的 `Card` 字典） | **高** | M1 阶段就显式列出"远端不可用"成员，宁可提前报错也不要静默降级 |
| 2 | `BattleScreen` 改动面积大，可能伤到已发布功能 | **高** | M1 全程用 `--verify-ui` 逐项验证；单机路径行为必须逐字节不变 |
| 3 | 服务端进程拉不起来（缺 .NET 运行时、卡包路径不对） | 中 | M0 先用探针验证；`ServerProcess` 已内置就绪行解析与 `--parent-pid` 兜底 |
| 4 | 卡包数据不满足 v2 manifest / sidecar / lock | 中 | 先跑 `AssetMigrationTool.Plan` 看 `IsFullySkipped` |
| 5 | 引擎再更新时单机侧仍需手工适配 | 中 | 这是路线 C 的固有成本，接受；把适配点集中在 `OrcMatchSession`／`OrcMatchHost` 两个文件 |
| 6 | 45 秒心跳判死导致"挂机被踢" | 低 | `OrcClient` 已内置 30 秒 ping |
| 7 | 房主防火墙弹窗导致外部连不上 | 低 | 开房面板给出提示（指南 §2.3 第 7 条） |

---

## 9. 验收标准

### M0
1. 本地服务端进程能起来，stdout 首行输出 `{"ready":true,"port":N}`
2. `room.info` 能返回座位结构

### M1
1. **全部现有 verify 照旧通过**（`--verify-ui`、243 单测、`--verify-export`）
2. 单机对局行为与 Beta.4 **逐字节一致**（同 seed 同操作，比对 `UiMatchView`）

### M2
1. 两台机器经 Radmin 打完一整局，双方 `view.outcome` 一致
2. 拔网线后 5 秒内出现断线遮罩；重连后 `observe.view` 拉回全量且状态正确
3. 终局面板不依赖 `resultTitle` 文本
4. `unauthorized` 不会出现在正常操作路径上

### M3
1. 座位面板能正确显示 `local`/`remote`/`ai` 三种控制器
2. 房主"添加 AI"后 AI 在服务端驱动，不经过协议
3. 组播 3 秒内发现房间

---

## 10. 待决策项

| # | 问题 | 选项 | 建议 |
|---|---|---|---|
| 1 | 联机是否要求所有玩家装 Radmin | 是／否 | **是**（否则要自建中继，等于搭服务器，违背前提） |
| 2 | 单机是否也改用本地服务端 | 是／否 | **否**（M1 的零回归承诺；将来可再评估） |
| 3 | DIY 卡包是否迁移到 `AssetEditor` | 是／否／暂缓 | **暂缓**，但导出格式先合规 |
| 4 | 是否接入 MCP / 沙盒 | 是／否 | **只做工具链调研**，不进游戏 |
| 5 | 反制解除提示放哪个版本 | Beta.5 | Beta.5 |

---

## 11. 参考

- 前端适配（基于 `6d72eac`，那些 P0/P1 我们已全部完成）：`OrC-KSD/docs/前端适配-后端变更-20261020.md`
- 客户端接入指南：`docs/多人支持-服务端客户端分离/客户端接入指南.md`
- 反制实现计划：`docs/反制效果装配/implementation.md`
- 无头沙盒：`docs/无头沙盒-测试环境/开发指南.md`
- 反制语义验收：`tests/Orc.Game.Tests/CounterArmingWindowTests.cs`
- 昨天的联机方案（网络层仍有效）：`docs/design/MULTIPLAYER_LOCKSTEP_20261021.md`
