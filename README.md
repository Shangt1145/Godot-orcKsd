# OrC-KSD.Godot · 对战 UI

## 从源码构建（给别人看的唯一前置）

本仓库**不含引擎源码**，构建时引用同级的引擎 checkout。三选一：

```powershell
# ① 同级目录约定（推荐）：engine 克隆到本仓库旁边，目录名为 OrC-KSD
git clone https://github.com/hitman-OLD6/OrC-KSD.git ..\OrC-KSD

# ② 环境变量（engine 目录可以随便放）
$env:ORC_ENGINE_ROOT = "D:\dev\OrC-KSD"

# ③ 构建参数
dotnet build -p:OrcEngineRoot="D:\dev\OrC-KSD\src\Orc.Game"
```

找不到引擎时构建会直接报 `error ORC001` 并列出这三种办法，不会静默失败。
另需 .NET 8/9 SDK 与 Godot 4.7 **.NET(mono)** 版；编辑器一键入口 `tools\open-editor.bat`，
构建/验证/运行 `tools\run.ps1`。原版美术不进 Git，见 [资源重建说明](tools/OriginalAssetExtractor/README.md)。

**当前发行：2.0.0-alpha.2（2026-10-24）**。热修复：联机选靶被拒后单位锁死。**热修复只发补丁**，新玩家先装 [alpha.1 完整包](docs/release/RELEASE_2.0.0_ALPHA1_20261024.md) 再打补丁；必须使用更新器 1.2.0。说明见 [2.0.0-alpha.2 发行说明](docs/release/RELEASE_2.0.0_ALPHA2_20261024.md)。

**多人能力边界**：本地 DIY 与卡组构筑可用；两人房间目前只接受已装载卡包的 `pack/` 卡，尚不支持各玩家携带 `diy/` 卡及自动分发规则、卡图的完整闭环。原版美术不进入 Git，发行包已携带；源码运行须另行准备这些素材，见 [资源重建说明](tools/OriginalAssetExtractor/README.md)。

Beta.3（2026-10-20）：接入引擎 `6d72eac`，DIY 卡面按单位、指令、反制切换 Astral 模板，提供卡图对齐工具；Inspector 使用组合编辑窗口，效果文本解析入口调用后端编译器。发布说明、兼容补丁与验证范围见 [Beta.3 发行记录](docs/release/RELEASE_BETA3_20261020.md)。

2026-10-19：对局外页面改用固定侧栏、统一的档案式配色与控件。首页直接使用当前卡组进入对局；收藏、卡组库、构筑、工坊、卡牌与效果编辑、设置已接入新菜单主题。对局保持原有界面，菜单主题与对局主题独立。说明与验证见 [菜单 UI 改版](docs/design/MENU_UI_OVERHAUL_20261019.md)。

DIY 工坊现提供两级卡牌选择及抉择式过渡，国家候选读取后端清单，并在国家下拉旁提供自定义入口；卡牌编辑分为属性、规则与词条、卡图、风味四页。当前修复见 [工坊改版](docs/design/DIY_WORKSHOP_REWORK_20261019.md)。

DIY 工坊现按卡包组织：入口选择“卡包”或“扩展包”，再选择创建或修改。卡包内可创建词条、编辑卡牌和预览卡池；扩展包先选择基础卡包，继承其词条和国家。支持自定义国家及本地图标。完整卡面导入会裁出插画，以可编辑的游戏卡框、国家图标、数值和文字重绘；保留原图及可调整裁切。操作与验证说明见 [DIY 工坊](docs/design/DIY_WORKSHOP.md)。

离线增量更新已接入：首次解压更新器 ZIP，选择旧游戏文件夹和 `.orcupdate` 补丁，点击更新；之后从游戏顶部“离线更新”进入。只重建发生变化的程序内容，保留卡组、DIY、卡图与设置，支持中断恢复和回退。使用与发布说明见 [离线增量更新](docs/release/OFFLINE_UPDATE.md)。

> 版本规则见 `docs/release/VERSIONING.md`，当前版本见根目录 `VERSION`（应用内页脚同步显示）

