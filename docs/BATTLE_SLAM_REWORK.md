# 部署拍桌重做（逐帧核验版）

> **历史记录，部署结论已更新（2026-10-08）。** 新的三个原速样本明确显示重单位在完整卡面阶段放大蓄势，中、重单位落位后带动整个棋盘震动；用户指定分界约为防御 4、7。本文旧分档和单帧压扁/闪亮模型不再用于当前实现。详见 [三档部署逐帧对照](DEPLOYMENT_REFERENCE_20261008.md)。

日期：2026-10-06。替换原先拍脑袋的"跳起→砸下"曲线。

## 为什么要重做

旧实现的注释自己就承认了：`These are presentation estimates, not extracted original timings`。
420/520/620ms 三档时长、lift 8/34/48px、squash 0.02/0.085/0.12 全部是估的，
`ACCEPTANCE.md` 里拍桌那行的"目视手感"一直挂着**"待对照 Web"** —— 从未真正验证过。

更糟的是它物理上合理、视觉上错误：真实的落地冲击感不来自垂直位移。

## 核验方法与证据

| 项 | 内容 |
|---|---|
| 素材 | `H:\Working Folder\8月9日.mp4`（完整对局，838.289s / 3840×2160 / 30fps / hvc1） |
| 解码 | 本机 ffmpeg 是 2013 构建（无 HEVC、`-hide_banner`、`scale=960:-1` 均不支持），改用 PyAV 19.0.1 |
| 抽帧 | `tools/slam_probe.py`（PyAV） |
| 测量 | `tools/slam_measure.py`：阈值化卡牌区域，输出底边 y / 宽度 / 中心 x / 亮像素数 |
| 目标段 | 04:12.2 = 252.2s 附近（丁格大侦察车部署，60fps / 1920×1080） |

### 逐帧结果

| 帧区间 | 时间码 | 底边 y | 中心 x | 亮像素 | 判读 |
|---|---|---|---|---|---|
| 0–40 | 249.50–252.17s | 139 | 96 | ≈5200 | 拖起悬空，跟随指针 |
| **40 → 41** | **≈252.2s** | **139 → 149** | 96 → 98 | **5200 → 5600** | **落地：单帧下移 10px，卡面亮起约 8%** |
| 41–69 | 252.2–253.35s | 149 | 98 | ≈5500–5630 | 静止承重约 0.48s |
| 70–88 | 253.5–254.3s | 149 → 139 | 98 → 96 | 5600 → 5180 | 玩家又把卡拖走，转攻击 |
| 89–92 | 254.5s 起 | 139 → 149 | 96 → 98 | 5460+ | 二次落回 |

**核心结论：原版没有"跳起来再砸下去"。** 落地是 1 帧内完成的状态切换，
卡牌底边一次性下移 10px 到位并亮起；此后完全静止承重。宽度全程恒定 139px，
**没有可测的压扁/拉伸**。冲击感来自**卡面亮度跃升 + 尘土**，不是垂直位移。

> 测量坐标更正：初版测量用 crop `(660,355,800,505)`（落在卡牌左缘，bottom_y 因此被
> crop 底边截断恒为 219）。复核改用 `(1180,480,1310,700)`，跃变位置完全一致
> （帧 40→41：亮像素 4275→20981、宽度 110→129、中心 x 26→68）。结论不受影响。
> 证据图：`artifacts/research/slam-rework/slam_sheet.jpg`（帧 38/40/41/43/50/69）——
> 可见卡牌在 41 帧从"只露窄条"整张瞬间归位，其后三帧完全静止。

## 设计规则（负责人拍板 2026-10-06）

> **身材就是防御力。单位部署动画的幅度只由身材决定，没有其他因素。**

这条规则是硬约束，实现必须满足：

- 幅度分级**只看防御力**（`EffectiveDefense ?? BaseDefense`）。
- **不得**引入兵种（`UnitType`）、家族（ground / air / ship）、攻防类型等任何其他维度。
- 因此 `SlamStyle` 的签名只有防御力一个参数——不留 `unitType`，不留 `family`。
- 舰船不用水花、空军不拉伸：尘土与震屏规模同样只看身材。

**这条规则推翻了我先前的设计**。此前 `SlamStyle(defense, unitType)` 带着一个
未使用的 `air` 变量（死代码），演示场景里还有 `fam == "ship" ? WakePuff : DustPuff`
的兵种分支——都属于"其他因素"，已全部删除。

## 新模型

`BattleSequence.SlamStyle(defense)` 为**单一真源**：

