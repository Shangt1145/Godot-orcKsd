# 新架构调研：编排实体化 ＋ 卡牌 V4 组件化

- **调研对象**：`hitman-OLD6/OrC-KSD` → `origin/main` = `8a5dd8f`（2026-10-28 19:54）
- **调研时间**：2026-10-28
- **结论状态**：已核对代码与测试，未做任何迁移改动

---

## 0. 结论先行

1. **新架构是真的，不是设计稿。** 我在独立 worktree 里跑 `dotnet test OrcEngine.sln`：**2116 通过 / 0 失败 / 0 跳过**（`artifacts/probe-8a5dd8f-test.log`）。
2. **但远端这条线不含我们本地的 5 个提交。** `origin/main` 是从 `a0b3511` 重新分叉的，`97e4011`（卡包传输、内置服务、移动端脚本引用）等**都不在里面**。也就是说：新架构和"我们一直在用的那条线"目前是两个分叉，不是一次升级。
3. **UI 桥接层对新引擎有 5 条声明期编译错**（3 个文件）。修完这 5 条**还会再露一批**——方法体里对 `Trigger<>`／`Engine.Bus` 的引用被编译器的级联抑制挡掉了，但这两个类型在新引擎里确实不存在。
4. **真正的坏消息不是编译错，是 `Authoring*` 整层被删。** UI 的 DIY 代码生成器**直接往生成的 csx 里写字面量** `Orc.Game.Effects.AuthoringTargetContext.SelectAsync(...)` 和 `Orc.Game.Effects.AuthoringAuraRuntime.Validate(...)`。这两个类型在新引擎里**不存在**——于是**每一张 UI 编译出来的 DIY 卡，脚本都编译不过**。
   细分（切到 v4 后重测）：**选靶**有替代（`EffectRuntime.SelectAsync` ＋ 公开的 `EffectSelector`），改代码生成器的目标调用即可；**持续效果（光环）**没有替代，要按 v4 的 `wraps`/`mounts` 重写。
5. **资产格式 v4-only。** `effects/*.prefab.json` 会被**整包拒装**（`CardPackAssembler.cs:53`），而 UI 的工坊正是往这个目录写（`NativeWorkshop.Effects.cs:45`）。

一句话：**这是一次换地基，不是一次升级。** UI 侧受影响最重的不是编译错误，而是 DIY 编排这条产品线要重新落到新模型上。

---

## 1. 我看了什么、怎么验的

| 项 | 事实 | 怎么复跑 |
| --- | --- | --- |
| 提交数 | `a0b3511..origin/main` 共 **3** 个提交 | `git log --oneline a0b3511..origin/main` |
| 改动规模 | **478 文件，+32630 / −29942** | `git diff --stat a0b3511 origin/main \| tail -3` |
| 三条提交 | ①`0ba6ad3` 编排实体化＋卡牌 V4＋测试基建＋示例卡包 ②`3993e99` 文档同步 ③`8a5dd8f` 合并远程 main | `git show --stat <sha>` |
| 引擎测试 | **2116 / 0 失败 / 0 跳过**（Game 1113、Packages 371、Orc.Tests 324、Server 118、Mcp 118、Script 39、Manager 20、Ai 13） | 建 worktree 到该提交 → `dotnet test OrcEngine.sln` |
| UI 桥接编译 | **失败，5 条 error** | `dotnet build proto/bridge/Kards.Ui.OrcBridge/Kards.Ui.OrcBridge.csproj -p:OrcEngineRoot=<新引擎>/src/Orc.Game` |
| 基线对照 | 同一命令打旧引擎（本地 HEAD `4c238db`）**成功** | 同上，去掉 `-p:OrcEngineRoot` |

> 注意：我第一次探测时把 worktree 放在 `H:/g/_probe-8a5dd8f`，那里没有 `src/Orc.Manager/bin/.../Orc.Manager.dll`，导致 `Orc.Manager` 相关 3 条错误是**我的探测环境造成的假阳性**。已排除。上表 5 条错误里仍有 3 条属于这一类，见 §4.1。

---

## 2. 新架构在讲什么（按代码核对后的说法）

### 2.1 四类实体 + 预制体＝类

运行时对象统一为**实体**：引擎实体（对局级单例）、卡牌实体、组件实体、handler 实体。
实体模板＝**预制体**（纯文本的"类"）：可 `extends` 全继承、可整段覆写成员、成员带**可见性位标志**（`callable`/`mountable`/`hostable`，默认全关，子只能收紧）。