Beta.1 更新：新建卡组先多选卡包，选择随保存、复制及卡组代码保留；收藏与构筑只显示引擎已准入的卡牌（当前 240 种：24 张参考卡 + 216 张原版卡）。扩展内容自动归入原版卡包，无需另选扩展包。旧卡组按已有卡牌推导来源；缺失机制持续补齐，不能把排除卡牌当作实现完成。编辑时可调整卡包，不能带着已有卡牌取消其来源。详见 [卡包选择与导出验收](docs/acceptance/PACK_SELECTION_EXPORT_ACCEPTANCE.md)。

Windows 游戏导出：运行 `tools/release.ps1`。脚本补齐匹配的 .NET 导出模板，构建并运行发布门，再导出、打包、解压到独立目录启动 EXE，验证卡牌数据、216 张原版卡效果、卡图、内置解析器及真实开局。玩家无需 Godot、Node.js 或 .NET SDK。成品 ZIP 位于 `dist/`，解压后运行 `KardsUi.exe`，须保留旁边的 `.pck` 和 `data_...` 运行库目录。

Godot 4.7 .NET + C# 原型，入口为 `proto/scenes/Main.tscn`。

**Beta.1：DIY 卡图仅支持导入本机图片，216 张已收录的原版 KARDS 卡绑定官方中文卡面。** 移除现有素材选择入口；旧存档卡图保留，可移除或导入替换。运行时直接读取本地 PNG，不联网获取卡图。首次准备卡图可运行 `tools/sync-official-art.ps1`（需 Python Pillow 支持 AVIF 解码）。卡图和规则覆盖率分开统计，见 [Beta.1 验收](docs/acceptance/BETA_1_ACCEPTANCE.md)。

默认使用中文效果搭建。进入 DIY 工坊 → 效果编辑器，按“什么时候触发 → 要做什么”组合效果。普通页面隐藏内部路径、字段名和数据类型；动作以中文搜索、分组选择，填好对象与数值即可。条件、计算值、触发限制按需展开；重复、分支、逐个执行和子效果可以继续嵌套。卡牌自带词条使用勾选界面，数值按需填写。空白卡可以从一句话描述开始。

完整目录仍由原版 schema 提供：161 种动作的必填/可选参数都可编辑，不依赖几个固定配方。高级工具保留原始结构、JSON、教程与编译报告，未知字段始终保留。撤销重做、实时草稿和保存重开沿用同一份无损数据；旧简易行仅在有旧结构时显示兼容入口。

整个游戏运行在 Godot 内，后端继续使用 **OrC-KSD**。完整原版语法解析器在同一游戏进程中通过随包的 ClearScript/V8 运行，不打开浏览器、不提供网页服务、不启动 Node.js，也不需要源项目目录。JS 仅提供解析、声明模型与校验；对局仍由 OrC-KSD 执行。原版 UI 文件仅作移植参考，不编入运行时。

1553 条语料中，1540 条完整结果保持与 Kards-Desktop 原版一致；13 条针对总部、混合目标及全目标修复，分别保留审核后的回归哈希，原始基线未覆盖。现为 698 条完整解析，语法解析数与原生实战覆盖率分别统计。**161 项可编辑不等于 161 项已获规则层支持**：未适配的声明无损保存为草稿，准入和执行以 OrC-KSD 为准。来源、验证和缺口见 [移植记录](docs/design/DESKTOP_AUTHORING_PORT_20261016.md)。专项入口为 `--verify-desktop-authoring` / `--capture-desktop-authoring`。

A.22 精简主菜单入口：顶部统一进入我的卡组、卡牌收藏和 DIY 工坊，移除重复侧栏、首页快捷入口与卡组库重复新建。首页主视觉区使用当前有效卡组开始对战；构筑由卡组库的新建或编辑进入。加入真实加载阶段的 splash、可打断的页面 transition、卡牌展开及轻微鼠标跟随、按钮悬停／焦点反馈；减弱动效可即时关闭位移。收藏采用连续滚动和附近行渲染，保留筛选、详情与返回位置。验收见 [MENU_INTERACTION_ACCEPTANCE.md](docs/acceptance/MENU_INTERACTION_ACCEPTANCE.md)，原版研究见 [KARDS_MENU_RESEARCH_20261011.md](docs/research/KARDS_MENU_RESEARCH_20261011.md)。专项入口 `--verify-menu` / `--capture-menu`。