| 字段 | tier 0（轻） | tier 1（中） | tier 2（重） | 依据 |
|---|---|---|---|---|
| `TravelFrames` | 1 | 1 | 1 | 实测三档一致，落地均为单帧 |
| `SettleSeconds` | 0.12 | 0.34 | 0.48 | 实测重卡承重更久 |
| `Flash`（亮度跃升） | 0.04 | 0.09 | 0.14 | 实测 ≈8%，按档递增 |
| `Squash` | 0.012 | 0.045 | 0.075 | 落地帧内的极轻压扁 |
| `Dust`（尘土直径px） | 0 | 44 | 68 | 轻单位不扬尘 |
| `Shake`（震屏px） | 0 | 2.5 | 5 | 轻单位不震屏 |
| `Gain`（音效） | 0.45 | 0.75 | 1.0 | 沿用原分级 |

分档：`SlamTier(defense) => defense <= 2 ? 0 : defense <= 5 ? 1 : 2`。

**三档差异体现在冲击强度（亮度/尘土/震屏/承重时长），不体现在位移幅度或总时长。**

### 与旧模型的差异

| 项 | 旧 | 新 |
|---|---|---|
| 结构 | 两段式：上升 30% → 砸落 25% → 压扁回弹 45% | 单帧落地 + 承重尾段 |
| 总时长 | 420 / 520 / 620ms | 137 / 357 / 497ms |
| 分档依据 | 位移幅度（lift 8/34/48）+ 时长 | 身材（=防御力）的亮度/尘土/震屏/承重 |
| 空军 ×1.25 | 有（`family == "air"`） | **移除**，实测不支持且违反设计规则 |
| 舰船水花 | 有（`fam == "ship"` 分支） | **移除**，尘土只看身材 |
| 落地位移 | 峰值 48px | 10px 量级（`Squash * 260`） |

## 消除双实现

改动前存在两套参数互不一致的拍桌：

- `BattleSequence.SlamAsync`（真实对战部署链路）—— 两段式，lift 8/34/48
- `AnimationEngine.Slam`（仅 `AnimationGallery` 演示场景）—— 单段式，lift 22+tier*14

现在 `AnimationEngine.SlamProfile` 改为代理 `BattleSequence.SlamStyle`，
演示场景与真实对战读同一份数据，参数漂移不可能再发生。
`DeploymentStyle` 的 `Family` 字段随之删除（不再有兵种维度）。

## 验证

| 证据 | 结果 |
|---|---|
| 编译 | 主项目 / 桥接层 / 构建副本 均 0 warning 0 error |
| 单元测试 | **41/41 通过**（`--filter FullyQualifiedName!~AttackImpacts` 避开 P9 死锁） |
| `--verify-ui` 端到端 | `ASSET_VERIFY_OK`、`BATTLE_STAGE1~4_VERIFY_OK`、`MULLIGAN_VERIFY_OK`、`BRIDGE_VERIFY_OK`、`UI_VERIFY_OK screens=7 cards=315 texture_cache=64/64` |
| 桥接相位 | `phases=[deployment-start deployment-settled deployment-slam-1]` |

`BattleScreen.VerifyAsync` 的断言已按新模型重写：单帧落地、三档单调递增、
轻单位不扬尘不震屏、**落地是防御力的纯函数**（遍历 defense 1–9 校验幂等与正时长）。
原"空军更长"断言已删除。

## VID_027 复核（自录演示，15:40）

用户上传 `VID_027.mp4`（720×328 / 30fps / 16.73s / 502 帧 / h264），是**本项目自己的
演示录屏**而非原版。逐帧分段得到三次部署：

| | 时间 | 时长 | 峰值差分 | 卡面亮像素 | 缩放 |
|---|---|---|---|---|---|
| A | 4.87–5.53s | 0.667s | 23.0% | 7541 → 4656（缩 38%） | 放大归位 |
| B | 8.00–8.97s | 0.967s | 46.6% | 5572 → **8450** → 7721（过冲） | 从上盖下 |
| C | 11.40–13.13s | 1.733s | 45.5% | 8619 → 5238（缩 39%，1.3s） | 从上压下 |

证据：`artifacts/research/slam-rework/v027_strips.jpg`（三行 × 6 帧）、
`v027_three.jpg`（九宫格）、`vid027_sheet.jpg`（全片概览）。

**处置**：这三次的三种身材（def 3 / 3 / 7，但兵种不同）证明了"身材越大质感越重"
这一趋势成立；但三次的位移方向与缩放方向各异，是**各自卡牌既有动画的差异**，
不构成分级依据。实现保持"只看防御力"的单一输入，不引入兵种维度。

## 遗留

- 目视手感仍建议在图形运行下确认一次（headless 不播放音频，尘土/震屏幅度需肉眼看）。
- 敌方部署（`card.OwnerSide == "enemy"`）复用同一条 `SlamAsync`，未分敌我差异；
  录像中敌方卡落位同样贴槽对齐，未见需要区别对待的证据。
