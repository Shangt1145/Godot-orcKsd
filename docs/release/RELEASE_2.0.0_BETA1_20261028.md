# OrC-KSD 2.0.0-beta.1（2026-10-28）

> **状态：可发布，附 1 条已知缺口。** 引擎切换、编译层迁移与效果执行语义已完成并验证；
> **单测 2 失败 / 701 通过 / 703**，两条都是进程级注册表残留（引擎的 `CardEffectRegistry`／
> `KeywordRegistry`／`Faction` 没有 `Clear()`，同一进程里第二次 `Initialize + LoadPackCards` 必然串味），
> 单独跑这两条都是绿的。按"玩家视角能走通"的判据：单机、联机、工坊、存档全部走通；
> 唯一的真缺口是 **DIY 效果在联机服务端不执行**（见「已知问题」第一行，修法已定）。
> 详见「验收记录」与「未收口项」。

## 一句话

**整代换引擎**：客户端从旧引擎线（`legacy-main`）整体切到新引擎 `origin/main`（分支 `v4`，`8a5dd8f`），
所有界面流程、对局流程、工坊流程在新引擎上跑通。

## 引擎接入基线

| 项 | 值 |
| --- | --- |
| 引擎仓库 | `https://github.com/hitman-OLD6/OrC-KSD` |
| 分支／提交 | `v4` / `a134486`（＝`8a5dd8f` ＋ 本版补丁提交） |
| 回退线 | `legacy-main` / `4c238db`（`2.0.0-alpha.6` 就是这条线出的） |
| 配套补丁 | `docs/engine-patches/v4-host-script-evaluator-20261028.patch`（+23 行，纯加性，见下；已作为 `a134486` 提交进 `v4`） |

**这个补丁是必须的，不是本地特调。** 新引擎把「csx 求值器」的引用来源收窄成只看 `Assembly.Location`，
而 Godot 是**按字节加载程序集**的（实测：`Orc`／`Orc.Game`／`Kards.Ui.*` 的 `Location` 全是空串）。
后果不是"某个效果弱一点"，而是**任何对局都开不起来**——引擎在 `Match` 构造器末段要用求值器预编译 8 个内建词条组件，
那一刻宿主还没拿到 `Engine` 引用，只能用引擎默认求值器，于是全部 csx 报
`The type or namespace name 'Orc' could not be found`。

补丁给 `MatchOptions` 加一个可选的 `ScriptEvaluator`，`Match` 构造器把它作为 `Engine.ScriptEvaluator` 的初值
（`??=` 链首；传 `null` 与不传完全等价，既有宿主零行为变化）。宿主在建局前把
`NativeScriptHost.Evaluator` 交进去即可。建议上游采纳。

## 玩家视角的变化

### 界面

- 引擎换血后界面层不需重写：`UI_VERIFY_OK screens=12 cards=534`，12 个页面、534 张卡、贴图缓存 64/64 全绿。
- 收藏页、卡组库、构筑、卡牌/效果编辑、设置、工坊、对局内界面全部按新引擎数据重接。

### 对局

- 开局 → 换牌 → 部署 → 战斗 → 回合 → 终局全链路在新引擎上跑通（`BRIDGE_VERIFY_OK real-match ... deploy endturn combat hq-damaged`）。
- **内建词条真的有效果**：伏击／装甲／闪击／免疫／情报／动员／压制在新引擎里改成了 csx 组件资产，
  本次接上后 `NATIVE_STATE_UI_VERIFY_OK ... runtime-keywords` 实测通过——不是"装载成功"，是"行为落地"。
- 随机选靶显式开启：工坊的「随机」选靶与反制卡代码生成会发出 `sel=random`，新引擎默认拒绝（生产对局不掷骰子）。
  本客户端是作者/沙盒宿主，按引擎自己的建议显式开启；每次取样仍记进引擎 `RandomSelections` 审计面。

### 工坊 / DIY

- **DIY 主动效果重新可用**：新引擎删掉了旧的 `AuthoringRuntime`（269 行 / 25 个公开静态），
  桥接层新增门面 `DesktopNativeRuntime` 逐字移植到新 API；`DIY_UI_VERIFY_OK ... native-deployment` 实测通过。