代码落点：`src/Orc/Core/Model/`（`Prefabs.cs` 528 行、`Members.cs` 251、`Mounting.cs` 520、`OrchestrationRuntime.cs` 649、`ComponentAssembly.cs` 444、`ViewCarrier.cs` 186…），约 3.3k 行新内核。

### 2.2 成员＝有序边集合，三条通道

一个成员的执行序固定为：**自有边 → 外来挂载边（声明序）→ 框架边**。不存在独立于边集合的 "core"。
三条通道职责互斥，装载期强制：

| 通道 | 谁用 | 表达什么 | 混用后果 |
| --- | --- | --- | --- |
| **挂载** `mounts[]` | 设计端（编排） | 这个流程里有哪些步骤、什么次序 | 挂载里带 `phase` ＝装载期拒绝 |
| **包装** `wraps[]` | 元效果端（减伤/免疫/反制） | 改写或拦截别人的结算（before/after/replace） | 包装里发起新步骤＝拒绝 |
| **框架边** | 框架 | 注销兜底 `framework:disposal`、一次性 `framework:one-shot` | 作者不能写 |

`band`（`Rewrite`/`Veto`/`Apply`/`Commit`）与"接缝（seam）"**全部退役**。

### 2.3 信号＝引擎实体的成员，`hooks` 退役

原来的 `hooks: ["card.played"]` 变成挂到 `engine#card.played` 这个成员上——**订阅就是挂载**，不再是独立通道。
寻址语法 `作用域#成员名`：第一个 `#` 之前是作用域（`engine`/`host`/`self`），之后整体是成员名，**成员名允许含 `.`**（`unit.position.changed` 零转换）。

### 2.4 视图＝字段清单，csx 统一签名

不再为每种数据形状写 C# 视图类。成员里声明 `view.fields[]`（类型限于 实体/数字/字符串/布尔 及其数组，不支持嵌套），运行时由**唯一一个承载视图类型**按名投影。
csx 入口签名统一为：

```csharp
Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => { … };
```

双闸门：装载期用 **Roslyn 语法树**做静态检查（`view.<未声明>`／写只读字段＝`binding` 失败）＋ 运行期 setter 兜底。
私有字段只能走 `owner.<Name>`；`view.<私有字段>` 直接报错。

### 2.5 选择：targeter 并入判定器，random 归引擎且**生产对局禁用**

- 确定性选择（`all`/`self`）→ 判定器，不产生 targeter。
- `random` → 引擎随机服务（种子可复现），**不产生 targeter**，且 `MatchOptions.AllowRandomTargeting` 默认 `false`；生产对局执行到它会**明确抛错**（不静默取首个）。
- `choose`/`one` → 必须经**提交通道**由前端（人类 UI／MCP／AI）裁定；没前端就显式挂起/失败。

### 2.6 失败面：装载即验证，卡包原子

csx 在**装载期全量预编译**（不保留懒编译）；失败＝拒绝装载，不再"隔离记录后继续"。
卡包**原子**：任一资产失败＝整包零注册，报告列全部失败项。包分**必需**（失败＝不开局）与**可选**（失败＝跳过并在报告/审查面可见）。

### 2.7 旧 → 新 术语对照（作者视角）

| 旧 | 新 |
| --- | --- |
| `hooks: ["unit.deployed"]` | `mounts[] → engine#unit.deployed` |
| `injects[].target: "伤害处理触发器"`（显示名） | `wraps[] → engine#damage-apply` |
| `band: Rewrite` / `Commit` / 无 band | `wraps + phase: before` / `after` / 外来挂载边 |
| `mainTrigger` + 事件表 | 组件成员 + 边集合 |
| `kind: passive/active` | 可见性位标志 |
| `viewType: "…DamageApplyView"` | `members[].view.fields[]` |
| `disposingTrigger` | 框架注销边（作者不能写） |
| `oneShot` | `one-shot: true`（框架承载） |

---

## 3. 文档说的 vs 代码做的（逐条核）

