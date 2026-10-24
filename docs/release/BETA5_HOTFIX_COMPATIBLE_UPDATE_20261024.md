# beta.5 及更早 beta 的统一热修复补丁

目标仍为 `0.0.0beta5.hotfix`、构建 `A0D3C7A4491108B0907C40802FB556B541F70344FA533BCA6F54F390688F0D33`，游戏完整包没有重新导出。本次将先前仅接受 beta.5 的增量扩展为一份多来源兼容补丁。

## 来源范围

已保存的已发布 beta.1～beta.5 共七个不同来源构建：

| 版本 | 构建 |
|---|---|
| beta.1 初版（尚无构建清单） | `639C08B0B49D` |
| beta.1 首次更新器版 | `953CA6DF4CC7` |
| beta.1 工坊版 | `3649400D4B11` |
| beta.2 | `6AC795352BE0` |
| beta.3 | `EE2A3B2040C8` |
| beta.4 | `EF601290B053` |
| beta.5 | `39E673735CCD` |

同一补丁可直接升级这些来源到 hotfix，不需要逐版安装。使用已提供的 1.1.0 更新器即可，不必更换更新器。来源由程序文件大小及 SHA256 自动识别，旧本地清单或目录名不决定匹配。未收录的自行构建、修改过的程序文件、混装以及比 beta.5 更新的构建仍会拒绝，不能仅把版本文字改小后强行应用。

## 发行逻辑

- `tools/find-update-baselines.ps1` 自动扫描 `dist/releases/` 和外部基线档案 `H:/g/baselines`，按清单识别旧版、按构建去重，排除高于上限的 beta 和 hotfix；如存在 alpha 基线也会纳入。无清单的最初 beta.1 仅用已记录 ZIP SHA256 识别，不猜版本。
- 要求 beta.1 到指定上限的每个 beta 都有来源；缺失任何一版直接报错，避免生成少一个分支却声称全兼容的产物。
- `release.ps1` 对 `0.0.0betaN.hotfix` 默认收集 beta.1～beta.N；常规 `0.0.0-beta.N` 收集更早 beta。可用 `-CompatibleThroughBeta` 明确指定范围。不同来源各自生成差分、合并并按新增块内容去重，随后逐来源运行原有强制升级验证。
- `tools/build-compatible-update.ps1` 可直接复用已导出的目标生成兼容补丁，生成后逐来源运行原生更新器、文件比对及升级后 EXE，全部通过才复制到交付目录。该流程保持格式 2，与现有 1.1 更新器兼容，没有放宽安装器的来源校验。

复跑命令：

```powershell
tools/build-compatible-update.ps1 -TargetDir H:\g\kards\dist\windows -ThroughBeta 5
```

## 成品与验证

- 统一补丁：`dist/releases/0.0.0beta5.hotfix/patches/OrC-KSD-0.0.0beta5.hotfix-compatible-through-beta5-to-A0D3C7A44911.orcupdate`。
- 大小：20,274,582 字节（约 20.27 MB）。比原 2.94 MB 的 beta.5 单来源补丁更大，因为还包含旧版缺少的资源；仍按新增内容共享去重。
- SHA256：`4F81392B7D4841F59D7B37EA56FA1C87AA08438AF4922EA8F009636A49B19984`。
- 上表全部七个来源分别用同一份补丁通过真实 Godot 更新器、目标文件一致性比对、升级后独立 EXE 的 `EXPORT_VERIFY_OK`；每次都检查额外玩家文件哨兵未改变，结果 7/7。原用户卡组和工坊数据保持不变。
- 更新核心专项测试 30/30；来源发现检查确认 beta.1 为三构建、到 beta.4 为六构建、缺少 beta.6 时拒绝生成。
- 原始完整 hotfix ZIP、目标构建及游戏程序未改变。原来的单来源补丁移到当前版本 `notes/superseded/`，供历史核对，不再作为当前推荐补丁。

发行结果在当前版本 `notes/compatible-update-report.json`，逐来源证据在 `verification/compatible-update-verification.json` 和对应构建的 `*-updater.log`、`*-game.log`。完整生成日志 `artifacts/compatible-hotfix-20261024.log`，测试报告 `tests/TestResults/compatible-hotfix-update-20261024.trx`。没有上传远端。
