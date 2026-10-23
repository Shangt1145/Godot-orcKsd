# 交接：0.0.0-beta.5（2026-10-23）

接手先看这一页。它只写"当前是什么状态、怎么跑、别踩什么坑"，历史推演看
[ENGINE_7F3AE35_IMPACT_20261022.md](../engine/ENGINE_7F3AE35_IMPACT_20261022.md) 与
[ENGINE_64D8DB2_RECOVERY_20261023.md](../acceptance/ENGINE_64D8DB2_RECOVERY_20261023.md)。

## 1. 现状（可以信的数字）

| 项 | 状态 |
|---|---|
| 版本 | `0.0.0-beta.5`（`VERSION` 单一真源） |
| UI 仓库 | `Shangt1145/Godot-orcKsd`，HEAD `6827833`，已推送 |
| 引擎仓库 | `hitman-OLD6/OrC-KSD`，远端 `64d8db2`；**本地多一个未推送提交 `e80198b`**（卡组抽空防崩） |
| 引擎自测 | ✅ 1354/1354（`tests/Orc.Game.Tests`） |
| UI 单测 | 🟡 530/533（3 项同一根因，见 §4） |
| 单机全链 | ✅ `--verify-ui` |
| 联机链 | ✅ `--verify-room` / `--verify-combat` / `--verify-room-discovery` / `--verify-room-reconnect` |
| 观战+回放 | ✅ `--verify-viewer-replay`（上次绿；引擎换代后未重跑，见 §4） |
| 发行包 | `dist/OrC-KSD-0.0.0-beta.5-win-x64.zip`（324 MB，SHA256 `754C6A8B…`）+ 离线更新器包 |

一句话：**能打完一局、能开房联机、能观战能回放；剩一个引擎 bug 与几处功能缺口。**

## 2. 怎么跑

```powershell
# 源码目录（只改这里）
cd "H:\Working Folder\OrC-KSD.Godot"
dotnet build Kards.Ui.sln

# 一键：同步到 H:\g\kards + 构建 + 指定探针
tools\run.ps1 -Verify            # 全量单机链
tools\run.ps1 -VerifyRoom        # 房间（含 local-art：AI 座位卡图）
tools\run.ps1 -VerifyCombat      # 联机打总部
tools\run.ps1 -VerifyRoomDiscovery
tools\run.ps1 -VerifyRoomReconnect
tools\run.ps1 -VerifyViewerReplay
```

- 运行副本 `H:\g\kards` **不要手动改**，`run.ps1/release.ps1` 会自动同步。
- 引擎源码在 `H:\Working Folder\OrC-KSD-github`；UI 通过 `OrcEngineRoot` 项目引用它。
- 单测：`dotnet test tests\Kards.Ui.Tests.csproj`（先 `taskkill /F /IM testhost.exe`，偶发锁文件）。

## 3. 怎么发版

```powershell
tools\version.ps1 -Set 0.0.0-beta.6   # 预发布必须 0.0.0-beta.N
tools\release.ps1                     # 默认跑 gate（单测子集 + --verify-ui）
tools\release.ps1 -SkipGate           # 跳过 gate（本次 beta.5 用了这个，原因见 §4）
```

产物进 `dist/`：主包 + `SHA256.txt` + 离线更新器；给了 `-BaselineZip` 才会额外产出 `.orcupdate`。

## 4. 已知问题（按优先级）

1. **指令效果弹两次选靶**（唯一在红的 bug，引擎侧）
   一张指令打出时会连续弹两次 `fieldUnit` 选靶，导致
   `OfficialCardExpansionTests` 3 项红（`TargetedSacrifice…`、`MixedUnitAndHqDamage…` ×2）。
   已排除：卡上效果只挂 1 个、`card.played` 只发 1 次、`OrderFlow` Resolve 的 orders=0。
   剩余嫌疑：**取消后引擎会重新发起一次选择请求**，被记进下一次提交（selector id 不同）。
   复现：`dotnet test tests\Kards.Ui.Tests.csproj --filter FullyQualifiedName~TargetedSacrificeCancelsBeforePayment`
2. **匹配（自动配对/密码房）没做**：现在只有「好友对战 / 局域网」手动房 + 列表一键加入 + 观战。
3. **13 张空壳指令卡被过滤**：有卡面文本无效果组件，`OrcEngineHost.RejectedPackCards` 可见；导出日志有名单。
4. **疲劳规则未实现**：卡组抽空时回合开始不再崩（`e80198b`），但也没有掉血/判负。
5. **未覆盖验证**：`--verify-setup-selection` 在无头环境挂死（需图形环境）；
   `--verify-room-segments` / `--verify-room-ping` 引擎换代后未重跑；双机组网（ZeroTier/n2n）从未真机验证。

## 5. 必须知道的坑（都是踩过的）

- **棋盘模型**：`Slot` 已退场，位置句柄是 `FormationGap`（**临时快照**，占用一变整批重建）。
  出牌/移动要拿引擎候选里的**同一个实例**，自己 `GetInsertionGaps()` 重建会拿到失效对象 →
  `ExecutionRejected`。
- **规则**：前线有敌方存活单位 ⇒ 禁止推进；支援线只能打敌前线，前线才能打敌支援线/总部；
  部署要选空隙（`PlayCard.GapId`，缺省走自动应答）。
- **部署的意图生命周期**：`SubmitAsync` 的 finally 会 `ClearIntent()`，所以"事后被问再答"的路径拿不到落点——
  带 `GapId` 时直接在 `PlayAsync` 里 `PlayUnitAsync(unit, gap)`。
- **战斗余效**是渐隐自删的：`PresentCombatAsync` 结束前等它们淡完（≤1s），否则 `LiveEffectCount==0` 会误报。
- **`project.godot` 编码**：只能用 UTF-8 **无 BOM** 读写。PS5.1 的 `Get-Content` 默认 ANSI，
  会把中文项目名读坏再写回 → Godot 导出时 signal 11 崩（`tools/version.ps1` 已修）。
  反过来，给 `.ps1` 打补丁必须保留 **UTF-8 with BOM**，否则 PS 解析报错。
- **git 推送 502**：先 `git config http.version HTTP/1.1`（代理掐 HTTP/2），两个仓库都设过。
- **实机验证**：`--verify-ui` 不算实机；实机通道是 `-- --capture-real`（真实鼠标＋帧截图）。

## 6. 下一步建议顺序

1. 修引擎"取消后重试"导致的重复选靶 → 3 项红自动转绿，release gate 可以恢复默认。
2. 把本地引擎提交 `e80198b` 推上去（或并进上游）。
3. 补 13 张空壳卡的效果快照（`docs/acceptance` 里有名单）。
4. 疲劳规则（卡组抽空＝掉血/判负），需要引擎侧设计口径。
5. 匹配与密码房（M4 剩余），属新功能。