| # | 文档声明 | 代码事实 | 判定 |
| --- | --- | --- | --- |
| 1 | S13：`Trigger<TView>`／`Effect`／`Bus`／`DynamicEffect` 全部删除，不留 Obsolete 壳 | 这四个文件确实是 `D`（`git diff --name-status`）；全树 `grep "class Trigger"` 只剩 `TriggerNode`/`TriggerRoles`/`TriggerDto`/`TriggerPrefab`；无任何 `Obsolete` 兼容层 | ✅ 成立 |
| 2 | S13：三层预制体 `EventPrefab`/`TriggerPrefab`/`EffectPrefab` 退役 | 这三个类**还在**，在 `src/Orc/Cards/EffectSnapshot.cs`——注释写明：只作**旧快照→v4 的迁移输入**，运行期走 `LegacySnapshotComponentBinder` | ⚠️ 表述不准，实际是"降级为迁移输入" |
| 3 | `docs/卡牌DIY开发文档/09` §状态："**既有卡包不必迁移**——旧写法继续有效，走翻译路径执行" | `CardPackAssembler.cs:53-56`：包里有旧 `effects/` 目录＝**failure**，"旧格式 effects/ 目录已不再装载（v4-only）"；`McpService.MatchLaunch.cs:177` 同样硬失败 | ❌ **文档错**（文件级旧包装不上；只有**内存里注册的旧快照/内联效果**才走翻译路径） |
| 4 | 内联效果（`effects.inline`）仍可装载 | `CardBase.MountDeclaredComponent` → `_engine.Prefabs.TryGetPrefab` → `LegacySnapshotComponentBinder.Register`（内部先把快照序列化回旧 JSON，再跑 `AssetSchemaMigrator.Migrate`，含 csx 头重写） | ✅ 成立，但桥是 `internal`，UI 调不到 |
| 5 | R2c：`random` 归引擎，生产对局禁止 | `MatchOptions.AllowRandomTargeting` 是无初始化的 `bool`（默认 false）；MCP 只在 `Authoring` 模式置 true | ✅ 成立——**这是玩家可见的行为变更** |
| 6 | 审查导出 `schemaVersion` 升 2 | 未逐字段核对 | ⏸ 未验 |

第 3 条值得单独说：**引擎自己的两份文档互相矛盾**。DIY 文档说旧包能用，`CardDistributionValidator.cs:188` 的注释说"旧格式 `*.prefab.json` 不走任何一条通道（v4-only 装载链不读它）——留在目录里只是死文件"。代码站在后者一边。

---

## 4. 对 UI 仓库的影响

### 4.1 编译面（声明期 5 条错，3 个文件）

| 文件 | 缺的符号 | 性质 |
| --- | --- | --- |
| `DesktopTargetContext.cs:16` | `Orc.Game.Effects.AuthoringTargetContext` | **整类被删** |
| `SourceEffectAssembly.cs:225` | `ActiveEffect<>` | 旧效果基类，按 S13 退役 |
| `ServerLauncher.cs:2/10/22` | `Orc.Manager` / `ServerManager` | ⚠️ **假阳性**：我的探测 worktree 里没有 `Orc.Manager.dll`（`Reference` 用的是 `bin/Debug/net8.0` 的固定路径）。正常 checkout 不会这样 |

**还有第二层没露出来**：`SourceEffectAssembly.cs:55/69/217` 用了 `new Trigger<CardEventView>(…)`、`match.Engine.Bus.Mount(…)`、`Bus.UnmountOwner(…)`，`OrcMatchSession.Debug.cs:28` 用了 `DynamicPassiveEffect` 与 `MainTrigger.Hooks`。这些类型在新引擎里都不存在，但编译器在声明期就报错后**没有继续绑定方法体**，所以没出现在列表里。**修完这 5 条一定还会再露一批**——这一点我没亲手跑到第二层，属于推断（依据：符号确实不存在）。

按引用面统计，**至少 7 个桥接文件 + 2 个测试文件**会碰到被删 API：

| 被删 API | 引用文件 |
| --- | --- |
| `AuthoringRuntime` | `DesktopEffectRuntime.cs`、`DesktopNativeCodegen.cs`、`OrcMatchSession.Debug.cs`（＋2 测试） |
| `AuthoringTargetContext` | `DesktopTargetContext.cs`、`DesktopNativeCodegen.cs`、`OrcStructuredGraph.cs` |
| `AuthoringAuraRuntime` | `DesktopNativeCodegen.cs` |
| `Trigger<>` / `Bus` / `ActiveEffect<>` / `DynamicPassiveEffect` | `SourceEffectAssembly.cs`、`OrcMatchSession.Debug.cs` |

### 4.2 能力面：`Authoring*` 整层消失 ← **最重的一条**

