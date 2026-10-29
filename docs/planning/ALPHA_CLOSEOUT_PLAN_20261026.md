# alpha 收口计划（2026-10-26）

背景：引擎随后要大改，`2.0.0-alpha.*` 这一代必须**完整落幕**——凡是这版对外承诺过的能力，
要么兑现，要么在 UI 与文档里明确降级，不留"看起来能用、实际不听使唤"的灰区。
本计划只收口，不开新功能面；引擎侧改动只做必要的最小补丁。

判据统一：**玩家视角能走通** ＞ 测试绿灯。每条都要写明"怎么验"，不接受"参数写了"。

---

## P1 数据安全（最高优先，先做）

| 项 | 现状 | 收口标准 |
|---|---|---|
| `--verify-diy` 红灯「复制→删副本」 | ✅ **根因已查明并修掉一半** | 见下方进度 |
| 卡库无备份 | ✅ **已修（2026-10-27 补做）** | 每次保存前留上一版（环形 3 份），失败可回滚 |

### P1 进度（2026-10-26）

`--verify-diy` 的红灯其实是**两个问题叠在一起**：

1. ✅ **已修（真 bug）**：从工坊点进单卡＝聚焦模式，左侧库列表整体隐藏，"删除/恢复原版"按钮跟着消失，
   而"复制/保存"还在。于是**复制一张卡、保存、想删副本——删除按钮够不到**。
   修法：把 `DeleteButton` 并入聚焦动作栏（与校验/构筑/试打/微调同一排）。
2. ✅ **已修（探针误导）**：那条断言是复合条件，把"对话框没弹 / 确认没生效 / 原卡被误删"说成同一句
   `Copy deletion damaged original`。已拆成三条，失败时直接说哪种。
3. ⛔ **新发现，未修**：构筑页搜不到刚保存的 DIY 卡（按卡名、按编号都搜不到）。
   而 `DeckConstructionService.PlayableIds` 里**有**这张卡——服务层认，UI 层不显示。
   这是 M2（自己卡组）的核心链路，列为 **P1.5，优先于 P2**。

已排除：`DiyCardLibrary` 的 Save→SaveCopy→DeleteCopy 单测通过，存储层无问题。

### P1 备份环收口（2026-10-27 补做）

**这条当初是计划里写着、代码里根本没有的**。`grep -r '\.bak\|Backup' proto/core/` 零命中——
也就是说"覆写式原子写"只保证了"不会写出半个文件"，从没保证过"写出一个逻辑上错的文件"还救得回来。

做法（`proto/core/BackupRing.cs`，环 3 格）：

- `Rotate(path)` 在每次 `.tmp` 写入**之前**调用：`cards.json` → `.bak1` → `.bak2` → `.bak3`。
- 为什么固定 3 格而不是时间戳：时间戳会让目录无限增长，而且"该保留哪一份"变成玩家要做的决定。
  3 格是"我可能手滑两三次"的量级，每格含义确定（bak1＝上一次保存前的样子）。
- **备份写不出去就让这次保存失败**（异常抛给调用方，调用方已有内存回滚）。
  反过来"备份失败但照样覆盖"是最糟的组合——玩家以为有后路，实际没有。
- `LoadError` 后面拼 `BackupRing.Hint(path)`，直接告诉玩家"上一版备份在 X，改名覆盖即可回滚"。
  这句话是真的显示在界面提示行上的，不是日志。
- 接入点：`DiyCardLibrary.Mutate`、`DeckLibrary.Mutate`、`DiyWorkshopLibrary.Save`。

怎么验：

1. `tests/BackupRingTests.cs` 11 条：格子语义、连存四次只留三份、空档容忍、`Newest` 回退、
   `Restore`、`keep:0` 空转；另有三条打真库——存两次后 `bak1` 是"第一版"、删卡后 `bak1` 还在、
   把主文件写坏后 `LoadError` 里含「备份」和 `.bak1`。
2. 手工路径（玩家视角）：改一张 DIY 卡存两次 → 把 `diy-cards.json` 手工改坏 → 重开 →
   提示行会指明备份文件；把 `.bak1` 改名盖回去 → 卡回到上一版。

验证边界（写清，不假装）：第 2 条**我没有亲手跑过**，写的是给下一个人的复现步骤。
"提示行会显示"这一步是读代码确认的——`LoadError` 被四处界面直接读进提示文本
（`DiyEditorScreen.cs:296`、`DeckLibraryScreen.cs:58`、`DeckEditorScreen.cs:356`、
`DiyWorkshopScreen.Navigation.cs:43`），不是只躺在字段里。但目前**没有 GUI 探针**去点这条路径，
`BackupRingTests` 里的三条集成测试覆盖的是"库层读失败时 `LoadError` 含备份提示"。
要做成探针，得在临时目录里造一个坏文件再拉起对应界面读提示行。

**写测试时逮到实现的一个真 bug**：原实现"先删最老格、再依次后退"，遇到空档
（比如 `bak2` 被手工删过）就会白丢 `bak3`——环里明明还有位置。改成从最老格往下搬、
只在真有东西搬进来时才覆盖，满环行为不变，空档不再丢历史。这条是测试红出来的，不是想出来的。

## P1.5 DIY 卡进构筑（新增，优先）— ✅ 已收口

| 项 | 现状 | 收口标准 |
|---|---|---|
| 构筑页不显示 DIY 卡 | ✅ 已修 | `--verify-diy` = `DIY_UI_VERIFY_OK`（含 builder / exact-deck） |

结论：**规则没错，错的是没说清**。卡组按国籍过滤卡池（DIY 卡 `Set = Nation`，默认 `av76`），
"用于构筑"把你送到构筑页、还把卡名填进搜索框，但卡被国籍筛掉 → 玩家以为卡丢了。三处改动：

1. **`Main.ConstructRequested`**：空卡组时把主国切成这张卡的国家（无代价）；有卡的卡组不动人家的构筑。
2. **`DeckEditorScreen.EmptyPoolReason`**：搜索结果为空时说明原因——"搜到了 N 张，但它们属于 X 国，
   当前主国是 Y；把对应国家设为主国或盟国后才能加入"。原来只会说"没有符合筛选条件的卡牌"。
3. **探针**（`Main.DiyVerification`）：改用合规卡组验证端到端；把 3 处复合断言拆开
   （"对话框没弹/确认没生效/原卡被误删"三合一、加卡失败与校验失败混判、`Verified==24` 写死）；
   网格查找从 `GetChild(0)` 改成全网格扫描（分页顺序不保证）。

## P1.6 探针自身的欠账 — ✅ 已收口（2026-10-27）

原来 `--verify-diy` 里有一处 `DIY_SKIP`（效果行 ↑/↓ 排序断言），记的原因是"长列表滚出可视区后
坐标点击会落空"。现在改成**真的点按钮**，不直发信号：

- `EffectRow.Up`/`Down` 从局部变量提成字段（`internal readonly Button Up, Down;`），
  `DiyEffectEditorScreen.CurrentEffects` 暴露"行里现在的值"。
