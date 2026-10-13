# 引擎新资产模型迁移 · 交接与问题报告（交前端开发人员）

日期：2026-10-13
范围：本仓库（`OrC-KSD.Godot`）从**自造卡包/资产模型**（`pack.json` + `PackSystem`/`PackLibrary`）迁移到 OrC 引擎**新资产模型**（`Orc.Packages`：`EngineHost` / `PackageManifest` / 资产生命周期），并把"UI 投影"与"卡包词条加载"下沉为**引擎侧插件**。

**状态：代码迁移完成。**
- 引擎仓库 `d:\OrC-KSD`：`dotnet build OrcEngine.slnx --no-incremental` = **0 警告 0 错误**；`dotnet test` = **1488 通过 / 0 失败**。
- 本仓库：`proto/contracts`、`proto/core`、`proto/bridge/Kards.Ui.OrcBridge`、`tests/Kards.Ui.Tests` **全部 0 错误 0 警告**。
- ⚠️ **前端测试运行 117/258 失败**（主因＝环境与夹具，见 §5）；Godot 主工程本机无 Godot .NET SDK，未能验证（见 §5.4）。

---

## 1. 本次交付

### 1.1 引擎侧新增能力（消费方按此适配）

| 能力 | 位置 | 要点 |
|---|---|---|
| 中立 UI 契约 | `src/Orc.UI.Contracts`（`UiCardDefinition`/`UiCardView`/`UiMatchView`/`Visibility`/`DtoKind`/`IDtoKind`） | **零引擎依赖**；位于 `Orc.Game` **之下**（`Orc ← Orc.UI.Contracts ← Orc.Game`），故前端与引擎侧插件都可引用且无环 |
| 卡包目录管道 | `Orc.Packages.CardPackLoader` / `CardPackContent` / `CardPackAssembler` | 按**目录约定**读包内 `cards/`（可多目录 `cards-*`）、`effects/`、`texts.json`、`keywords.json`；装载前跑分发预检**双零门**（不达＝整包隔离）；卡 id 前缀 `pack/<包id>/<原卡id>`（**前缀不变**）；卡面文本→效果由引擎编译 |
| 卡包生命周期 | `Orc.Packages.EngineHost` | 发现/择版/依赖拓扑/冷加载/**热加载·卸载·重载·回滚**；`LockCardPacks()`/`UnlockCardPacks()`（**对局进行中禁止卸载/重载**）；`CreateSnapshot()` 冻结对局资产（含 `RuntimeCards`）；`FromDiscovery(discovery, evaluator, extraPackages)` 注入**代码入口包** |
| 包生命周期观察 | `Orc.Packages.IPackageLifecycleContext`（`IGlobalPluginContext` 扩展） | 成功 `load`/`unload`/`reload` 后回调观察者；冷加载期已就位的卡包也会补一次 `Loaded` |
| 投影契约与注册面 | `Orc.Game.Ui`：`IUiProjectionProvider`（`Project(Match,Player)` + `ReadCard(...)`）/ `IUiProjectionProviderFactory` / `UiProjectionRegistry`；`Match.UiProjections` | `CreateProvider(artLookup, matchId, definitionLookup)`；**单个** provider；投影抛错＝**运行期隔离**（调用方保留上一帧） |
| 内置 `.ui` 插件 | `src/Orc.UI`：`UiPluginPackage`（包 `orc.builtin.ui`，固定 guid）、`UiProjectionProvider`、`StaticCardProjection`、`CardPackKeywordLoader` | 一个包两职：`process` 相加载卡包词条；`match/preCommand` 相注册投影 provider 工厂 |
| 词条语义 | `Orc.Game.Cards.KeywordRegistry.RegisterOrReplace`；`KeywordSnapshot`；`Match` 装配时冻结快照 | 运行期**只允许新注册与覆盖、不允许注销**（注销须冷重启）；对局开始冻结快照 ⇒ 之后覆盖**不影响进行中的对局**。`Unregister` 仍 public，但**仅装配维护/测试清理**用 |
| 运行期卡注册 | `Orc.Game.Cards.RuntimeCardRegistration`；`EngineHost.CardData`（引擎级）；`MatchCardService.RegisterCard(...)`（对局级） | **只追加不覆盖**；新实例经 ID 水位线自动判"构筑之外"；**须先声明效果再实例化**；引擎级随快照冻结进之后新建的对局 |

### 1.2 引擎侧另应用的补丁（A.20）

- 补丁：`feat!: add action input admission and formation gap deployment`（18 文件，基线 `8b0ae98`）。
- 来源文件（仓库根）：`0001-feat-add-action-input-admission-and-formation-gap-de.patch`；同内容备份：`docs/engine-patches/6d2-action-portals.patch`。
- 内容：`JudicatorNames.ActionInput` + `ActionInputJudicator`；`Match.CanAcceptInput(...)`/`Match.InteractionStateReader`；`PlayManager.CanPlay(...)`；`BattleLine`/`PlayManager` 的**阵型空隙**（`FormationGap`）与 `GetDeploymentGaps`；`CommandFailureReason/CommandBlockReason.InteractionBlocked`。
- 结果：原先"前端依赖的引擎能力缺失"这一阻塞**已解除**。

---

## 2. 本仓库改动清单（逐文件）

> 约束遵循：`proto/ui/screens|components|anim` 除 `proto/ui/bridge/OrcMatchRunner.cs` 外**未改动**；`proto/ui/**` 因 csproj 级全局 `Using` 而**源码零改动**。

### 2.1 契约层（view 三件上移）

| 文件 | 改动 |
|---|---|
| `proto/contracts/Kards.Ui.Contracts.csproj` | 新增 `OrcEngineRoot` 属性、`<Using Include="Orc.UI.Contracts" />`、`ProjectReference → $(OrcEngineRoot)/../Orc.UI.Contracts/Orc.UI.Contracts.csproj` |
| `proto/contracts/CardNodeKey.cs` | **新增**（原在 `IDtoKind.cs`；`CardNodeKey` 是 `proto/ui/**` 与测试依赖的缓存键，必须留在前端） |
| `proto/contracts/UiCommand.cs` | **新增**（原在 `UiViewModel.cs` 后半段；输入契约留前端） |
| `proto/contracts/IDtoKind.cs` | **删除**（`Visibility`/`DtoKind`/`IDtoKind` 已上移） |
| `proto/contracts/UiViewModel.cs` | **删除**（`UiCardDefinition`/`UiCardView`/`UiMatchView` 已上移） |
| `Kards.Ui.csproj`、`proto/core/Kards.Ui.Core.csproj`、`proto/bridge/.../Kards.Ui.OrcBridge.csproj`、`tests/Kards.Ui.Tests.csproj` | 各加 `<Using Include="Orc.UI.Contracts" />`（保持源码零改动） |

### 2.2 桥接层（S8 重写）

| 文件 | 改动 |
|---|---|
| `proto/bridge/.../PackSystem.cs` | **删除**（自造包模型；进程级全局状态） |
| `proto/bridge/.../PackLibrary.cs` | **删除**（`packs.json` 注册表，从未接线） |
| `proto/bridge/.../OrcCardReader.cs` | **删除**（投影逻辑迁入 `Orc.UI.UiProjectionProvider`） |
| `proto/bridge/.../OrcMatchReader.cs` | **删除**（同上） |
| `proto/bridge/.../PackCardTextParser.cs` | **删除**（卡面文本→效果能力已上移引擎 `Orc.Game.EffectParsing.CardTextEffectCompiler`；原唯一消费者 `PackSystem` 已删，属迁移遗留死代码） |
| `proto/bridge/.../PackSet.cs` | **新增**：`AssetCatalogSnapshot` + 由快照装载的 `CardPackContent` 列表（`Definitions` 合并引擎级运行期卡） |
| `proto/bridge/.../OrcEngineHost.cs` | **新增**：前端持引擎宿主（幂等初始化；注入 `orc.builtin.core` + `orc.builtin.ui` 两个**代码入口包**；按约定发现 `<base>/packs`；`RegisterProcessDefaults()`；`LoadPackCards()` 产出图鉴用静态卡定义） |
| `proto/bridge/.../OrcMatchHost.cs` | **重写**：整盘/单卡视图改由 `Match.UiProjections` 的 **provider** 产出（**每帧拉取**；投影抛错＝**运行期隔离**：保留上一帧 + 上报错误）；`Index` 本地构建；`ReadChoiceDefinition` 走 `StaticCardProjection`；`SetSourceDefinitions` 转为 provider 的 `definitionLookup` |
| `proto/bridge/.../OrcMatchSession.cs` | **重写卡包装配链**：`packs` 参数改为 `PackSet?`；定义集/效果装配改取引擎快照（`CardPackLoader.Load` → `CardPackAssembler.Attach`）；`new Match(..., packageActivator: new SnapshotPackageActivator(snapshot))`；合并 `snapshot.RuntimeCards`；挂载复验改用 `CardPackContent.Declarations`；新增 `PackCardDefinitions`（静态投影 + 包内文本）；`FactionOf` 返回 `string` |
| `proto/bridge/.../OrcUpdateTranslator.cs` | 构造参数由 `OrcCardReader` 改为**委托** `Func<Card, Player, string, int, UiCardView?>`（表现层取卡视图仍走引擎侧投影） |
| `proto/bridge/.../Kards.Ui.OrcBridge.csproj` | 新增引 `Orc.Script` / `Orc.Packages` / `Orc.Plugins.BuiltIn` / `Orc.UI`（经 `$(OrcEngineRoot)/../…`） |
| `proto/bridge/.../DeckConstructionService.cs` | 注释更新（去掉对已删 `PackSystem` 的引用） |
| `proto/ui/bridge/OrcMatchRunner.cs` | `Packs` 属性类型 `IReadOnlyList<PackContent>?` → **`PackSet?`**（null＝由 `OrcEngineHost` 当前快照兜底） |
| `proto/ui/screens/Main.Diy.cs` | `ReloadPacks` 重写：改为 `OrcEngineHost.LoadPackCards(<res://proto/data/kards>)`，把静态投影喂 `CardCatalog.SetPackCards` / `DeckConstructionService.UpdatePacks`；装载失败＝警告日志，不抛到启动链 |

### 2.3 漂移修复（引擎枚举→字符串常量类）

| 文件 | 改动 |
|---|---|
| `proto/bridge/.../CardPoolCompiler.cs` | `UnitTypes` → `UnitType.TryNormalize`；`TryCategory`/`TryFaction`/`RarityOf` 返回 `string` |
| `proto/bridge/.../DiyCardCompiler.cs` | 兵种清单改遍历 `UnitType.All`；校验改 `UnitType.TryNormalize` |
| `proto/bridge/.../OrcMatchSession.cs` | `FactionOf` 返回 `string`（`Faction.Soviet` 等常量） |
| `tests/Stage6C2Tests.cs` | `Definition(..., string category)` |
| `tests/DeckConstructionTests.cs` | `SourceRaritiesReachNativeDefinitions(string rarity, string expected)` |
| `proto/bridge/.../OrcMatchSession.cs:627` | **保持引擎类型** `ActionInteractionState?`（**不是**契约的 `UiActionInteractionState`；桥接层负责二者转换）——**勿改回** |

### 2.4 数据与测试

| 文件 | 改动 |
|---|---|
| `tools/build_builtin_packs.py` | 产物由自造 `pack.json` 改为引擎 **`manifest.json`**（`kind=cardpack`；`guid` 由包 id 经 **uuid5 确定性派生**；`version="1.0.0"`；扩展包 `dependsOn:[{"id":"builtin","version":"1.*.*"}]`；`assets:[]` 留空）；输出布局改为 **`proto/data/kards/packs/<包id>/`**（对齐 `PackageDiscovery` 的 `<base>/packs` 约定） |
| `tests/PackSystemTests.cs`、`tests/BuiltinPackTests.cs` | **删除**（模型已不存在；等价验证在引擎侧 `tests/Orc.Packages.Tests/CardPackUiEndToEndTests.cs`） |

---

## 3. 引擎侧新增的界面数据接口（两处裁决，请确认）

1. **静态卡定义投影**：图鉴/构筑需要"无对局上下文"的 `UiCardDefinition`（投影是"对局级每帧"的）。裁决＝`Orc.UI.StaticCardProjection.Project(cardId, definition, artLookup, definitionLookup, text)`，与实例投影**同源**（同一实现），不另立第二套。
2. **provider 的 `definitionLookup`**：`Create(artLookup, matchId, definitionLookup)`。`matchId` 由前端提供（引擎无对局 id 概念，而 `UiMatchView.MatchId` 是必需键）；`definitionLookup` 供前端注入**富定义**（美术/文本/flavor）——引擎不参与渲染。
3. 另：`CardPackContent.Texts`（包内原卡 id → `texts.json` 原文）——供前端为卡包卡补齐卡面文本。

---

## 4. 保留的行为与降级说明

- **没有降级**：A.20 的"行动输入判定 + 阵型空隙部署"能力因补丁已入引擎而**保留**。
- **卡 id 前缀不变**：仍为 `pack/<包id>/<原卡id>`，`CollectionScreen` 的分包页与 `"pack/"` 约定**未受影响**。
- **`UiBattleActions`（可用性）仍由前端 `OrcActionReader` 产出**；`UiUpdateTranslator`（段→表现载荷）留前端——只有"卡与盘面投影"下沉后端。
- 旧模型的**进程级全局启用集**被移除：对局资产一律来自 `EngineHost` 的**快照**（对局内不可变）。

---

## 5. 已知问题与建议

### 5.1 前端测试运行：117/258 失败（**非本次重写引入**，需前端修夹具）

两大主因：

1. **测试硬编码原开发机绝对路径**：大量用例读 `H:\Working Folder\OrC-KSD.Godot\proto\data\nations`（本机仓库在 `D:\Godot-orcKsd-main`）→ `DirectoryNotFoundException`。
   **建议**：把测试数据根改为可配置（csproj 属性/环境变量），或改为相对本仓库的路径。
2. **内置词条注册面为空**：绕过 `OrcEngineHost` 的用例直接构造 `CardDefinition`（中文词条字面量如 `'烟幕'`），报
   `词条声明清单含未实现标识 '烟幕'（合法标识集＝词条注册面内容：**空**）`。
   根因：引擎资产化后，19 枚内置词条与 6 个卡数据组件由 **`orc.builtin.core` 插件的进程相**注册；`RegisterProcessDefaults()` 只在该包被装配时执行。
   **建议二选一**：
   - 夹具侧：在测试启动/基类里调用 `Orc.Plugins.BuiltIn.BuiltInPlugin.RegisterProcessDefaults()`（幂等），或统一走 `OrcEngineHost.Initialize(...)`；
   - 引擎侧：给 `Orc.Plugins.BuiltIn` 加 `[ModuleInitializer]` 调 `RegisterProcessDefaults()`（与既有注释"模块初始化与宿主进程相可各自调用"一致）。
     ⚠️ **本任务未实施**：它会改变 `KeywordRegistry` 的进程初值，可能冲击引擎侧"未注册标识被拒"的断言（当前引擎 1101 用例全绿），**请评估后再动引擎**。

### 5.2 `OrcMatchRunner.Packs` 的调用方

当前 `Packs` 为 `null` 时由 `OrcEngineHost` 当前快照兜底（等价于旧"进程启用集"行为）。若要显式选定本局包集，请由调用方（`Main.cs` 等）传入 `PackSet`。

### 5.3 卡包管理 UI 仍未实现（本期**有意不做**）

导入/启停/删除＝**交给前端开发者**。引擎只提供 API：`EngineHost`（`TryLoadCardPack`/`TryUnloadCardPack`/`TryReloadCardPack`/`RestoreFrom`/`LoadCardPacks`/`LockCardPacks`）、`CardPackLoader`、`RuntimeCardCatalog`。持久化（原 `packs.json` 那套"哪些启用"）与界面均需前端自理。

### 5.4 未能验证的部分

- **Godot 主工程 `Kards.Ui.csproj`**：本机无 Godot 4.7 .NET SDK，无法编译/运行。`proto/ui/**` 虽有源码改动（仅 `OrcMatchRunner.cs` + `Main.Diy.cs`），但**未经编译验证**，请在装有 Godot .NET 版的环境跑 `tools/run.ps1 -Test -Verify`。
- **运行时行为**（建局→投影→演出→行动判定/阵型空隙）：需在 Godot 环境实测。

---

## 6. 构建与验证方式（务必按此操作）

```powershell
# 引擎（d:\OrC-KSD）
dotnet build OrcEngine.slnx --no-incremental      # 期望 0 警告 0 错误
dotnet test  OrcEngine.slnx                       # 期望 1488 通过 / 0 失败

# 本仓库（须在仓库外目录执行以绕过 global.json 的 SDK 9.0.315 钉版本；或装 .NET 9 SDK）
cd D:\
dotnet build "D:\Godot-orcKsd-main\proto\bridge\Kards.Ui.OrcBridge\Kards.Ui.OrcBridge.csproj" `
  -p:OrcEngineRoot=d:\OrC-KSD\src\Orc.Game -t:Rebuild -nodeReuse:false
dotnet build "D:\Godot-orcKsd-main\tests\Kards.Ui.Tests.csproj" -p:OrcEngineRoot=d:\OrC-KSD\src\Orc.Game

# 引擎引用路径：OrcEngineRoot 默认指向不存在的 H:\Working Folder\OrC-KSD-github\src\Orc.Game
# 验错前先删 obj/bin（增量构建会给出"陈旧错误清单"：曾只报 5 条，实际 25 条）
Remove-Item 'D:\Godot-orcKsd-main\proto\bridge\Kards.Ui.OrcBridge\obj','D:\Godot-orcKsd-main\proto\bridge\Kards.Ui.OrcBridge\bin' -Recurse -Force
```

---

## 7. 后续建议步骤

1. 修 §5.1 的测试夹具（数据路径可配置 + 内置词条注册），把 117 条失败清零。
2. 装有 Godot .NET 的环境跑一遍端到端（§5.4）。
3. 实现卡包管理 UI（§5.3，引擎 API 已备）。
4. 若要"卡包词条/卡数据在运行时增删"的界面能力，可直接用 `EngineHost` 生命周期 + `RuntimeCardCatalog`（注意：**对局进行中禁止重载**；词条**不允许注销**，注销须冷重启）。
