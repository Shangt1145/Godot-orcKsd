# 文档索引

按 **7 个桶** 组织，共 120 篇（另有 `archive/` 归档 7 篇）。这份文件是唯一入口：
新增文档必须在这里出现一行，否则等于没写——历史上 138 篇里有 57 篇没有任何入口，就是这个原因。

状态口径：**现行**＝还在指导实现，读它；**参照**＝原版/上游的事实依据，长期有效；
**记录**＝某一轮工作的完整证据，只在追溯时读；**历史**＝结论已被取代，别照着做。

---

## 先读这三份

| 文档 | 作用 |
|---|---|
| [项目现状](planning/STATUS.md) | 现在到哪一步、下一刀切哪里。改代码前先读 |
| [引擎接入基线](engine/ENGINE_INTEGRATION_BASELINE.md) | 当前引擎版本、累计本地补丁、缺哪个就构建不了 |
| [版本号规则](release/VERSIONING.md) | 一版一补丁、发版硬顺序、tag 与资产命名 |

## 按任务找

| 我要做的事 | 该读 |
|---|---|
| 发一次版 | [VERSIONING](release/VERSIONING.md) → [RELEASE_INDEX](release/RELEASE_INDEX.md) → 最近一篇 `release/RELEASE_*` |
| 改 DIY 工坊 / 卡包 / 效果编辑 | [DIY 工坊](design/DIY_WORKSHOP.md)、[卡包设计](design/DIY_PACK_DESIGN_20261012.md)、[解析与 Inspector](design/DIY_PARSE_AND_INSPECTOR_REDESIGN.md)、[自定义词条](engine/CUSTOM_KEYWORD_SYSTEM.md) |
| 改联机 / 房间 / 卡包同步 | [组网选型](design/NETWORK_OPENSRC_20261022.md)、[房间屏](design/ROOM_SCREEN_20261022.md)、[卡池对齐](design/POOL_ALIGNMENT_20261024.md)、[工坊联机验收](acceptance/NETWORK_WORKSHOP_20261025.md) |
| 动引擎或桥接层 | [接入基线](engine/ENGINE_INTEGRATION_BASELINE.md)、[桥接分层](engine/ENGINE_BRIDGE.md)、[契约依赖](design/MATCH_FEED_DEPENDENCIES_20261021.md) |
| 对齐原版手感 / 数值 | 整个 [research/](research/) 桶，按主题挑 |
| 判断"某功能到底做到哪一步" | 对应的 `acceptance/` 记录，**只看结论段与"验证边界"** |
| 接上一次没做完的活 | [STATUS](planning/STATUS.md) + 对应的 `*_HANDOFF_*` |
| 找"踩过的坑" | [STATUS](planning/STATUS.md) 的教训段、[2026-10-07 全局回顾](planning/RETROSPECTIVE_2026-10-07.md) |

---

## engine/ — 引擎桥接与版本适配

| 文档 | 内容 | 状态 |
|---|---|---|
| [ENGINE_INTEGRATION_BASELINE](engine/ENGINE_INTEGRATION_BASELINE.md) | 引擎 commit、累计本地补丁、构建前置 | 现行 |
| [ENGINE_BRIDGE](engine/ENGINE_BRIDGE.md) | 桥接分层与 P0/P1 责任边界（UI 不许出现引擎类型） | 现行 |
| [CUSTOM_KEYWORD_SYSTEM](engine/CUSTOM_KEYWORD_SYSTEM.md) | 自定义词条的注册、编译与执行 | 现行（开发中） |
| [KEYWORD_DRAG_MECHANICS](engine/KEYWORD_DRAG_MECHANICS_20261017.md) | 词条主动技能的拖拽行动实现 | 参照 |
| [ENGINE_PATCH_PUBLISH](engine/ENGINE_PATCH_PUBLISH_20261011.md) | A.20 行动判定补丁推送上游的过程记录 | 历史 |
| [ENGINE_SYNC_B5E0A5C](engine/ENGINE_SYNC_B5E0A5C_ADAPTATION.md) | b5e0a5c 升级的 UI 侧适配 | 历史 |
| [ENGINE_7F3AE35_IMPACT](engine/ENGINE_7F3AE35_IMPACT_20261022.md) | 7f3ae35 的影响面与复验记录 | 历史 |

引擎升级记录散落在三处（`engine/` 的两次适配、`acceptance/` 的两次整合、`design/ENGINE_UPDATE_PLAN`），
**每次都只对当时那个 commit 有效**；现行口径永远以接入基线为准。