- 断言读 `CurrentEffects`（界面当前值）而不是草稿缓存——缓存对不代表界面在改。
- 点完 ↑/↓ 还断言**保存后的草稿顺序**，界面和落盘两边都钉住。

复现时踩到的真原因，和当初记的不一样，值得写下来：
**不是"滚出可视区"，是控件不可见**。`DiyEffectEditorScreen.Open()` 会把标签页重置回
「图形搭建」并 `SetTabHidden(1,true)`，简易效果行所在的「结构编辑」页跟着整页隐藏；
**不可见的控件收不到 `PushInput` 的合成点击**，点击静默落空、不报错。
探针第一处进效果页后有一句 `ShowCompatibility()`，第二处（`Load()` 之后）漏了，于是红在这里。
修法：每次进效果页都先 `ShowCompatibility()` 再点。

诊断手法记一笔：先看 `[fx-move]` 打印在不在——**在＝逻辑问题，不在＝没点到**；
没点到就打 `IsVisibleInTree()` 和祖先链，一眼能看出是哪一层 `Visible=False`。
这比盯着坐标猜快得多。

验证：`-VerifyDiy` → `DIY_UI_VERIFY_OK mouse-save edit-reload copy confirmed-delete dirty-guard
invalid-and-valid-import export native-json catalog builder exact-deck quarantined-invalid
trial-mulligan native-deployment pack-zip-export pack-zip-import overlay-effects=0`；
日志里 `[fx-move] loading=False effects0=buff count=2` 与 `effects0=draw count=2` 两条，
就是两次真点击的痕迹（`loading=False` 说明不是加载期重建，是点击触发）。


## P2 DIY 选靶（这版的核心承诺）— ✅ 已收口

| 项 | 现状 | 收口标准 |
|---|---|---|
| 结构化 Ops 的单目标不弹选靶 | ✅ 已修 | 结构通道编译期转声明图，玩家选谁打谁 |
| 覆盖范围 | ✅ 已覆盖 | 10 个默认单目标各一条用例；`DiyTargetingTests` 的 Skip 已删 |

修法：新增 `OrcStructuredGraph`（结构化 op → 声明图），`OrcEffectEditing.Compile` 对
`Sel="one"` 的 op 改走 `DesktopNativeLowering`（`csx` + `AuthoringTargetContext` 预打出捕获），
其余 op 仍走已验证的 op 模板。声明图同时要在两处装配：对局（`OrcMatchSession`）与联机工坊导出
（`NetworkWorkshopSession` 的 `desktop-effects.json`）——少一处，对方装包后就退回无头选靶。

为什么必须换通道：op 模板在 `sel=one` 时走 `runtime.SelectAsync`（无头，取候选第一个）；
声明图才有 `PrePlayTrigger` 捕获 + `TargeterManager` 交互，执行时直接取回玩家点的那一个，不会问第二遍。

验证：`DiyTargetingTests` 15 条全绿（文本通道 1、结构化伤害 1、10 个 op 各一条、混排 1、指令 1、导出往返 1）。
每条都断言"选靶面板恰好弹一次" **且** "只有玩家点的那个单位生效"——只断言前者会漏掉
"问了但答案被丢掉"，只断言后者在单候选时分不清。`--verify-ui` 仍是 `UI_VERIFY_OK`（12 屏 / 534 张卡）。

验证边界（写清，不假装）：GUI 探针 `--verify-diy` 里没有"场上有可点的敌方单位"这一步，
所以结构化选靶的**面板**没有自动化点击证据；面板本身由 `--verify-ui` 的原生终端步骤覆盖
（`one-result-after-deployment`），结构化通道则由 `DiyTargetingTests` 覆盖到 session 层的
`TargetRequested` 与落点。要补 GUI 那一步，得先在试打里造出敌方单位再点面板——留给
Inspector 重做时一起做（见 P1.6 同类欠账）。

诚实降级的边界（不是遗漏，是刻意）：

1. 只有 `deploy_basic` / `played_basic` 有"打出前"时机；回合开始、受伤、亡计等触发的单目标
   仍由引擎无头挑（那时没人能应答）。
2. 阵营筛选与"排除自身"在选靶声明里没有无损字段 → 这类 op 留在 op 模板，不悄悄放宽成"任意单位"。
3. 期限只支持永久／本回合结束；"下个己方回合开始"没有对应值，同样留在 op 模板。
4. `reveal` / `upgrade` 在 DIY 稻草人身上本身是空操作（没有隐蔽标记、没有老兵形态），
   用例只验选靶通道，不验揭示/晋升语义。
5. **简易效果行（旧卡兼容）** 的单目标仍是引擎自动挑：那条通道没有"打出前"时机，
   已在效果编辑器里当面写明并指向结构编辑。要交互选靶只能用结构编辑。

## P3 DIY 内容分发 — ✅ 已收口

| 项 | 现状 | 收口标准 |
|---|---|---|
| 卡包整包导出/导入 | ✅ 已修 | DIY 包可导出 ZIP（国家、词条、卡图、展示文本齐全），对方导入即用 |
| 分享前对账 | ✅ 已覆盖 | 导出时把"失效引用"拦下；拦不住的一律明确提示 |

修法：新增 `DiyPackExport`（`Check` 只读检查 + `Export` 打包）。导出**复用**
`NetworkWorkshopSession.Prepare`——它本来就是"DIY 草稿 → 原生卡包目录"的唯一编译器（联机工坊也走它），
再 `WorkshopPackageArchive.Export` 压成 ZIP。不另写第二份编译器：两份编译器一定会漂，而漂的代价是
"我这边能导、对方那边装不上"。

UI 上的死路（`DiyWorkshopScreen.Library.cs` 的 `if (native is null) return;`）改成
`BuildCompatiblePackageActions`；同时把「导出卡包 ZIP…」放进**卡包详情页的标题栏**——
玩家打开自己的包就该看见分享入口，而不是绕去"修改卡包"面板（那处也保留，两处共用
`AddCompatibleExportButtons`）。

导出前对账拦住这四类（`DiyPackExport.Check`）：空包、对原生卡的本地修改（`SourceCardId` 非空，
包里没有那张原始卡的元数据，必然打不出来）、`DiyCardCompiler.Validate` 不过的卡、卡图文件已丢。
词条编译失败也算。扩展包缺基础包**只提示不拦截**（包本身完整，对方先装基础包即可）。

另外补了一条以前没人管的链路：**词条名字**。规则在 `network-vocabulary.json`（只有
`kw-…<hash>` 这种引擎 id），名字以前压根不随包走——收包人的卡面会显示成
`◇ kw-76e1c7658f9a…`，规则对、名字没有。现在名字进 `workshop.presentation.json` 的 `keywords`
段（和 `nations` 同构），收包人侧由 `NativeWorkshop.ReadKeywords` +
`OrcEngineHost.NativeKeywords`（按展示文件的时间戳/长度缓存，因为它在卡面渲染路径上）还原，
`WorkshopAuthoring.KeywordLabel` 加了一级回退。

验证（`DiyPackExportTests`，4 条全绿）：

