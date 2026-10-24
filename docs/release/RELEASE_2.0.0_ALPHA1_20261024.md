# 2.0.0-alpha.1（2026-10-24）

## 为什么是 2.0

手机版（Android）独立成代：本版起游戏可以打包到手机上运行，渲染管线、输入模型、字体回退都有了移动分支。
按 `VERSIONING.md` 的例外条款，整代重写允许用 `X.0.0-alpha.N` 宣告，故为 `2.0.0-alpha.1`。

## ⚠️ 必须先换更新器：1.2.0

旧更新器（`离线更新 1.1` / 文件名 `...updater-1.1.0...`）会报 **「补丁的文件清单无效」**。
原因：补丁清单从本版起包含 `OrcServer/` 与 `OrcRuntime/` 路径（这样引擎侧修复才能随增量送达），
而 1.1.0 的校验白名单只认 `KardsUi.exe` / `KardsUi.pck` / `data_Kards.Ui_windows_x86_64/`，遇到新路径直接判非法。

**操作**：下载 `OrC-KSD-offline-updater-1.2.0-win-x64.zip` → 解压 → 用里面的「更新游戏.exe」应用补丁。
界面标题会显示 `OrC-KSD 离线更新 1.2`，可据此确认版本。

## 本版变化

- **手机版可出包**：Android 导出预设（arm64）、GL Compatibility 移动渲染、ETC2/ASTC、项目图标；
  中文字体回退列表（Android 无微软雅黑）；触屏长按 0.45s 等价右键取消；桌面可用 `--mobile-sim` 以 20:9 窗口验证。
- **换牌入场**：服务端新增 `notify/match.started`，在推首帧视图之前广播；客户端入场改挂该信号，
  第一帧投影退为兜底（此前慢半拍会导致整段换牌被跳过）。换牌面板改为投影驱动对齐，换局清理残留。
- **卡池对账 P0**：客人上报真实卡包声明，界面读真实 `pool` 指纹，删除硬编码状态。
- **烟幕生命周期**（承接 hotfix.3）：进前线或主动攻击时移除烟幕；前线单位不能重新获得；取消不误清。
- **构建可移植**：`OrcEngineRoot` 三级解析（`-p:` → `ORC_ENGINE_ROOT` → 同级目录），缺失时报 `error ORC001`；
  外部贡献者不再需要改文件。

## 发行文件

| 文件 | 大小 |
|---|---|
| `full/OrC-KSD-2.0.0-alpha.1-win-x64.zip` | 1165 MB |
| `patches/OrC-KSD-2.0.0-alpha.1-from-E86AF958D1AF-to-4DDCFE28C54E.orcupdate` | **8.8 MB**（仅换 pck） |
| `updaters/1.2.0/OrC-KSD-offline-updater-1.2.0-win-x64.zip` | 71 MB |
| `SHA256.txt` | 相对发行目录的校验和 |

- 补丁来源构建：`E86AF958D1AF…`（`0.0.0beta7.hotfix.3`）
- 本版构建：`4DDCFE28C54E…`

## 验证

- `PATCH_EXACT_FILES_OK`：补丁应用后逐文件哈希与目标构建一致
- `OFFLINE_UPGRADE_EXE_VERIFY_OK`：用**新更新器 1.2.0** 实际把 hotfix.3 升到本版，并复验升级后的 EXE
- `EXPORT_VERIFY_OK` / `WORKSHOP_VERIFY_OK`：导出包与工坊回归
- UI 单测与原生 UI 门禁随发布流程执行

## 已知边界

- 本版补丁**只覆盖 `0.0.0beta7.hotfix.3` → `2.0.0-alpha.1`**；更老的版本请用完整包。
- 手机 APK 另行单独分发（约 1.07 GB，arm64），不在本发行资产内。
- 旧更新器读不了新补丁，这不是补丁损坏，是校验规则代差——见上。
