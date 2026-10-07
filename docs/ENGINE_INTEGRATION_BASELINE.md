# OrC-KSD 接入基线

记录日期：2026-10-07。本轮没有 fetch、pull、切换引擎版本或修改引擎源码。

## 源码与版本

- 上游：[Shangt1145/OrC-KSD](https://github.com/Shangt1145/OrC-KSD)。
- 当前引用目录：`H:\Working Folder\OrC-KSD-github`。
- HEAD：`73f76abd2046c293921d5cfecf650d353c2f5b10`。
- 已有本地修改：`src/Orc.Game/Managers/PlayManager.cs`，部署候选放宽为全部空槽。本轮保留，没有回滚或覆盖。
- 该文件 SHA-256：`6AC9ED8D8023A507A871CA6F6D993E64D046CE1BD3CF4AE684A48E19E65B68FC`。
- `OrcEngineRoot` 指向 **Orc.Game 项目目录**，默认是 `H:\Working Folder\OrC-KSD-github\src\Orc.Game`。可通过 MSBuild 覆盖，例如 `-p:OrcEngineRoot='D:\engines\OrC-KSD\src\Orc.Game'`。
- 未修改另一份 `H:\Working Folder\OrC-KSD` 中的大量未提交工作。

当前部署实测依赖已有本地修改，不能无条件推广到干净上游 checkout。

## 能力状态

| 能力 | 状态 | 接入方式或限制 |
|---|---|---|
| 初始化、换牌、单位部署、移动、攻击 | 已验证 | Session 统一入口；返回真实拒绝原因；Godot 不计算规则 |
| 移动槽位、攻击对象候选 | 已验证 | ActionReader 投影引擎实际候选；UI 从候选选择槽位，独立记录前线画面顺序 |
| 手牌可打出预查询 | 当前接口未提供 | `HandPlayabilityKnown=false`，可尝试集合不能画成确认可打出的高亮 |
| `TakeSegments()` 单次消费 | 已验证 | 主 host 消费；观察 host 只刷新；座位切换复用主 host |
| 桥接驱动动作的历史终态 | 已验证 | 每次部署、指挥、结束回合及对手动作完成后立即冻结，Pump 稍后分发 |
| 段完成回调/任意外部批量动作历史状态 | 当前未具备/未验证 | 多段积压时无法恢复各段历史终态；`HistoricalStatesVerified=false` 显式暴露限制 |
| 表现串行、混合伤害与抽牌、取消 | 已验证 | PresentationPlayer 消费冻结 DTO；伤害进入同一步骤流；不另行消费引擎队列 |
| 卡池准入审核 | 6B1 已验证 | 9 种完整单位声明绑定源 SHA-256；真实入口拒绝未装配效果、生成卡、缺失数值和声明漂移 |
| 效果装配与本地 effects 转换 | API 存在，项目接入未验证 | 留在 6B2；卡面和动画不替代规则执行 |
| 秘密反制触发与费用隐藏 | 尚未验证 | 当前 `counter.triggered` 不能直接当作原版条件命中；留在 6C |
| 空牌库疲劳 | 当前不可用 | 现有空牌库路径会抛错；不在 UI 补扣血规则 |
| 双方真人换牌与完整热座闭环 | 部分具备 | 6A 关闭真人模式自动代打、统一消费；完整闭环留在 6C/6D |

原始 `SegmentPlayer` 保留；真实桥接载荷由 `PresentationPlayer` 管理串行、冻结和取消。两者没有同时消费引擎队列。

## 用户通知更新后的流程

1. 用户提醒后才拉取。先检查 HEAD、未提交修改和实际引用目录；保留用户工作，不自动 reset 或覆盖。
2. 记录新 commit，核对初始化、部署、指挥候选、选择、事件段和效果装配的 API 与语义变化。
3. 适配集中在导入层及 `proto/bridge/Kards.Ui.OrcBridge`。如果上游新增段完成回调，替换动作边界采集点；Godot 继续消费冻结契约。
4. 核对运行库和模板资源，重建并执行桥接测试、UI 程序集边界检查及两种尺寸的拖拽检查。
5. 更新本表和验收记录；通过场景断言后才能将新能力标记为已验证。

客户端验收见 [STAGE_6A_ACCEPTANCE.md](STAGE_6A_ACCEPTANCE.md)。
