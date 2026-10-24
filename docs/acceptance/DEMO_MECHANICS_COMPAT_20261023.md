# 机制示范包兼容修复（2026-10-23）

本轮在 UI `cd7aa41`、引擎 `e80198b` 上复现并修复两处问题。没有拉取、推送、重新种入示范包或发布安装包。引擎修改目前是本地未提交修改；明天更新前必须保留。

## 确认的故障与修复

1. **虎视攻击在转换成功后报失败，并漏掉扣费和攻击机会收尾。**
   `CommandManager.DispatchAttackAsync` 先执行攻击与攻击后词条；虎视已将原目标离场、替换并销毁，随后指向通知仍读取 `targetRef.Value`，抛出 `StaleReferenceException`，外层返回 `CommandFlowFault`。现在仅向仍存活的原目标发送指向通知；已执行的攻击正常扣费、消耗行动机会，不会将通知发给替换出来的另一个单位。
2. **独立发射扣费、消耗后零伤害。**
   保存的发射卡声明词条参数为 2，而 `data.damage` 只接受实例记录、缺少记录便抛错；出牌事件中的异常被隔离，卡却已经付费。恢复此前文档与种子卡声明的兼容语义：记录存在则使用记录（包括 0），缺少该记录则使用词条参数，未声明参数才用程序 `DefaultValue`。装填生成的发射继续读取捕获值；没有重写玩家保存的词条。

这两项不能解释用户反馈的所有异常，也没有证明新版/旧版冲突是唯一原因。

另修复一处验证夹具的版本适配错误：`OrcDragVerification.PrepareMovementAsync` 曾将攻击额度硬写为 1，导致机动第二次拖拽不可用。现在调用原生 `KeywordRules.ResetActionQuotas`，按 `attack.limit` 恢复额度。正常部署路径与探针准备路径分别覆盖两次攻击，避免只让其中一条路径通过。

## 验证

| 项目 | 结果 |
|---|---|
| 新增真实存档回归 | 16/16 通过；包含 11 个示范词条的关键行为、取消重试、死亡不触发复员、独立与生成发射、机动两条准备路径 |
| 引擎全量 | 1357/1357 通过，含新增 3 项回归 |
| UI 全量 | 546/549 通过；仍为原有 3 项 `OfficialCardExpansionTests` 失败 |
| Godot 无头实际拖拽探针 | `findings=0`；独立发射扣 2 点、卡离手、伤害 2 |
| Godot Vulkan 渲染与输入注入 | `findings=0`；独立发射检查实际执行；截图 `H:/g/kards/artifacts/demo-pack-standalone-fire-arrow.png` |
| Godot 词条移动探针 | `KEYWORD_DRAG_VERIFY_OK`；正常后撤、两次攻击、失去机动、第三次拖拽不提交 |
| Godot 完整单机链 | `UI_VERIFY_OK screens=11 cards=521 texture_cache=64/64`，退出码 0 |
| 玩家数据 | `diy-packs.json`、`diy-cards.json`、`decks.json` 与调查前 SHA256 一致 |

新增回归位于 `tests/SavedDemoMechanicsTests.cs`，输入为 `tests/fixtures/demo-mechanics/` 中从真实存档提取的示范包快照（11 张种子卡，不含玩家另建卡牌）。测试在临时目录运行，既不运行种子工具，也不修改真实工坊。

原 Godot 探针会在缠斗杀死目标后跳过独立发射，并在 `findings>0` 时以成功退出。本轮为独立发射创建单独存活目标、验证精确伤害/扣费/离手，并让任何 finding 导致失败退出。

日志：`artifacts/mechanics-godot-baseline.log`、`artifacts/mechanics-godot-fixed.log`、`artifacts/tiger-failure.log`。测试报告：UI `tests/TestResults/mechanics-full-ui.trx`；引擎 `tests/Orc.Game.Tests/TestResults/mechanics-full-engine.trx`。

## 明天接引擎更新

本轮引擎补丁：[demo-mechanics-compat-20261023.patch](../engine-patches/demo-mechanics-compat-20261023.patch)。它只含本轮两个修复与原生回归，不含更早的 `e80198b` 空牌库补丁。

1. 保留当前引擎修改；用独立 checkout 核对上游是否已包含 `e80198b` 和本轮两项修复。
2. 未包含时先 `git apply --check`，再按实际差异合入；不要在已经修改的本地工作区重复应用。当前补丁已通过反向 `git apply --check --reverse` 检查，确认与本地修改对应。
3. 重跑示范包 16 项、引擎全量、UI 全量、Godot 示范包和移动探针。原生词条程序变更不能只凭编译或卡包准入通过判验收。

仍未覆盖新引擎更新后的表现、双机联机、自定义词条全部组合、完整玩家操作长局。已发布 beta.5 ZIP 仍是旧包，本轮仅更新开发源码和运行副本。
