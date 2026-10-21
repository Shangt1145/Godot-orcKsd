# Beta.4 暂停交接（2026-10-21）

## 先读：本次没有完成交付

用户最后要求：**“你不要把毛坯房当商品房啊！”**，随后要求 **“写交接，，今天到这里”**。

当前代码是未提交的开发现场。ZIP 导入与 AssetEditor 的最小流程已经贯通，但新增原生编辑页仍是基础实现，**没有达到用户要求的完整界面与交互质量**。不要把“测试通过”“能编辑几个字段”“能部署一张卡”解释为整个 Beta.4 已完成。

今天到此停止功能开发。**本次没有提交、推送、打标签、制作 Beta.4 安装包或补丁。** 用户先前授权“做完推送加打包”，这是后续完成并验收后的动作，不是现在发布半成品的理由。根目录 `VERSION` 仍是 `0.0.0-beta.3`。

## 1. 用户范围与不可越界项

- 本轮目标是 Beta.4 的卡包导入、原生编辑器接入和相关 UI，且用户明确说 **“联机先别做”**。
- 对局界面的布局与视觉不要重新设计。此前另行提出的三项对局 bug 已获授权并已修复，保留这些改动。
- 引擎在另一仓库，前端不改引擎规则、AI 决策、伤害或胜负。UI 渲染引擎给出的状态、动作与事件。
- DIY 首页继续是“卡包／扩展包”两张大卡，选择时保留用户视频参考的正面横向展开、收拢抉择动画。导入只能作为次级工具栏入口。
- 内置、自带内容也要能编辑；不能直接写发行缓存。复杂规则、未知字段、中文国家、词条、插画和旧草稿不能因适配而静默丢失。
- Inspector 应清楚呈现通用的时机、条件、目标、动作与参数，不能堆一页技术字段，也不能做成具体卡牌专用触发预设。
- 用户要的是能交付的界面和完整操作流程。基础数据链验证只算开发证据，不是视觉验收。

## 2. 仓库与工作现场

| 项目 | 当前状态 |
| --- | --- |
| UI 源码 | `H:\Working Folder\OrC-KSD.Godot` |
| 分支 | `main`，工作区有本轮未提交改动 |
| UI HEAD | `8d1f937dce1a91901d7a2651c292c8e131e7cc63`，已发布 Beta.3 |
| UI remote | `https://github.com/Shangt1145/Godot-orcKsd.git` |
| 引擎源码 | `H:\Working Folder\OrC-KSD-github` |
| 引擎 HEAD | `21e537e483cd31f29a7212efef0187cd9ab56bf5` |
| 引擎工作区 | 交接时 `git status --short` 为空；本轮未修改引擎源码 |
| 调试副本 | `H:\g\kards`，不作为源码真源 |
| 当前版本 | `0.0.0-beta.3`，没有升为 Beta.4 |

交接时没有发现仍运行的本轮 Godot 验证、发布或打包任务。不要清理用户已有运行进程。

**有一个本轮没有创建、也没有查看内容的未跟踪文件：** `docs/design/MULTIPLAYER_LOCKSTEP_20261021.md`。它在开发过程中出现在工作区。保留它，不要把它自动加入本轮提交，也不要据此开展联机。

参考文件：

- [Beta.4 调研方案](BETA4_UPDATE_RESEARCH_AND_PLAN_20261020.md)：后端 API、阶段安排、已知缺口。它是方案，不能整篇当作已完成项。
- [P0 修复验收](../acceptance/BETA4_P0_IDLE_TURN_AND_CARD_FACE_20261021.md)：此前三项 bug 的实现与证据。
- 用户交接原文：`C:\Users\Alan\Downloads\前端交接文档.md`。
- 后端原生示例：`H:\Working Folder\OrC-KSD-github\samples\card-pack-space-war`。

## 3. 已完成但尚未发布的 P0 修复

这些改动在本轮卡包工作前已完成、通过专项与完整 UI 验证，仍处在同一个未提交工作区。不要误删或重复实现。

1. **回合开始抽牌等待玩家下一次操作才播放**：在原有逐帧事件消费外加入 0.2 秒保底拉取，读取未封口根事件；按日志对象身份去重，后来封口时不重播。不从快照差值虚构抽牌。
2. **玩家后手时 AI 不行动**：完成换牌及空闲轮询会唤醒原生 AI 驱动；沿用命令信号量和任务去重，避免同一回合启动两次。没有改 AI 的规则或决策实现。
3. **对局无完整卡图时仍显示旧卡面**：手牌和放大预览复用工坊 `CardFace` 母版，包含单位／指令／反制及隐藏正面检查。保留已有对局布局。

