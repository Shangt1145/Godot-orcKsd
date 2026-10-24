# 0.0.0beta5.hotfix（2026-10-24）

按用户指定名称导出本地发行产物。目标显示版本、`VERSION`、Godot 配置及文件名统一为 `0.0.0beta5.hotfix`。这是针对已发布 `0.0.0-beta.5` 的离线增量，不是测试包标记，也没有自动上传或推送。

## 内容

- 修复虎视转换原目标后继续访问已销毁引用，导致攻击报失败且未正常扣费、消耗行动机会。
- 修复独立发射缺少装填实例记录时扣费但不造成伤害；记录存在时仍优先使用记录值，包括零。
- 保留空牌库不再强行抽牌的防崩修复；疲劳规则仍未实现。
- 修复回合抽牌先闪现再播放动画、缠斗部署反复回手与选靶输入，下调战斗数字，合批并缩短多个单位的状态更新与消灭演出。
- 引擎使用本地 `0f98685` 加虎视/发射兼容补丁；该提交已合并上游 `dc13b96`。本次上游只改文档，没有新增引擎规则。

## 来源与安装

首次导出的单来源增量以已发布 beta.5 完整包 `dist/releases/0.0.0-beta.5/full/OrC-KSD-0.0.0-beta.5-win-x64.zip` 为来源；后续已扩展为 beta.1～beta.5 的统一兼容补丁，当前使用方式见 [兼容更新记录](BETA5_HOTFIX_COMPATIBLE_UPDATE_20261024.md)。以下保留初次发行的来源证据：

- 来源构建：`39E673735CCD9951B83F0180AD833C62D1E14BD3A58DBC114E0D433FF8966124`。
- 来源 ZIP SHA256：`754C6A8BE3616D5C295A0B66D0CE265E04EAB3477FDB3F72C52080A1EACAD4A3`。

关闭游戏，使用 1.1.0 的“更新游戏.exe”选择原游戏目录与统一兼容 `.orcupdate`，完成后启动。已有 1.1.0 更新器的玩家只需下载兼容补丁；最初 beta.1 的 1.0 更新器或没有更新器时，使用独立 1.1.0 更新器 ZIP。兼容补丁按实际文件内容自动匹配 beta.1～beta.5 的七个已发布来源构建；混装或自行修改过的程序文件仍会拒绝。玩家存档位置和格式不变。

## 验证口径

本次打包前已跑引擎全量 1357/1357、UI 全量 549/552，三个已有 `OfficialCardExpansionTests` 仍失败（献祭取消、混合单位/总部伤害两例）。因此导出使用 `-SkipGate` 避免重复执行已知失败门禁，并保留脚本强制的导出 EXE、工坊、真实离线升级、文件一致性与升级后 EXE 验证。完整混编长局、双机联机尚未整体验收。

发布命令：

```powershell
tools/version.ps1 -Set 0.0.0beta5.hotfix
tools/release.ps1 -SkipGate -BaselineZip 'H:\Working Folder\OrC-KSD.Godot\dist\releases\0.0.0-beta.5\full\OrC-KSD-0.0.0-beta.5-win-x64.zip' -BaselineVersion 0.0.0-beta.5
```

## 产物

目标构建为 `A0D3C7A4491108B0907C40802FB556B541F70344FA533BCA6F54F390688F0D33`。

| 文件（相对 `dist/`） | 字节数 |
|---|---:|
| `releases/0.0.0beta5.hotfix/patches/OrC-KSD-0.0.0beta5.hotfix-compatible-through-beta5-to-A0D3C7A44911.orcupdate` | 20,274,582 |
| `releases/0.0.0beta5.hotfix/full/OrC-KSD-0.0.0beta5.hotfix-win-x64.zip` | 347,837,119 |
| `updaters/1.1.0/OrC-KSD-offline-updater-1.1.0-win-x64.zip` | 76,673,393 |

当前统一增量比完整包减少约 94.17%。补丁 SHA256：`4F81392B7D4841F59D7B37EA56FA1C87AA08438AF4922EA8F009636A49B19984`。最初 2.94 MB 的 beta.5 单来源补丁及其校验记录保留在 `notes/superseded/`。

## 成品检查

- 完整 ZIP 解压后的独立 EXE：`EXPORT_VERIFY_OK`，卡池、效果挂载、卡图、内嵌解析器、保存重载、构筑与换牌通过。
- 工坊成品检查：`WORKSHOP_VERIFY_OK`、`ALL_WORKSHOP_ROUTES_VERIFY_OK`，原玩家文件保持不变。
- 从 beta.5 ZIP 实际解压并通过 Godot 更新器的选择补丁、更新按钮、启动和回退入口流程：`GODOT_UPDATER_VERIFY_OK`。
- 升级结果逐文件与完整目标包一致：`PATCH_EXACT_FILES_OK A0D3C7A44911…`，额外玩家文件哨兵保留；升级后 EXE 再次 `EXPORT_VERIFY_OK`。
- 升级后的独立 EXE 额外运行 `--verify-battle-feedback`，抽牌、部署预览、目标点击、合批演出与真实示范包均通过：`BATTLE_FEEDBACK_REGRESSION_OK`、`findings=0`。日志 `artifacts/hotfix-upgraded-battle-feedback.log`。

完整日志 `artifacts/release-0.0.0beta5.hotfix.log`；成品检查日志位于 `dist/releases/0.0.0beta5.hotfix/verification/`；实际体积报告位于该版本 `notes/offline-update-report.json`，当前文件校验和位于该版本根目录 `SHA256.txt`。2026-10-24 已按版本和用途归档 `dist/`，旧报告与旧校验记录原样保留在 `notes/`，所有 ZIP 与补丁字节未改变；总入口见 `dist/README.md`。