1. `DIY卡包导出ZIP后对方导入即可用`——走完整条链路：草稿库 → 导出 → **另一台机器**的工坊目录
   `NativeWorkshop.Import` → 断言卡进了卡池、无诊断、已启用；再断言国家在
   `workshop.presentation.json`、词条 id 与 `network-vocabulary.json` 一致、卡图字节相同、
   风味与卡面文本都在、卡名在卡定义里。最后清空本地草稿库（模拟"收包人没有我的包"），
   把包真正装进引擎宿主，断言 `KeywordLabel(引擎词条 id) == "成长"`、
   `NationLabel(国家 id) == "测试国"`——这一格是玩家在卡面上真正看到的东西。
2. `分享前对账把坏卡挡在导出之前`——删掉卡图后导出，断言返回 null、**文件不存在**、原因里点名哪张卡。
3. `空包与原生卡本地修改都不许静默导出`。
4. `扩展包缺基础包只提示不拦截`——ZIP 必须真写出来，那句话只能出现在提示里
   （这条是回归：初版把提示当成拦截，扩展包一张都导不出去）。

GUI 证据：`tools/run.ps1 -VerifyDiy` 新增两步（`pack-zip-export` / `pack-zip-import`）——
打开自己的卡包 → 找到「导出卡包 ZIP…」按钮 → 点它 → ZIP 真的落盘 → 用「导入卡包 ZIP…」
同一个入口装回去，断言卡数 1、无诊断、已启用。无头模式下原生文件对话框弹不出来，所以
`DiyWorkshopScreen.VerificationFileTarget` 是给探针留的缝（正常运行为 null，走真对话框）；
不加这个缝，这条链在探针里只能靠读代码。

验证边界（写清，不假装）：GUI 那两步验的是"按钮接上了、ZIP 出来了、对方装得上"；
国家/词条/卡图/风味齐全这套内容级断言由 `DiyPackExportTests` 覆盖，探针里没有重复做。
`-Verify` 仍是 `UI_VERIFY_OK screens=12 cards=534`。

顺带修好的环境问题：`dotnet test` 直接跑会有 5 条 `NetworkWorkshopTests` 报
`找不到 Orc.Server.dll`——那是没设 `ORC_ENGINE_ROOT`（`tools/*.ps1` 会设）。设上之后
**651/651 全绿、0 跳过**，此前记的"5 条已知失败"是调用方式问题，不是代码问题。

诚实降级的边界：

1. 对原生卡的本地修改（`SourceCardId` 非空）不能随 DIY 包导出——包里没有原始卡的元数据。
   `Check` 直接说明，而不是打出一个对方装不上的包。
2. 扩展包不把基础包一起打包：只提示"对方需要先装基础包"。要自动带上基础包，得先决定
   "分享基础包算不算越权"，这是产品决定，不是技术缺口。
3. 导出是**一次性快照**：对方改不了我的包，我也拿不到他的改动。双向同步不在 alpha 范围。
4. 词条名字走展示层，所以手改坏 `workshop.presentation.json` 只会丢名字（卡面退回显示引擎 id），
   不会让整包卡消失——和 `PackPresentationData` 的不变量一致。

## P4 能力目录诚实化 — ✅ 已收口

| 项 | 现状 | 收口标准 |
|---|---|---|
| 161 个动作里 62 个可执行（2026-10-29 复测；原 57） | ✅ 已分区 | 选择器分区：「alpha 可用」/「预览（后续引擎版）」，并说明 |

**判据来自编译器，不是手写名单**：新增 `DiyOpCatalog`，对每个动作拿一份默认声明过一遍
`DiyCapability.OperationProblem`（真实的 `DesktopNativeLowering`），过不去的进"预览"。
引擎补上机制后这一格自动变绿——不会出现"文档说能用、代码里其实早就好了"这种反向漂移，
也不需要有人记得回来改名单。

实测（2026-10-27，`DiyOpCatalogTests` 记账并断言）：目录 **161** 个，alpha **57** / 预览 **104**。
分组：单位与总部 30/41、卡牌 13/40、资源与环境 6/10、流程 5/5、记录 3/8。

**2026-10-29 复测（切 v4 之后）：161 个，alpha 62 / 预览 99。**

中间掉过一次 55 / 106，掉的那 2 个不是引擎没了机制，是 `InspectorVocabulary.Action` 拼默认声明时
把目录的字段表整份塞进去了。那张表是**按原语汇总的并集**——`heal` 里列着 `attack`／`defense`
（那是 `changeAttribute` 的参数），`changeAttribute` 里又列着 `keyword`／`opCostMod`——而 OrC 侧的
正常化器是**按"哪些字段在场"来认变体**的，塞满并集就把形状撑坏了，22 个原本能跑的动作全被拒。

修法不是回头"跳过可选字段"（那样 `conditional`／`repeat`／`setVar` 会塌成 `{"op":"conditional"}`，
OrC 侧对缺失字段的 `undefined` 再 `JSON.parse` 一次就是 SyntaxError，条件与循环直接不存在），
而是**先拼精简形状、过不了编译器才退回全量**：判据还是问编译器，不写死名单。结果 62 / 99——
条件、循环、变量这三个复杂 DIY 最需要的东西回来了，另有 `reveal`／`setStats`／`upgradeSelf`／
`removeKeyword` 四个同类（全字段 `optional`）一并转正，且**零回归**。
`DiyOpCatalogTests` 里 62/99 与这 7 个名字都被钉住，掉了会红。

界面（`NativeEffectInspector.Friendly.cs`）：

- 选择器里两个**不可选表头**：「✓ alpha 可用 (62)」「○ 预览（后续引擎版） (99)」，
  表头用金色/灰色区分。
- 说明写在选择器下方那行常驻文字里（"动作目录 161 个：62 个这版能真跑（✓）…"），
  **不是"首次弹一次"**。理由：能跑的动作会随引擎版本变，一次性提示必然过期；
  这行每次打开都说当前实话。也没有另开一行 Label——对话框是固定高度的，多一行会把
  「添加这个动作」挤出可点区域（实测：探针的坐标点击会直接落空）。
- 预览动作**不灰掉**，改成选中后把那版编不出来的原因写在那行说明里。原来是"灰掉 + tooltip"，
  手机上根本没有 hover，玩家只看到"点了没反应"。玩家路径下「添加这个动作」随之关掉
  （等价于原来的不能加，但原因可读）；作者路径仍可加进草稿——检查器本来就是"先草稿、后编译"。

**不裁剪**：全部 161 个动作一个不少地留在选择器里，只是排在预览下面并写明原因。
藏起来才是真的不诚实——玩家会以为这版没有这个能力。`Main.DesktopAuthoringVerification`
原来断言 `ItemCount == 161`，插表头后改成断言 `PickerOpCount == 161`（数真正可选的动作行），
守住同一条不变量。

验证：

- `DiyOpCatalogTests` 2 条：目录不裁剪/不重复/与词表逐项一致；分区判据与编译器**逐条**重算一致；
  分区顺序成立（所有 alpha 在 preview 之前，表头只画一次靠这条）；161/62/99 记账，
  并把 `conditional`／`repeat`／`setVar` 等 7 个"修好的"动作钉在 alpha 区防回退。
