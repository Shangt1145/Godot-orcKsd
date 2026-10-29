# UI 适配 v4 — 进度与配方（2026-10-28）

引擎已切到 `origin/main`（分支 `v4`，`8a5dd8f`）；旧线保留为 `legacy-main`（`4c238db`）。
回退：`git -C "H:/Working Folder/OrC-KSD-github" checkout legacy-main`。

> **先把上一版文档的错误更正掉。**
> 上一版写"切到 v4 后只剩 2 个错误"——**那是错的**，是我被增量构建骗了：切分支后 MSBuild 复用了
> `obj/` 里的旧引用集，只重编了一部分文件，于是只报出那一部分里的错误。
> 全量重建（`dotnet build -t:Rebuild`）的真数是 **57 个错误 / 13 个文件**。
> 教训记在这里：**改引擎分支后必须 `-t:Rebuild` 再数错误**，否则数字没有意义。

---

## 一、现在的状态

**桥接层编译：0 错误 / 3 个既有警告**（`dotnet build -c Debug`，引擎在 `v4`）。
**玩家视角：完整 UI 探针跑绿**（`UI_VERIFY_OK screens=12 cards=534`，Godot 退出码 0）。

这一次不再只报"编译绿"。下面是**跑出来的**验收证据（`tools/run.ps1 -Verify` 同一条链）：

```
UI_BOUNDARY_VERIFY_OK
COLLECTION_SCROLL_VERIFY_OK all=24 ...
MENU_VERIFY_OK ... native-selected-deck resume-current-match ...
OFFICIAL_ART_VERIFY_OK cards=219
ASSET_VERIFY_OK textures=1619 cache=64/64
BATTLE_STAGE1..4_VERIFY_OK（部署/移动/武器/反制/伤害/死亡/指令/多目标/治疗/增益/压制…）
BRIDGE_VERIFY_OK real-match play opening-hand=4 ... deploy endturn combat hq-damaged
ORDERS_STAGE6B2_VERIFY_OK ... counter-arm counter-refund counter-rearm ...
NATIVE_STATE_UI_VERIFY_OK upgrade reveal suppress extend clear inhibit runtime-keywords ...
MULLIGAN_VERIFY_OK / DECK_VERIFY_OK / PACK_SELECTION_VERIFY_OK / MULTI_CHOICE_VERIFY_OK
UI_VERIFY_OK screens=12 cards=534 texture_cache=64/64
```

> 其中 `NATIVE_STATE_UI_VERIFY_OK ... runtime-keywords` 这一项是内建词条（伏击/装甲/闪击/免疫/情报/动员/压制）
> **真的跑出了效果**——不是"装载成功"，是"行为落地"。

### 1.1 真修好的（行为等价或已明确退化）