相关文件：

- `proto/bridge/Kards.Ui.OrcBridge/OrcMatchHost.cs`
- `proto/bridge/Kards.Ui.OrcBridge/OrcMatchSession.cs`
- `proto/bridge/Kards.Ui.OrcBridge/OrcUpdateTranslator.cs`
- `proto/core/TurnInputGate.cs`
- `proto/ui/bridge/OrcMatchRunner.cs`
- `proto/ui/components/BattleCard.cs`
- `proto/ui/screens/BattleScreen.cs`
- `proto/ui/screens/Main.cs`
- `proto/ui/screens/Main.NativeFeedbackVerification.cs`
- `proto/ui/screens/Main.Beta4P0Verification.cs` 及 `.uid`
- `tests/IdleTurnDeliveryTests.cs` 及 `.uid`
- `tests/TurnInputGateTests.cs`
- `tools/release.ps1`：此前已扩展 P0 测试过滤器。

**历史验证范围：** 250 项桥接／单元回归通过，完整 `UI_VERIFY_OK screens=10 cards=521 texture_cache=64/64` 通过；1600×900 与 1280×720 的无输入推进、后手 AI、卡面对照通过。证据在 `artifacts/beta4-research-20261020/`，详见 P0 验收文档。

上述完整验证针对 P0 当时的源码。**后来加入原生工坊、改动 CardControl／CardFace／导出数据后，没有重跑整套门禁，不能拿这 250 项和完整 UI 的旧结果为现在的全部改动背书。**

## 4. 本次新写入的基础实现

### 4.1 ZIP 传输层

新增 `proto/bridge/Kards.Ui.OrcBridge/WorkshopPackageArchive.cs`。

- 接受根目录有 manifest 的 ZIP，或仅有一个外层文件夹的 ZIP；一次导入一个包。
- 解压路径检查、大小写重复检查、链接拒绝、8192 条目与 256 MiB 总解压大小上限。
- 用暂存目录安装，已有安装移动到备份，失败恢复旧包。
- 目录复制与完整 ZIP 导出，保留源文件、资源与未知文件。
- 身份目录采用包 ID 的哈希，后端新建包仍用后端选定的目录名；现有代码同时处理这两种来源。

这层只做文件运输，不解释效果规则。

### 4.2 原生包与编辑器桥接

新增 `proto/bridge/Kards.Ui.OrcBridge/NativeWorkshop.cs`，给 Godot 提供不泄露引擎类型的门面。

已有能力：

- 发现自带包和本机安装包，本机同 ID 内容覆盖自带来源；本机安装内容放在 `user://workshop/installed/`，不写 `runtime-data` 缓存。
- 安装、重复内容识别、同 ID／不同 GUID 拒绝、替换前冲突通知、启用／停用、正式包和会话草稿导出。
- 调用后端 `PackageRegistrar` 注册，并使用真实 `LoadListPlanner` 计算 lock；没有把空 sink 的 `AssetEditor.Reload()` 当作已执行证明。
- `AssetEditor.BeginSession`、`CreateNew`、`CreateAsset(Card)`、`Commit`、`DeleteAsset`、`PreviewLocks`、`Promote`、`Discard`。
- 原生 JSON 不转换成 `DiyCardDraft`，未知字段可往返保留；版本和 GUID 交给后端。
- 转正后从持久目录重建注册信息，并更新新对局使用的包集，避免依赖已经删除的 `.tmp` 源路径。
- 自带包编辑时复制到用户目录；旧文件留在原处。
- 中断的已有包 `.tmp` 不自动删：管理页可显式备份它，再从正式版重新编辑。**没有实现完整的草稿恢复继续编辑。**

`OrcEngineHost.cs` 新增 `SetWorkshopRoots`：先保留冻结快照，再更新有效卡包集合；失败恢复宿主目录，当前对局锁定时拒绝更改。没有重建全局引擎，也没有改规则。

**不要夸大：** 当前只有部分原生作者操作的 UI；依赖、版本、效果／触发器／处理器的完整编辑体验尚未完成。完整迁移与完整恢复也尚未完成。