## research/ — 原版 KARDS 与上游事实依据

| 文档 | 内容 | 状态 |
|---|---|---|
| [KARDS_BATTLE_RESEARCH](research/KARDS_BATTLE_RESEARCH.md) | 对战界面结构与动作特效 | 参照 |
| [KARDS_BATTLE_ANIMATION_AUDIT](research/KARDS_BATTLE_ANIMATION_AUDIT_20261023.md) | 演出补充调研（含截图证据边界） | 参照 |
| [KARDS_FULL_BATTLE_MOTION](research/KARDS_FULL_BATTLE_MOTION_20261023.md) | 整局动作逐段复核与落地 | 参照 |
| [KARDS_MENU_RESEARCH](research/KARDS_MENU_RESEARCH_20261011.md) | 主菜单与对局外界面 | 参照 |
| [KARDS_MATERIAL_STUDY](research/KARDS_MATERIAL_STUDY_20261022.md) | 纸面/织物材质与字重对照 | 参照 |
| [KARDS_SELECTION_RESEARCH](research/KARDS_SELECTION_RESEARCH.md) | 交互式选择（三选一、多选） | 参照 |
| [KARDS_DECK_BUILDING](research/KARDS_DECK_BUILDING_20261009.md) | 卡组构筑规则与张数/稀有度限制 | 参照 |
| [KARDS_COSMETICS_RESEARCH](research/KARDS_COSMETICS_RESEARCH_20261024.md) | 卡背、桌饰、表情、总部纹理 | 参照 |
| [DEPLOYMENT_REFERENCE](research/DEPLOYMENT_REFERENCE_20261008.md) | 三档拍桌部署的分档依据 | 参照 |
| [ORDER_ANIMATION_REFERENCE](research/ORDER_ANIMATION_REFERENCE_20261009.md) | 指令/反制/抉择的停靠与退场时序 | 参照 |
| [CARD_POOL_COVERAGE_RESEARCH](research/CARD_POOL_COVERAGE_RESEARCH_20261012.md) | 卡池规模与机制覆盖率 | 参照 |
| [DIY_WORKSHOP_UI_CASE_STUDY](research/DIY_WORKSHOP_UI_CASE_STUDY_20261021.md) | 制卡工具与规则组合的案例 | 参照 |
| [MOBILE_PORT](research/MOBILE_PORT_20261024.md) | 安卓优先的移植调研 | 参照 |
| [ASSET_EDITOR_ISSUE_REVIEW](research/ASSET_EDITOR_ISSUE_REVIEW_20261022.md) | "编辑器未接入资产编辑器"的核对结论 | 记录 |
| [KARDS_RESEARCH_2026-10-07](research/KARDS_RESEARCH_2026-10-07.md) | 最初的接入差距分析 | 历史 |
| [RESEARCH_2026-10-06](research/RESEARCH_2026-10-06.md) | 阶段调研（第一天） | 历史 |

## acceptance/ — 某一轮做到哪一步

**现行能力边界**（提到某个功能时先看这些）：

| 文档 | 内容 |
|---|---|
| [NETWORK_WORKSHOP](acceptance/NETWORK_WORKSHOP_20261025.md) | 工坊卡包/扩展包联机、规则同步与开局校验 |
| [ANDROID_PARITY](acceptance/ANDROID_PARITY_20261025.md) | 安卓与 PC 功能对齐范围（含未测项） |
| [DEBUG_DEPLOYMENT](acceptance/DEBUG_DEPLOYMENT_20261025.md) | 图形调试台、部署选靶与取消不扣费 |
| [BATTLE_FEEDBACK](acceptance/BATTLE_FEEDBACK_20261024.md) | 抽牌闪现、数字基线、缠斗选靶、演出提速 |
| [COSMETICS](acceptance/COSMETICS_20261024.md) | 卡背/总部/表情归属与双端同步 |
| [REMOTE_MOVEMENT_PRESENTATION](acceptance/REMOTE_MOVEMENT_PRESENTATION_20261024.md) | 联机前线移动与演出链 |
| [REMOTE_MULLIGAN_DEPLOYMENT](acceptance/REMOTE_MULLIGAN_DEPLOYMENT_20261024.md) | 真人联机换牌与部署 |
| [SMOKESCREEN_LIFECYCLE](acceptance/SMOKESCREEN_LIFECYCLE_20261024.md) | 烟幕的前线与攻击移除规则 |
| [ENGINE_DC13B96_INTEGRATION](acceptance/ENGINE_DC13B96_INTEGRATION_20261024.md) | 上游合并与本地修复保留 |
| [ENGINE_64D8DB2_RECOVERY](acceptance/ENGINE_64D8DB2_RECOVERY_20261023.md) | 引擎升级后的联机与战斗恢复 |
| [BATTLE_OPENING](acceptance/BATTLE_OPENING_20261023.md) | 双方入场、起手发牌、换牌衔接 |
| [BATTLE_PAPER_MOTION](acceptance/BATTLE_PAPER_MOTION_20261023.md) | 悬停/拖牌/回手/变形/武器余效 |
| [DEMO_MECHANICS_COMPAT](acceptance/DEMO_MECHANICS_COMPAT_20261023.md) | 机制示范包兼容（含必须保留的本地补丁） |
| [UI_NAVIGATION](acceptance/UI_NAVIGATION_20261023.md) | 页面职责与入口收敛 |