| 文件 | 改了什么 |
| --- | --- |
| `SourceEffectAssembly.cs` | 整类重写：`ActiveEffect<>`／`AddEffect`／`Trigger<>`／`Bus` 全删 → 主动效果改挂 `engine#card.played`、反制死亡触发改挂 `engine#card.died`，`Dispose` 用边句柄撤销 |
| **`DesktopNativeRuntime.cs`（新增 ~470 行）** | **让 DIY 重新能用的门面**：逐字移植被删的 `Orc.Game.Effects.AuthoringRuntime`（269 行 / 25 个公开静态）到 v4 API；`DesktopNativeCodegen.Runtime` 常量改指它；`Match` 句柄用 `ConditionalWeakTable<LogicEngine, Match>` 在开局登记 |
| `DesktopTargetContext.cs` | 选靶走 `EffectRuntime.SelectAsync`；筛选条件本地实现；**顺手修掉两个我自己编出来的 API**（`card.Model`／`c.Cost`／`u.Attack`／`c.Keywords` 在 v4 都不存在，见 §三） |
| `DesktopEffectRuntime.cs` | 转调 `DesktopTargetContext.ValidateFilter/Matches` |
| `OrcMatchSession.cs` | 开局闸门 `card.Effects…IsMounted` → `IsEffectAssembled`（卡实体上是否有该 id 的**已装配组件**）；`MatchOptions` 显式给 `AllowRandomTargeting` 与 `ScriptEvaluator`（见 §一.3） |
| `OrcMatchSession.Debug.cs` | 调试面板「效果」列改投影组件（id · 装配状态 · 挂载目标）；创建/治疗/设值改走 `MatchCardService` ＋ `SetModifier` |
| **`NativeWorkshop.Effects.cs`** | 落盘 v3 快照后立刻 `AssetSchemaMigrator.MigratePackToDisk` 补出 `effects-v4/*.component.json`（v4 装载面只认它）；迁移失败＝整包失败并列出全部失败项 |
| `NativeScriptHost.cs` | 自带 `ScriptReferenceSource`（三级回退：真实路径→宿主同目录副本→`TryGetRawMetadata`）；**并显式预载 `Microsoft.CSharp`／`System.Linq.Expressions`**（`dynamic` 的 DLR 绑定器，不拉起来引用集里就没有它，见 §三）；`csproj` 加 `AllowUnsafeBlocks` |
| `ServerLauncher.cs` | 删 `ServerOptions.PluginsRoot/ScriptEvaluator/ReuseProcessVocabulary`（v4 已无这三个开关） |
| `OrcEngineHost.cs` | 删 `CardTextEffectCompiler.OpTemplateDirectory` 注入（v4 改成不可注入的静态 `Lazy`） |
| `DiyCapability` / `DiyCardCompiler` / `OfficialCardCompiler` / `OrcImportService` / `VerifiedCardPool` | 删 `DynamicEffectFactory.Instantiate` 终检（Effect 家族不存在） |
| `RemoteMatchFeed.Packs.cs` ＋ 新增 `PackPaths.cs` | 联机卡包传输整条线停用（见 §四）；本地打包需要的路径/摘要工具搬进本仓库 |

### 1.2 仍然是"有意不做"的（玩家可见的退化，不是遗漏）

| 位置 | 状态 | 后果 |
| --- | --- | --- |
| `DesktopNativeCodegen` 的 passive 分支 | 作者期直接抛 `Gap(...)` | DIY **持续效果（光环）** 做不出来——见 §五 |
| `DesktopNativeCodegen` 的 `bindEvent`／`grantEffect` | 作者期直接抛 `Gap(...)` | "给目标临时附加效果"做不出来：v4 没有运行期挂载效果快照的入口（`AttachSnapshotAsync` 只剩文档注释） |
| `RemoteMatchFeed.Packs.cs` | `SupportsPackTransfer => false` | 联机时本机缺卡包只报"请让对方单独发给你"，不再自动拉包——见 §四 |

### 1.3 引擎侧必须打的一个补丁（已落地，需随 pull 重放）

`docs/engine-patches/v4-host-script-evaluator-20261028.patch`（`src/Orc.Game/Match/{Match,MatchOptions}.cs`，+23 行、纯加性）。

**为什么绕不过去**（这一条是实测出来的，不是推测）：

1. Godot **按字节加载程序集**。探针在真实宿主里打印：`Kards.Ui`／`Kards.Ui.OrcBridge`／`Orc`／`Orc.Game`／`Orc.Script`
   的 `Assembly.Location` **全是空串**，`AssemblyLoadContext` 是无名个体上下文。
2. v4 的 `CSharpScriptEvaluator` 取引用只走 `assembly.Location`，**没有路径就跳过**（引擎注释原文：
   "单文件发布等无路径场景：跳过"）。v4 又删掉了 `Orc.Script.ScriptAssemblyReferences`（唯一能兜住这种情况的东西）。
