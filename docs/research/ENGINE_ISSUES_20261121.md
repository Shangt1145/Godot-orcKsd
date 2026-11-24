# 已提交给引擎仓库的问题（2026-11-21）

目标仓库：`hitman-OLD6/OrC-KSD`（引擎）。基线：`v4` @ `c950b2a`，工作区有未提交改动（`ServerHost.Cosmetics.cs` 等）。
游戏侧基线：`main` @ `f73dfcd`，`2.0.0-rc1-hotfix`。

> **状态：三份均已提交（2026-11-21 18:08）**
>
> - [#6](https://github.com/hitman-OLD6/OrC-KSD/issues/6) 外部进程无法引用宿主程序集（求值器白名单）
> - [#7](https://github.com/hitman-OLD6/OrC-KSD/issues/7) 外部起服词条→预制体绑定核验失败
> - [#8](https://github.com/hitman-OLD6/OrC-KSD/issues/8) `orderTargets` 恒空 / 跨效果回指不复用
>
> 提交途径：GitHub 连接器（App 集成令牌）对该仓库无 issues 写权限，返回
> `403 Resource not accessible by integration`；改用本机 `gh`（账号 `Shangt1145`，对该仓库有
> `push`+`triage`）提交。下面保留原文，便于日后对照与引用。

---

# Issue 1

**标题**

```
外部 Orc.Server 进程无法引用宿主程序集：脚本求值器白名单封闭且无进程外注入口
```

**正文**

## 背景

游戏仓库（Godot 前端）的 DIY／桌面导入效果走的是「结构化图 → 生成 csx → 引擎编译执行」。引擎 v4 删掉 `AuthoringRuntime`／`AuthoringTargetContext`／`AuthoringAuraRuntime` 之后，宿主侧在 `Kards.Ui.OrcBridge.DesktopNativeRuntime` 里逐字移植了那 25 个辅助调用，生成的 csx 直接调用 `Kards.Ui.OrcBridge.DesktopNativeRuntime.*`（`DesktopNativeCodegen.cs:13` 的 `Runtime` 常量）。

这本身是宿主按「引擎没有替代入口」做的权宜，前提是**执行环境里有宿主程序集**。

## 问题

`CSharpScriptSecurityOptions.DefaultAllowedAssemblies` 只有 `System.*` + `Orc` + `Orc.Game`（`src/Orc.Script/CSharpScriptSecurityOptions.cs:21-33`），且 `CSharpScriptEvaluator.BuildScriptOptions()` 只从 `AppDomain.CurrentDomain.GetAssemblies()` 里挑白名单内的程序集，`_scriptOptions` 还是 `Lazy`（首次求值后引用集就固定）。于是：

- **进程内**（同进程已加载 `Kards.Ui.OrcBridge`）：能编译、能跑。
- **外部 `Orc.Server` 进程**：既没有该程序集，它也不在白名单 → `当前上下文中不存在名称"Kards"`。

`ServerOptions.ScriptEvaluator` 是现成的注入口，但只在**进程内** `ServerBootstrap.StartAsync` 时能传；桌面默认路径是 `ServerManager` 拉起独立进程，调用方没有任何注入通道。

## 影响

桌面「房主即服务器」（默认走外部进程）时，**所有 DIY／桌面生成的卡牌效果在服务端一律失效**；只有 Android／iOS（内嵌进程 + 宿主求值器）能跑。这不是测试环境问题，是产品级能力缺口。

## 复现

```
cd <游戏仓库>
set ORC_ENGINE_ROOT=<引擎仓库>/src/Orc.Game
set ORC_SERVER_DLL=<引擎仓库>/src/Orc.Server/bin/Debug/net8.0/Orc.Server.dll
dotnet test tests/Kards.Ui.Tests.csproj --filter "FullyQualifiedName~AuthoredCardReachesBothPlayers"
```

`inProcess: False` 的两条用例报：

```
System.InvalidOperationException : 引擎异常：InvalidDataException: Keyword behavior could not be instantiated:
当前上下文中不存在名称"Kards"；…（重复 4 次）
```

## 请求

三选一（我方不擅自改引擎契约）：

1. 外部服务端支持声明「额外可引用程序集」（CLI 参数／配置文件／包清单字段），宿主把 `Kards.Ui.OrcBridge.dll`、`Kards.Ui.Core.dll` 一起放进 `OrcServer/`；
2. 允许进程外注入求值器（宿主提供实现，按约定程序集名/参数传入）；
3. 明确这些门面应由引擎承载（把 `AuthoringRuntime` 的替代下沉进 `Orc.Game`），我方删除宿主副本。

## 附注

`_scriptOptions` 只扫一次 AppDomain，所以「先求值、后加载插件程序集」的用法同样会失效——如果第 1 条要做，这里也需要一个显式的失效/重扫入口。

## 环境

- 引擎：`v4` 分支 @ `c950b2a`（工作区有未提交改动：`ServerHost.Cosmetics.cs` 等）
- 游戏：`main` @ `f73dfcd`，`2.0.0-rc1-hotfix`
- Windows / .NET 8

---

# Issue 2

**标题**

```
外部服务端起服：词条→效果预制体绑定核验失败；同一批包进程内起服正常
```

**正文**

## 现象

同一份包根（roots）、同一份卡组，`ServerLauncher.StartAsync(..., inProcess: true)` 通过；
`inProcess: false`（外部 `Orc.Server` 进程）在开局门禁处失败：

```
引擎拒绝（门禁/状态不符）：词条效果库绑定核验失败（配置错误——装配期 fail-fast）：
词条 '机动' → 效果预制体 'keyword.mobility'；
词条 '固守' → 效果预制体 'keyword.entrench'；
词条 '抢修' → 效果预制体 'keyword.repair'。
```

## 复现

```
cd <游戏仓库>
dotnet test tests/Kards.Ui.Tests.csproj --filter "FullyQualifiedName~DefaultEnabledShippedPackagesCanStartRoom"
```

两条用例只差 `inProcess` 参数，`True` 绿、`False` 红。

## 我方已核查（不是这些原因）

- 包内确有 `effects-v4/keyword.mobility.component.json` 及其 `.meta.json`；
- `manifest.json` 的 `metadata` 共 6 条，**0 条** legacy `effects/` 前缀，3 条指向 `effects-v4`；
- 我方会话在构建传输用包根时会剔除 legacy `effects/` 目录（测试断言已通过），其余文件原样复制；
- 两条路径拿到的是**同一份 roots**（同一 `NetworkWorkshopSession.Roots`）。

## 猜测（需引擎确认）

外部 bootstrap 的「激活顺序/挂载范围」与进程内不同：绑定核验发生在预制体挂载之前，或外部服务端只挂载了 roots 的某个子集。

## 请求

1. 说明外部路径的挂载/激活顺序（`PackageActivation` 里 `PostCommand` 装配不变量与预制体挂载的先后）；
2. 或者直接指出「包要带 prefab 需要什么」——挂载声明字段？`lock.formal.json`？v4 的 `effects-v4` 是否要求某条清单登记？

## 环境

同 Issue 1。

---

# Issue 3

**标题**

```
v4 上两处目标相关行为：orderTargets 投影恒空 / 跨效果回指未复用目标
```

**正文**

两条用例在引擎 `v4 @ c950b2a` 上稳定失败，怀疑是 v4 有意变更或投影缺口。请确认语义归属：若确实有意变更，我方改测试；若不是，麻烦修。

## 一、`orderTargets` 恒为空（指向性指令无法预判目标）

**用例**

```
dotnet test tests/Kards.Ui.Tests.csproj --filter "FullyQualifiedName~DebugCommandsRespectCommandLock"
```

**断言**

```csharp
for (var i = 0; i < 5; i++)
    Assert.Contains(session.Actions.OrderTargets, t => t.Uid == actor.Uid);
```

**实际**：`Assert.Contains() Failure: Filter not matched in collection / Collection: []`

**影响**：UI 画不出指向箭头，也无法在提交前拦住"空放"——玩家看到的是一条没有目标的指令。

## 二、跨效果回指未复用目标

**用例**

```
dotnet test tests/Kards.Ui.Tests.csproj --filter "FullyQualifiedName~CrossEffectTargetTests"
```

**断言**：最后一击的回合结束效果应当消灭「打出时选中的那个单位」——实际该单位（第 4 先锋营）仍在场上：

```
Assert.DoesNotContain() Failure: Filter matched in collection
Collection: [UiCardView { CardId = pack/builtin/4_pioneer_battalion, Zone = support, OwnerSide = self, … }]
```

**现象**：`read.of` 指向前序效果选中的目标时，后序效果没有复用那次选择，落到了别的对象上（或没生效）。

## 请求

1. 这两处是否是 v4 有意的语义调整？如是，请给一句口径，我方改断言；
2. 如不是：`orderTargets` 的投影面在哪里补齐（我方记得历史上是 `McpService.Projection.cs` 恒空）；
   `read.of` 跨效果回指的复用规则在 v4 里由谁负责。

## 环境

同 Issue 1。

---

## 备注（给后续接手的人）

- 这五条在**干净工作区**上同样失败（`git stash -u` 对照过），与游戏侧近期改动无关。
- 引擎仓库当前有未提交改动，上述结论基于包含这些改动的工作区状态。