**工坊与菜单批次**（2026-10-21 ～ 10-22）：

| 文档 | 内容 |
|---|---|
| [ROOM_AI_AND_SETUP_SELECTION](acceptance/ROOM_AI_AND_SETUP_SELECTION_20261022.md) | 联机 AI 开局与作战准备页 |
| [SETUP_CARD_BACK_REFERENCE](acceptance/SETUP_CARD_BACK_REFERENCE_20261022.md) | 卡组选择按原版卡背重做 |
| [WORKSHOP_HUMAN_EDITOR_ENGINE_88C96BC](acceptance/WORKSHOP_HUMAN_EDITOR_ENGINE_88C96BC_20261021.md) | 效果编辑交互修正与引擎 88c96bc |
| [WORKSHOP_ALL_PACK_ROUTES_AND_DRAFT_RECOVERY](acceptance/WORKSHOP_ALL_PACK_ROUTES_AND_DRAFT_RECOVERY_20261021.md) | 全卡包入口与草稿恢复 |
| [BETA4_WORKSHOP_WORKSPACE_UPDATE](acceptance/BETA4_WORKSHOP_WORKSPACE_UPDATE_20261021.md) | 工坊工作区更新 |
| [BETA4_P0_IDLE_TURN_AND_CARD_FACE](acceptance/BETA4_P0_IDLE_TURN_AND_CARD_FACE_20261021.md) | 空闲回合推进与卡面同步 |

**早期阶段验收**（⛔ 结论只对当时成立，别当现状）：

| 文档 | 内容 |
|---|---|
| [ACCEPTANCE](acceptance/ACCEPTANCE.md) | 阶段一（占位编辑器时代） |
| [MENU_UI_ACCEPTANCE](acceptance/MENU_UI_ACCEPTANCE.md) | A.21 主菜单改版 |
| [MENU_INTERACTION_ACCEPTANCE](acceptance/MENU_INTERACTION_ACCEPTANCE.md) | A.22 菜单交互与连续滚动 |
| [BETA_1_ACCEPTANCE](acceptance/BETA_1_ACCEPTANCE.md) | Beta.1 卡牌覆盖与卡图 |
| [PACK_SELECTION_EXPORT_ACCEPTANCE](acceptance/PACK_SELECTION_EXPORT_ACCEPTANCE.md) | 卡包选择与 Windows 导出 |
| [CARD_POOL_REPORT](acceptance/CARD_POOL_REPORT.md) | 真卡池编译报告（张数与准入） |
| [DEMO_PACK_VERIFICATION](acceptance/DEMO_PACK_VERIFICATION_20261017.md) | 机制示范包实际数据核查 |
| [ROUTED_DISCARD_FIX](acceptance/ROUTED_DISCARD_FIX_20261017.md) | 溃退多余选靶与弃牌动画 |

**STAGE 6 系列**（引擎接入期的九个阶段，按序读）：

[6A 客户端可靠性](acceptance/STAGE_6A_ACCEPTANCE.md) ·
[6B1 卡池准入](acceptance/STAGE_6B1_ACCEPTANCE.md) ·
[6B2 指令与反制](acceptance/STAGE_6B2_ACCEPTANCE.md) ·
[6C1 选择协议迁移](acceptance/STAGE_6C1_ACCEPTANCE.md) ·
[6C2 通用多选](acceptance/STAGE_6C2_ACCEPTANCE.md) ·
[6C3 真实战场反馈](acceptance/STAGE_6C3_ACCEPTANCE.md) ·
[6D1 DIY 与试打](acceptance/STAGE_6D1_ACCEPTANCE.md) ·
[6D2 行动判定与空隙部署](acceptance/STAGE_6D2_ACCEPTANCE.md)