当前交付为 **6A + 6B2 + 6C1 + 6C2 + 卡组构筑 + 抽牌因果调度 + 真实战场反馈 + DIY 卡牌 + 行动判定与阵型部署 + 主菜单动效与连续收藏（A.22）**：友方回合连续操作不等待动画，未操作的牌在抽牌、受击或重排过程中仍可操作；新抽牌飞到一半即可抓住并打出，拖拽接管该牌的飞行动画。打牌、移动、攻击演出优先于无关的等待反馈，相关抽牌仍按产生、使用的因果顺序演出。同类按发生顺序播放；敌方回合禁止我方行动。结束回合按钮在友方回合为橙色，敌方回合为灰色且不可点击。开始对战时使用已保存并选中的卡组，由引擎随机洗牌、发牌。首次创建明确开启不限国家的混编初始卡组（39 张卡牌 + 总部），自动对手使用同等张数；新建卡组默认 40 张含总部、主盟国与稀有度限制。当前参考卡审核支持集为 10 种单位、13 种指令和 1 种反制。315 张原始卡保留为源数据，其中仅 24 张审核卡进入收藏与构筑；150 张仅表示结构可映射，不表示全部效果已实现。支持清单、使用方式与限制见 [STAGE_6B2_ACCEPTANCE.md](docs/acceptance/STAGE_6B2_ACCEPTANCE.md)。

抽牌因果顺序与逐张可操作的验证见 [CAUSAL_DRAW_SCHEDULING.md](docs/battle/CAUSAL_DRAW_SCHEDULING.md)，可单独运行 `--verify-causal-draw`。

A.18 将运行时词条/参值、老兵升级、隐蔽揭示、状态与兵种变化、双方洗牌接入真实战场反馈。友方回合菜单可认输，引擎决定结果；总部致死有毁灭演出，结果不会重新画出零血总部。终局立即禁止操作，重开清理旧动画和原生待选请求。通用机制接入不放宽 21 种审核卡的准入。范围与验收见 [STAGE_6C3_ACCEPTANCE.md](docs/acceptance/STAGE_6C3_ACCEPTANCE.md)，专项入口 `--verify-native-feedback`。

A.19 将占位编辑器替换为 DIY 卡牌页面，可新建单位或指令，编辑属性、词条及有序效果，保存、复制、删除、导入导出并生成原生单卡 JSON。经过现有引擎编译和实例化校验的卡可进入图鉴、构筑和真实对局。试打使用临时 12 张卡牌的卡组随机发牌，不改动已保存卡组；可从战场齿轮菜单打开“DIY 卡牌”。范围与验收见 [STAGE_6D1_ACCEPTANCE.md](docs/acceptance/STAGE_6D1_ACCEPTANCE.md)，专项入口 `--verify-diy` / `--capture-diy`。

A.20 在本地引擎加入 `action.input.allowed` 判定器，每次查询和提交都读取 UI 状态。UI 只保留手势、回合演出及已操作对象的占用，原生规则继续决定费用、归属、候选和行动资格。支援部署使用引擎提供的总部/单位之间及两端空隙，提交稳定的候选 ID；引擎按插入位置重排单位和总部，陈旧落点明确拒绝。范围、补丁与验证见 [STAGE_6D2_ACCEPTANCE.md](docs/acceptance/STAGE_6D2_ACCEPTANCE.md)，专项入口 `--verify-action-portals` / `--capture-action-portals`。

结束回合后立即锁定战场输入。即使自动对手已在后台行动完毕，也必须等演出进入“你的回合”才恢复操作；此时我方回合提示和抽牌动画可以继续播放。回合锁同时覆盖选牌、悬停、拖牌、移动、攻击和结束回合，敌方期间的操作不会提交给引擎或排入演出队列。相关验证：`TurnInputGateTests`、`--verify-input-scheduling`（包含真实自动对手提前返回我方的复现）。

指令从实际松手位置直接进入最后展示段并向左移出，正常速度下停靠阅读1.5秒，展示至退场共约2.12秒，命中效果同时发生；指向性指令拖出箭头指定单位，松手直接打出，空地和非法目标取消。参考与验证记录见 [ORDER_ANIMATION_REFERENCE_20261009.md](docs/research/ORDER_ANIMATION_REFERENCE_20261009.md)。