新引擎 `src/Orc.Game/Effects/` 只剩 3 个文件，`AuthoringRuntime.cs`、`AuthoringAuraRuntime.cs`、`AuthoringTargetContext.cs`、`EffectDynamicResults.cs`、`EffectRuntime.DynamicEffects.cs` 全部删除，**没有同名替代**。

而 UI 的 DIY 代码生成器是**把这两个类型当字面量写进生成的 csx**：

```csharp
// DesktopNativeCodegen.cs:32
$"var {local}=await Orc.Game.Effects.AuthoringTargetContext.SelectAsync(orcSelf,{Literal(target.GetRawText())},{Literal(DesktopTargetContext.Scope(graph))},ct);"

// DesktopNativeCodegen.cs:36-38（持续效果分支）
Orc.Game.Effects.AuthoringAuraRuntime.Validate(normalized);
return $"if(self is {Card} orcAuraHost) await Orc.Game.Effects.AuthoringAuraRuntime.RegisterAsync(orcAuraHost,view,{Literal(normalized.GetRawText())},ct);";
```

后果链条很硬：

1. UI 编译出的每张 DIY 卡，其 csx 都引用不存在的类型；
2. 新引擎装载期**全量预编译** csx，编译不过＝**拒绝装载**；
3. 新引擎把这种失败判为**卡牌加载失败**（`CardBase.cs` 里 `LegacyScriptUnsupportedException` 分支，注释原话："卡的能力已经不可信 ⇒ 判卡牌加载失败"）；
4. 若卡包声明为必需，就是**整局不开**。

也就是说：**DIY 编排（图形搭建 → 原生动作 csx）在新架构上跑不了**——它依赖的"编排运行期"本身被新模型取代了。新模型里对应能力是 `OrchestrationRuntime`（`CreateEntity`／`Bind`／`MountAtRuntime`／`RegisterMember`／`RunMemberAsync`）＋ v4 组件的 `mounts`/`wraps` 声明。

> **2026-10-28 补充核对（切到 v4 后重测，修正上一版的过度悲观）**：
> 逐个查了被删能力有没有替代，结论是**分两半**——
>
> | 被删的 | 新引擎里有没有替代 | 依据 |
> | --- | --- | --- |
> | `AuthoringTargetContext.SelectAsync`（选靶） | **有**：`EffectRuntime.SelectAsync(Card, EffectSelector, ct)` 仍在，`EffectSelector` 是公开 record（`Sel/Side/Zone/UnitType/Keyword/Count/Threshold/IncludeCovert`），可直接从声明构造 | `EffectRuntime.cs:790`、`:22` |
> | `AuthoringTargetContext.ValidateTarget` / `Scope` | **可搬**：`Scope` 是纯 SHA256；`ValidateTarget` 只校验 JSON 形状 | 被删文件 194 行，公开面已核 |
> | `AuthoringTargetContext.ResolveCandidates` / `HasInteractivePlayEffect` | 无同名；选靶走上面那条即可覆盖 | `git grep` 零命中 |
> | `AuthoringAuraRuntime.RegisterAsync`（持续效果） | **没有**。新模型的对应物是 v4 组件的 `wraps`/`mounts` 声明，是**声明式**的，不是运行期注册 | 全树无等价 API |
>
> 所以 DIY 的工作量分布是：**选靶＝改代码生成器的目标调用（机械）**，**持续效果（光环）＝按 v4 重写（真工作量）**。
> 我在第一版里写的"整条线都要重写"过头了，以本表为准。

### 4.3 资产面：`effects/` 目录会被整包拒装

- UI 写：`NativeWorkshop.Effects.cs:45` → `"effects/" + id + ".prefab.json"`，内容 `schemaVersion: 3` + `mainTrigger`/`hooks`/`viewType`。
- 新引擎读：`CardPackAssembler` **只装** `effects-v4/*.component.json`；遇到旧 `effects/` 目录直接 `failures.Add(...)`。

所以**工坊导出的卡包在新引擎上装不进去**。迁移工具有现成的：`AssetSchemaMigrator.MigratePackToDisk(packDir)`（产物写到 `effects-v4/`，旧目录不删），且 `Migrate(json)` 是公开静态方法——这是**机械可转**的一条。

### 4.4 不受影响的部分

| 层 | 结论 |
| --- | --- |
| 卡数据 `cards/*.card.json`（schemaVersion 1） | **零改动**，仍是 `components[].effects.prefabs: [id]` |
| `Orc.UI.Contracts`（12 文件） | **零改动** |
| 协议 / 客户端 | 相对 `a0b3511` 未动 |
| 服务端 / 管理器 | 相对 `a0b3511` 未动；但**相对本地 HEAD 少了 400 行**（卡包传输、网络词汇表），见 §5 |
| `--parent-pid` 子进程看护 | 仍在（`ManagedInstance.cs` ＋ `ParentWatchdog.cs`），P8 不受影响 |