## battle/ — 战斗演出与交互

| 文档 | 内容 | 状态 |
|---|---|---|
| [BATTLE_DRAG_INTERACTION](battle/BATTLE_DRAG_INTERACTION.md) | 一律拖拽提交、点击只选中 | 现行 |
| [BATTLE_SLAM_REWORK](battle/BATTLE_SLAM_REWORK.md) | 三档拍桌（按防御力分档） | 现行 |
| [CAUSAL_DRAW_SCHEDULING](battle/CAUSAL_DRAW_SCHEDULING.md) | 抽牌因果顺序与逐张可操作 | 现行 |
| [BATTLE_STAGE1](battle/BATTLE_STAGE1.md) | 棋盘与基础操作 | 历史 |
| [BATTLE_STAGE2](battle/BATTLE_STAGE2.md) | 原版参照与战斗演出 | 历史 |
| [BATTLE_STAGE3](battle/BATTLE_STAGE3.md) | 抽牌/指令揭示/多目标结算 | 历史 |
| [BATTLE_STAGE4](battle/BATTLE_STAGE4.md) | 连贯抽牌、反制、状态变化 | 历史 |
| [BATTLE_STAGE5](battle/BATTLE_STAGE5.md) | 起手换牌面板 | 历史 |

## design/ — 界面、工坊与联机的实现方案

**DIY 工坊**（9 篇，从"要做什么"到"当时怎么改的"）：

| 文档 | 内容 | 状态 |
|---|---|---|
| [DIY_WORKSHOP](design/DIY_WORKSHOP.md) | 工坊总说明：卡包/扩展包、四页编辑、卡图重绘 | 现行 |
| [DIY_PACK_DESIGN](design/DIY_PACK_DESIGN_20261012.md) | 包与扩展包对引擎架构的映射，含唯一硬缺口 | 现行 |
| [DIY_PARSE_AND_INSPECTOR_REDESIGN](design/DIY_PARSE_AND_INSPECTOR_REDESIGN.md) | 自动解析 + Inspector 重做方案 | 现行 |
| [DIY_INSPECTOR_COMPOSITION_PROGRESS](design/DIY_INSPECTOR_COMPOSITION_PROGRESS_20261020.md) | 组合编辑能力基线与原型进展 | 现行 |
| [DIY_PARSE_INSPECTOR_IMPLEMENTATION_PLAN](design/DIY_PARSE_INSPECTOR_IMPLEMENTATION_PLAN_20261020.md) | 上一份的实施拆解 | 记录 |
| [DIY_CARD_ART_ALIGNMENT_FIX](design/DIY_CARD_ART_ALIGNMENT_FIX_20261020.md) | 卡图预览对齐修复 | 记录 |
| [BUILTIN_WORKSHOP_EDIT](design/BUILTIN_WORKSHOP_EDIT_20261019.md) | 内置/随附卡包可改的边界 | 历史 |
| [DIY_EDITOR_UPDATE](design/DIY_EDITOR_UPDATE_20261019.md) | 编辑器更新设计 | 历史 |
| [DIY_WORKSHOP_REWORK](design/DIY_WORKSHOP_REWORK_20261019.md) | 工坊改版与国家级联修复 | 历史 |
| [DIY_UI_ENGINE_REVIEW](design/DIY_UI_ENGINE_REVIEW_20261019.md) | 引擎更新后的只读检查与界面优化 | 历史 |

**联机与网络**：

| 文档 | 内容 | 状态 |
|---|---|---|
| [NETWORK_OPENSRC](design/NETWORK_OPENSRC_20261022.md) | 组网选型：ZeroTier/n2n 可透传广播，Tailscale 不行 | 现行 |
| [ROOM_SCREEN](design/ROOM_SCREEN_20261022.md) | 房间屏结构、入座、开局、重连接线 | 现行 |
| [POOL_ALIGNMENT](design/POOL_ALIGNMENT_20261024.md) | pool-v1 卡池对齐与开局前校验 | 现行 |
| [MULTIPLAYER_LOCKSTEP](design/MULTIPLAYER_LOCKSTEP_20261021.md) | 联机与匹配方案 | **同步层已被取代**：网络层（虚拟局域网）与发现层（组播宣告）仍有效，指令锁步改为服务端权威，见 NETWORK_OPENSRC |