- `-VerifyAuthoring`（本轮新增开关）→ `DESKTOP_AUTHORING_UI_VERIFY_OK … operations=161`。
- `-VerifyNavigation` → `NAVIGATION_VERIFY_OK`（这条探针也走动作选择器加 `drawOne`）。
  注意它**不带 `--headless`**，且走 `run.ps1` 时前面还有一段静默的资源导入，可能十几分钟没有输出——
  别误判成挂了。直接对已 prepare 的构建目录跑这条探针是 33 秒出结果。
- `-Verify` → `UI_VERIFY_OK screens=12 cards=534`。
- 全量 `dotnet test` 653/653、0 跳过（收口时）。本轮修完探针后重跑 **654/654、0 跳过**——
  多的 1 条在 P2/P3/P4 那批未提交的测试文件里，本轮没有改任何测试文件。
  注意必须走 `tools/run.ps1 -Test`：它先设 `ORC_ENGINE_ROOT` / `ORC_SERVER_DLL`，
  直接 `dotnet test` 会有 7 条 `NetworkWorkshopTests` 因找不到 `Orc.Server.dll` 变红（环境问题，不是代码问题）。

验证边界（写清，不假装）：

1. "能真跑"的判据是**默认声明**能不能编译，不是"任意参数组合都能跑"。alpha 只代表这个动作
   在检查器里排得出可执行效果，具体参数仍会走效果编译检查——tooltip 里也这么写。
2. 预览动作在作者路径下仍可加进草稿（会被标成 `DraftOnly`）。这是检查器的设计前提：
   draft-first 才能让引擎升级后旧草稿自动变可用。
3. 本轮顺手把 `-VerifyAuthoring` 接进 `run.ps1`，并修好它一直红着的两处**探针**问题
   （不是产品问题，产品经实测是对的）：
   - 动作选择器加了分区表头后，`ItemCount` 不再等于动作数 → 改数可选行；
   - 组合设置是独立 `Window`，探针的合成鼠标事件点「应用修改」会落空（实测按钮位置
     `(428,412) 96×49` 在窗口内、按钮可点，直接 `EmitSignal(Pressed)` 就能正确写入
     `{"expr":"math","operator":"add","items":[1,1]}`）。现在落空时退回信号直发并打印
     `AUTHORING_NOTE composer-apply-click-missed` 留痕，**不静默当成点过了**。
     合成鼠标坐标在子窗口里的偏移没根治，记在这里当欠账。
4. 本轮另修好一处**探针自身的环境依赖**（同样不是产品问题，产品行为经实测是对的）：
   `-Verify` 曾两次全绿、之后突然报 `A light unit was not placed gently`。根因不是 P4，
   而是探针把**玩家保存的动效偏好**当成了输入：
   - `BattleSequence.SlamAsync` 在 `ReducedMotion` 打开时直接 `return`，**只有** `ImpactAsync`
     会发 `deployment-slam-*` 相位，所以那条断言在"减少动效"下永远不可能过；
     `Main.VerifyBridgeAsync` 的 `deployment choreography never played` 同因。
   - 更关键的是 `AnimClock.SetSpeed/SetReducedMotion` **无条件落盘** `user://fx_speed.cfg`。
     旧代码里"故意打开 Reduce Motion"那段探针（`VerifyCombatAsync` 末尾）在还原前抛错一次，
     就把 `reduced_motion=true` 留在了磁盘上，于是**下一次**运行才变红——看起来像随机 flaky，
     实际是跨进程的探针状态污染。实测确认过这个文件的内容，不是猜的。
   - 修法：整轮 UI 探针（`Main.VerifyUi`）先把动效归一化为 `Normal`＋关，退出时（成功/失败两条路）
     还原玩家真实偏好；`BattleScreen.VerifyAsync` 里另有一层同样的前置归一化，让它单独调用时也成立；
     那段故意开 Reduce Motion 的探针把还原移进 `finally`，失败也不会再写脏磁盘。
   - 判据：`reduced_motion=true` 的配置文件**故意保留在磁盘上**重跑，仍得
     `UI_VERIFY_OK screens=12 cards=534`；跑完文件内容不变（`speed=2` / `reduced_motion=true`），
     即探针既不读玩家的偏好、也不改玩家的偏好。
5. 顺带修好 `tools/run.ps1` 的一个真 bug：`-Test` **单独使用时不返回**。
   `$onlyTest`（第 73 行）本意是"只跑测试"，但它只跳过了资源导入那一步，
   流程照样掉进第 114 行的 `else`，以交互方式把游戏窗口拉起来——那个进程不会自己退出，
   于是 `run.ps1 -Test` 静默挂死（实测挂满 10 分钟，CPU 全在窗口进程上）。
   现在 `$onlyTest` 为真时跑完测试就 `return`，并打印 `[5/5] 只跑测试，跳过 Godot 运行。`。
   判据：`run.ps1 -Test` 2m18s 返回、退出码 0、末行就是那句提示。

## P5 存档与 ID — ✅ 已收口

| 项 | 现状 | 收口标准 | 结果 |
|---|---|---|---|
| 卡组引用 `diy/<guid>` | 删卡重导即断 | 卡组引用走稳定 id；断链时能给"重新绑定"入口 | ✅ 两条都做了 |
| 四处 `Schema != 1` 硬失败 | 无迁移，升级即失效 | 加 schema 迁移骨架 + 读失败时保留原文件并给可读提示 | ✅ 判定收成一处，并补掉一个真数据丢失漏洞 |

### 断链：为什么不能只改一处

先确认了一个前提：**导入时重新发号是刻意的**，`DiyCardTests` 与 `WorkshopTests` 都明确断言
"导入必然换号"（分享出去的卡不该和作者那张撞号）。所以不能靠改导入策略来解决断链，
必须"让引用能自己接上" ＋ "接不上时给人修法"两件一起做。

- **库层（自己接上）**：`DiyCardLibrary.ImportCard(text, packId)`——代码里带的编号在本机
  **空闲**时沿用，被别的卡占用时才另发新号。于是"删掉再导回来"是原地复活，卡组那条
  `diy/<guid>` 自动对上。返回 `(Card, ReusedId)`，界面因此能说清"这次是复活了原卡还是另存了新号"，
  而不是让玩家自己猜卡组会不会坏。
  - 不覆盖的保证没有被破坏：只在编号**空闲**时沿用，空闲＝没有现存卡牌。
  - 静态 `Import` 的旧语义原样保留；拆出 `Parse`（保留代码里的编号）与 `Import`（永远换号）
    两个语义，落盘路径走 `ImportCard`。
- **界面层（接不上时能修）**：断链行以前渲染成 `?  diy/8f3a…`，点标题没反应、`+` 是灰的——
  玩家既不知道发生了什么，也没有任何能做的动作。现在渲染成 `⚠ 这张卡在本机找不到了`，
  带 tooltip 说明常见原因，右侧给「重新绑定」：从本机 DIY 卡里挑一张顶替
  （`ConstructedDeck.Rebind` 只换编号、不动张数，换完是否合法仍交给 `Validate`）。
  说明行同时报"有 N 种卡在本机找不到了"。
  只列 DIY 卡：拿官方卡顶替会让"玩家自己那张卡"悄悄变成另一张，比留一条断链更糟。

