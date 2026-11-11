# 2.0.0-beta.8（2026-11-11）

PC 功能发行。沿用整代版本 `2.0.0`，预发布序号从 `beta.7` 递增到 `beta.8`。

本版是**引擎接入版**：把引擎 `origin/main` 的更新（`192c04b` → `6d45628`）合进 `v4`（合并提交 `c950b2a`），
核心结果只有一件——**工坊产出的 v4 组件效果，第一次真正进入联机对局的开局装配**。界面、卡池与规则没有变化。

## 更新内容

### v4 组件效果进联机开局装配

此前引擎的对局装配只认「卡→效果声明」与内联效果预制体，`effects-v4/*.component.json` 这类组件资产
在联机开局时不会装进对局编排运行时——卡面在、数值在，效果不跑。

本版接入引擎的新增量入口 `CardPackAssembler.AttachComponents`：

- 服务端开局走 `match.start` → `McpService.CreateMatchAsync`，在既有的声明／内联／旧目录手动段之后调用它，
  **只装 `effects-v4/` 的组件**，不重复登记另外两面；同一引擎上两个入口二选一，不会在装载门里记两条。
- 失败语义：**可选包**组件装载失败＝整包跳过并留痕（对局照常开）；**必需包**失败＝`Failed` 后不开局。
  没有 `effects-v4/` 目录的包记 `Loaded`（没有组件要装，不是错误）。
- 客户端**零代码改动**：本机单机／本地对局仍走 `OrcMatchSession` 的 `CardPackAssembler.Attach`
  （一次性注册全部面的装配方，内部与 `AttachComponents` 共用同一 `LoadComponents`，组件照常装载）；
  联机开局由服务端新入口负责。两者分属不同引擎实例。

### 创作与资产同步（开发侧，玩家无感）

- 引擎新增资产同步子系统 `Orc.AssetSync`（客户端／`SyncRunner`／CLI）与 `tests/Orc.AssetSync.Tests`，
  以及服务端同步端点；新增 `docs/MCP创作工作流/README.md`（外部 agent 经 MCP 造卡并「下一局生效」的完整序列）。
- 工具面新增 `pool.inspect`（每包装载状态与失败原因）、`editor.named_instantiate` / `editor.named_save` /
  `editor.template_list` / `editor.template_get`；`effect.write` 改为拒绝旧格式提交（给出三条替代路径，不写盘）。
- 服务端新增可选参数 `--effect-templates <dir>`，模板库根推导失败只写 stderr 告警、不阻断启动。
- 这些都在创作面，**不改玩家侧界面、卡池与规则**；`assets.pull` 前端自 v4 迁移起仍停用，
  联机对局只能用双方都装好的包。

## 已知问题

- **Android：本版仍然不发 APK。** 保留上一版数据覆盖安装后进程会在 1 秒内退出
  （`pm clear` 清空数据后同一个包能正常启动，上一版在同样的旧数据上也能启动）。
  从 beta.3 覆盖安装是现有安卓玩家唯一的升级路径，在这条查清之前不发。Android 侧仍是 `2.0.0-beta.3`。
- **引擎侧模板库没有入库。** `packs/effect-templates` 不在引擎仓库里（也不在 `.gitignore`，就是漏提交），
  所以 `template.*` 便捷通路在任何干净 checkout 上都回 `template-root-missing`；引擎自带的
  `AuthoringReloadFlowTests` 6 例中 2 例因此失败，工具链 `run_e2e.py` 也卡在模板实例化这一步。
  **权威通路（手写 v4 资产）不受影响**，玩家侧无感。
- **单位换线的画面表现**：有反馈说单位移动到前线后位置与动画不对。只看画面的探针（`--verify-move-motion`）
  仍复现不出来（439 → 267、抬升峰值 0.42、落定无偏差）。本版没有修它，只是把它变成可测的。
- **异画是本地展示设置**：不走房间外观同步，联机双方需各自安装同一个卡包才会看到同一张异画；
  任意宽高比的完整卡面会整张铺满卡牌矩形（拉伸显示），数值框仍按同一套百分比对齐。
- 契约包的目录快照不带 `builtInOps`（引擎 issue #5）；`Check` 缺 `templateRoot`（issue #3）。

## 安装

- **Windows**：本版补丁从 **beta.7 完整包**升级（单基线）。
  - 已在 **beta.7**：启动游戏会自动下载（断了会续），退出后生效；也可下载本页补丁，
    用游戏目录里的 `更新游戏.exe` 手动应用。
  - 还在 **beta.3～beta.6**：先用 beta.7 的兼容合集补丁升到 beta.7，再升本版。
  - 全新安装：需要完整包（约 1.2 GB），按仓库规则不挂在本页。
- **Android**：本版不发，仍是 `2.0.0-beta.3`。

## 验证记录

- 构建标识 `51399EAF80E94BE99F650AA0DC00AF0235D65F71CC2215ACC2981C5B80A256B2`；
  全量包 **1,224,143,521 字节**（SHA256 `47FAF75D17701CE65F6B0A0EE834D6EFB4B4C269A6EE3D6FF8B3DA2746C188A8`）。
- 从 beta.7（`54467400CA5E`）升到本版的单基线补丁 **8,939,782 字节**（改动 47 个文件，相对全量省 **99.27%**），
  更新器 74,328,029 字节，首次更新下载合计 **83,267,811 字节**。
- 发版门禁：源码同步 → 编译 → headless 原生 UI 验证（`UI_VERIFY_OK screens=13 cards=534 texture_cache=64/64`）
  → 导出 → postflight 确认 `Orc.Game.dll` 与运行时依赖齐全 → 解压到独立目录启动真实 EXE
  （`EXPORT_VERIFY_OK playable=243 pack-cards=219 rejected=0 artwork=219`）→ 工坊验证
  （`WORKSHOP_VERIFY_OK`、`ALL_WORKSHOP_ROUTES_VERIFY_OK packs=17`）→ 对 beta.7 基线跑真更新器
  （`GODOT_UPDATER_VERIFY_OK`）→ 升级后的 EXE 再跑 `EXPORT_VERIFY_OK` → `OFFLINE_UPGRADE_EXE_VERIFY_OK`
  → `MANIFEST_OK v2.0.0-beta.8 assets=3` → `RELEASE_OK`。
- 交付四件套：`full/`（完整包）、`patches/`（单基线补丁）、`notes/`（离线更新报告）、`verification/`（四份验证日志）。
- 客户端侧另有一次独立验证：对合并后引擎（`c950b2a`）构建 `Kards.Ui` 0 错误，
  `tools\run.ps1 -Verify` 通过 `UI_VERIFY_OK screens=13 cards=534 texture_cache=64/64`。