**界面与视觉**：

| 文档 | 内容 | 状态 |
|---|---|---|
| [MENU_UI_OVERHAUL](design/MENU_UI_OVERHAUL_20261019.md) | 固定侧栏 + 档案式配色 | 现行 |
| [ASTRAL_MASTER_SWITCH](design/ASTRAL_MASTER_SWITCH_20261020.md) | 单位/指令/反制母版自动切换 | 现行 |
| [ASTRAL_TYPOGRAPHY](design/ASTRAL_TYPOGRAPHY_20261020.md) | 卡面字体与文字位置 | 现行 |
| [UI_PHASE1_SPEC](design/UI_PHASE1_SPEC.md) | 最早的 Godot UI 规格书 | 参照 |
| [SETUP_VISUAL_SAMPLE](design/SETUP_VISUAL_SAMPLE_20261019.md) | 作战准备页样板 | 记录 |

**其他**：

| 文档 | 内容 | 状态 |
|---|---|---|
| [ANIMATION_PORT](design/ANIMATION_PORT.md) | `animate.js` 导出项到 C# 的映射表 | 现行 |
| [DESKTOP_AUTHORING_PORT](design/DESKTOP_AUTHORING_PORT_20261016.md) | 原版解析器移植（ClearScript）与语料核对 | 现行 |
| [MATCH_FEED_DEPENDENCIES](design/MATCH_FEED_DEPENDENCIES_20261021.md) | `IMatchFeed` 依赖清单与最小抽象 | 现行 |
| [ENGINE_UPDATE_PLAN](design/ENGINE_UPDATE_PLAN_20261021.md) | 6d72eac → 88c96bc 的接入评估 | 历史 |

## release/ — 发版与离线更新

| 文档 | 内容 | 状态 |
|---|---|---|
| [VERSIONING](release/VERSIONING.md) | 一版一补丁、发版顺序、资产上传规则 | **现行（硬约束）** |
| [RELEASE_INDEX](release/RELEASE_INDEX.md) | 发行文件索引，`tools/` 生成，勿手改 | 生成物 |
| [2.0.0-alpha.5](release/RELEASE_2.0.0_ALPHA5_20261025.md) | 当前发行：调试台、部署选靶、工坊联机 | 现行 |
| [2.0.0-alpha.2](release/RELEASE_2.0.0_ALPHA2_20261024.md) | 增量基线 | 记录 |
| [2.0.0-alpha.1](release/RELEASE_2.0.0_ALPHA1_20261024.md) | 整代起点 | 历史 |
| [OFFLINE_UPDATE](release/OFFLINE_UPDATE.md) | 离线增量更新器用法 | 现行 |
| [COMPATIBLE_OFFLINE_UPDATE](release/COMPATIBLE_OFFLINE_UPDATE.md) | 多版本兼容补丁（一份补丁覆盖多个旧版） | 现行 |
| [BETA5_HOTFIX_COMPATIBLE_UPDATE](release/BETA5_HOTFIX_COMPATIBLE_UPDATE_20261024.md) | beta.1～beta.5 统一增量实测 | 记录 |
| [BETA7](release/RELEASE_BETA7_20261024.md) / [hotfix](release/RELEASE_BETA7_HOTFIX_20261024.md) / [h2](release/RELEASE_BETA7_HOTFIX2_20261024.md) / [h3](release/RELEASE_BETA7_HOTFIX3_20261024.md) | beta.7 系列（换牌入场信号、卡池对账、增量补丁） | 历史 |
| [BETA6](release/RELEASE_BETA6_20261024.md) | beta.6 | 历史 |
| [BETA5_HOTFIX](release/RELEASE_BETA5_HOTFIX_20261024.md) | beta.5 热修复 | 历史 |
| [BETA3](release/RELEASE_BETA3_20261020.md) | DIY 模板、Inspector、后端接入 | 历史 |
| [BETA2](release/RELEASE_BETA2_20261017.md) | 拖拽词条与离线增量更新 | 历史 |

## planning/ — 现状、交接与计划