### 4.3 插画与表示数据

新增的 `workshop.presentation.json` 是**前端自定义的表示扩展**，不是已与后端约定的正式标准。现有形状：

```json
{
  "schemaVersion": 1,
  "cards": {
    "local.card.id": {
      "art": "art/content-hash.png",
      "artKind": "illustration"
    }
  }
}
```

- `SetArtwork` 在临时包保存 PNG；`illustration` 和 `full` 的路径分别投影为 `packart://` 和 `packfull://`。
- `TextureCache` 读取持久目录的运行时图片；包变更刷新时清缓存。
- `WorkshopVisuals.NativeCardIds` 标记安装包中的卡，让卡池和对局使用可编辑母版，避免修改数值后继续显示整张旧官方图片。
- `CardFace` 对 `packfull://` 也采用现有完整旧卡面的插画裁切规则。
- 国家／词条／扩展包关系等尚无完整可移植表示契约；旧 DIY 系统继续保留这些能力，**未宣称已经迁移到新格式**。

### 4.4 工坊基础界面

新增 `proto/ui/screens/DiyWorkshopScreen.Native.cs`，修改 `DiyWorkshopScreen.cs` 与 `.Navigation.cs`。

- 顶部增加“导入卡包 ZIP…”和“原生卡包管理”，两张首页入口卡保留。
- 管理页显示包的卡面样例、状态、编辑／导出／启用停用／诊断入口。
- 原生工作区有卡面网格、搜索、资产选择、属性树、保存草稿、转正、新建卡牌、删除资产、导入插画、完整声明与 lock 预览。
- 未保存表单导航会提示；放弃会话和替换包有确认。
- 原生修改不会另存成旧式覆盖，避免两份效果同时执行。
- 已经有旧式 `SourceCardId` 覆盖的包仍使用旧编辑入口；原生管理入口会阻止叠加第二套修改。**这只是保守兼容措施，不是完成迁移。**

**用户已否定把这类界面作为成品交付。** 这份代码只是后端贯通的 UI 基础。通用字段树仍有明显产品缺口，见第 6 节。

### 4.5 启动、数据缓存与导出设置

- `Main.cs`：创建 NativeWorkshop、加载持久包，挂接工坊，新增 `--verify-native-workshop`。
- `Main.Diy.cs`：刷新卡池、持久插画路径与 NativeCardIds。
- `RuntimeGameData.cs`：导出数据由“仅物化 JSON”改为复制除 `.import`／`.uid` 外的普通数据文件。
- `export_presets.cfg`：include_filter 从 `proto/data/**/*.json` 改为 `proto/data/**`。

**这两项导出改动尚未通过实际导出 EXE 验证。** 特别要确认脚本、图像等在 PCK 中的实际源文件和重映射行为，不能仅凭编辑器模式运行正常就认为已可发行。

## 5. 本次新增验证及其边界

### 5.1 单元／桥接测试

新增 `tests/NativeWorkshopTests.cs`，最近一次 TRX 在：

`artifacts/beta4-implementation/native-workshop.trx`

交接时复核计数：**13 项执行，13 项通过，0 失败、0 跳过**。

覆盖：根 ZIP／包装目录、重复安装、重启发现、导出条目对照、启用状态持久化、草稿／正式版隔离、未知字段、越界路径与大小写冲突、安装失败回滚、中断草稿备份、新包与新卡、转正后新对局实际部署与冻结旧定义不变、太空战争示例四张卡／原生效果文件原样导入。

限制：

- 此次 13 项结果早于后续少量插画／界面修整，最终源码应重跑。
- “太空战争”这个新增测试证明原生包和效果源未转成旧格式，**没有逐项执行四类词条行为**。
- 导出对照目前比较条目与长度，后续应强化为逐文件内容哈希，不能写“完整字节一致性已验收”。
- 边界安全检查虽有覆盖，仍缺超限、损坏 ZIP、更多替换失败与依赖冲突等场景。

### 5.2 实际 UI 全链路

新增 `proto/ui/screens/Main.NativeWorkshopVerification.cs`。

入口：`--verify-native-workshop`。使用后端新建一张原生卡与插画，导出 ZIP 后经主程序导入，点击卡面、修改名称、保存临时包、确认正式卡池未提前变化、转正刷新、再次导出／导入，最后在原生对局实际部署并检查共享卡面。