### schema：判定收成一处，并修掉一个会吃掉原文件的漏洞

`SchemaVersioning`（`proto/core/SchemaVersioning.cs`）是四个库唯一的版本判定入口。
原来四处各写 `Schema != 1`，且都把失败折叠成"版本或内容无效"——玩家无法判断是文件坏了、
还是自己的游戏比存档旧，而这两种情况的处置完全不同。现在区分：

- 比当前新 → "来自更新版本的游戏，升级后再打开；原文件保留不动"
- 比当前旧 → "格式太旧，本版只支持 vN–vM；原文件保留不动"

`Migrate<T>` 是迁移入口，目前只有 v1 恒等——**没有假装已经有迁移链**（本轮没有 v2）。

**顺手修掉一个真的会丢数据的洞**：`OrcImportLibrary` 读失败时 `_rows` 是空的，
而 `Merge`/`Remove` 照样落盘——随便导入一行就把那份"读不懂但还有救"的原文件整份覆盖。
卡库/卡组/工坊三个库一直有这道闸（`Mutate` 里 `if (LoadError is not null) throw`），只有它漏了。
现在 `Merge`/`Remove` 先过 `GuardLoaded()`。

验证：

- `DeckIdentityTests` 8 条：删卡重导沿用原编号且卡组能解析；编号被占用时另发新号且不覆盖现有卡；
  `Parse` 保留编号 / `Import` 换号（两个语义分开钉住）；断链能被检出、`Rebind` 只换编号不动张数
  与其它字段；版本太新与太旧给不同的话且**原文件字节不变**、读失败后拒绝写入；
  `OrcImportLibrary` 读失败后 `Merge`/`Remove` 都抛且原文件字节不变；四处库共用同一判定。
- `-VerifyDecks`（本轮新增开关，以前这条探针只能靠整轮 `-Verify` 跑到）
  → `DECK_VERIFY_OK … broken-reference-rebind …`
- `-Verify` → `UI_VERIFY_OK screens=12 cards=534`（含新增的断链/重新绑定步骤，真点按钮）
- `-VerifyDiy` → `DIY_UI_VERIFY_OK … invalid-and-valid-import …`（导入对话框改成 `ImportCard` 后重跑）
- `-VerifyAuthoring` → `DESKTOP_AUTHORING_UI_VERIFY_OK … operations=161`（它直接用卡库增删）
- 全量 `run.ps1 -Test` → **662/662、0 跳过**（654 + 新增 8）

验证边界（写清，不假装）：

1. "稳定 id"的实现口径是"编号空闲就沿用"，**不是**"编号永不变"。真需要换号的情况
   （编号已被本机另一张卡占用）仍会断链，靠重新绑定修——两件事缺一不可，所以都做了。
2. 重新绑定只列本机 DIY 卡，不允许拿官方卡或导入卡顶替。
3. 迁移骨架目前只有 v1，**没有可执行的迁移步骤**；真升 v2 时改 `Latest` 并在 `Migrate<T>` 里加一条。
4. 「重新绑定」对话框的 OK 按钮，探针的合成鼠标又打不中（与 P4 组合设置同一处根因）。
   探针改为"点不到就退回信号直发"，并打印 `DECK_NOTE rebind-ok-click-missed` 留痕，
   **不静默当成点过了**；按钮本身可点、走信号能正确改写卡组，已实测。
   这是第二次撞上子窗口合成坐标偏移，根因仍未修，记在这里当欠账。

## P6 对局完整度 — ✅ 已收口

| 项 | 现状 | 收口标准 | 结果 |
|---|---|---|---|
| 回合计时器 | 无 | 我方回合可见倒计时；超时不强制（alpha 只做提示） | ✅ |
| 战报 | 只有 3 张缩略图，真实路径不记录 | 对局内可看完整行动序列（不要求回放） | ✅ |
| 弃牌堆 / 词条释义 | 无 | 详情面板可查 | ✅ |

### 数据来源：只有引擎给的，没有推的

`BattleLog`（`proto/core/BattleLog.cs`）只吃两样东西：表现步骤 `UiPresentationResolution`，以及前端自己
发起的动作。移动没有独立演出段，所以由动作本身补记；其余每一条都能在投影里找到出处。

刻意不记的（写进注释，免得以后被当成遗漏）：

- 资源点变化——顶部资源条已经逐点显示，写进战报只会淹没真正的行动。
- 逐张抽牌——按方合并成"抽牌 N 张"一条。
- `UiStatusKind.Damaged`——同一次伤害已经由 board-impacts 记成"受到 N 点伤害"，再记一条就是同一件事写两遍。

回合号以**投影**为准（`Observe(state.Turn)`，只增不减）。这不是洁癖：拖动进攻这类条目完全不带回合信息，
不同步就会写出一堆"0 回合"。本轮实测就踩到了——探针断言 `attack.Turn > 0` 直接红，改完才绿。

### 三个页签

- 战报：`第 3 回合 · 我方 · 部署 虎式（4 费）`。费用未知就不写"（? 费）"，不留问号给玩家猜。
- 弃牌堆：两方分开，弃牌与"手牌上限烧掉"都在这一堆里（用颜色区分）。对手那张引擎本来就不给牌面，
  所以写"弃掉 1 张牌（对手手牌不可见）"——**不是**"未知卡牌"。
- 词条：本局场上/手上所有词条，带参值（"装甲 2"）与携带它的卡名。

### 词条释义：宁可缺一条，不编一句

引擎**没有**释义字段：`KeywordRegistry` 只登记"标识 → 组件工厂"，内置内容在 `Orc.Plugins.BuiltIn`，
投影里也只有 id 与参值。所以这张表由界面侧维护（`proto/core/KeywordGlossary.cs`），收录规则是
**只收核对过的**，每条的注释写明核对依据的引擎源码位置。目前 11 条
（armor / blitz / fury / ambush / mobilize / pincer / suppressed / immune / intelligence / smokescreen / guard）。
没核对的返回 null，界面明说"引擎未提供这个词条的释义文本"。

老兵 / 隐蔽 / 被抑制不进词条页——卡面本来就有徽记，再列一遍只是噪声；其余一律列出，包括还没核对过释义的。

### 倒计时：超时只提示，不判负

我方回合在"结束回合"上方显示"我方回合 · 剩余 75 秒"，最后 20 秒变色，超时改成"已超时（不强制）"，
**不结束回合**。alpha 阶段强制超时会把正在想事情的玩家直接判掉，收益远小于风险。

### 验证

- `BattleLogTests` 17 条：真引擎弃牌（`OrcDiscardVerification` 现跑一局）分别进两方弃牌堆、我方带牌名、
  对手写"1 张牌"且不出现"未知"；每种表现步骤都能被折叠且不抛；回合号只增不减；疲劳记成伤害；
  抽牌按方合并；伤害带来源、阵亡单独一条；Damaged 不重复记；资源步骤不记；未知费用不留问号；
  终局行只在 over 时出现且缺标题时明说；2000 条上限丢最旧并计数；Reset 清干净；空文本不入库；
  词条表无空值、未知词条返回 null。
