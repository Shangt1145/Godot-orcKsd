# 2.0.0-alpha.2（2026-10-24）

## 性质：热修复（只发补丁，且**只有这一条**）

本版是 `2.0.0-alpha.1` 的热修复。按发行规则，**同一版本号下只维护一条补丁**：
起点永远是 alpha.1 的完整包，终点是当前最新构建。此前的补丁已被这条**覆盖**，不再提供中间版本。

- 新玩家：先装 [alpha.1 完整包](https://github.com/Shangt1145/Godot-orcKsd/releases/tag/v2.0.0-alpha.1)，再打本补丁
- 老玩家：直接用更新器 1.2.0 应用本补丁（覆盖安装，可重复应用）

## 修了什么

### ① 指向性指令空放（例：德国 1 费「抑制」）

**现象**：把指令拖到没有单位的支援阵线，播了释放动画，但没有任何效果，卡也没了。

**根因**：引擎投影里的 `orderTargets` **恒为空数组**（`McpService.Projection.cs`，注释写明"指令目标在打出后由选靶请求提供"）。
于是 UI 的"指向性指令"判定永远为假 → 箭头永远不画 → 所有指令都按"无目标"提交 → 引擎照常打出 → 卡内脚本找不到目标 → 静默空放。

**修法**：落点上站着单位时，把该单位作为 `TargetUid` 一并提交；服务端开选靶时可自动命中它，
不再落到"候选第一个"（打错人）或空放。**拖到空地仍会提交**——因为引擎没有提前告知哪些指令需要目标，这一点需引擎侧补
`orderTargets` 或"选靶完成前不落子"才能真正拦住。

### ② 单位攻击有概率卡住

两条通路：

1. 选靶第二段（`target.submit`）被拒时**从不通知 UI**，该单位永久"待定"，后续命令被静默丢弃。
2. 引擎对某些动作**返回成功却不产生任何演出**，`Presented` 与 `CommandRefused` 都不来 —— 没有任何回调可等。
   新增派发看门狗：命令发出 8 秒后若仍是待定，则本地解锁并提示可重试（服务端若稍后真执行，投影会纠正画面）。

### ③ 验证流程被噪声掐断

Godot headless 打印 `EditorSettings not instantiated yet …` 会让 PowerShell 的 `Stop` 策略中断验证脚本；
已降级为按退出码判定。

## 发行文件

| 文件 | 说明 |
|---|---|
| `patches/OrC-KSD-2.0.0-alpha.2-from-4DDCFE28C54E-to-72971A0DECD1.orcupdate` | 增量补丁（12.5 MB，**唯一对外补丁**） |
| `SHA256.txt` | 校验和 |

更新器沿用 **1.2.0**（[alpha.1 资产](https://github.com/Shangt1145/Godot-orcKsd/releases/tag/v2.0.0-alpha.1)）。用 1.1.0 会报「补丁的文件清单无效」。

## 构建

- 基线（alpha.1）：`4DDCFE28C54E0B3A6496BCF366B2536FECA9BED88E4D7298ACF72E3B008286D6`
- 本版（alpha.2）：`72971A0DECD12EEA5E39D9276A873DA24AA13EDA27E101D82B63B5A0D23A91D0`

## 2026-10-25 重出（同一版本、同一条补丁，覆盖前一条）

按「一版一补丁」，本版不涨版本号、不新增补丁链：起点仍是 alpha.1 完整包，
终点换成当前最新构建，旧补丁 `…-to-3B0301B6A703` 已下线。

这次补丁额外带上：

- **手机版启动即失败**：内置插件清单（`orc.builtin.core` / `orc.builtin.ui`）改为构建期嵌入、
  运行期物化到可写目录。Android 上 `AppContext.BaseDirectory` 是空串，原先拼出的相对路径
  `package/plugins/…` 恒不存在，游戏在启动阶段直接失败。
- **国家口径**：`av76` / `deran` / `USG` / `UN` / `NSF`（星盟）都是**独立国家**，
  不再被折成苏/德/美/英；`Faction.RegisterCustom` 注册为引擎自定义主国。
  同义写法（中文名 ↔ 引擎 id、大小写）仍归并。
  连带修好「主盟国一致时开不了局」与「星盟的卡进不了构筑」。
- 卡组构筑界面的国家显示、主盟国互斥、盟国 12 张上限统一走一个真源。

同批的 Windows 导出复核：`EXPORTED_PLAYER_VERIFY_OK`、`OFFLINE_UPGRADE_EXE_VERIFY_OK`、
`PATCH_EXACT_FILES_OK`、`PATCH_BUILD_OK changes=4`。


## 验证

- `dotnet build` 0 错误；UI 单测 **571/571**
- `--verify-ui` → `UI_VERIFY_OK screens=12`（看门狗在正常流程中未误触发）
- `PATCH_EXACT_FILES_OK`：应用后逐文件哈希与目标构建一致
- `OFFLINE_UPGRADE_EXE_VERIFY_OK`：用更新器 1.2.0 实测从 alpha.1 升级并复验升级后的 EXE

## 边界

- 指令空放的**根本拦截**需要引擎改：`orderTargets` 真实发布，或 `play.command` 在选靶完成前不落子。
- 攻击卡住的看门狗是**兜底**，不是修复服务端静默成功；若频繁触发，说明引擎侧有动作返回成功却不产生步骤。
- 补丁只覆盖 `2.0.0-alpha.1` → `2.0.0-alpha.2`。
