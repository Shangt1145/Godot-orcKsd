# 手机版调研（Android 优先，2026-10-24）

> 结论先行：**能上手机，但不是"换个导出预设"能解决的**。有 5 个真阻断项，其中 1 个是架构级的
> （房主拉子进程），其余 4 个是配置/资产/输入层。iOS 另算——本机是 Windows，编不了。

---

## 0. 可行性分级

| 形态 | 可行性 | 说明 |
|---|---|---|
| 手机当**客人**（连房主的房间） | 🟢 较高 | 只有网络 + 渲染 + 输入三块 |
| 手机**单机**打 AI | 🟡 中等 | 同上，但要进程内起服务端 |
| 手机当**房主**（开房给别人连） | 🟡 中等偏难 | 架构级改造：禁止子进程 + 前台保活 + NAT |
| 手机**工坊/DIY 编辑器** | 🔴 不可行（现有实现） | 依赖 ClearScript V8 native，只有 win-x64 |
| iOS | ⚪ 未评估 | 需要 macOS + Xcode，当前工具链不具备 |

---

## 1. 阻断项（不改就跑不起来）

| # | 项 | 现状证据 | 手机上的后果 |
|---|---|---|---|
| 1 | **渲染器是 Forward+** | `project.godot:16` `config/features=("4.7","Forward Plus")` | Android 必须用 Mobile（Vulkan）或 Compatibility（GLES3）。Forward+ 在移动端不可用 |
| 2 | **房主用子进程拉起服务端** | `ServerLauncher.cs:30` → `ServerManager.SpawnAsync` → `ManagedInstance.cs:87` `ProcessStartInfo`；运行时靠包内 `OrcRuntime/dotnet.exe` | Android/iOS 不允许这样拉托管子进程。**必须改成进程内托管**（`ServerBootstrap.StartAsync` 本来就是库入口，见 `Orc.Server/ServerBootstrap.cs:28`） |
| 3 | **ClearScript 只有 win-x64** | `Kards.Ui.OrcBridge.csproj:57` `Microsoft.ClearScript.V8.Native.win-x64` | 任何走 V8 的路径在 ARM 上直接崩。好消息：只有 `DesktopOrc.cs` 用它（桌面创作用），引擎与服务端不依赖 V8 |
| 4 | **中文字体靠系统字体** | `UiStyles.cs:65` `SystemFont{FontNames=["Microsoft YaHei","Noto Sans CJK SC"]}` | Android 没有雅黑；系统字体名匹配不上就是满屏方块。必须内嵌字体（`artifacts/original-install-assets/fonts/DroidSansFallback.ttf` 已有素材） |
| 5 | **工程只有 Windows 导出预设** | `export_presets.cfg` 只有 `platform="Windows Desktop"`；`Kards.Ui.csproj` 是 **net9.0** | 需要 Android 预设 + .NET Android 工作负载 + Android SDK/JDK + mono 模板。另外 net9.0 与 Godot Android 运行时是否匹配要先验证，不匹配就得降到 net8.0 |

---

## 2. 体验项（能跑但难用）

| # | 项 | 现状 | 建议 |
|---|---|---|---|
| 6 | **画布 1600×900，棋盘绝对坐标** | `project.godot:20` 视口 1600×900；`BattleScreen.cs:10` `BoardSize=1280×720`；手牌 x 用 `640 + relative*spacing`（`BattleScreen.cs:312`） | 20:9 手机默认 letterbox，两侧黑边约 25%；15px 字号缩到手机上约等于 4pt，**基本读不了**。需要确定缩放策略（见 §3） |
| 7 | **输入是鼠标模型** | `BattleScreen.cs` 20 处 `InputEventMouseButton`；右键取消/`Esc` 共 18 处；悬停预览 26 处；全项目只有 1 处触屏事件（`CardControl.cs:202`） | 触屏没有右键、没有悬停、没有 Esc。需要：长按=取消、点两下=查看、拖拽改成分段操作 |
| 8 | **手牌 144×202 / 卡面 198×277** | `BattleScreen.cs:312`、`Stage5.cs:70` | 手指触点在 7mm 以下基本点不准。手机上必须放大手牌、或改成"选中放大再操作" |
| 9 | **开局解包 2 MB 到 user://** | `RuntimeGameData.cs:13-22`，`proto/data` 仅 2 MB | 这条其实没问题，成本可忽略（比预想好） |
| 10 | **纹理缓存上限 64** | `TextureCache.cs:12` | 移动端显存够，64 张卡面可控；但要实测峰值 |
| 11 | **包体 1.17 GB** | beta.7 完整包 1164.6 MB（`KardsUi.pck` 约 1.17 GB） | 侧载 APK 可行；上架 Google Play 要 Play Asset Delivery 拆包（单 APK 上限 200 MB） |
| 12 | **音频 53 个 mp3** | `proto/` 下 53 个 `.mp3` | 可播，但移动端更推荐 ogg（体积/解码开销）；非阻断 |
| 13 | **离线更新器是 Windows 程序** | `dist/updaters/1.1.0` 是 Godot 导出的 EXE + `.orcupdate` 增量体系 | 手机上这套完全作废。更新只能走应用商店或"重新装 APK"。**beta.7 的 11.4 MB 增量补丁在手机上一点用都没有** |
| 14 | **UDP 广播发现** | `RoomDiscovery.cs:55,80` `EnableBroadcast` + `255.255.255.255` | Android 发送广播没问题，**接收**常被系统过滤（需要 MulticastLock，Godot 没暴露）。要有"手填 IP"兜底（好在已经有了） |