- `-VerifyDiscard`（本轮新增开关）→ `DISCARD_ANIMATION_VERIFY_OK checks=4 … discard-pile`
- `-Verify` → `BATTLE_LOG_VERIFY_OK entries=1 tabs=3 clock=我方回合 · 剩余 75 秒`、
  `BATTLE_STAGE2_VERIFY_OK … log-panel turn-clock`，末行 `UI_VERIFY_OK screens=12 cards=534 texture_cache=64/64`
- 全量 `run.ps1 -Test` → **679/679、0 跳过**（662 + 新增 17）

探针怎么验（玩家视角，不是"参数写了"）：

- 点「行动记录」按钮开面板 → 三个页签逐个翻 → 点「关闭」关上。全程走按钮信号，不直接改 `Visible`。
- 词条页签行数必须等于 `词条数 × 3`（标题 / 释义 / 携带者），且释义逐字等于词条表，或明说"未提供"。
- 倒计时：切到我方回合 0.6 秒后文本含"剩余"，再过 1.2 秒文本必须变了（证明在走），切到对手回合必须消失。

验证边界（写清，不假装）：

1. 超时不强制——这是刻意的产品决定，不是没做完。
2. 词条释义覆盖 11 个内建词条，其余显示"引擎未提供"；DIY 词条取作者在工坊填的说明。
3. 战报只读，不回放。
4. 上限 2000 条，超出丢最旧，面板顶部写明"更早的 N 条已超出记录上限"。
5. 倒计时是 0.25 秒轮询，窗口失焦/切走再回来秒数会跳（没做暂停）。
6. 战报按时间直排，没有按回合分组；条目多了想找"第 5 回合"只能靠肉眼。

### 截图暴露的两个问题（已修）

`-Capture` 会顺带拍 `artifacts/battle-log-{clock,0,1,2}.png`（倒计时正常态 + 三个页签）。第一版截图直接看出两处：

1. **倒计时被切字**：`我方回合 · 剩余 75 秒` 塞在 170px 的居中框里，两边各切一截；超时文案更长会更糟。
   改成右对齐、右缘与「结束回合」按钮对齐、框宽 255px。
2. **面板开着时露半个倒计时**：面板右缘正压这一行，屏幕上出现半截"余 75 秒"。
   现在面板打开时倒计时隐藏——玩家此刻在看战报，不需要这个数字。

这两条**只有看图才能发现**：探针断言的是"文本含剩余、1.2 秒内会变"，切掉两个字它照样通过。

### 顺手修的两个工具坑

- `run.ps1` 的增量编译偶尔会坚持一个源里已经不存在的错误：本轮实测——源里刚给 `BattleLog` 加 `Observe`，
  运行副本仍报 `CS1061`，紧接着 `-t:Rebuild` 立刻通过（两边 `BattleLog.cs` 字节一致）。
  现在编译失败会全量重建重试一次；真错误会原样再报一遍，不会被这条重试掩盖。
- 新增 `-VerifyDiscard` 开关：弃牌演出与弃牌堆以前只能靠整轮 `-Verify` 才跑得到。

## P7 联机收尾 — ✅ 已收口（密码与观战延迟明确降级）

| 项 | 现状 | 收口标准 | 结果 |
|---|---|---|---|
| 房间名 | 写死"OrC 作战房间" | 房主可起名，局域网列表显示的就是这个名字 | ✅ |
| 端口固定 + 映射提示 | 每次开房随机端口 | 可固定；说明公网要不要做映射 | ✅ |
| 加入失败重试 | 一次失败就结束，报错是英文 socket 文案 | 瞬时故障自动重试；明确结论给「重试加入」 | ✅ |
| 踢人 UI | 只有"批准/拒绝" | 已连接成员可移除，且二次确认 | ✅ |
| 密码 | 无 | —— | ⛔ 明确降级（见下） |
| 观战延迟 | 无 | —— | ⛔ 明确降级（见下） |

### 房间名：进广播，不只是进控件

`RoomSettings`（`proto/core/RoomSettings.cs`）存"想怎么开房"：房间名、是否固定端口、端口号。
口径与卡库/卡组一致——带 schema（走 `SchemaVersioning`）、原子写、读失败保留原文件并给可读原因。

名字的关键不是界面上那行字，而是**好友列表里看到的那一行**。所以改名走
`RoomAnnouncer.Rename`：宣告循环每 2 秒读一次字段，下一轮广播就带新名字，**不必重开房间**。
玩家打字与探针改值都走同一个 `ApplyRoomName(name, save)`——分成两份就会漂，
探针那份只改内存不通知宣告器，于是"改名后好友那边还是旧名字"这种 bug 探针永远抓不到。

### 固定端口：被占用不静默换，也不让玩家开不了房

`ServerLauncher.StartAsync(..., port:)` 把端口一路带到 `ServerOptions.Port` / `SpawnRequest.Port`。
固定端口启动失败时**不静默换端口**（那会让"我给好友的是 4717"变成谎话），而是退回自动分配，
并把"为什么这次地址变了"写在提示行上。端口说明随开关变化，直接写"公网联机要在路由器把
TCP 端口映射到本机；局域网或虚拟局域网直连不用"——玩家最容易卡住的一步写在按钮下面，比藏进帮助页有用。

### 加入失败：分开"会自己好的"和"已经定论的"

- 连接层失败（`SocketException` / `IOException` / `TimeoutException`）退避重试，最多 3 次，
  状态行显示"第 N/3 次尝试"。
- `ConnectionRefused` / `TimedOut` / `HostNotFound` / `NetworkUnreachable` 翻成人话
  （"对方没有在开房，或者地址/端口填错了"这类），玩家才分得清是填错还是对方没开。
- "房间已满/已开局"是**明确结论**，不重试，直接给「重试加入 地址:端口」——对方可能刚腾出座位。

### 踢人：移除＝断开＋释放座位，对局中等于认输

「入场申请」改成「房间成员」：上半是待处理申请（批准/拒绝），下半是已连接（移除）。
移除走管理通道 `conn.revoke`（引擎释放座位并断开该连接）。**对局中移除等同于该座位认输**，
这句话必须写在确认页上——不能让玩家点完才发现比分变了。房主自己不列出来（移除自己＝拆房间，
那是「离开房间」的事）。确认是**就地二次确认**，不叠第二个窗口：叠窗既容易点错，探针也点不中。

### 顺手修好的重连死路

`_needsReapply`：大厅阶段断线会释放座位并作废令牌，服务端给新连接的状态是 `connected`
——既不是 `rejected` 也不是 `revoked`。只看 `AdmissionState` 判断的话，「重新申请入场」按钮
永不出现，界面让玩家去点一个不存在的按钮。现在断线恢复失败时自己置位这个标记。
房主侧另加 `FeedRestored` 事件：房主的战场不在房间屏里装配，没有它，房主对局中掉线重连后
画面永久冻结，唯一出路"离开房间"还会顺手关掉服务端把好友一起踢下线。