---

## 5. 分叉：远端没有我们那 5 个提交

```
a0b3511 ──┬── 4e7b2a2 → 2806e04 → 97e4011 → 025d44e → 4c238db   （本地 HEAD，未推）
          └── 0ba6ad3 → 3993e99 → 8a5dd8f                        （origin/main）
```

`git merge-base --is-ancestor 4c238db origin/main` → **false**。
`git log origin/main..HEAD` → 5 个本地提交，其中：

- `97e4011 feat(engine): 卡包传输、内置服务与移动端脚本引用` — UI 的**工坊联机同步**依赖它（`NetworkPackageArchive`、`ServerHost.PackTransfer`）；
- `025d44e` / `4c238db` — 跨效果回指、部署指向候选池两处效果修复。

后果：**切到 `origin/main` 等于丢掉卡包传输**（`Orc.Protocol/NetworkPackageArchive.cs` 与 `ServerHost.PackTransfer.cs` 在新线上不存在，我 grep 过整棵树）。UI 的 `RemoteMatchFeed.Packs.cs`、`NetworkWorkshopSession.cs` 会连带编译失败/功能缺失。

这一点必须由你确认：是**新架构把卡包传输重写掉了**（那我该去找替代实现），还是**那次 push 漏了这条线**（那应该先把两边合起来）。

> **2026-10-28 决策**：用户裁定「**一切以新引擎为准，本地的不管**」。
> 因此本条按**路线 B** 执行：本地 5 个提交不再合并，卡包传输功能视为**已放弃**（`Orc.Protocol/NetworkPackageArchive` 与
> `ServerHost.PackTransfer` 在新线上不存在）。UI 的 `RemoteMatchFeed.Packs.cs`、`NetworkWorkshopSession.cs` 里依赖它的部分
> 随之作废——迁移时要显式摘掉，不能留"编译能过但运行时没人应答"的半截路径。

---

## 6. 三条路线

| 路线 | 做什么 | 代价 | 风险 |
| --- | --- | --- | --- |
| **A. 先合再迁**（推荐） | 把本地 5 个提交合进新架构，解决"卡包传输 vs 新编排"的冲突，再迁 UI | 一次真实的三方合并，冲突集中在 `Orc.Server`/`Orc.Protocol`（结构层）与 `Orc.Game/Cards`（编排层） | 合并期可能两头都不绿 |
| **B. 直接切新架构** | UI 放弃本地线，迁到 `origin/main` | 卡包传输要重做或放弃；DIY 编排要重写代码生成目标 | 工坊联机功能回退，玩家可见 |
| **C. 不动** | 继续钉在 `4c238db` | 0 | 与上游越走越远，将来合并成本只增不减 |

**我倾向 A**，理由不是"合并更优雅"，而是：B 会**静默丢掉一个已经验证过的功能**（P7 联机收尾那一批），而且丢的方式很难被发现——编译能过、单机能跑，只有联机传包时才炸。

---

## 7. 迁移清单（若走 A/B，按此顺序）

### 第一批：资产格式（不迁这个，其他都白搭）

1. `NativeWorkshop.Effects.cs`：`effects/<id>.prefab.json`（v3）→ `effects-v4/<id>.component.json`（v4）。
   - 现成转换：`AssetSchemaMigrator.Migrate(json)`；整包：`MigratePackToDisk(packDir)`。
   - **怎么验**：造一个含旧 `effects/` 的包 → 装载 → 必须报"旧格式不再装载"；跑完 `MigratePackToDisk` → 再装载 → 进对局能看到效果。
2. `DiyCardCompiler` 的内联快照（`components[].effects.inline`）：**短期可留**（走 legacy 桥），但 legacy 桥是 `internal`，且新 csx 必须能在 `dynamic` 承载视图下编译——**旧 csx 头 `Func<DamageApplyView,…>` 会被 `RewriteScriptHeader` 改写**，改写后正文里若还写 `view.Amount` 而字段没在 `view.fields[]` 里声明，就是 `binding` 失败。
   - **怎么验**：拿现有 DIY 卡逐张装载，统计 `binding`/`compile` 失败率。这一条我**没有跑**，是最大的未知数。

