# 2.0.0-alpha.2（2026-10-24）

## 性质：热修复（只发补丁）

修的是一个联机对战里的**卡死**问题，属于就地修复，因此按发行规则**只出增量补丁，不出全量包**；
新玩家请先装 [2.0.0-alpha.1](https://github.com/Shangt1145/Godot-orcKsd/releases/tag/v2.0.0-alpha.1) 的完整包，再应用本补丁。

## 修了什么

**现象**：联机对战中，把单位攻击指向非法目标松开后，这个单位**再也无法攻击**，直到它自己的状态更新。

**根因**（通知链断了一环）：

1. 攻击是两段式：`command.begin{ref}` → 服务端回 `targeting` → 玩家选定 → `target.submit{sessionId,picks}`。
2. UI 发起时会把该单位记为"待定"（`BattleInteractionStateMachine.Reserve`）。
3. 第二段 `target.submit` 被引擎拒绝时，只打了 `ErrorRaised`，**从未发 `CommandRefused`**。
4. 待定标记没人清 → `Submit` 开头的 `IsPending(actor)` 守卫把该单位后续所有命令**静默丢弃**。
5. 只有该单位产生演出（`Presented`）或投影刷新时才清 —— 这就是"直到它状态更新才恢复"。

同一条链路还有两个同类漏洞：玩家右键取消（`target.cancel`）与选靶超时（`notify/target.expired`）也都不清待定。

**修法**：记录发起选靶的命令（`_targetCommand`），在 `target.submit` 失败、`target.cancel`（无论成败）、
`notify/target.expired` 三处统一调用 `RefuseTargeting` → 补发 `CommandRefused`；提交成功时不拒绝，
交给演出清理。另加一行可检索日志：

```
[net] targeting refused -> unlock CommandUnit actor=<uid>
```

## 发行文件

| 文件 | 说明 |
|---|---|
| `patches/OrC-KSD-2.0.0-alpha.2-from-4DDCFE28C54E-to-27592182B321.orcupdate`（9.4 MB） | 增量补丁（仅此一项对外分发） |
| `SHA256.txt` | 校验和 |

更新器仍用 **1.2.0**（本版未改动更新器，沿用 [alpha.1 的资产](https://github.com/Shangt1145/Godot-orcKsd/releases/tag/v2.0.0-alpha.1)）。
用 1.1.0 会报「补丁的文件清单无效」，这是校验规则代差，不是补丁损坏。

## 构建

- 来源构建：`4DDCFE28C54E0B3A6496BCF366B2536FECA9BED88E4D7298ACF72E3B008286D6`（alpha.1）
- 本版构建：`27592182B32173FFD6EFBBBCEECFB3F914259637F4BA2E196DE59114503F138A`（alpha.2）

## 验证

- `dotnet build` 0 错误；UI 单测 **571/571**（全量，`-FullTests`）
- `PATCH_EXACT_FILES_OK`：补丁应用后逐文件哈希与目标构建一致
- `OFFLINE_UPGRADE_EXE_VERIFY_OK`：用更新器 1.2.0 实测从 alpha.1 升级，并复验升级后的 EXE

## 边界

- 这是**联机专属路径**，单机不存在该问题（单机取消时本来就会 `RefuseCommand`）。
- 双端实机仍建议复验一次：把单位拖向非法目标 → 松开 → 再用同一单位攻击合法目标，应能正常出手。
- 补丁只覆盖 `2.0.0-alpha.1` → `2.0.0-alpha.2`。