### 密码：不做，且说清为什么

引擎协议 `session.attach` 的 `SessionAttachParams` 只有 Roles / Seat / DisplayName / Deck /
DeckConfig / Packages / Token，**没有口令字段**，服务端无从校验。客户端自校验等于假安全：
绕过客户端直接连，服务端照样收人。真要做必须动引擎协议 + `ServerHost`，属"引擎大改"范围，
而本计划的前提就是"引擎随后要大改，本轮只收口"。

现有的门是**房主逐条批准**（`conn.admit`）：比口令更严——每个连接都要人点头，
只是没有"一眼挡住陌生人"的过滤。发行说明与界面按这个口径写，不写"支持房间密码"。

### 观战延迟：不做，且说清为什么

观战者分两种：绑座 viewer（脱敏，只看到一方）与**不绑座 viewer（全知，看到双方手牌）**
（`ServerHost` 的角色注释写得很明确）。延迟只把"泄牌"推后几十秒，挡不住它——
慢节奏卡牌里 30 秒前的信息照样有用。真正的修法是限制观战视角，那是引擎侧角色设计，
同样留到引擎大改。所以不做延迟，改为在发行说明里写清"观战者能看到双方手牌"。

### 验证

- `RoomSettingsTests` 15 条（8 个用例 + 端口区间 7 个取值）：默认值下 `RequestedPort == 0`
  （不固定端口时不偷偷固定）、保存重读、房间名去控制字符/截断、端口只收 1024–65535、
  非法值回落默认、存档比游戏新与格式太旧的**提示不同**且**原文件字节不变**、读失败后 `Save` 抛
  且不覆盖原文件、多余字段不静默吞掉。
- `-VerifyRoomSettings`（本轮新增开关）→ `ROOM_SETTINGS_VERIFY_OK fixed-port name-in-announce
  rename-without-reopen members-list remove-confirm`。断言落在别人能看见的东西上，不看控件文字：
  ① 服务端真的绑在请求的那个端口上（先绑 0 探一个空闲端口，再让房间开在那儿）；
  ② 宣告器里的名字；③ **另一个房间屏的监听器在环回广播上收到的名字**（名字真的走了 UDP）；
  ④ 改名后广播跟着变（不重开房间）；⑤「已连接」里恰好一行（房主自己不列）；
  ⑥ 移除要过二次确认，确认后**服务端连接表里再没有这个人**。
- `-Verify` → `UI_VERIFY_OK screens=12 cards=534`（P4/P5/P6 的老断言没被这轮改动碰坏）。

验证边界（写清，不假装）：

1. 房间名只做展示，**不参与鉴权**（见上"密码"）。
2. 固定端口被占用时退回自动分配并提示，不重试同一个端口；玩家要换个号得自己改。
3. 「重试加入」只在失败后出现，点它会重走完整加入流程（含 3 次退避重试）。
4. 移除的确认页是就地替换列表内容，不叠子窗口；关掉窗口等于取消。
5. 子窗口合成点击打不中仍是欠账，见下。

### 子窗口合成点击：第三次撞上，这次查清了不是坐标问题

P4 组合设置的「应用修改」、P5「重新绑定」的 OK、P7「移除/确定移除」，三处探针的合成鼠标都点不中。
本轮按"第三次就该改根因"的要求动手查了，结论是**坐标算法不是原因**：

实测把同一个控件按三种方式推事件，`GuiGetHoveredControl()` 全是 `null`——
① 推到对话框自己的 viewport（canvas 坐标）、② 推到同一个 viewport（屏幕坐标）、
③ 推到主 viewport（老写法的屏幕反算坐标）。事件根本没进对话框那一层。
对话框的 `GetWindow() != GetWindow()` 成立、`GetViewport()` 也不是根 viewport，
位置与尺寸都正常（`winPos=(480,290) winSize=(640,320)`），按钮 `IsVisibleInTree() == true`。

所以这不是"落点偏了"，是 headless 下事件进不到子窗口。三处探针一律保留
"点不到就退回 `EmitSignal(Pressed)` + 打印留痕"（`AUTHORING_NOTE` / `DECK_NOTE` / `ROOM_NOTE`），
**不静默当成点过了**；按钮本身可点、走信号能正确改状态，已实测。
下一步要么把这类对话框从 `Window` 改成屏内 `Control` 覆盖层（这样合成点击与其它屏一致，
顺带省掉移动端弹窗的观感问题），要么用键盘驱动（`GrabFocus` + `ui_accept`）。两条都没在本轮做。

## P8 手机与进程 — ✅ 已收口（一项补做，一项验证后发现引擎早已实现）

| 项 | 现状 | 收口标准 | 结果 |
|---|---|---|---|
| 切后台掉线的显式提示 | `_Notification` 只处理返回键 | 回前台主动确认连接并说出结论；单机与短切出不吭声 | ✅ 本轮补做 |
| 服务端子进程随 UI 退出 | 计划按"待做 `--parent-pid`"立项 | UI 被杀后子进程自行退出、端口能再绑 | ✅ 引擎已实现，本轮实测确认 |

### 切后台：先确认，再说话

`_Notification` 以前只接 `NotificationWMGoBackRequest`，`NotificationApplicationPaused/Resumed`
一个都没接。手机被挂起时 socket 断掉，玩家回到前台看到一张不动的棋盘——**没有任何解释**，
这正是最容易被当成"游戏卡死"的形态。

现在回前台做三件事，顺序不能反：

1. **先确认连接还在不在**（`RoomScreen.ProbeConnectionAsync` → `room.info` 真往返一次），
   而不是拿"没有报错"当"还连着"。半开连接（对端进程没了、TCP 还没超时）只有真发一次请求才看得出来。
2. **再说结论**。文案只有四种，全部集中在 `Main.Lifecycle.cs` 的常量里，探针断言同一份常量：
   - `切到后台期间连接断了 · 正在重连…`
   - `切到后台期间连接可能已中断 · 正在确认连接…`
   - `已回到前台 · 连接正常` / `已恢复连接`
   - `连接没有恢复 · 请返回房间重新加入`
3. **重连一旦开始就换成"正在重连"**，并且**一直留着**，直到重连真的结束——
   重连不可能成功时绝不能翻成"已恢复"。探针专门用"杀掉服务端子进程"来钉这一条。

两种不吭声的情形是刻意的：**单机对局**断不了，弹"连接可能中断"纯属制造焦虑；
**3 秒以内的切出**（权限弹窗、来电悬浮条）多半没断，说了反而误导。阈值是
`BackgroundNoticeSeconds`，经验值。

顺手把 Android 前台服务重新点亮（`RoomScreen.ReassertHosting`）：系统在后台可能回收服务，
唤醒锁与组播锁随之丢失，而 UI 里的 `_server` 还在跑——玩家看不出区别，好友却已经连不上了。
`startForegroundService` 是幂等的，服务还活着就是一次多余的 `onStartCommand`。

