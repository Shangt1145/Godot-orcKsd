# 0.0.0-beta.7（2026-10-24）

## 本版变化

- **换牌阶段不再整段丢失**。服务端新增 `notify/match.started` 推送（`Orc.Protocol.ProtocolMethods.NotifyMatchStarted`，
  在 `room.MarkInMatch()` 之后、首帧视图之前广播给 `Admitted/Viewer/Omniscient` 会话）；客户端入场改为挂这个信号，
  第一帧投影退为兜底。此前客户端只能靠"第一帧视图"判断对局开始，慢半拍时首帧已是 `play` 相位，换牌被跳过。
- **换牌面板改为投影驱动对齐**。每收到一次投影若"待换牌但面板没开"就补开；换 `MatchId` 时清理上局残留
  （`_mulliganOpen` 若带到新局会把新局面板挡掉）。手牌比相位晚到时不再静默丢弃，日志记
  `[mulligan] 相位=mulligan 但 SelfHand 为空`。
- **卡池对账打底（P0）**。客人 `session.attach` 现在上报本机真实卡包声明（`PackDeclarations.Scan`）；
  连接详情读 `pool.fingerprint` / `pool.packPolicy`（回退旧 `packFingerprint`），并删除硬编码的 `"loaded"`——
  服务端没报就显示 `—`，不再编一个状态出来。
- **更新器清单纳入 `OrcServer/` 与 `OrcRuntime/`**（`OrC.Update.Core/Model.cs` 的 `Scan`/`Validate` 白名单）。
  此前增量补丁**从不携带服务端**，引擎侧修复发不到玩家机器上；本版补丁首次包含 `OrcServer/Orc.Server.dll`。
- `release.ps1` 新增 `-SingleBaseline`（只出「指定基线 → 本版」单补丁）；修掉最后一步在 Windows PowerShell 5.1 下
  崩溃的 `[IO.Path]::GetRelativePath`（.NET Framework 无此 API，导出白跑 9 分钟）。

## 发行文件

| 项 | 值 |
|---|---|
| 完整包 | `full/OrC-KSD-0.0.0-beta.7-win-x64.zip`，1,164.6 MB |
| 增量补丁 | `patches/OrC-KSD-0.0.0-beta.7-from-D504B2B1C5C8-to-8597230D4C78.orcupdate`，**11.4 MB**（相对完整包减少 99.02%） |
| 离线更新器 | `dist/updaters/1.1.0/OrC-KSD-offline-updater-1.1.0-win-x64.zip`，70.9 MB，各版本共用 |
| 目标构建 | `8597230D4C78CD23E0E0E4D09BE5B4351DF211FBDF2330F40EF11E7FAF9FD996` |
| 校验和 | `releases/0.0.0-beta.7/SHA256.txt` |

补丁只改动 8 个文件：`KardsUi.pck`、`Kards.Ui.dll`、`Kards.Ui.OrcBridge.dll`、`Orc.Protocol.dll`（客户端侧），
以及 `OrcServer/Orc.Server.dll`、`OrcServer/Orc.Protocol.dll` 与两份 pdb（**引擎侧**）。`OrcRuntime/` 无变化，因此不占补丁体积。

## 验证

- `EXPORT_VERIFY_OK`（导出包）：`playable=230 pack-cards=206 rejected=13 artwork=206 …`
- `GODOT_UPDATER_VERIFY_OK exact-files`：用离线更新器把 beta.6 实际升级到本版，逐文件比对一致
- `verify-upgraded-game.log`：**升级后**的 EXE 再次跑 `EXPORT_VERIFY_OK`
- `WORKSHOP_VERIFY_OK`
- UI 单测 565/565；引擎服务端 119/119（新增 `MatchStartedNoticeTests`：两端都收到 `match.started` 且早于首帧视图）
- `--verify-room` / `--verify-room-reconnect` / `--verify-opening` 全通过；重连探针日志可见
  `[room] guest enter battle via match.started`

## 已知边界

- **补丁只覆盖 beta.6 → beta.7**。beta.1～beta.5 的卡包内容差异极大，单来源分片接近 943 MB，
  合集比完整包还大，不适合作为热更新发布；这些版本请用完整包。
- 从 beta.1 用旧补丁升上来、目录里**没有 `OrcServer/`** 的安装，本版补丁会被源校验拦下并报"文件缺失"。
  这是预期行为（此前会静默升成一个不能建房的包），修复方式是完整包重装一次。
- 换牌修复的真机双端时序（房主先换完牌）仍建议人工复验一次；探针只覆盖了单进程房间与重连。
- 原版卡池中 13 张无有效效果的卡仍被准入过滤，不开放为已验证可玩卡。