通过标记：

```text
NATIVE_WORKSHOP_VERIFY_OK zip-install idempotent portable-art native-form commit-draft promote-refresh unknown-fields export-reimport real-native-deploy shared-face
```

日志：

| 文件 | 实际含义 |
| --- | --- |
| `artifacts/beta4-implementation/native-ui.log` | 首次失败：验证代码程序赋值 LineEdit 后没有触发 TextChanged，未实际提交改名。已修正验证输入。 |
| `artifacts/beta4-implementation/native-ui-pass.log` | 无头流程通过，但仍暴露脱离树的旧编辑器资源泄漏。不能当作干净最终结果。 |
| `artifacts/beta4-implementation/native-ui-1600.log` | 1600×900 正常渲染流程通过；该日志复核没有 ERROR／WARNING。旧编辑器重新挂到工坊节点、隐藏后不再成为孤儿树。 |

1600×900 截图原件：

`C:\Users\Alan\AppData\Roaming\Godot\app_userdata\OrC-KSD · 卡牌指挥部\verification\native-workshop\ae2cde8cdb4e4dbea74556c509c1129f\native-editor.png`

这是**基础界面的开发截图，不是已获用户认可的视觉方案**。

### 5.3 最后一次修改后的状态

正常渲染验证后，又调整了原生属性树：隐藏重复的 schemaVersion／id／component，字段改为紧凑横行，展开控件改为扁平按钮，字符串数组支持逗号分隔输入。

**这次紧凑化只做了源码 `dotnet build Kards.Ui.csproj --no-restore -v quiet`，结果 0 警告、0 错误；没有同步到 `H:\g\kards`，也没有再运行实际 UI。** 最新截图和正常渲染日志对应紧凑化之前的基础界面。下一位不要把它们标成最新界面通过。

## 6. 必须先补齐的事项：不要直接打包

### 6.1 产品与界面

1. 先检查原有 `DiyEditorScreen`、`DiyEffectEditorScreen`、Inspector 组合窗口及文档模型，决定原生声明如何接入已有编辑体验；不要继续把一棵 JSON 属性树称为“完整编辑器”。用户打断前，尚未来得及完成这次检查。
2. 属性页需要清楚的名称、国家、卡牌类型、费用、战斗属性、稀有度等操作；类型和可枚举字段要有合适选择控件，未知值必须保留并说明。国家不能退化成只有几个固定选项，也不能偷偷替换成默认国家。
3. 规则页要沿用通用时机／条件／目标／动作的结构化编辑；技术性的 band、event、source、版本、GUID 等不能挤进主操作流。未知结构可只读保留并说明原因，不可悄悄重建。
4. 卡面预览要足够大，输入后应明确反馈；卡牌列表、选中态、编译错误、待完善与已准入状态需要区分。现在只有保存后的刷新，不能称为完整即时预览。
5. 补齐空状态、无效输入、保存／转正失败后的继续编辑、未保存改动保护、离开整个工坊页面的保护、所有确认框关闭后的反馈。
6. 调整大包性能和分页：当前管理页/原生卡池可能一次创建大量控件，卡图与资产列表没有完成分页／虚拟化。不能拿单张验收卡的顺畅代替大卡包验收。
7. 检查 1280×720、1600×900 的真实布局和交互；本次新界面只有紧凑化前 1600×900 通过。

### 6.2 数据与后端适配

1. 旧 DIY 草稿、旧覆盖、自定义国家／词条／图标、裁切和扩展关系继续保留。迁移应逐项证明可表示才进行，保留备份和失败诊断；当前只是阻止双重覆盖，未实现完整迁移。
2. 中断 `.tmp` 的完整恢复没有完成；新建包尚未转正便中断时，其正式目录不存在，现有管理页可能不能发现该孤立 `.tmp`。至少让它可见、可导出／备份，不能静默删除。
3. `NativeWorkshop` 的损坏 catalog 处理目前保留文件并阻止写入，但加载时默认启用规则仍需审核；不能意外启用玩家先前停用的包。
4. 审核 `workshop.presentation.json` 的格式、路径、缺图、未知字段与兼容版本策略；它目前只是前端扩展，不能宣称为后端正式契约。
5. 审核转正失败和安装失败时的原始异常保留、目录恢复、设置恢复，以及部分无效资产的真实诊断。当前调用 `LoadListPlanner` 主要阻止 fatal 问题，非 fatal 的依赖／版本问题尚未完整呈现给玩家。
6. 新原生界面目前只支持新建卡牌；效果／触发器／处理器创建、依赖编辑、版本操作还没有完整 UI，不要把后端有 API 写成前端已支持。
7. `DiyInjectionCatalog` 仍有“非空 band 全拒绝”的旧限制。新导入的原生包不走这条旧适配，但旧编辑器能力表仍与新引擎存在差异。要按实际目标与 band scheme 验证适配，不能简单全面放开。
8. 增加太空战争四类词条的实际执行测试、重复挂载检查；“JSON 没丢”不证明机制执行正确。

