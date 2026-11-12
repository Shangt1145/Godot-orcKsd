# 2.0.0-beta.9（2026-11-13）

PC 修复发行。沿用整代版本 `2.0.0`，预发布序号从 `beta.8` 递增到 `beta.9`。

本版只有一个主题：**作战房间打不开**。beta.8 起，点「与好友一起作战」进房会在左侧状态栏甩出一句
`✗ 未知管理操作 'pool.transfer.policy'`，房间停在 83% 左右的进度上，谁也没法开局。

## 更新内容

### 作战房间不再被已废弃的管理操作卡住

房间初始化流程里，`RoomScreen.OpenAsync` 把 `pool.transfer.policy` 当成了**硬性前置**：

```csharp
var policy = await server.AdminAsync("pool.transfer.policy", ...);
if (!policy.Ok) throw new InvalidOperationException(...);
```

而 v4 引擎把整条卡包传输线删掉时，服务端就不再有这个管理操作了——于是这条调用**必然**返回
「未知管理操作」，`throw` 直接把整个进房流程中断。讽刺的是房间本身已经起来了：服务端在监听、
座位能占、快照能读，只是客户端在这句断言上自己把自己绊倒。

- 删掉这条前置调用。联机对局现在只认「双方各自装好的卡包」，这一步没有任何可做的事。
- `docs/design/CUSTOM_DESK_COSMETICS_20261105.md` 早在 2026-11-05 就记录过这个阻挡（当时绕开它去验外观通道），
  本版把它真正修掉，并在该文档补了后续记录。

### 「接收好友卡包」开关移除

同一个根因。那个勾选框调的是同一条已不存在的 RPC，点一下必然再报一次同样的错，而它承诺的
「载入好友提供的效果脚本」在 v4 里根本不可能发生。

- 开关与说明文字换成一行静态说明：**联机不传输卡包：请双方各自安装同一批卡包后再开局。**
- 下方的卡包状态从「等待卡包同步」（传输停用后这个状态永远不会到来）改为实际信息：
  「正在核对本机卡包…」→「卡包齐备」或「本机缺少卡包：xxx（请让对方单独发给你）」。

### 探针与测试清理（开发侧，玩家无感）

- `Main.AndroidParityVerification` 不再向服务端发这条请求。
- `NetworkWorkshopTests` 删掉同一条无意义调用与配套辅助方法；该用例本来就只断言「客人带房主没有的包时开不起来」。

## 已知问题

以下问题自 beta.8 起未变，本版**没有**处理：

- **Android：本版仍然不发 APK。** 保留上一版数据覆盖安装后进程会在 1 秒内退出
  （`pm clear` 清空数据后同一个包能正常启动，上一版在同样的旧数据上也能启动）。
  从 beta.3 覆盖安装是现有安卓玩家唯一的升级路径，在这条查清之前不发。Android 侧仍是 `2.0.0-beta.3`。
- **联机不传输卡包**：带 DIY 卡组开房时，客人若没装对应卡包会停在「本机缺少卡包」。
  这是 v4 引擎的能力缺失，不是本版的降级开关；恢复前提是引擎把 `pool.*` 加回来。
- **单位换线的画面表现**：有反馈说单位移动到前线后位置与动画不对，`--verify-move-motion` 仍复现不出来。
- **异画是本地展示设置**：不走房间外观同步，联机双方需各自安装同一个卡包才会看到同一张异画。
- 契约包的目录快照不带 `builtInOps`（引擎 issue #5）；`Check` 缺 `templateRoot`（issue #3）。

## 安装

- **Windows**：本版补丁是**单基线补丁**，来源为 **beta.8**（`F8D9F40D09D0`）。
  - 已在 **beta.8**：启动游戏会自动下载（断了会续），退出后生效；也可下载本页补丁，
    用游戏目录里的 `更新游戏.exe` 手动应用。
  - 还在 **beta.6 / beta.7**：先升到 beta.8（用 beta.8 的兼容合集补丁），再升本版。
  - 全新安装：需要完整包，按仓库规则不挂在本页。
- **Android**：本版不发，仍是 `2.0.0-beta.3`。

## 验证记录

- 构建标识 `BB5BB9A0D7E2E4CFD24D80210E92F7E77A754485115CB2AD3FC752D5079C0F5C`；
  全量包 **1,224,146,965 字节**（SHA256 `AA66983E1A607F6E154A0B8A0D9A2CCD31626C0A79EB4538E01E21FBABDEDB9C`）。
- 单基线补丁 **26,499,464 字节**（相对全量省 **97.84%**），来源 beta.8（`F8D9F40D09D0`）→ 本版（`BB5BB9A0D7E2`）；
  更新器 74,328,029 字节，首次更新下载合计 **100,827,493 字节**。
- 发版门禁：源码同步 → 编译 → 过滤集单元测试 → headless 原生 UI 验证
  （`UI_VERIFY_OK screens=13 cards=534 texture_cache=64/64`）→ 导出 → postflight 确认
  `Orc.Game.dll` 与运行时依赖齐全 → 解压到独立目录启动真实 EXE
  （`EXPORT_VERIFY_OK playable=243 pack-cards=219 rejected=0 all-admitted-pack-effects-mounted artwork=219`）→ 工坊验证
  （`WORKSHOP_VERIFY_OK`、`ALL_WORKSHOP_ROUTES_VERIFY_OK packs=17 cards=17`）→ 对 beta.8 基线跑真更新器
  （`GODOT_UPDATER_VERIFY_OK`、`OFFLINE_UPGRADE_EXE_VERIFY_OK`）→ 升级后的 EXE 再跑 `EXPORT_VERIFY_OK` →
  `MANIFEST_OK v2.0.0-beta.9 assets=3` → `RELEASE_OK`。
- 房间专项验证：`tools\run.ps1 -VerifyRoom` →
  `ROOM_VERIFY_OK server claim ai start local-art ui-mulligan battle play ai-turn-return`。
  修复前这条探针会停在 `pool.transfer.policy` 上，修复后从建房到 AI 回合完整跑通。
- 联机工坊测试：`dotnet test --filter NetworkWorkshopTests` 9/9 通过。
- 交付四件套：`full/`（完整包）、`patches/`（单基线补丁）、`notes/`（离线更新报告）、`verification/`（四份验证日志）。