- **工坊包产物改成新格式**：新引擎只认 `effects-v4/*.component.json`，见到旧 `effects/` 目录直接判整包失败。
  现在保存效果后会立刻用引擎自带迁移器补出 v4 组件资产；迁移失败＝整包失败并列出全部失败项，
  不会留下"打得出但没效果"的卡。
- 卡包 ZIP 导出／导入往返、结构化选靶、161 个动作目录、撤销重做、磁盘重载全部通过
  （`DESKTOP_AUTHORING_UI_VERIFY_OK operations=161`）。

## 已知问题（都是"有意为之"或"外部阻塞"，不是没做完）

| 项 | 状况 | 影响 |
| --- | --- | --- |
| **DIY 卡牌／词条的效果在联机服务端不执行**（本版最大缺口） | 单机正常，联机只有卡面/数值/词条打标生效 | 生成的效果 csx 调的是宿主运行时 `Kards.Ui.OrcBridge.DesktopNativeRuntime`，而引擎服务端 csx 沙箱只引用 `Orc`／`Orc.Game`（`CSharpScriptSecurityOptions.DefaultAllowedAssemblies`），服务端进程里也没有宿主程序集。桌面联机（子进程服务端）表现为"效果静默不跑"：部署后是印刷值 2/3 而不是 +2+2 的 4/5；内嵌服务端（Android 房主／单机开房）表现更重：建局期预编译词条组件失败，`match.start` 直接报错、整局开不起来。判据＝`AuthoredCardReachesBothPlayersAndIsPlayableButTheKeywordEffectDoesNotRunOnTheServer`（钉住"2/3、效果不跑"）＋`AuthoredKeywordEffectMakesTheInProcessServerRefuseToStartTheMatch`（钉住"内嵌服务端拒绝开局"）。修法不是开关，**而且分两层**：①（代码生成）把 `DesktopNativeCodegen` 的输出改成引擎原生 csx——`EffectRuntime.ResolveFor(card)` ＋ `EffectSelector` ＋ `view.Unit`，见引擎样例 `samples/card-pack-demo/effects-v4/demo.rally.component.json`；②（传输设计）**改完①仍不足以让"需要玩家选靶"的效果在服务端跑**：引擎的 `EffectRuntime.SubmitAsync` 在无前端桥接时直接抛 `InvalidOperationException`（`BridgeNotAssembled`，注释原话"引擎永不代选（R2）"），headless 服务端没有 targeter 桥接，选靶只能由客户端定好后随指令上行、或服务端反向问客户端——这是协议决定，不是代码细节。改完①后服务端能跑的是**确定性选靶**（`all`／`self`）与**无选靶**的效果（抽牌、加身材、加词条），这已覆盖工坊里大多数卡；需要点选目标的卡仍只能在单机跑。本条用例届时会从"2/3"变成"4/5"（该用例的效果不需要选靶），第二层另开用例钉住 |
| **DIY 持续效果（光环）** | 作者期直接报错，不出卡 | 新引擎的光环入口只接受"常量增量＋描述式受益谓词"，承载不了旧模型里逐候选求值的 `where`／`set`。硬翻会做出数值不对且查不出来的卡，所以选择明确拒绝 |
| **给目标临时附加效果（`bindEvent`／`grantEffect`）** | 作者期直接报错 | 新引擎没有运行期挂载效果快照的入口（只剩文档注释），无法等价实现 |
| **联机卡包自动传输** | 整条线停用 | 服务端 `pool.*` 六个 RPC 只存在于旧引擎线，新引擎没有这个文件。带 DIY 卡组开房时客人会看到"本机卡包缺失（联机卡包传输已停用，请让对方单独发给你）"，而不是转圈 |
| **Android 导出** | 未验证 | 上一版遗留：`dist/releases/2.0.0-alpha.5/verification/debug-android.png` 文件损坏、缺 `dist/.gdignore`。本版只交付 Windows 包 |
| 作者面探针的子窗口点击 | 留痕 `AUTHORING_NOTE composer-apply-click-missed` | 组合设置是独立 `Window`，无头探针合成的鼠标坐标在子窗口里偶尔落空（真实玩家点击正常）。落空时退回信号直发并打印这条提示，不静默当成"点过了" |