### 探针抓到的真问题：重连中切后台，一句提示都没有

第一版把"是否挂着远端会话"写成 `_room.Feed is not null || _feed is RemoteMatchFeed`。
探针第四步（杀掉服务端 → 转重连 → 切后台 → 回前台）直接红了：`paused network=False`。
原因在 `BeginReconnect()`——它进入重连时**先把 `_feed` 置空**。于是"正在重连"这个状态
在判断里等于"没有会话"，玩家恰好在最需要说话的时刻拿到一句不吭声。

修法是把 `IsReconnecting` 也算进会话判断。这条不是推想出来的，是跑红了才发现的。

### 子进程随 UI 退出：不用改代码，需要的是验证

计划里写的是"待做 `--parent-pid`"，那个结论来自在本仓库 grep 不到——因为它在**引擎**里：

- `Orc.Manager/ManagedInstance.cs` 起子进程时自动追加 `--parent-pid <Environment.ProcessId>`，
  而这个管理器就跑在 UI 进程内，所以传的正是 UI 自己的 PID；
- `Orc.Server/ParentWatchdog.cs` 每 2 秒轮询父进程，父进程消失即 `Environment.Exit(0)`。

所以这一项**不需要改代码**，需要的是**证明它在真机上成立**。做法分两半：

- 进程内探针 `-VerifyHostWatchdog`：开真房间，打印
  `HOST_WATCHDOG_READY port=… serverPid=… uiPid=…`，然后原地等 45 秒；
- 进程外驱动 `tools/watchdog-probe.ps1`：只杀 UI（**不带 `/T`**，模拟崩溃/被系统回收），
  然后看子进程是否退出、端口能否再绑。

实测（2026-10-27）：`childExited=True elapsed=2.1s portRebindable=free parentPidMatches=yes`
→ `WATCHDOG_OK`。子进程命令行里确实是 `--parent-pid 25116`，25116 就是 UI 进程。

顺带给 `ServerLauncher` 加了 `ProcessId`：排障需要它——"管理通道里的实例"和"进程"不是一回事，
子进程赖在后台占端口时只有 PID 能对症。

### 验证

- `-VerifyBackground`（本轮新增开关）→ `BACKGROUND_RECOVERY_VERIFY_OK short-silent long-confirms
  alive-recovers gone-stays-reconnecting`。四段断言：
  ① 单机切后台再回来，没有任何提示；② 1 秒的切出不提示；
  ③ 长切出且连接还在 → 先"正在确认"、再"连接正常"、然后自己收掉；
  ④ 杀掉服务端后再长切出 → 停在"正在重连"，4 秒后仍是"正在重连"（不许谎报恢复）。
- `-VerifyHostWatchdog` + `tools/watchdog-probe.ps1` → `WATCHDOG_OK`（见上）。
- `run.ps1 -Test` → 694/694；`-Verify` → `UI_VERIFY_OK`（老断言没被碰坏）。

验证边界（写清，不假装）：

1. **没有真机验证**。桌面不发 `paused/resumed`，Windows 上那条 `_Notification` 分支永远进不去，
   所以"通知接线"只有代码阅读，"行为"靠直接调同一对方法。Android 切后台仍待真机验。
2. 3 秒阈值是经验值，没有按机型调过。
3. 提示是顶部横幅（`MouseFilter = Ignore`），不挡操作；它**不负责重连**——
   重连仍由房间屏既有的 2.5 秒循环驱动，这里只负责把状态说出来。
4. `ReassertHosting` 只在 Android 生效，桌面恒返回 false，探针验不到。
5. 子进程退出依赖引擎的 2 秒轮询，不是即时；PID 复用窗口在服务端这种短命进程上可接受
   （引擎注释里已写明这个取舍）。

## P9 发版收口 — ✅ 已发版 2.0.0-alpha.6（2026-10-28）

| 项 | 状态 |
|---|---|
| `VERSION` / Android 版本 | `2.0.0-alpha.6`，`versionCode=11` |
| Windows 全量 | `dist/releases/2.0.0-alpha.6/full/OrC-KSD-2.0.0-alpha.6-win-x64.zip`（1,223,387,432 字节，本机保留不上传） |
| 增量补丁 | `from-E78E6CD6D59B-to-12CD07C37C83`，6,908,561 字节（alpha.5 → 本版，45 个文件变化） |
| 更新器 | 1.2.0（未升版，补丁校验规则没变） |
| 构建标识 | `12CD07C37C83A93F788897D864A9801C4B4DF7BF56110BA8CEBDEF94CC8264542` |
| 升级验证 | 真实更新器 `GODOT_UPDATER_VERIFY_OK` → 逐文件一致 → 玩家文件未被改动 → 升级后 EXE `EXPORT_VERIFY_OK` |
| Android | 签名 APK，`arm64-v8a` + `x86_64`，`versionCode=11` |
| 发行说明 | [RELEASE_2.0.0_ALPHA6_20261028](../release/RELEASE_2.0.0_ALPHA6_20261028.md) |
| `check-consistency.ps1` | `CONSISTENCY_OK` |

### 发版时踩到的三件事（写下来，下次省一次排查）

1. **引擎侧不是"没动"**。`4c238db`（10-25 23:39）比 alpha.5 打包时间（22:09）晚，所以本版带一个引擎提交，
   累计补丁要按 `a0b3511..HEAD` 重新生成，不能照抄 alpha.5 那份。
   **判据：比的是"打包时间"，不是"我记得没改过"。** 补丁生成后用 `git worktree` 拉一个 `a0b3511` 的干净树
   `git apply --check` 过一遍，确认能干净应用。
2. **`prepare-android-platform.ps1` 缺 BOM**。它是 `tools/` 下唯一一个"有中文却没有 BOM"的脚本，
   PowerShell 5.1 按 ANSI 读会把中文和引号配错，报的是 `Unexpected token 'RoomHostService.java')`——
   看起来像脚本写错了，其实是编码。已补 BOM（内容未改）。这就是交接文档里"外部 .ps1 不要写中文"那条坑的翻版。
3. **引擎 `Orc.Server.Tests` 有 1 条时序用例偶发红**：`MatchStartedNoticeTests` 在断言"房主侧收到入场信号"时
   没有等待（只等了客人侧），整机满载并行跑时可能抢不到。单独复跑 5/5 通过，与本版改动无关。
   发行说明里如实写了，没有粉饰成"全绿"。

### 发版后仍然悬着的

**ARM64 实体机验收**——模拟器只能替一部分；P8 的切后台提示、前台服务重开、ARM64 打包都没上过实体机。
本版发行说明已明确写"不宣称实体手机实测"，没有把模拟器结论冒充真机结论。

---

## 执行顺序与理由

P1 → P2 → P3 → P4 是"这版承诺的 DIY 能力"，必须做完；
P5 → P6 → P7 → P8 是耐用性，能做完最好，做不完要在发行说明里写清边界；
P9 永远最后，且必须工作区干净。

**不做的事**：不重构引擎侧选靶语义（等引擎大改）、不加新的对外功能面、
不为过时方案（指令锁步）续命。
