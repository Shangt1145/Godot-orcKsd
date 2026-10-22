# 文档索引

**当前引擎兼容状态（2026-10-22）**：[7f3ae35 更新影响与观战接口验证](engine/ENGINE_7F3AE35_IMPACT_20261022.md)。引擎已更新，UI 尚有 19 项编译错误和旧会话协议待适配；下列历史验收不代表新基线已通过。

按主题分桶，每桶内的文件保持原文件名（`STAGE_6A_ACCEPTANCE` 这类阶段名不重命名，避免与提交历史和外部引用脱节）。

| 目录 | 装什么 | 什么时候看 |
|---|---|---|
| `engine/` | 引擎桥接、集成基线、词条系统、引擎补丁发布 | 动桥接层或词条时 |
| `research/` | 原版 KARDS 的行为研究、部署/指令参考、卡池覆盖 | 要对齐原版手感或数值时 |
| `acceptance/` | 各阶段逐项验收记录（6A–6D2、菜单、卡包、DIY） | 判断"某个功能到底做到哪一步" |
| `battle/` | 战斗各阶段设计、拖拽交互、拍桌重做、抽牌调度 | 改战斗演出时 |
| `design/` | UI 规格、DIY 工坊、桌面端移植、动画移植 | 改界面或编辑器时 |
| `release/` | 离线更新、兼容补丁、发版记录、版本规则 | 打包发版时 |
| `planning/` | 交接、阶段性计划、复盘、素材模型迁移问题 | 接手项目或排下一步时 |
| `整合-引擎AI-20261017/` | 与上游引擎合并的调研、隔离方案、整合预案 | 做引擎合并时 |
| `整理-20261017/` | 2026-10-17 项目目录整理前的扫描与清理账目 | 想知道东西被搬到哪了 |
| `engine-patches/` | 已提交上游的 `.patch` 原件（A.20 行动判定等） | 追溯对上游的改动 |

当前更新方案：[Beta.4 调研与实施方案](planning/BETA4_UPDATE_RESEARCH_AND_PLAN_20261020.md)。

新引擎适配方案（2026-10-21）：[基于 a35e0b9 重新定制 Beta.4](planning/BETA4_REPLAN_ENGINE_A35E0B9_20261021.md)，后续以此方案为准。

制作前参考研究：[KARDS 与卡牌制作／规则编辑案例](research/DIY_WORKSHOP_UI_CASE_STUDY_20261021.md)。

工坊实施进展：[工作区更新与验证记录](acceptance/BETA4_WORKSHOP_WORKSPACE_UPDATE_20261021.md)。

最新交互修正：[面向制作任务的效果编辑与引擎 88c96bc 适配](acceptance/WORKSHOP_HUMAN_EDITOR_ENGINE_88C96BC_20261021.md)。

全入口遗漏修正：[所有卡包统一工作区与草稿自动恢复](acceptance/WORKSHOP_ALL_PACK_ROUTES_AND_DRAFT_RECOVERY_20261021.md)。

2026-10-21 暂停现场：[Beta.4 开发交接（尚未完成，不可直接发布）](planning/BETA4_HANDOFF_20261021.md)。

2026-10-22 大更新后的改造准备：[联机主流程 UI 核查与实施顺序](planning/UI_REPLAN_MULTIPLAYER_20261022.md)。

本轮实现：[联机 AI 开局修复与作战准备页](acceptance/ROOM_AI_AND_SETUP_SELECTION_20261022.md)。

联机网络层选型（Radmin → 开源组网）：[从 Radmin 切换至开源组网](design/NETWORK_OPENSRC_20261022.md)（ZeroTier / n2n；广播发现链路一行不改）。

卡组选择视觉修正：[以原版卡背陈列重做准备页](acceptance/SETUP_CARD_BACK_REFERENCE_20261022.md)。

材质细化：[KARDS 纸面、织物与字重对照及第一轮实装](research/KARDS_MATERIAL_STUDY_20261022.md)。

## 不在本目录的文档

- `../架构docs/` —— **上游引擎方提供的架构文档**（2026-10-07 基线，受众写明"Godot 前端开发者"），独立编号 01–09，不并入本目录，以便与上游原文逐条对照。
- `../README.md` —— 面向使用者的功能说明与发布记录。
- `../proto/contracts/README.md` —— 上游契约提案。
- `../proto/assets/battle/ASSETS.md` —— 战场美术资源清单。

## 约定

- 文件名里的日期是 `YYYYMMDD`，用于区分同一主题的不同批次。
- 新增文档直接放进对应桶；没有合适桶就先放根目录，攒够再分。
- 移动文档时同步改引用：`docs/<bucket>/<file>.md` 是唯一合法形式，平铺的 `docs/<file>.md` 已不再存在。