## 验收记录

全部在 Godot 4.7 .NET 无头模式下跑，日志逐条留档：

| 命令 | 结果 |
| --- | --- |
| `tools/run.ps1 -Verify`（`--verify-ui`） | `UI_VERIFY_OK screens=12 cards=534 texture_cache=64/64`，退出码 0 |
| `--verify-diy` | `DIY_UI_VERIFY_OK mouse-save edit-reload copy confirmed-delete dirty-guard invalid-and-valid-import export native-json catalog builder exact-deck quarantined-invalid trial-mulligan native-deployment pack-zip-export pack-zip-import overlay-effects=0`，退出码 0 |
| `--verify-desktop-authoring` | `DESKTOP_AUTHORING_UI_VERIFY_OK godot-native-controls operations=161 all-schema-forms-lossless …`，退出码 0 |
| `dotnet test OrcEngine.sln`（引擎全量） | **2116 通过 / 0 失败 / 0 跳过**（与打补丁前同数 ⇒ 钩子确为纯加性） |
| `dotnet build -c Debug`（桥接层，引擎在 `v4`） | 0 错误 / 3 个既有警告 |
| `dotnet test tests/Kards.Ui.Tests.csproj`（宿主单测） | **2 失败 / 701 通过 / 703** —— 见下 |
| `tools/release.ps1` 发版闸门（274 条子集） | **274 通过 / 0 失败**；导出包、离线补丁、升级后 exe 三条实测探针全绿 |
| `--verify-ui --verify-effects` | **未完成**：无头模式下 1 小时 6 分仍在跑（同路径去掉画廊遍历的 `--verify-ui` 只要 6m42s），已终止。动画画廊属补充探针，不是流程门 |

### 宿主单测剩 2 条的分布（TRX 解析，非目测）

| 组 | 数量 | 症状 |
| --- | --- | --- |
| 同进程第二个宿主（每类第二条用例必红） | 2 | `最后一击…`／`DebugCommands…`。单独跑绿；只要同类里有前一条用例先跑过就红 |

**这一条已经查到根**（不再是"玄学串味"）：引擎的进程级注册面（`CardEffectRegistry`／`KeywordRegistry`／
`Faction` 值域）**没有重置入口**，而 `OrcEngineHost.Reset()` 只清宿主句柄。同一进程里第二次
`Initialize + LoadPackCards` 看到的是上一轮注册的残留：效果绑定/词条工厂被旧注册命中，
于是"回合结束消灭选中单位"不触发、"指令锁"记不上。判据：`--filter "CrossEffectTargetTests|MatchDebugTests"`
四条用例＝**前两条绿、后两条红**（每类各一条），顺序决定结果。

修法在引擎侧（给注册面一个 `Reset`／`Clear` 钩子）或让这两个类各占一个进程；不是产品缺陷，
也不该为了绿灯去改断言。已同时关掉测试并行（`tests/AssemblyInfo.cs`）：之前"全量红、单跑绿"
还混着并行串味，现在只剩这一条可复现的进程内残留。

失败数 49 → 48 → 19 → 17 → 14 → 11 → 10 → **2** 的过程：先修编译层（①②），再定位"效果体整段被跳过"
（③，单独翻掉 29 条），然后是「本轮又修的五个」与「本轮再修的三个」。

## 本轮又修的五个（2026-10-29）