3. 桥接层的 `NativeScriptHost` 自己补了三级回退，所以**卡包效果、DIY 效果都能编过**。
   但 `Match` 构造器末段那句 `KeywordComponentLibrary.LoadInto(EngineRuntime, Engine.ScriptEvaluator)` 发生在
   宿主拿到 `Engine` 引用**之前**，只能用引擎默认求值器 → 8 个内建词条组件的 csx 全部
   `The type or namespace name 'Orc' could not be found` → 必需包装载失败 → **任何对局都开不起来**。

引擎自己看不出来：它的测试跑在真 DLL 上，`Location` 有值。只有"按字节加载程序集的宿主"（Godot／Unity）会踩。

补丁做的事：给 `MatchOptions` 加一个可选的 `ScriptEvaluator`，`Match` 构造器用它作为 `Engine.ScriptEvaluator`
的**初值**（`??=` 链首，传 `null` 与不传完全等价，既有宿主零行为变化），桥接层建局时把 `NativeScriptHost.Evaluator`
交进去。这是该往上游提的引擎缺口，不是本地特调。

---

## 二、`SourceEffectAssembly` 的 v4 形态（已落地，作为参考）

v4 没有"给卡加一个 C# 效果"的入口，主动效果的承载点是**引擎成员上的一条边**：

```csharp
// 指令主动效果：挂在 engine#card.played（PlayManager 在 order.cast 的宣告点发，反制之后、扣费/离手之前）
_edges.Add(match.Engine.AttachMemberEdge(
    GameUpdates.CardPlayed, "source/execute:" + id, this,
    MemberEdges.For<CardEventView>((view, _, ct) => CastAsync(command, plan, view, ct))));

// 反制的友方死亡触发：挂在 engine#card.died，卡内 IsActive 闸门不变
_edges.Add(match.Engine.AttachMemberEdge(
    GameUpdates.CardDied, "source/friendly-death:" + id, this,
    MemberEdges.For<CardEventView>((view, _, ct) => OnFriendlyDeathAsync(counter, plan, view, ct))));
```

为什么不用 `engine#order.cast` 直接挂结算边：那要依赖该成员内部的优先级布局（0＝反制、100＝宣告点、自有边最后），
属于引擎流程的私有排序；`card.played` 是引擎文档写明的主动效果承载点（`IOrderAction`／`OrderCard` 注释），排序由引擎保证。

**已知位次差异（写清楚）**：旧 `ICastAction` 在 `OnResolve`（自有边，最后）执行；新边在 `card.played` 的订阅序里执行。
两者都在扣费之前，但**同一张卡上其它 `card.played` 订阅者（词条组件等）与它的先后不再固定**。

**目标来源变了**：`card.played` 载荷只有 `Card/Player/Source`，没有 `Target`。目标唯一来源是预打出捕获
（`_pendingCapture`）——本来也是同一张卡（捕获箱第一个可解析 Card 就是流程拿到的 Target），所以语义等价；
但捕获缺失时旧实现退化成"无目标"，这里改成**明确抛错**（`RequireTarget` / `TakeCastSelection`）。

---

## 三、v4 里"看起来该有、其实没有"的成员（踩过的坑）