### 6.3 回归与发行

1. 最新源码重新同步和构建，先复核 native verifier，再跑完整 UI、P0 双分辨率、旧工坊／Inspector／构筑回归。
2. 发布门禁必须加入实际导出 EXE 的 `--verify-native-workshop` 检查；当前 `tools/release.ps1` **尚未增加这个独立发行包检查**。已有过滤器 `~WorkshopTests` 会匹配 `NativeWorkshopTests`，但最好明确列出新测试，避免误读。
3. 验证导出包完整资源和脚本物化；同时验证冷启动持久包发现、启用状态、插画与编辑草稿保留。
4. 明确哪些 Beta.4 方案项已实现、哪些延期；重写发行说明，禁止把方案整表复制成完成清单。
5. 全部门禁通过后再设置 `VERSION=0.0.0-beta.4`，使用显式 Beta.3 `BaselineZip` 生成补丁。
6. 独立中文路径运行完整包与升级后的 EXE，核对逐文件哈希和玩家文件保留，再提交／推送／打包交付。
7. 不要把未跟踪的联机设计文档加入本轮提交；不改引擎仓库。

## 7. 续接操作与命令

先读本文件，执行 `git status --short` 核对新增的其他人改动，继续在现有工作区工作，不 reset。

源码构建和新专项：

```powershell
dotnet build Kards.Ui.csproj --no-restore -v quiet
dotnet test tests/Kards.Ui.Tests.csproj --filter FullyQualifiedName~NativeWorkshopTests --logger 'trx;LogFileName=native-workshop.trx' --results-directory artifacts/beta4-implementation
```

同步并实际运行：

```powershell
& ./tools/prepare.ps1 -SkipAssets
dotnet build 'H:/g/kards/Kards.Ui.csproj' --no-restore -v quiet
& ./tools/godot-wait.ps1 --headless --editor --path 'H:/g/kards' --quit
& ./tools/godot-wait.ps1 --headless --path 'H:/g/kards' '--' --verify-native-workshop
& ./tools/godot-wait.ps1 --path 'H:/g/kards' --resolution 1280x720 '--' --verify-native-workshop
```

PowerShell 的参数分隔符必须写成字符串 **`'--'`**，否则 Godot 的 `OS.GetCmdlineUserArgs()` 可能收不到验证参数。执行结束除了看退出码，还必须检查日志中的明确成功标记与异常。

完整 UI 可用现有 `tools/run.ps1 -Godot (Join-Path $PWD 'tools/godot-wait.ps1') -Verify`；它同步、构建并补全脚本依赖闭包。只有需要新资源时再执行资源导入。

**以下仅是完成验收后使用的发布命令，今天没有执行：**

```powershell
& ./tools/release.ps1 -BaselineZip 'H:/Working Folder/OrC-KSD.Godot/dist/OrC-KSD-0.0.0-beta.3-win-x64.zip' -BaselineVersion '0.0.0-beta.3'
```

已有 Beta.3 完整包为 306,049,555 字节。构建输出 `dist/`、`artifacts/` 都在 gitignore 内，不要强行提交二进制。发布约定参考 `docs/release/RELEASE_BETA3_20261020.md`。

新 C# 文件的部分 `.uid` 仅在调试副本中由 Godot 生成，源目录尚未统一补齐；下一次资源同步／准备提交时核对项目约定。不要复制整个调试副本覆盖源码。

## 8. 交接结论

P0 修复有充分的历史验证；本轮原生卡包基础数据链有单元和实际 UI／对局证据。但新增编辑体验、恢复迁移、完整机制执行与发行验收仍未完成。**下一步先补齐产品与数据流程，不是立即打包推送。**