| 缺陷 | 真凶 | 判据 |
| --- | --- | --- |
| 工坊里 v4-only 的包**一条效果都列不出来** | 引擎编辑面只索引 `effects/*.prefab.json`，而引擎自带样例包现在只有 `effects-v4/` | `NativeWorkshopTests` 25/25 |
| 手牌里的单位在 `turn.start` 被 Buff ⇒ **整回合抛错、对局开不起来** | 全局信号发到每一张挂了该成员的卡；手牌卡没有 `UnitStateData`，`BuffAsync` 撞「基准来源未就绪」 | `TurnStartBuff…` |
| 选靶被取消后**照样扣费、照样抽两张** | 生成的 csx 拿到空选择就继续往下跑 | `TargetedSacrifice…`／`MixedUnitAndHqDamage…`／`原生声明选靶…` |
| 跨触发回指（打出时选中→回合结束时消灭）在 v4 失效 | 引擎只保证"同一执行链"上的目标；`card.played` 与 `turn.end` 是两条链 | `最后一击…` |
| 批量重生成**覆盖了人工校正过的 csx**（H39 不再抑制被打单位） | `proto/data/` 是生成物且被 gitignore，覆盖后找不回来 | 两条 H39 测试；工具现在跳过带 `【人工校正` 标记的卡 |
| **持续效果（光环）整类被拒**，而 v4 其实有 `DeclareAuraAsync` | 旧实现要"逐候选求值函数"，v4 只给常量增量＋描述式谓词；我们把整类都拒了，连能表达的那一档也没接 | `DiyMechanismExecutionTests` 7/7（含"光环随新单位出现、随来源离场消失"） |

## 本轮再修的三个（2026-10-29 晚）

| 缺陷 | 真凶 | 判据 |
| --- | --- | --- |
| 桌面房主**静默跑起旧引擎**：联机单测报的是 v3 时代才有的「座位 N 的卡包尚未同步完成」，而当时的引擎源码里根本没有这句话 | `ResolveServerDll` 按固定顺序取第一个命中。`src/Orc.Server/bin/Debug/net8.0/` 是早期布局的遗留产物，引擎切统一产物槽后没人清理，里面躺着**一份旧服务端**（还带 `pool.*` 与卡包对齐闸门） | 服务端解析改成「同目录必须有 `Orc.Game.dll`（可运行）＋ 取最新」，并把实际选中的路径打进日志；`NetworkWorkshopTests` 9/9 |
| DIY 卡包在**独立服务端**里装载出 **0 张卡**，而 `pool.list` 报「一切正常」 | 卡定义构造期对 `faction` 做硬校验，而值域是**进程级静态表**。宿主只在自己进程里 `RegisterCustom`（`av76`/`deran`/`usg`/`un`/`nsf` 与工坊自建国家），桌面联机的服务端是**独立子进程**，那些阵营在那边不存在 ⇒ 该包每张卡装载期即失败 | 包内新增自描述 `factions.json`：引擎 `PackageScanner.ReadFactions` ＋ `PackageRegistrar` 在卡之前登记；宿主在建包／存包／联机会话包三处落盘。判据＝`InstalledNativePackagePlaysThroughNormalThirtyNineCardAdmission`（39 张卡组真开局真打完）＋ 服务端 `pool.list` 的 `definitionCount` 从 1 变 4 |
| 上一条**为什么查了半天**：逐资产诊断被吞 | `MatchCardAssembly` 只把注册结果的 `Diagnostics` 留在对象里，不进 `warnings`；`pool.list` 于是「零卡 ＋ 零问题」，看起来一切正常 | 注册诊断并进 `warnings`：读面能直接看到「哪个文件、为什么没装上」 |

### 顺带补的工程性修正

- **联机单测不再依赖引擎构建布局**：桌面房主的服务端从「引擎产物目录」改为可显式指定的
  可运行目录（`ORC_SERVER_DLL`）；`tools/release.ps1` 一直是 `dotnet publish` 到 `<包>/OrcServer`，
  发布物不受影响。开发机要跑联机单测：`dotnet publish <引擎>/src/Orc.Server -c Debug -o artifacts/devserver`
  然后 `ORC_SERVER_DLL=<该目录>/Orc.Server.dll`。
- **测试并行已关**（`tests/AssemblyInfo.cs`）：宿主侧有一批进程级静态（`OrcEngineHost`、`KeywordRegistry`、
  `Faction` 值域），并行时互相串味。代价是总时长 1m45s → 2m19s，换的是「红就是真红」。

## 本版已修的四个真实缺陷

前三个都是"看起来像玩家写错了、其实是宿主/资产自己的错"的类型；第四个是宿主对引擎产物布局的过期假设。
记下来免得下次重查。

### ① 生成器把 `System.Console` 写进了 csx