| 文档 | 内容 | 状态 |
|---|---|---|
| [STATUS](planning/STATUS.md) | **项目现状与下一步**（唯一现状口径） | 现行 |
| [ALPHA_CLOSEOUT_PLAN](planning/ALPHA_CLOSEOUT_PLAN_20261026.md) | alpha 收口计划：9 个阶段、每项的收口标准与验证口径 | **现行（执行中）** |
| [SCOPE_PLAN](planning/SCOPE_PLAN_20261024.md) | 体量重规划：砍什么、留什么 | 现行 |
| [DEPLOY_TARGETING_ALPHA5_REOPEN_HANDOFF](planning/DEPLOY_TARGETING_ALPHA5_REOPEN_HANDOFF_20261025.md) | 部署指向仍不能选目标：**未解决**，含接手验收矩阵 | **现行（未闭合）** |
| [DEPLOY_TARGETING_HANDOFF](planning/DEPLOY_TARGETING_HANDOFF_20261025.md) | 上一轮记录，顶部有 23:35 的真因（候选池缺自己）与 99 张白板卡修复 | 记录（**与上一份互为前置，两份都要读**） |
| [HANDOVER_BETA5](planning/HANDOVER_BETA5_20261023.md) | beta.5 时的现状数字、怎么跑、怎么发版、已知问题 | 历史 |
| [UI_REPLAN_MULTIPLAYER](planning/UI_REPLAN_MULTIPLAYER_20261022.md) | 大更新后的 UI 改造顺序 | 历史 |
| [BETA4_HANDOFF](planning/BETA4_HANDOFF_20261021.md) | Beta.4 暂停现场（不可直接发布） | 历史 |
| [BETA4_REPLAN_ENGINE_A35E0B9](planning/BETA4_REPLAN_ENGINE_A35E0B9_20261021.md) | 基于 a35e0b9 的重新适配 | 历史 |
| [BETA4_UPDATE_RESEARCH_AND_PLAN](planning/BETA4_UPDATE_RESEARCH_AND_PLAN_20261020.md) | Beta.4 调研与实施 | 历史 |
| [DIY_EDITOR_ADVANCE_HANDOFF](planning/DIY_EDITOR_ADVANCE_HANDOFF_20261019.md) | DIY 编辑器的提前交接 | 历史 |
| [ASSET_MODEL_MIGRATION_ISSUES](planning/ASSET_MODEL_MIGRATION_ISSUES_20261013.md) | 新资产模型迁移的问题报告 | 历史 |
| [NEXT_DEVELOPMENT_PHASE](planning/NEXT_DEVELOPMENT_PHASE.md) | 早先的"真实对战闭环"规划 | 历史 |
| [HANDOVER](planning/HANDOVER.md) | 2026-10-07 交接 | 历史 |
| [RETROSPECTIVE_2026-10-07](planning/RETROSPECTIVE_2026-10-07.md) | 全局回顾，含早期教训 | 记录 |

---

## archive/ — 归档区

一次性过程记录（某次整理、某次待命准备），结论已被吸收，只作追溯。
归档规则与清单见 [archive/README.md](archive/README.md)。

- [整合-引擎AI-20261017/](archive/整合-引擎AI-20261017/整合预案.md)：与上游引擎合并前的隔离调研与预案（5 篇）
- [整理-20261017/](archive/整理-20261017/整理结果.md)：目录搬迁前的扫描与账目（2 篇）

## engine-patches/ — 上游补丁原件

`docs/engine-patches/*.patch`：已提交上游或本机累积的引擎 `.patch` 原件，追溯"我们对上游改了什么"用。
每个版本对应哪一份见 [STATUS](planning/STATUS.md) 与 [接入基线](engine/ENGINE_INTEGRATION_BASELINE.md)。

## 不在本目录的文档

- `../架构docs/` —— **上游引擎方提供的架构文档**（2026-10-07 基线），独立编号 01–09，不并入本目录，以便与上游原文逐条对照。
- `../README.md` —— 面向使用者的功能说明与发布记录。
- `../proto/contracts/README.md` —— 上游契约提案。
- `../proto/assets/battle/ASSETS.md` —— 战场美术资源清单。

## 约定

- 文件名里的日期是 `YYYYMMDD`，用于区分同一主题的不同批次；阶段名（`STAGE_6A_ACCEPTANCE`）不重命名，避免与提交历史和外部引用脱节。
- 新增文档直接进对应桶，**并在这里补一行**。没有合适桶就先放 `planning/`，别在 `docs/` 平铺。
- 路径统一写 `docs/<bucket>/<file>.md`；桶内互链用相对路径。移动文档时同步改引用——`docs/` 内部不允许出现断链。
- 判断"过时"只改本索引的状态列，**不删、不改名**；只有"一次性过程记录"才进 `archive/`。