### 第二批：DIY 代码生成器（真正的重写）

3. `DesktopNativeCodegen`：生成目标从"调 `AuthoringTargetContext`/`AuthoringAuraRuntime` 的原生动作 csx"改为"v4 组件声明 + 走提交通道的 csx"。
   - 选靶：`AuthoringTargetContext.SelectAsync` → targeter 提交通道（前端应答）。
   - 持续效果：`AuthoringAuraRuntime.RegisterAsync` → `wraps`/`mounts` 声明。
   - **怎么验**：同一张 DIY 卡，迁移前后在同样局面下产生同样的状态变化（对照 `MigratedAssetEquivalenceTests` 的做法）。

### 第三批：编译面收尾

4. `DesktopTargetContext` / `DesktopEffectRuntime` / `OrcStructuredGraph`：删掉对 `Authoring*` 的转发，改到新 API。
5. `SourceEffectAssembly`：`Trigger<>` + `Bus.Mount` 的 12 张"源码效果"改为 v4 组件注册（`CardEffectRegistry.DeclarePrefab` / `OrchestrationRuntime.RegisterMember`）。
6. `OrcMatchSession.Debug.cs`：`DynamicPassiveEffect.Snapshot.Root.MainTrigger.Hooks` 改读审查导出的边集合（新模型里 `hooks` 这个概念没了）。

### 第四批：行为对齐

7. **`MatchOptions.AllowRandomTargeting`**：UI 建对局时**没有设置它**（我 grep 过，`proto/bridge` 里零命中）。迁到新引擎后，任何 `sel=random` 的 DIY 卡会在对局中**抛错**。要么 UI 显式开启，要么把这类卡改成确定性选靶。
   - **怎么验**：用含 `sel=random` 的卡（示例里的「齐射」就是）开一局，看是抛错还是正常结算。

---

## 8. 风险与未验证项（写清楚，别当成已验）

| 项 | 状态 |
| --- | --- |
| 引擎单元/集成测试 | ✅ 我跑过，2116 绿 |
| **引擎能不能真的开一局、打到结束** | ❌ **没跑**。测试绿 ≠ 玩家视角能走通 |
| 第二层编译错误全貌 | ❌ 推断，未跑到（级联抑制） |
| 现有 DIY 卡在 v4 下的实际失败率 | ❌ 没测，可能是最大的坑 |
| 存档/卡组兼容 | ❌ 未验（卡数据 schema 没变，理论上无影响） |
| 审查导出 schema 升 2 的字段变化 | ❌ 未逐字段核对 |
| 引擎文档自相矛盾（§3 第 3 条） | ⚠️ 已确认矛盾，**以代码为准** |

---

## 9. 建议的下一步

1. **已按"一切以新引擎为准"执行**：引擎工作区已切到 `origin/main`（分支 `v4`），旧线保留为分支 `legacy-main`。
   回退一条命令：`git -C "H:/Working Folder/OrC-KSD-github" checkout legacy-main`。
2. 现状：**UI 桥接层对新引擎是红的**（2 条真实编译错），这是迁移的起点，不是意外。
3. 建议顺序：先把 6 个受阻文件按"选靶换 API / 光环按 v4 重写"处理到能编译，再动资产格式，
   最后补 `AllowRandomTargeting`。
4. 在那之前，UI 仓库**不能发版**——alpha.6 若还要出，必须基于 `legacy-main`。

---

## 附：本文用到的命令

```bash
# 远端差异
git -C "H:/Working Folder/OrC-KSD-github" fetch origin
git -C "H:/Working Folder/OrC-KSD-github" log --oneline a0b3511..origin/main
git -C "H:/Working Folder/OrC-KSD-github" diff --stat a0b3511 origin/main

# 引擎测试（独立 worktree，不动本地工作区）
git -C "H:/Working Folder/OrC-KSD-github" worktree add "H:/g/_probe-8a5dd8f" 8a5dd8f --detach
cd "H:/g/_probe-8a5dd8f" && dotnet test OrcEngine.sln --nologo

# UI 桥接对新引擎（会失败，这就是结论）
dotnet build proto/bridge/Kards.Ui.OrcBridge/Kards.Ui.OrcBridge.csproj \
  -p:OrcEngineRoot=H:/g/_probe-8a5dd8f/src/Orc.Game

# 分叉确认
git -C "H:/Working Folder/OrC-KSD-github" merge-base --is-ancestor 4c238db origin/main; echo $?
```
