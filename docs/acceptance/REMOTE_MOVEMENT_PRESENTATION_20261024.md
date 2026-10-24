# 联机前线移动与演出链修复（2026-10-24）

## 根因

1. 服务端移动候选将前线写成 `front`，界面使用 `frontline`，因此拖到前线被界面取消。
2. 指挥拖动只传单位编号，没有把选中的移动空隙传给远端选靶应答。
3. Main 每收到一次远端投影就销毁 PresentationPlayer，并通过 ApplyProjection 中断所有演出。
4. notify/segment 先于新投影到达，客户端立即在旧快照中查新部署的卡，导致部署、移除等步骤丢失；翻译器还缺少伤害、攻击来源和指令步骤。

上一轮只证明了换牌后单位可部署，没有验证前线推进和演出实际播放，不能据此认定联机战斗已完整验收。

## 修改

- UiMoveOption 增加可选 GapId；服务端前线候选统一为 frontline，并携带引擎当前 FormationGap 的真实编号。原生行动读取也输出同一字段。
- RemoteMatchFeed 按 UI 选择的区域与位置找到 GapId，在 command.begin/command.move 的候选中自动应答，不从位置序号伪造引用。
- 远端事件段按序号缓存，等同批服务端视图到达，再使用 before/after 一起翻译；即使没有显式演出步骤，移动等静默变化也通过同一队列呈现。
- AI 路径也保证先发送段、后发送对应投影。对局与观察视角未变时保留演出队列，最新输入投影独立更新；观察者切换视角会建立新的显示基线。
- 远端翻译补齐伤害、攻击来源、死亡、指令、资源与可见状态反馈，并去重同一次抽牌的 drawn/hand.add。
- 服务端附带公开玩家座位，隐藏抽牌仍只呈现匿名敌方抽牌；已打出的公开指令携带单卡投影，避免离场后无法查到卡面。没有把任意事件中的对手手牌添加到单卡投影。

## 验证

- UI 全量 570/570；服务端全量 119/119。
- 新增 4 个远端翻译单测：新快照部署与抽牌去重、双方同时离场时的攻击来源与致死伤害、隐藏敌方抽牌、已离场的公开指令卡面。
- 加强 --verify-room-remote：双方真人连接、延迟确认换牌、实际拖牌部署、动画中主动刷新投影、同一部署仅播放一次并完整收尾、真实拖动到前线、逐帧观察位移、真实拖动攻击总部、伤害与武器命中阶段均通过。
- `artifacts/remote-motion-full-gameplay.log` 标记包含 `deployment-animation-survives-refresh actual-drag-frontline animated-move actual-drag-attack weapon-impact`。
- 此处真人连接仍在同一台机器；不是跨机器组网或完整卡池长局验收。最终发行包与实际升级回归见本轮 hotfix.2 发行记录。

引擎源码改动位于 `H:/Working Folder/OrC-KSD-github/src/Orc.Mcp/McpService.Projection.cs` 与 `src/Orc.Server/Transport/ServerHost.cs`。累计引擎工作区补丁保存为 `docs/engine-patches/remote-movement-presentation-20261024.patch`，其中也保留该工作区此前 beta.7 的入场通知与协议增量，更新引擎时不可丢失。
