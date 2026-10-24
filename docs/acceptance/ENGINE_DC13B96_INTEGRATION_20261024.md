# 引擎上游整合（2026-10-24）

本轮重新 `fetch origin`，并用 `git ls-remote --heads origin` 核对远端：只有 `main`，最新为 `dc13b96b5ba584af615546a0c99d3a36af134729`。相对上次远端 `64d8db2`，本次只有 `.ams/context/github/交接说明.md` 的同步通道文档更新，没有引擎、协议或测试代码变更，不能当作新的机制修复已经推送。

## 合并结果

- 本地原 HEAD 为 `e80198b`（空牌库不再强行抽牌），另有虎视失效引用、独立发射缺少实例数据时回退参数的未提交修复与三项原生回归。
- 以保留本地历史的方式合并 `origin/main`，新本地 HEAD 为 `0f98685`。没有冲突，远端 `dc13b96` 已是本地祖先；本地 `main` 比远端多两个提交（空牌库修复和合并提交），未推送。
- 合并前后引擎 `src/`、`tests/` 的提交内容相同；三份未提交文件的二进制补丁 SHA256 均为 `4AADDE39AF95C4803ED34201B43384FEF5E069970DB72334BB21385D8B3FD004`。已有示范包兼容补丁反向检查通过，修复未丢失、未重复应用。
- 备份位于 `artifacts/engine-integration-20261024/`，含合并前后未提交补丁与空牌库提交补丁；没有使用 reset、清理玩家内容或覆盖工坊文件。
- 仓库未配置提交身份，因此合并提交仅在该命令中使用 `Codex <codex@openai.com>`，没有修改全局或仓库 Git 身份配置。

## 验证

客户端构建与运行副本继续使用 `H:/Working Folder/OrC-KSD-github/src/Orc.Game`；今天的抽牌、数字、缠斗选靶和演出提速修改全部保留。

| 检查 | 结果 |
|---|---|
| `dotnet build Kards.Ui.sln`、同步并构建 `H:/g/kards/Kards.Ui.csproj` | 均成功，0 错误 |
| 引擎原生全量 | 1357/1357 通过 |
| UI 全量（含示范包 16 项和抽牌/合批 3 项） | 549/552 通过；仍为已有三个 `OfficialCardExpansionTests` 失败，没有新增失败 |
| Godot 无头 `--verify-battle-feedback` | 退出 0；抽牌、部署预览、目标点击、合批速度、真实保存示范包均通过，`findings=0` |
| Godot 无头 `--verify-keyword-movement` | 退出 0；后撤、两次攻击、失去机动后拒绝第三次拖拽通过 |
| 玩家数据 | `decks.json`、`diy-cards.json`、`diy-packs.json` 与调查备份 SHA256 一致 |
| 整合后再次核对远端 | `main` 仍为 `dc13b96` |

原生报告为引擎 `tests/Orc.Game.Tests/TestResults/integration-dc13b96-20261024.trx`，UI 报告为客户端 `tests/TestResults/integration-dc13b96-20261024.trx`。Godot 日志位于客户端 `artifacts/engine-integration-20261024/{battle-feedback,keyword-movement}.log`。因为此次上游没有代码变化，未重复此前已通过的完整单机长链和 Vulkan 画面检查。

此次整合不更新已发布 ZIP，也不代表完整混编长局或双机联机已验收。机制兼容和对局反馈的具体变更见 [机制修复](DEMO_MECHANICS_COMPAT_20261023.md)、[对局反馈](BATTLE_FEEDBACK_20261024.md)。