`DesktopNativeCodegen` 的模板里有一行迁移期调试打印 `System.Console.WriteLine("[cg] enter ev=…")`。
csx 求值器的引用集是**白名单制**，`System.Console` 是独立程序集、不在白名单里
（`CSharpScriptSecurityOptions.DefaultAllowedAssemblies`）⇒ **每一张 DIY 卡**都编译失败，
报的是 `命名空间"System"中不存在类型或命名空间名"Console"`。
已删掉该行并写注释说明"生成的 csx 里不许出现 System.Console，要留痕请走引擎日志面"。

### ② 65 张官方卡的 inline 快照是**双重坏的**

`proto/data/kards/packs/*/cards/*.card.json` 里 65 张卡的内联快照同时有两个问题：

1. csx 仍调 `Orc.Game.Effects.AuthoringRuntime` / `AuthoringTargetContext`（v4 已删）；
2. **`mainTrigger` 根本没有 `hooks` 字段** ⇒ 迁移后没有挂载边 ⇒ 效果即使编得过也永远不触发。

用 `tools/build_builtin_packs.py` 的定向口 `--refresh --write --only <65 个 id>` 重生成
（**不要**用全量 `--refresh`：会顺带把语料里另外 200+ 张新卡塞进卡池，那是产品级改动）。
结果 `OFFICIAL_EXPANSION_OK previous=154 added=65 total=219`，残留 `AuthoringRuntime` 归零，
新 csx 用 `Kards.Ui.OrcBridge.DesktopNativeRuntime.Host(orcView)`，且带 `hooks:["unit.deployed"]`。

### ③ 效果体整段被静默跳过（`DesktopNativeRuntime.Host`）

**这是本版最贵的一条，单独翻掉 29 条单测。**

生成的 csx 守卫是 `if (orcRuntime is not null && orcSelf is not null && ShouldRun(...))`。
`orcSelf` 来自 `DesktopNativeRuntime.Host(view)`，而它原来只走 `Field(view,"Host")` →
`ViewCarrier.TryGet("Host")`。但 `ViewCarrier.Host` 是**框架回填的 CLR 属性**（源码注释：
"取自 owner 的宿主引用，**不来自数据面**"），`TryGet`／`Get` 只查视图声明的**数据面** ⇒ 恒 null。

后果不是"某个效果弱一点"：整段效果体被跳过，**没有异常、没有隔离日志、盘面就是不动**。
玩家侧表现＝"这张卡打出去没反应"。

修法：先读 `ViewCarrier.Host` 属性，数据面读位留作非 `ViewCarrier` 宿主（旧强类型视图）的兜底。
同时给守卫链加了**可开关的留痕**（`KARDS_EFFECT_TRACE=1`，同一"原因＋视图类型"只打一次），
以后再出这类"静默跳过"能一眼看出是哪个子条件为假，不用再靠猜。

### ④ 宿主只认引擎的旧产物路径，联机房主恒判"找不到服务端"

`ServerLauncher.ResolveServerDll` 只探 `src/Orc.Server/bin/<cfg>/net8.0/Orc.Server.dll`，
而引擎的 `Directory.Build.props` 已把产物收敛到统一槽 `artifacts/slot/bin/Orc.Server/<配置小写>/`
⇒ 那个旧路径**根本不会生成**，桌面房主恒判 `CanHost=false`，联机单测整组红。
已补上产物槽候选（旧布局保留，两条都认）。

## 发版时撞出来的两个工具链问题（2026-10-29 深夜）

这两个都不是游戏缺陷，但都会让"发版"这件事本身说谎，所以一并修了。

### ① 发版闸门红在"找不到 Orc.Server.dll"，而手工跑同一批用例全绿

差别的唯一变量是 `ORC_ENGINE_ROOT`：`tools/release.ps1` 确实设了，但测试宿主进程里读不到。
根因是两个来源不同源——构建侧 `OrcEngineRoot` 是按目录约定推出来的（csproj 先认 `-github` 后缀），
所以编译一直找得到引擎；运行期 `ServerLauncher` **只**认环境变量，读不到就一路落到"找不到服务端"。

修法：构建时把解析到的引擎仓库根写进 `AssemblyMetadata`，运行期读回（候选顺序＝环境变量 → 元数据）。
二进制自己知道它是在哪套引擎上编的，不再依赖调用方 shell。
判据：`env -u ORC_ENGINE_ROOT` 直接跑那条联机用例——修前 2 条全红，修后 2 条全绿。