二／三选一在桌面展开候选卡牌，未选牌翻回背面退场，选中卡面直接接最后展示段；抉择可点击空白或右键取消并回手，不接受键盘提交或取消。反制挂起／解除从松手位置回手，显示窄条提示，预留和退还费用由引擎决定。已对照正确视频 `8月9日(2).mp4` 的三段演示接入。

多选复用开局调度的红圈叉号，支持取消标记和跨页保留。下方仅有翻页按钮与“已选数量/需要选择数量”；选满引擎提供的 Max 数量后自动提交，不允许少选、不选或键盘提交/取消。引擎提供的候选定义显示注册卡面，选择不自行生成新卡。本地同屏双人入口与切换界面已删除，真实对局保留玩家对本地对手。详见 [STAGE_6C2_ACCEPTANCE.md](docs/acceptance/STAGE_6C2_ACCEPTANCE.md)。

当前源码在 `H:/Working Folder/OrC-KSD.Godot`，实际构建和运行副本在 **`H:/g/kards`**。只在源码目录编辑，使用脚本同步；短路径中的 `.godot`、bin、obj 不会复制回源码。参考项目保持只读；A.20 按用户授权加入的引擎行动判定与阵型空隙部署已于 2026-10-11 推送至上游 `main`，当前 HEAD 为 `7540b43`，基础提交为 `8b0ae98`。发布记录见 [ENGINE_PATCH_PUBLISH_20261011.md](docs/engine/ENGINE_PATCH_PUBLISH_20261011.md)。迁移范围与验证见 [STAGE_6C1_ACCEPTANCE.md](docs/acceptance/STAGE_6C1_ACCEPTANCE.md)。

## 启动

启动脚本兼容 Windows PowerShell 5.1 和 PowerShell 7；`.ps1` 使用带 BOM 的 UTF-8，确保中文素材目录名能正确解析。编辑脚本时保留该编码（`.editorconfig` 已配置）。

首次准备卡池与素材：

```powershell
& 'H:\Working Folder\OrC-KSD.Godot\tools\prepare.ps1'
```

打开原型：

```powershell
& 'H:\Working Folder\OrC-KSD.Godot\tools\run.ps1'
```

`run.ps1 -Editor` 打开 Godot 编辑器；`-Test -Verify` 执行消费层测试、战斗交互检查和场景启动检查；`-VerifyEffects` 检查各类演出生命周期；`-Capture` 生成战斗棋盘、瞄准、手牌详情、小窗口、图鉴与演示场景截图后退出。