---

## 3. 必须做的一个产品决策：布局策略

棋盘是 1280×720 的固定坐标世界，手机屏幕是 20:9 且物理尺寸小。三条路：

| 方案 | 做法 | 代价 | 适合 |
|---|---|---|---|
| **A. 等比放大 + 横屏锁定** | 保持 16:9 画布，整体放大到屏幕高，两侧留黑边 | 最小改动；但卡面文字仍偏小，且黑边难看 | 先跑通（1 周内） |
| **B. 横屏 + 局部重排**（推荐） | 棋盘保持等比缩放，**手牌/卡面单独放大 1.4~1.6×**，日志/侧栏收起为抽屉 | 中等：要动 `BattleScreen` 的手牌布局与命中区 | 真正能玩 |
| **C. 竖屏重设计** | 上下分栏（对手区/我方区），手牌横向滚动 | 大：棋盘、演出、动画全部要重排 | 长期，且不建议（卡牌对战横屏更合适） |

**建议先 A 后 B**，不要一上来做 C。

---

## 4. 改动清单（按优先级）

### P0 · 能起来（Android 单机能进对局）
1. `project.godot`：`rendering/renderer/rendering_method="mobile"`（或 `gl_compatibility`），加 `display/window/handheld/orientation="landscape"`、`stretch/aspect="keep_height"`。
2. 新增 Android 导出预设；装 .NET Android workload + Android SDK/JDK + Godot mono Android 模板。
3. **统一 TargetFramework**：确认 Godot 4.7 Android 运行时对应的 net 版本，`Kards.Ui.csproj` 从 `net9.0` 对齐过去（当前子工程是 net8.0，两套并存本身就是风险）。
4. 内嵌中文字体（`DroidSansFallback.ttf` 或 Noto Sans SC），`UiStyles` 改 `FontFile`，去掉对系统字体的假设。
5. **输入层加触摸适配**：给 `BattleScreen` 加一层输入归一化（触摸→按下/移动/抬起），长按=右键取消，双击=查看卡面。

### P1 · 能玩（单机 + 当客人）
6. 手牌/卡面尺寸按屏幕 DPI 缩放（方案 B）。
7. 关闭悬停语义：触摸环境用"按下即预览"。
8. 图形冒烟：换渲染器后逐个复跑 `--verify-opening`/`--verify-battle-feedback` 等 19 个自绘 `_Draw` 组件（项目铁律：碰 Draw/polygon/shader 必图形冒烟）。
9. 性能基线：低端机 30fps 验收（卡面纹理、粒子、余效）。

### P2 · 能联机（手机当房主）
10. **进程内托管服务端**：新增 `InProcessServerLauncher`，走 `ServerBootstrap.StartAsync`，不再 `SpawnAsync`；`RoomScreen` 按平台选择实现。这是本次调研里唯一真正的架构改动。
11. Android 前台服务保活（Godot 需 Java 插件或 `OS.request_permission` + keep-screen-on）。
12. 发现降级：Android 上"广播发现"标记为实验性，默认引导手填 IP / 扫码。
13. 明确网络口径：**同 Wi-Fi 才行**（4G 下手机没有可被外部连入的地址）。

### P3 · 不做
- 手机上做 DIY 工坊编辑（V8 只有 win-x64）。可以只读浏览，不能编译。
- 手机端离线增量更新（现有更新器是 Windows 程序）。
- iOS（工具链不具备；且 App Store 对"应用内开房托管"另有审核口径）。

---

## 5. 工作量粗估

| 阶段 | 内容 | 估时 |
|---|---|---|
| P0 | 导出链 + 渲染器 + 字体 + 输入归一化 | **1~1.5 周** |
| P1 | 布局方案 B + 图形冒烟 + 性能 | **1.5~2 周** |
| P2 | 进程内服务端 + 保活 + 发现降级 | **1~1.5 周** |
| 合计（Android，单机+客人+房主） | | **约 4~5 周** |

如果只做"手机当客人，房主永远是 PC"，可以砍掉 P2 的大半——**这是性价比最高的第一期**，约 2.5~3 周。

---

## 6. 验收清单（可执行）

- [ ] Android 真机（中端机，如骁龙 7 系）启动进主菜单，中文不乱码、不方块
- [ ] 单机开局 → 换牌 → 出牌 → 移动 → 攻击 → 结算面板，全程触屏可完成
- [ ] 长按能取消选靶，无右键也能退出所有弹层
- [ ] 手牌/卡面文字在 6.5" 屏上可读（截图人工确认，不靠代码断言）
- [ ] 换渲染器后 19 个 `_Draw` 组件 + 4 个图形探针全绿
- [ ] 手机当客人连 PC 房主：同 Wi-Fi 可开局、断线可重连
- [ ] （P2）手机当房主：PC 能连进来并打完一局；锁屏 5 分钟服务端不死
- [ ] 包体与安装：侧载 APK 体积、首次启动耗时、峰值内存记录在案

---

## 7. 一句话

**P0+P1 是"移植"，P2 是"改造"。** 真正的深水区不是触屏也不是渲染器，而是"房主即服务器"这件事在移动 OS 上没有子进程可用——
`ServerBootstrap` 已经是可以进程内调的库入口，所以不是重写，是把 `SpawnAsync` 换成 in-process 托管。
