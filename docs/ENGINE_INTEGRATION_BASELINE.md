# OrC-KSD 接入基线

记录日期：2026-10-09。按用户要求 fetch 后快进更新上游，再恢复已有部署修改；新增能力未绕过卡池审核。

客户端 A.18 增量（2026-10-10）：只使用上述现有引擎，新增真实战场状态和原生终局接入，未重新拉取或改动引擎。详情见 [STAGE_6C3_ACCEPTANCE.md](STAGE_6C3_ACCEPTANCE.md)。

客户端 A.19 增量（2026-10-11）：接通 DIY 卡定义、原生 DSL/预制体编译及装载、图鉴/构筑/试打。Godot 从内存加载程序集时，桥接宿主提供现有构建 DLL 的 Roslyn 元数据引用，并绑定到已经加载的原生程序集；效果继续由引擎执行，未复制或修改引擎源码。详情见 [STAGE_6D1_ACCEPTANCE.md](STAGE_6D1_ACCEPTANCE.md)。

客户端 A.20 增量（2026-10-11）：按用户授权修改本地引擎，加入行动输入判定、手牌入口可用性查询和阵型空隙部署；替代旧的全部空槽候选修改。实现时没有重新 fetch、提交或推送，当时 HEAD 保持不变。抽牌飞行中由真实卡牌接受输入，拖拽接管该牌的补间。详情见 [STAGE_6D2_ACCEPTANCE.md](STAGE_6D2_ACCEPTANCE.md)。