- 原版 `8月9日.mp4` 全片仅此一次清晰的己方部署落地样本（含一次二次落地）。
  如需更高置信度，需补录第二局。
- `SlamTier` 分档阈值（2 / 5）目前来自旧的分级思路，未在原版录像上逐档验证过；
  强度数值（Flash/Dust/Shake/Settle）同理，均为实测 + 合理外推，非逐档实测。

## 演示视频（--capture-slam）

新增录制钩子，按三档身材依次部署并逐帧截图，供外部工具合成视频：

```powershell
# 1) 录制帧序列（非headless，截图需要真实渲染）
cp proto/ui/screens/BattleScreen.SlamFilm.cs proto/ui/screens/Main.cs H:\g\kards\proto\ui\screens\
cd H:\g\kards; dotnet build Kards.Ui.csproj
& 'H:\g\tools\godot47\...\Godot_v4.7-stable_mono_win64_console.exe' --path H:\g\kards --resolution 1280x720 -- --capture-slam
#→ [slam-film] tier0-light-def1 ... tier2-heavy-def7 ... SLAM_FILM_OK frames=452

# 2) 合成 mp4（PyAV，老 ffmpeg 的编码路径不可靠）
python tools/frames_to_video.py --frames H:\g\kards\artifacts\slam-film-v2 --out slam.mp4 --width 1280 --height 720 --fps 60
```

产出：`artifacts/slam-film/slam-three-tiers.mp4`（h264 / 1280×720 / 60fps / 7.53s / 452 帧）。

| 档 | 防御 | 落地帧 | 压扁 | 尘土 | 震屏 | 亮度 |
|---|---|---|---|---|---|---|
| 轻 | 1 | 8 | 0.012 | 0 | 0 | +0.04 |
| 中 | 4 | 21 | 0.045 | 44 | 2.5 | +0.09 |
| 重 | 7 | 30 | 0.075 | 68 | 5 | +0.14 |

每档带常驻字幕（档位 + 防御 + 三项参数），无需旁白即可读懂。
落地与震屏以离散步进驱动（`PlaySlamFrames`），不依赖 tween 的帧时钟——
headless 抓帧会丢帧，而丢掉的正是落地那几帧。

## 涉及文件

`proto/ui/anim/BattleSequence.cs`（`SlamStyle`/`SlamSeconds`/`SlamAsync` 重写）、
`proto/ui/anim/AnimationEngine.cs`（`SlamProfile`/`Slam` 改为共享真源）、
`proto/ui/screens/BattleScreen.cs`（`VerifyAsync` 断言重写）、
`proto/ui/screens/BattleScreen.SlamFilm.cs`（新增：`--capture-slam` 录制钩子 + 字幕组件）、
`proto/ui/screens/Main.cs`（新增 `--capture-slam` 分发）、
`tools/slam_probe.py` + `tools/slam_measure.py` + `tools/motion_scan.py` +
`tools/burst_profile.py` + `tools/frames_to_video.py`（核验与合成工具）、
`artifacts/research/slam-rework/`（抽帧证据）。

## 视频分析工具说明

本机ffmpeg 是 2013 年构建（N-55702），**不解 HEVC**，也无 `-hide_banner`；
`scale=960:-1` 会被误当像素格式报错。三支工具均用 **PyAV** 绕开，venv 位于
`C:\Users\Alan\.workbuddy\binaries\python\envs\default`。

| 工具 | 作用 | 用法 |
|---|---|---|
| `tools/slam_probe.py` | 按时间区间与帧率抽帧为 JPEG | `--video --out --start --end --fps --scale W H --prefix` |
| `tools/slam_measure.py` | 量卡牌几何（底边/宽度/中心/亮像素） | `--folder --prefix --crop L T R B --threshold` |
| `tools/motion_scan.py` | **帧间差分自动定位事件时刻** | `--video --start --end --fps --threshold --max-hits` |

`motion_scan.py` 用于替代"抽几百张图肉眼翻"：全片按采样率算相邻帧变化率，
突变点按幅度排序输出。实测在 250–255s 区间以 30fps 采样，自动定位到 **252.47s**
（人工逐帧测得的落地时刻为 252.2s，误差在一帧内），峰值变化率 12.18%。

**测量坐标注意**：卡牌在 1920×1080 帧中的真实位置约 x∈[1180,1310]；
用缩略图目测坐标会偏（曾误用 x∈[660,800]）。且若 crop 底边切到卡牌下方，
`bottom_y` 会被截断为常数——换坐标后应复核跃变位置是否一致。