工具路径默认使用本次下载的 `H:/g/tools/godot47/Godot_v4.7-stable_mono_win64/`，可以通过 `-Godot` 指定其他 4.7 .NET 可执行文件。工具包来自 [Godot 官方 4.7 下载页](https://godotengine.org/download/archive/4.7-stable/)。

本机 `H:/Godot_v4.7-stable_win64.exe` 是普通版，探针检查 `ClassDB.class_exists("CSharpScript")` 为 false；下载的 .NET 版为 true。`OS.has_feature("mono")` 不是本机可靠的判定方法。

## 已接入

- 固定侧栏导航；对战页沿用原有全屏界面，通过右上角齿轮进入卡组构筑、我的卡组、DIY 卡牌、图鉴和设置。
- DIY 卡牌：单位部署完成 / 指令打出时的抽牌、指挥点/槽、单位伤害、攻防增益、压制、抑制、行动费修饰；8 个已有单位词条；复用已有插图，动态绘制当前定义的卡面。草稿存于 `user://diy-cards.json`，未知声明明确拒绝；运行中的对局保留开始时的定义快照。
- 完整本地卡组构筑：新增/移除、筛选、费用曲线、保存草稿、复制、删除、选择对战卡组、代码导入导出；选项包括不限制主盟国、自定义总张数和不限张数。存于 `user://decks.json`，实际对局使用所选精确名单。规则调研与限制见 [KARDS_DECK_BUILDING_20261009.md](docs/research/KARDS_DECK_BUILDING_20261009.md)。
- 对战第二阶段：按原版分离大号橙色指挥点、K 与横线下槽数；使用 image_gen 桌面、卡背、HQ 地图和爆炸纹理。保留第一阶段部署/移动/手牌操作，增加五类兵种攻击、受击、反击、死亡和 HQ 演出。详见 `docs/battle/BATTLE_STAGE2.md`。
- 对战第三阶段：我方及对手抽牌、连续抽牌、指令揭示/停靠/退场、多目标同时命中与最终重排；外部载荷驱动，隐藏信息与中断清理受保护。详见 `docs/battle/BATTLE_STAGE3.md`。
- 对战第四阶段：抽牌移动中带惯性翻面到屏幕中央停留；反制在手牌内武装、解除与触发，治疗/强化/压制解除/费用变化反馈。详见 `docs/battle/BATTLE_STAGE4.md`。
- 315 张定义的收藏、5 维筛选、文本搜索、连续滚动、点击/长按详情；只保留附近行的卡牌节点。
- 卡牌组件：素材、基础/有效数值、词条参值、稀有度边框、国别信息、静默/抑制/老兵状态、目标与攻击高亮。
- 25 组动画演示，8 种攻击兵种，3 档拍桌防御值、速度档、减弱动效、音效与偏好持久化。
- mock 事件段消费、历史值复制、混合多槽位请求、首次拒绝后的重试和取消。
- 操作手势：**一律拖拽**提交行动，点击只负责选中与查看预览，右键/Esc 取消手势；选择面板仅允许鼠标取消。
  - 手牌：整张卡跟随光标拖到我方支援线部署。
  - 场上单位：**卡牌留在原位**，拖出的是箭头（移到前线＝移动箭头，指向敌方＝攻击箭头），松手提交。
  - 支援部署按引擎返回的阵型空隙插入，可在总部两侧及单位之间放置；前线仍为双方共用的一排，按已有移动候选与松手位置呈现。
- 部署**拍桌**按防御分为 1–3 / 4–6 / 7+：轻档平缓收牌，中档停顿后收牌并轻震，重档完整卡面放大蓄势后迅速拍下、强震并飞散颗粒。依据用户提供的三个原速样本逐帧校准，详见 [部署对照记录](docs/research/DEPLOYMENT_REFERENCE_20261008.md)。
- 独立的 net8 契约与消费层；Godot 主项目为 net9。

当前源码 `animate.js` 实际有 **63 个不同导出项**，含常量与工具，见 `docs/design/ANIMATION_PORT.md`。这些是动画移植初版，原版逐帧手感验收仍需记录在 `docs/acceptance/ACCEPTANCE.md`。

## 素材与内存

源数据在发行包 `dist/win-unpacked/resources/app`，prepare 脚本复制 7 个图片目录及根目录图片：576 张 PNG、53 个 mp3、11 个 nations JSON。其中 `_meta.json` 不含 cards。

实际卡池是 161 单位、150 指令、4 反制；39 张卡没有 art，使用文字卡面。另有部分自定义指令含非空基础攻防，读取器保留原值，不擅自修正源数据。

图鉴原始纹理采用 VRAM Compressed；图鉴每页 18 张，缓存最多 64 个纹理资源，渲染默认 Forward Plus。原始卡面、音效和卡池从本地发行包准备。四张新生成对战纹理保存在 `proto/assets/battle/`，源码同步时一并复制；完整提示词与生成记录见该目录的 `ASSETS.md`。

## 契约与后续接入

`proto/contracts/README.md` 保留上游契约提案。基础真实对局已接入 OrC-KSD；UI 消费值对象，规则、效果执行、Ref 和引擎对象留在桥接层。默认演示攻击使用本地夹具，真实入口使用经审核支持集；效果卡、联机及存档协议仍未完成。引擎收到用户更新提醒后才拉取，当前基线见 [ENGINE_INTEGRATION_BASELINE.md](docs/engine/ENGINE_INTEGRATION_BASELINE.md)。

引擎状态读面、可见性与历史值投影确认后，再用真实适配器替换 mock。不能将当前事件段当作存档，也不能从日志活引用延迟推断当时的数值。