用户授权发布（2026-10-11）：A.20 的 18 文件补丁已提交并推送至 [Shangt1145/OrC-KSD 的 main](https://github.com/Shangt1145/OrC-KSD/commit/7540b432c940e56283643adaa122cac6ee3cb14a)。原生 1099 / 1099 测试复验通过，远端与本地 HEAD 一致，引擎工作区干净。发布记录见 [ENGINE_PATCH_PUBLISH_20261011.md](ENGINE_PATCH_PUBLISH_20261011.md)。A.22 菜单改动不包含在引擎提交中。

## 源码与版本

- 上游：[Shangt1145/OrC-KSD](https://github.com/Shangt1145/OrC-KSD)。
- 当前引用目录：`H:\Working Folder\OrC-KSD-github`。
- 当前 HEAD：`7540b432c940e56283643adaa122cac6ee3cb14a`，已推送 `origin/main`。补丁基础提交为 `8b0ae98158394cae264b4ed80dfa10fb506e7351`。
- A.20 前的本地修改：`src/Orc.Game/Managers/PlayManager.cs` 将部署候选放宽为全部空槽。升级前写入 `codex-preserve-deployment-slots-20261009` stash，快进后恢复；stash 保留用于恢复旧基线修改。
- A.20 前该文件 SHA-256：`B336BD8EAF0C4792D4703288FCF42C3887295F81F03E5C5F7977E203A39B4C08`。当前 SHA-256：`F6453A0EF053A1744C69A5C5775E501F8908D2D18244B7F9F0BDA7054D8F0139`。
- 已发布的 A.20 补丁共 18 个文件，含新增 `Board/FormationGap.cs`、`Judicators/ActionInputJudicator.cs`、入口/链路改动、元数据及 4 个原生测试文件的候选契约迁移。完整补丁：[6d2-action-portals.patch](engine-patches/6d2-action-portals.patch)。SHA-256：`6F0C90D725E1AC468C36021A8DB8BD410C885DBEA972376143C6BD529FE35AE5`；包含未跟踪的新文件，已通过当前源码上的反向 `git apply --check`。
- `OrcEngineRoot` 指向 **Orc.Game 项目目录**，默认是 `H:\Working Folder\OrC-KSD-github\src\Orc.Game`。可通过 MSBuild 覆盖，例如 `-p:OrcEngineRoot='D:\engines\OrC-KSD\src\Orc.Game'`。
- 未修改另一份 `H:\Working Folder\OrC-KSD` 中的大量未提交工作。

当前客户端依赖包含 A.20 扩展的引擎版本 `7540b43` 或后续兼容提交。保存的补丁以 `8b0ae98` 为基础，用于旧基线恢复；已包含该提交的 checkout 无需重复应用。迁移前检查目标工作区，不覆盖其他本地工作。当前源码哈希清单见 `artifacts/action-portals/engine-source-baseline.json`。

新协议迁移与验收见 [STAGE_6C1_ACCEPTANCE.md](STAGE_6C1_ACCEPTANCE.md)。旧 `CreateTargeter`/候选收集/槽位 responder 已退出真实桥接，使用 `BeginTargeting`、`NextAsync`、`Submit`；引擎重试同一实例，界面每次交付独立令牌。

## 能力状态

| 能力 | 状态 | 接入方式或限制 |
|---|---|---|
| 初始化、换牌、单位部署、移动、攻击 | 已验证 | Session 统一入口；返回真实拒绝原因；Godot 不计算规则 |
| 移动槽位、攻击对象候选 | 已验证 | ActionReader 投影引擎实际候选；UI 从候选选择槽位，独立记录前线画面顺序 |
| 行动输入判定 | A.20 扩展已发布并验证 | `action.input.allowed` 注册为内置判定器；查询和提交拉取 UI 值状态；无读者时保持无界面引擎行为。总判定器 22 条，其中内置 21 条、可选示例 1 条 |
| 手牌可打出预查询 | A.20 扩展已发布并验证 | `PlayManager.CanPlay` 执行原生入口验证，`HandPlayabilityKnown=true`；不运行效果。效果内选靶和最终提交仍需复验 |
| 支援阵型部署 | A.20 扩展已发布并验证 | `GetInsertionGaps` 提供含总部的全部顺序边界；稳定候选 ID 绑定当前占用快照；原生复验后插入并更新单位与总部位置；满线无候选 |
| `TakeSegments()` 单次消费 | 已验证 | 主 host 消费；观察 host 只刷新；座位切换复用主 host |
| 桥接驱动动作的历史终态 | 已验证 | 每次部署、指挥、结束回合及对手动作完成后立即冻结，Pump 稍后分发 |
| 段完成回调/任意外部批量动作历史状态 | 当前未具备/未验证 | 多段积压时无法恢复各段历史终态；`HistoricalStatesVerified=false` 显式暴露限制 |
| 表现串行、混合伤害与抽牌、取消 | 已验证 | PresentationPlayer 消费冻结 DTO；伤害进入同一步骤流；不另行消费引擎队列 |
| 卡池准入审核 | 6B2 已验证 | 21 种声明绑定源 SHA-256：9 单位、11 指令、1 反制；真实入口继续拒绝未装配效果、生成卡、缺失数值及漂移 |
| 效果装配与本地 effects 转换 | 代表效果已验证 | 导入层审核声明；桥接经 CommandCard 预打出/主动 handler、原生 Selector 流程、EffectRuntime 与修饰链执行；详见 6B2 验收 |
| 条件反制与费用隐藏 | 部分已验证 | UseCounterAsync 负责预留/退费；USG/_13 监听真实友方 card.died、消费一次并造成总部伤害；激活信号与条件触发分开。敌方费用隐藏及三种复杂打断反制仍留待后续 |
| 空牌库疲劳 | 当前不可用 | 现有空牌库路径会抛错；不在 UI 补扣血规则 |
| 本地同屏双人 | 产品已移除 | 2026-10-09 用户要求删除入口、切换界面与双方真人换牌；内部双视角验收不作为游戏模式 |
| 通用多选与原生候选定义 | 6C2 已验证 | 原生 Hand 多引用与 CardPickerList 标识通过桥接；UI 调度红圈叉号/分页/已选计数/满额自动提交；少选、零选和键盘提交/取消禁用；注册定义直接映射，不实例化或生成卡牌 |
| 运行时词条、老兵、隐蔽揭示、兵种与洗牌 | 6C3 已验证通用接入 | 原生读取/事件和无事件词条变更进入战场反馈；不是原始全卡池效果准入 |
| 认输、总部致死及重开清理 | 原生路径已验证 | 引擎胜方/原因，一次终局；禁止继续行动，零血总部不复活；重开取消旧原生请求 |

原始 `SegmentPlayer` 保留；真实桥接载荷由 `PresentationPlayer` 管理串行、冻结和取消。两者没有同时消费引擎队列。

## 用户通知更新后的流程

1. 用户提醒后才拉取。先检查 HEAD、未提交修改和实际引用目录；保留用户工作，不自动 reset 或覆盖。
2. 记录新 commit，核对初始化、部署、指挥候选、选择、事件段和效果装配的 API 与语义变化。
3. 适配集中在导入层及 `proto/bridge/Kards.Ui.OrcBridge`。如果上游新增段完成回调，替换动作边界采集点；Godot 继续消费冻结契约。
4. 核对运行库和模板资源，重建并执行桥接测试、UI 程序集边界检查及两种尺寸的拖拽检查。
5. 更新本表和验收记录；通过场景断言后才能将新能力标记为已验证。

客户端验收见 [STAGE_6A_ACCEPTANCE.md](STAGE_6A_ACCEPTANCE.md)。