| 我写过 | v4 的真名 / 真做法 |
| --- | --- |
| `card.Model` | `card.ModelEntity`（`CardBase.ModelEntity`） |
| `c.Cost` | `((CardBase)c).Modifiers.GetEffectiveValue(CardStatFields.DeployCost)` |
| `u.Attack` / `u.Defense` | `u.GetData<UnitStateData>().Attack / .Defense` |
| `u.UnitType` | `u.GetData<UnitStateData>().UnitTypes`（**列表**，可 0..n 个） |
| `c.Keywords.Contains(k)` | `((CardBase)c).Keywords.Has(k)` |
| `e.Host == card`（找组件宿主） | `ReferenceEquals(e.Bound, card)`（`Host` 是 `OrchestrationEntity`，不是卡） |
| `entity.Mounts` | `entity.Type.Mounts`（声明；`MountDeclaration.Target`） |
| `card.Effects[i].IsMounted` | `model.Runtime.ComponentsOf(model)` 里 `IsAssembled` 的组件 |
| `AuthoringRuntime.CreateAsync(...zone...)` | `MatchCardService.CreateAndPlaceToHandAsync` / `CreateAndPlaceToAdjacentAsync` |
| `AuthoringRuntime.HealAsync` | `EffectRuntime.BuffAsync(target, 0, amount, ct: ct)` |
| `AuthoringRuntime.AttributeAsync(...,"set",...)` | `((CardBase)t).Modifiers.AddModifierAsync(new SetModifier(field, value, owner), ct)` |
| `EffectRuntime.MatchFor(card)` | **不存在**。按服务各自 `ResolveFor(card)`：`MatchCardService` / `MatchRandomService` / `TargeterManager` / `GameEnvironment` / `ResourceManager`。要 `Match.Players`／`TurnNumber` 得自己在桥接层登记（见 §六） |
| `AuthoringRuntime`（整个类型） | **被删**。桥接层门面 `DesktopNativeRuntime` 顶上（§一.1） |
| `Orc.Script.ScriptAssemblyReferences` | **被删**。`NativeScriptHost.ScriptReferenceSource` 顶上（§一.1） |
| `Assembly.Location`（宿主视角） | **在 Godot 里恒为空串**——Godot 按字节加载程序集，`AssemblyLoadContext` 是无名个体上下文。任何"按路径取引用"的代码在本宿主里都拿不到东西（`AppDomain.CurrentDomain.GetAssemblies()` 里能看到程序集，但 `Location == ""`）。v4 的三个求值器全都只走 `Location`，所以**宿主必须自带引用来源**，且必须**在建局前**把求值器交给引擎（§一.3） |
| `dynamic` 的 DLR 依赖 | 白名单里有 `Microsoft.CSharp` / `System.Linq.Expressions`，但**进程没加载就等于没有**。引擎求值器会显式 `Assembly.Load` 一次；宿主自己实现的求值器必须照做，否则报"缺少已加载的程序集 Microsoft.CSharp" |
| `MatchOptions.AllowRandomTargeting` | 默认 `false`＝执行到 `sel=random` **明确抛错**（不静默取首个）。宿主是作者/沙盒性质时要在 `MatchOptions` 里显式开启 |

---

## 四、联机卡包传输：整条线停用（玩家可见）

- 原实现调服务端 `pool.manifest` / `pool.offer` / `pool.chunk` / `pool.commit` / `pool.fetch` / `pool.ack`。
- 这六个方法只在 `legacy-main` 的 `src/Orc.Server/Transport/ServerHost.PackTransfer.cs` 里；**`origin/main` 没有这个文件**。
- 所以不是"本层临时降级"，是**对端不存在**。按「一切以新引擎为准」的裁定，本仓库不再假装能同步。
- 玩家后果：**联机只能用双方都装好的卡包**；带 DIY 卡组开房时客人不会自动下载，会看到"本机缺少卡包：…"。
- 恢复前提：引擎把 `pool.*` 加回来。

---

## 五、DIY 持续效果（光环）：作者期明确拒绝

v4 的光环入口只有 `EffectRuntime.DeclareAuraAsync(host, field, delta, EffectAuraFilter, ct)`：
值＝**常量增量**，受益者＝**描述式过滤器**。
旧 `AuthoringAuraRuntime` 登记的是 `(candidate, current) => int` 的**自定义求值函数**，因此支持 `set` 模式与逐候选重算。

把 add 类光环硬翻成 delta 会丢掉 `where` 条件与 `set` 模式（玩家看到的是数值不对，且查不出来），
所以在 `DesktopNativeCodegen` 的 passive 分支**直接抛 `Gap(...)`**——编辑器里立刻能看到原因，而不是做出一张哑弹。