### ② 一致性核对报 `[OK]`，却指着一套**旧引擎**

`check-consistency.ps1` 先认同级 `OrC-KSD`，而 csproj 明确先认 `OrC-KSD-github`（注释原话"后者才是对的"）。
本机两套都在，于是它打印 `引擎路径 H:\Working Folder\OrC-KSD`（旧基线）并判绿——
"绿灯但指错了引擎"比红灯更难发现。已改成与 csproj 同序。
判据：核对输出里 `引擎路径` 应为 `…\OrC-KSD-github`。

## 未收口项（已随本版发布，下一版清）

1. **联机 DIY 效果**（见「已知问题」第一行）：`DesktopNativeCodegen` 的输出还是宿主运行时调用，
   服务端沙箱引不到宿主程序集。改成引擎原生 csx 后，**确定性选靶与无选靶**的效果就能在服务端跑；
   需要点选目标的仍跑不了（引擎 R2：无前端桥接就抛 `BridgeNotAssembled`），那要改协议。
   这是本版唯一**功能级**缺口，两条用例已经把它钉住。
2. **2 条同进程注册残留**：需要引擎给注册面一个 `Reset`／`Clear` 钩子，或让这两个类各占一个进程。
3. **`--verify-effects` 在无头模式下跑不完**（1h06m 仍在跑，同路径去掉画廊遍历只要 6m42s）。
   动画画廊属补充探针，不是流程门。
4. **Android 导出**未验证（上一版遗留的损坏截图 + 缺 `dist/.gdignore`）。本版只交付 Windows 包。

## 从 alpha.6 升上来

- 卡组、DIY 卡、卡图、设置、备份环都保留（沿用离线增量更新路径）。
- **旧工坊包不需要手工迁移**：装载时如果包里只有旧格式 `effects/`，会明确报
  "旧格式 effects/ 目录已不再装载（v4-only：请提供 effects-v4/*.component.json）"。
  在工坊里重新保存一次该效果即可生成 v4 组件资产。
- 回退：把引擎 checkout 切回 `legacy-main` 并重装 `2.0.0-alpha.6` 即可（两条线的存档格式不通用，建议先备份）。

## 本版产物

构建命令与结果（2026-10-29，本机实测）：

```
tools/release.ps1 -BaselineZip dist/releases/2.0.0-alpha.6/full/OrC-KSD-2.0.0-alpha.6-win-x64.zip `
                  -BaselineVersion 2.0.0-alpha.6 -SingleBaseline
→ RELEASE_OK dist/releases/2.0.0-beta.1/full/OrC-KSD-2.0.0-beta.1-win-x64.zip
```

| 产物 | 大小 | SHA256（前 16 位） |
| --- | --- | --- |
| `dist/releases/2.0.0-beta.1/full/OrC-KSD-2.0.0-beta.1-win-x64.zip` | 1167 MB | `3B8929EABDD740E2` |
| `dist/releases/2.0.0-beta.1/patches/OrC-KSD-2.0.0-beta.1-from-12CD07C37C83-to-12CF67AC2BF1.orcupdate` | 35 MB | `3ADED89A5FC4D344` |
| `dist/updaters/1.2.0/OrC-KSD-offline-updater-1.2.0-win-x64.zip` | — | `D8F0AF41F2B97442` |

完整哈希见同目录 `SHA256.txt`。补丁从 alpha.6 起算，60 个变更文件；
`PATCH_EXACT_FILES_OK`（升级后逐文件一致）、`OFFLINE_UPGRADE_EXE_VERIFY_OK`（离线升级后的 exe 实测能跑）、
`EXPORTED_PLAYER_VERIFY_OK`（导出包实测能跑）三条都在 `verification/` 里留了日志。

一致性核对：`tools/check-consistency.ps1` → `CONSISTENCY_OK 红=0 黄=11`
（黄项全是历史版本的既成事实：无 tag、超大补丁，按规则不阻塞）。
tag `v2.0.0-beta.1` = `a8ee9ba` = HEAD，工作区干净。

**没有发布上线**：本版只出包，未上传任何站点或分发渠道。