恢复路径（二选一）：① 走 v4 组件资产的 `wraps` 通道；② 引擎补回带求值函数的光环入口。

---

## 六、下一步（按依赖顺序）

原表 1–5 项**已全部落地**（`DesktopNativeRuntime` 门面、`ViewCarrier` 字段读取、`Match` 句柄登记、
工坊 `effects-v4` 产出、`AllowRandomTargeting`），另外补了两件当时没预料到的：

| # | 事 | 状态 |
| --- | --- | --- |
| 1 | 新增 `DesktopNativeRuntime`（桥接层门面） | ✅ 已落地，`DIY_UI_VERIFY_OK` 跑绿 |
| 2 | `ShouldRun`／`EventCard`／`EventTarget` 按 `ViewCarrier` 字段名读 | ✅ |
| 3 | `Match` 句柄：`ConditionalWeakTable<LogicEngine, Match>` 开局登记 | ✅ |
| 4 | 工坊落盘后补 `effects-v4/*.component.json` | ✅（卡定义无需改：迁移器保留 `root.id`，卡→效果声明原样可解析） |
| 5 | `MatchOptions.AllowRandomTargeting` 显式开启 | ✅ |
| 6 | **`MatchOptions.ScriptEvaluator` 引擎钩子**（当时没预料到） | ✅ 补丁在 `docs/engine-patches/`，见 §一.3 |
| 7 | **`NativeScriptHost` 预载 DLR 程序集**（当时没预料到） | ✅ 见 §三 |
| 8 | 发版（`VERSION` → `2.0.0-beta.1` ＋ 一致性核对 ＋ 导出） | 见 §七 |

---

## 七、验收方式（每条都要能跑，不接受"参数写了"）

| 项 | 怎么验 | 实测 |
| --- | --- | --- |
| 桥接层编译 | `dotnet build -c Debug proto/bridge/Kards.Ui.OrcBridge/Kards.Ui.OrcBridge.csproj`（引擎在 `v4`，**必要时 `-t:Rebuild`**） | 0 错误 / 3 个既有警告 |
| 主流程（开局→换牌→部署→战斗→回合→终局） | `tools/run.ps1 -Verify`（`--verify-ui`） | `UI_VERIFY_OK screens=12 cards=534`，退出码 0 |
| 内建词条真的有效果 | 同上，看 `NATIVE_STATE_UI_VERIFY_OK ... runtime-keywords` | 通过（这是 §一.3 那个补丁换来的） |
| 真实对局桥接 | 同上，看 `BRIDGE_VERIFY_OK real-match ... deploy endturn combat hq-damaged` | 通过 |
| 12 张源码效果卡 | `VerifiedCardPool` 验收集 | `CARD_POOL_STAGE6B2_VERIFY_OK ... verified=24` |
| DIY 工坊全流程 | `--verify-diy` | `DIY_UI_VERIFY_OK ... native-deployment pack-zip-export pack-zip-import` |
| 全卡效果逐个演示 | `--verify-ui --verify-effects` | 见下方"待补" |
| 工坊卡包装载（不再报旧格式） | 工坊导出包 → `OrcEngineHost.SetWorkshopRoots` | 由 `DIY_UI_VERIFY_OK` 的 `pack-zip-import`＋`native-deployment` 覆盖 |
| 随机选靶 | 用「齐射」（`sel=random`）开一局 | 已开 `AllowRandomTargeting`，不再抛错（审计仍在 `Engine.RandomSelections`） |
| 联机卡包 | 带 DIY 卡组开房 | 界面显示"本机卡包缺失（联机卡包传输已停用，请让对方单独发给你）"，不转圈 |
| 安卓导出 | `tools/export-android.ps1` | **仍未验**：`dist/releases/2.0.0-alpha.5/verification/debug-android.png` 损坏、缺 `dist/.gdignore`（写进发行说明已知问题） |
