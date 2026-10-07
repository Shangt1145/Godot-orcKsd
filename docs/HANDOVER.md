# 交接文档 · 2026-10-07 18:20

> **6B2 增量更新**：版本 `0.0.0-alpha.13`；默认真实对局随机组牌与发牌，恢复 30 张牌组。支持集扩展为 21 种（9 单位、11 指令、1 反制），指令接入真实目标/抉择与效果，反制支持预留、退费及友方死亡条件触发；另外三种复杂反制仍拒绝准入。验收见 [STAGE_6B2_ACCEPTANCE.md](STAGE_6B2_ACCEPTANCE.md)。下方保留此前阶段记录。

> **6B1 增量更新**：版本 `0.0.0-alpha.12`，128 项回归；真实卡池准入改为经过源声明审核的 9 种单位，使用明确标注的 24 张验证牌组。结构覆盖 150/315 不再当作效果支持。源变更和数值缺失拒绝准入，生成卡不组牌，容量不足不返回短牌组。验收及下一子阶段见 [STAGE_6B1_ACCEPTANCE.md](STAGE_6B1_ACCEPTANCE.md)。下方 6A 与历史审计的数字按各轮时间保留。

> **6A 增量更新**：当前回归为 112/112；默认和真实对战操作可连续提交，不等待动画，画面仍按 FIFO 完整播放；部署重复画面已修复。运行副本 `H:/g/kards` 已同步并验证，已有进程需重启加载新程序集。忠实拒绝、生命周期清理及 UI 引擎边界护栏已实现，两种尺寸的前线落点矩阵通过。详细结果及缺口见 [STAGE_6A_ACCEPTANCE.md](STAGE_6A_ACCEPTANCE.md)，引擎基线与更新约定见 [ENGINE_INTEGRATION_BASELINE.md](ENGINE_INTEGRATION_BASELINE.md)。以下保留前轮审计，旧数字和问题状态以增量验收为准。

> 本文下方记录前轮**实测状态**。数字为当时亲自跑出的结果。
> 上一版（2026-10-06）的"待办"里三项早已完成却仍在催工 —— 那种文档比没有更糟，
> 所以本文只保留可复现的结论，未验证的推测明确标为"未验证"。

**项目**：`H:\Working Folder\OrC-KSD.Godot` —— Godot 4.7 .NET (C#) 的 KARDS 风格对战 UI。
**版本**：`0.0.0-alpha.11`。**引擎**：`Shangt1145/OrC-KSD`，本地副本 `H:\Working Folder\OrC-KSD-github` @ `73f76ab`。
**铁律**：UI 只做呈现与输入 —— 规则由引擎决定，UI 不算规则、不猜状态、不泄露隐藏信息。

---

## 1. 五分钟上手

```powershell
# ① 用 .NET(mono) 版 Godot —— 普通版会报 No loader found for resource: Main.cs
powershell -File tools\run.ps1 -Editor      # = 同步源码 + 构建 + 导入 + 开编辑器
# 或双击 tools\open-editor.bat（同一个入口）

# ② 只跑单测（不需要 Godot）
dotnet test tests\Kards.Ui.Tests.csproj --nologo

# ③ headless 全套验证（8 项）
tools\prepare.ps1 -BuildRoot H:\g\kards -SkipAssets
cd H:\g\kards; dotnet build Kards.Ui.csproj --nologo
H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe `
  --headless --path H:\g\kards -- --verify-ui

# ④ 实机审计（真实鼠标事件驱动真实对局，非 headless）
H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe `
  --path H:\g\kards --resolution 1280x720 -- --capture-real
# 产物：H:\g\kards\artifacts\real-audit\audit.txt + 每步截图
```

🔴 **`--verify-ui` 不等于实机测试**。它的 `VerifyBridgeAsync` 直接调 `_runner.SubmitAsync()`，
**绕过** `TryDrop` / `DropSlot` / 桥接应答链 —— 玩家实际碰的那一层完全没被覆盖。
本轮所有落点/攻击问题都是它测不出来、只有 `--capture-real` 能测出来的。

### 引擎拓扑

| 路径 | 状态 |
|---|---|
| `H:\Working Folder\OrC-KSD-github` | ★ **唯一活跃引擎副本**，**独立仓库**（不是 worktree），`origin`=Shangt1145/OrC-KSD |
| `H:\Working Folder\OrC-KSD` | 主仓库，**有他人 330 项未提交改动，未动**。备份在 `artifacts/engine-main-backup-20261007/` |
| `OrC-KSD-github.removed-20261007` / `OrC-KSD-research.removed-20261007` | 旧副本，改名隔离（内容重复，可删） |
| `H:\Working Folder\Kards-Desktop\OrC-KSD` | KardsKMP 项目的 gitignore 陈旧转储，**别碰** |

UI 的 `OrcEngineRoot` 路径**一个字没改**，换引擎零配置（换完实测 99/99 + 8/8 全绿）。

---

## 2. 当前基线（2026-10-07 18:15 实测）

| 项 | 结果 |
|---|---|
| UI 单测 | **99/99 通过** |
| `dotnet build` | 0 warning / 0 error |
| `--verify-ui` | **8/8 全绿**（ASSET / STAGE1-4 / MULLIGAN / BRIDGE / UI_VERIFY_OK screens=7 cards=315） |
| 实机 `--capture-real` | 跑完 10/10 步、不再崩溃；`PAINT checked=8 broken=0` |
| 引擎测试 | **765/803**，38 条失败（§4.2） |
| 引擎 `PlayManager.cs` | ⚠️ **有未提交改动**（§4.1） |

---

## 3. 本轮改了什么（都有实机证据）

### 3.1 抽牌动画播两次 —— 已修

引擎 `PlayerManager.DrawCard` 一次抽牌发**两条**信号（`EmitCardDrawn` + `EmitCardHandAdd`），
翻译器两条都当抽牌处理 → 一张牌生成两个 `UiDrawPresentation`。

**修**：段内按 `OrcRefs.KeyOf(card)` 去重。
**量化**：`total=8 distinct=4 duplicated=4` → `total=4 distinct=4 duplicated=0`。

同族第二只：回合横幅也重复（`turn.start` + `turn.start.after`），`per-segment=[2,2,2,2]` → `[1,1,1,1]`。

### 3.2 攻击打不中单位、总部替罪 —— 已修（引擎侧不改）

`CommandManager.RunSelectorAsync` 用 `coarseFilter: refs => refs.Where(allowedSet.Contains)`
把**桥接提交的引用 ∩ 引擎候选**。而桥接 `DefaultInteractables` 原本只提交「槽位 Ref + HQ Ref」，
**漏了场上单位** → 交集只剩敌方 HQ。官方示例 `samples/Orc.Game.Sample/DemoTargeterBridge.cs:44-46`
是收集单位的。

| | 目标 | 结果 |
|---|---|---|
| 修前 | 步兵 hp=5，hqHp=20 | `targetHpAfter=5` `hqHpAfter=18` ← 单位没掉血、总部掉 2 |
| 修后 | 同上 | `targetHpAfter=3` `hqHpAfter=20` ← 单位掉 2、总部不动 |

### 3.3 落点"只能落右边" —— 已修（三次迭代才对）

**语义纠正**：落点是**画面位置**，不是"新单位排第几"。
现在按画面牌距（1 槽 = `SlotPitch` = `FieldCardSize.X + 17` = 129px）把落点外推成目标槽位；
空行以棋盘中心 640 为锚。目标槽被占时取就近空槽并**优先落点来的一侧**。

**关键教训**：曾经把"落点左边有几张牌"（名次）当成了"引擎候选数组的下标"——
**名次数的是卡片，答案必须是槽位**，低位槽被占后两者错位，所有落点被推向右侧；
且空行时名次恒为 0 → 第一个单位无论丢哪都进槽 0。两条都实机复现过。

### 3.4 行布局铁律（用户当面纠正两次才定下来）

> **一行卡片紧凑相邻、整行以棋盘中心 x=640 居中、空槽位不留空隙。**

试过两版都被否：① 定长槽位网格 → 内容整体偏左、HQ 不居中（"极左"）；
② 行内按已占槽位居中 → **行中出现 253px 空隙**（"阴间玩意"、"我图纸里没有这个东西"）。

- 绘制：间距恒 129，整行居中。
- 🔴 **渲染顺序必须等于槽位顺序** —— `AddRow` 里 HQ 要**按槽位插入**
  （`FindIndex(c => c.SlotIndex > hq.SlotIndex)`）。曾把 HQ 的槽位索引当行内插入名次用
  （`Insert(Clamp(hq.SlotIndex, 0, row.Count))`），导致"丢右边、牌画到左边"。
- 🔴 **命中判定与绘制锚点必须分开**：`SelfArea`/`FrontArea` 当绘制锚点；
  `SelfBand`/`FrontBand`（同高、x 跨整块棋盘）当判定带。否则边缘槽位（名义 x 可到 1156）
  点不到、被判"取消"。STAGE1 的 `invalid-drop` 探针因此从靠 x 出界改成 `(1050,664)` 靠 y 出界，
  **测试意图不变**，实测仍过。

### 3.5 前线 = "盒子 + 让位" —— 已实现（用户模型）

> **"阵线就像一个只能容下五个球的盒子，如果前线已经有四个球，你在放一个球仍然可以自由选位置，
> 其他球把位子让开就行了"**

前提（查证过，不是推测）：前线规则**完全不读槽位索引**。`CombatRangeJudicator.Evaluate` 只按线判：

```csharp
if (_battlefield.FrontLine.Contains(attackerState.Position))
    return _battlefield.GetSupportLine(enemy).Contains(targetSlot);  // 前线 → 仅敌方支援线
```

全仓 grep 读槽位索引的只有 `IndexOfHq`（支援线 HQ 守护）。
**所以前线顺序是纯表现，可以由 UI 自己排** —— `_frontOrder` + `PlaceOnFront(uid, rank)`。

实测（前线 4 个单位、引擎只剩 1 个空槽）：

```
丢最左 → landed=frontline[1]
row=[USG第二军[1]@391, 卡尔拉[0]@520, 卡维拉尔[4]@649, 帝国坦克[3]@778]
     ↑ 排在最左边      ↑ 其余三个让位
```

### 3.6 预部署槽位预览（用户新提的需求，之前完全不存在）

拖拽**未松开**时把该行所有空槽画出来：每个空槽一条 6px 竖线标记，只有当前目标额外画卡宽的框。

- 早期版本每个空槽画一个**卡宽虚线框** → 密集时三个框中心只隔 32px、卡宽 112px，
  **完全重叠，画面上看起来只有一个位置**。这才是"看起来没得选"的真正来源。
- `SlotX(slot, cards, capacity)` 是 `DropSlot` 的**逆运算**，两者刻意同源 ——
  不同源就是"框在这里、牌落那里"。
- 实测：`opts=[0@382,1@511,2@640,3@769,4@898]`（5 空槽）、
  `opts=[1@576,4@898]`（剩 2 个）、`opts=[4@963]`（只剩 1 个，如实只画一个）。
- 候选 = **移动 ∪ 攻击并集**（`架构docs/05:40` 的要求），敌方可打单位也进预览。

### 3.7 拖拽分派归引擎（`架构docs/05:40`）

新增契约 `CommandUnit(uid)`，取代 UI 侧先判"攻击还是移动"：

- `TryDrop` 只陈述手势（点了谁 / 落在哪），**不预判**。
- 桥接层走 `CommandManager.BeginCommandAsync` → `DispatchSelectedAsync`
  （选中敌方 HQ/单位 → 攻击；选中空槽 → 移动）。
- 落在不可攻击的敌方卡上不再静默取消，由引擎给 `TargetingFailed`。

### 3.8 顺手修掉的两处

- `BattleScreen.Presentation` 的 switch 只处理 10 种 step 里的 7 种 → 补上
  `UiTurnPresentation`（`TurnBannerAsync` 从此不再是死代码）和 `UiDiscardPresentation`。
- 删除 `tests/DiagnosticTests.cs`（零断言脚手架，两处 "BUG CONFIRMED" 全靠 `WriteLine`，
  且 `previews.All(...)` 在**空集合上恒为 true**，把"零预览"误报成"只能打总部"——
  **"单位只能打总部"这个结论从未成立**）。

---

## 4. 🔴 待修的 bug（按优先级）

### 4.1 引擎 `PlayManager.cs` 有未提交改动 + 一处文档分歧

**状态**：`OrC-KSD-github` 工作区有一处**已改但未提交未推送**：

```diff
- var candidates = _battlefield.GetSupportLine(owner).GetAdjacentEmptySlots();
+ // ① 候选：己方支援线【全部空槽】——为空＝预打出不可开始（不进入交互）。
+ var candidates = _battlefield.GetSupportLine(owner).Where(slot => slot.IsEmpty).ToList();
```

**为什么改**：原邻位规则在 HQ 居中后候选仍受限，表达不了任意格；且旧
`docs/ENGINE_BRIDGE.md:96` 早就写明"**需上游放宽**：部署候选改为支援线全部空槽"。

⚠️ **与 `架构docs` 的实质分歧**：`架构docs/04-打出与部署.md:107` 与 `:24` 把它写成
**邻位规则是现状**，与本实现相反。文档已恢复原样（本轮误改后已还原），
**这条需要引擎作者裁决**：候选面是"规范"还是"现状快照"。

**合法性没有放宽**：指挥点仍由打出链的 `validation.cost.check` 拦截
（实测 2 费在 1 点下 `status=Failed reason=PlayVerificationRejected`，不扣点不弃牌），
槽空与未单位化在 `PlayUnitAsync` 内复验。

**回滚**：`git -C "H:/Working Folder/OrC-KSD-github" checkout -- src/ tests/`

### 4.2 引擎 38 条测试仍按旧布局断言

`765/803`。改 HQ 居中那笔留下的旧账，分组：`CommandRulesTests` 5、
`JudicatorRevalidationDedupTests` 5、`JudicatorCombatLegalityTests` 5、
`JudicatorCombatCounterAmbushTests` 4、`CardServiceTests` 4、`TargeterGameplayDemoTests` 3、
`PlayChainUnitTests` 3，其余各 1。**放宽部署候选没有新增任何失败。**

🔴 **不要盲目改断言** —— 这些是引擎作者钉住旧相邻性/布局语义的用例，
每条都要判断"它应该变成什么"。盲改等于把 38 条真断言糊过去。

**中途经验**：一处共享夹具 `CommandTestInfrastructure.PrepareOnSupportAsync` 的参数语义
从"线路第 index 格"改成"**第 index 个可部署格**"（升序、跳过 HQ 占位槽），
**一处修好 80 条级联失败**。改这类夹具优先于改断言。

### 4.3 🔴 `move=BROKEN[2,0,2]` —— 两次落点相同（本轮新发现，**未修**）

实机最新一轮：

```
move[row-middle]  x=640  pos=2/0  taken=[]        free=[0,1,2,3,4] -> slot=2  ✓
move[left-of-it]  x=8    pos=-3/0 taken=[]        free=[0,1,2,3,4] -> slot=0  ✓
move[right-of-it] x=1272 pos=1/1  taken=[0] free=[1,2,3,4]     -> slot=2  ❌ 与第一步同槽
DIRECTION move=BROKEN[2,0,2]
```

**未查清的不一致**：`pos=1/1` 表示按牌距外推算出的画面位置是第 1 位，
`free=[1,2,3,4]`（槽 0 被占）→ 按 `free[min(1,3)]` 应得 `1`，日志却写 `slot=2`。
**先弄清 `FrontDrop` 的 `rank` 与 `free[]` 索引关系**，再决定是判据太严还是算法有 bug。

**验收时务必注意**：前线语义是"画面顺序"（§3.5），引擎槽位只是实现细节，
所以判断落点对错**要看 `row=[名称[槽位]@x]` 的画面顺序**，不能只看 `landed=`。

### 4.4 `PlayableUids` 把手牌全部标成可出

`OrcActionReader.cs:29-33`，注释自认"引擎没有可出牌查询"，于是**手里所有牌**都 `playable=True`。
实测 `kredits=1` 时四张费 2/2/3/4 全部 `playable=True`，拖出去被引擎拒绝、牌回手，
提示只写"引擎拒绝了该操作"，**不区分点数不足**。

连带影响：审计脚本按"可出"挑最便宜的牌，会挑到**指令卡**（`UnknownCard`）→ 整轮审计卡死。
已让审计只挑 `CardType == "unit"`，但**产品问题本身未解**。

### 4.5 卡池一半是死牌

| 指标 | 实测 |
|---|---|
| 目录卡数 | 315 |
| `CardPoolCompiler` 编译出 | **150** |
| 桥接层根本提交不了的 | **84（56%）**：80 指令 + 4 反制 |
| 卡牌效果数据 | 315 张卡 `effect` 字段**全为 0 条** |

`DeckBuilder.Build` 产 15 单位 + 15 指令，而指令打不出；`Validate` 还**强制要求**牌组含指令卡。

**引擎其实接受指令卡** —— `PlayManager.BeginCommandPrePlayAsync` 是 **public**，
直接调用实测 `status=Success`、`kredits 1->0`、`hand 4->3`。
（旧 `CARD_POOL_REPORT.md:124-131` 写"引擎没有打出指令的入口"，**这句是错的**。）

**但没接**：目录里没有效果数据，且 `CreateAsync` 构造 `Match` 时 `effectRegistry` 传 `null`
→ 接上就是"扣你的费、扔掉卡、什么都不发生"，比干脆拒绝更坑。
要做就得接整条效果流水线（`EffectParsing/` 的 Lexicon + Templates + csx，**词表不在引擎源码树里**）。

### 4.6 9 张单位兵种丢失

`cruiser` 4 / `spacefighter` 3 / `landcruiser` 2 —— 引擎 `UnitType` 枚举只有 5 个值，
`Enum.TryParse` 失败返回 `null` = 静默裁剪，与编译器自己声明的"reject, not trim"原则不一致。

### 4.7 桥接层遗留问题

- 意图生命周期：`_pendingSupportIndex` 曾长期只写不读（P12 起是死代码），现已真正被消费；
  `_pendingSlot` / `_pendingSelection` 已加 `ClearIntent()` 用完即清（否则陈旧意图会应答下一个请求）。
- 契约死字段/死命令：`MulliganOpen`（从不赋值）、`RetreatUnit`、
  `ChooseTarget`（`SubmitAsync` 显式 `return Rejected`）。`CanEndTurn`/`AttacksEnabled` 恒 true。
- 🔴 `OrcMatchRunner.cs:59` 有 `proto/ui/` 里**唯一**一行 `Orc.*` 引用
  （`ActiveSeatOnTurn` 读 `Match.CurrentPlayer`，P14 引入），**破了自己立的"UI 层零引擎引用"规矩，
  且没有任何护栏会抓它**（`BridgeContractTests` 只冻结 DTO 成员名，不检查程序集引用）。
  建议：判定挪进桥接层（`OrcMatchSession.IsViewerOnTurn`），UI 只读 bool；
  再补一道**源码级护栏**（遍历 `proto/ui/**/*.cs` 断言无 `Orc.` 使用）——
  该仓库已有"测试从磁盘读源码"的先例（`RealDeckMatchTests` 直接读卡池目录）。
- `Main.cs:433` 硬编码 `Cards.Count != 315` → 换卡池即误报。
- 无 `[autoload]`，`AnimClock`/`SfxPlayer`/`AnimationEngine` 全在 `Main._Ready()` 手工 new，
  顺序依赖（`SfxPlayer` 读 `Clock.Changed`）无编译期保护。

### 4.8 杂项

- `project.godot` 会被某次工具运行**写脏**（BOM 被剥 + 插入乱码键 `"ï»¿config_version"=5`）。
  **发现它变脏直接 `git checkout -- project.godot`，别提交。**
- `Kards.Ui.OrcBridge.csproj` 里 `OrcEngineRoot` 是**机器本地绝对路径**，
  别人 clone 下来 `dotnet build` 直接找不到引擎。要源码分发得上 submodule 或发 NuGet。
- 导出模板 `.mono` 版未装（`%APPDATA%\Godot\export_templates\4.7.stable\` 是空的），
  `tools\release.ps1` 会在前置检查里抛带路径的可读错误。下载需要 Watt Toolkit 加速。
- 死代码清理（旧审计发现，本轮未动）：`BattleDemoAdapter` 之外，
  `OrcMatchSession` 的 `IsViewerOnTurn` 之类新入口建议一并收敛。

---

## 5. 本轮踩过的坑（写下来免得重犯）

### 5.1 `Complete` 返回 false 会让引擎永久 park 🔴

**本轮最贵的 bug，冻结了实机审计两分钟才发现。**

引擎在 `BeginInteraction`（**void、绝不 await**）里挂起等一个 `Task`，
而 `OrcMatchSession.Complete` **丢弃了 `responder.Complete(...)` 的返回值**。
`Complete` 返回 `false` 的含义是"提交被拒、**请求继续等待**"——
但引擎已经从回调返回、**不会再问第二次**，于是那个请求永远 park，
之后所有命令都跟着挂死。

**修法**：`Complete` 失败就 `Cancel`；`Cancel` 也被拒（请求 id 不匹配之类）则退到
保守策略 `AutoRespond`。`AnswerParked` 里两处裸调 `Cancel` 同样加固（`Refuse` 辅助）。

**验收判据**：`--capture-real` 跑完 10/10 步并打出 `REALCAPTURE end`。
死在这里时的症状是：日志停在 `endturn click channels=vvv` 之后再无输出、内存定死在 426MB。

### 5.2 `Math.Min` 不防负数 → 数组越界

`free[Math.Min(rank, free.Length - 1)]`：`rank` 是**画面位置**，空行时
`rank = round((x - 640)/129 + (cap-1)/2)`，`x=8` 算出 `rank = -3` → `free[-3]` 崩。
已改 `Math.Clamp(rank, 0, free.Length - 1)`。**教训：位置算出来的数要先夹再索引。**

### 5.3 `git push ... | tail; echo rc=$?` 拿到的是 `tail` 的退出码

害我误判"推送成功"，`git ls-remote` 复查才发现远端还是旧提交。
**正确姿势**：`git push > log 2>&1; echo rc=$?`，
**以 `git ls-remote origin refs/heads/main` 是否等于本地 HEAD 作为成败判据**。

### 5.4 推引擎前必须补 repo-local CA

UI 仓库配过 ≠ 引擎仓库配过（`--local` 不跨仓库）。缺它时 `git ls-remote` 直接报
`schannel: CRYPT_E_NO_REVOCATION_CHECK`：

```powershell
git config --local http.sslBackend openssl
git config --local http.sslCAInfo "H:/Working Folder/OrC-KSD.Godot/artifacts/combined-ca.pem"
```

**代理端口别记死**（旧文档写 5373，实测已是 4753）→ 一律读 `$HTTP_PROXY`。

### 5.5 演示夹具不发布线容量 → 整个 UI 静默走降级分支

`BattleDemoAdapter` 构造 `UiMatchView` 时**没有** `FrontLineSlotCount`/`SupportLineSlotCount`
→ 两者为 0 → 落点算法走 `capacity <= 0` 降级分支，demo 里"丢左边"永远插到最右。
已补 `= 5`。**任何跟线容量有关的逻辑，夹具必须发布容量。**

### 5.6 Godot 输入注入的坐标双重缩放

基准视口 1600×900、stretch=`canvas_items`；`BattleScreen.cs:174` 又把棋盘按 `Size/1280 = 1.25` 放大。

- `final`(视口→窗口) = **0.8**，`control.GetGlobalTransformWithCanvas()` = **1.25**
- `Input.ParseInputEvent` 入站会**再乘** `final⁻¹`(1.25) → 坐标被放大 1.5625 倍，
  飞到 y=848，卡牌根本命不中
- 正解：`GetViewport().PushInput(ev, in_local_coords: true)`，坐标用 `control × 棋盘坐标`
- **真实鼠标不受影响**（`final × control = 1`，两点缩放抵消）

### 5.7 断言要按玩家手势，别按抽象名次

按名次测是**自证自洽**（按自己定义的语义发请求，再按同一套语义断言结果），
会掩盖"够不到行尾"这类问题。必须按**玩家手势**测：行最左 / 某张牌右边 / 行尾。

### 5.8 验收必须同时覆盖引擎态与绘制态

`landed=support[3]` 全对 ≠ 画面对。判据：`RowSlotOrder(front)` 的绘制顺序槽位数组
必须严格升序（支援线），写成审计项 `PAINT checked=N broken=0`。
**这个 bug 连续溜过两轮**，因为我一直在验引擎返回的槽位、没验画面上单位相对 HQ 的位置。

### 5.9 审计自己的假阴性

- `landed` 检测固定等 8 帧 → 提交是**异步**的，投影晚一帧回来 → 成功的部署被读成
  `<still in hand>` → **重复部署把支援线塞满**。必须**轮询**。
- 移动那条多一层坑：**单位本来就在 `SelfLine` 里**，"找到它"证明不了移动成功，
  必须等 `Zone == "frontline"`。
- 造场景（填满前线）**必须同回合内**做，跨回合会被敌方 AI 打死（`selfUnits` 3→2，场景根本没造出来）。
- `pack` 内部的部署/移动要标 `record: false`，否则会把自己的填充动作算进 `DIRECTION` 判据。

---

## 6. Godot MCP（已装好，等 Trust）

`Vollkorn-Games/godot-mcp` v0.1.1（MIT），75 个工具，**带真实游戏交互**：

```
send_mouse_click / send_mouse_drag / send_mouse_motion
send_key / send_key_sequence / send_joypad_button / send_joypad_motion
capture_screenshot / game_screenshot / run_and_capture
run_project / run_interactive / pause_game / game_state / reset_scene
get_runtime_errors / get_debug_output / get_performance_metrics / batch_operations
```

配置在 `C:\Users\Alan\.workbuddy\mcp.json`（**注意不是**带点前缀的 `.mcp.json`）：

```json
{"mcpServers":{"godot":{
  "command":"C:\\Program Files\\nodejs\\node.exe",
  "args":["H:\\g\\tools\\godot-mcp\\build\\index.js"],
  "env":{"GODOT_PATH":"H:\\g\\tools\\godot47\\Godot_v4.7-stable_mono_win64\\Godot_v4.7-stable_mono_win64.exe"}}}}
```

🔴 **`GODOT_PATH` 必须指向 .NET(mono) 版**，否则 C# 工程加载不了
（会重演 `No loader found for resource: Main.cs`）。
装完要在连接器管理页右上角"自定义连接器"里点 **Trust**。
重建：`cd H:\g\tools\godot-mcp && pnpm install && pnpm run build`（自检 `pnpm run test:unit`，34/34）。

**已实测的坑**：

- 坐标 = **视口像素**，与截图 1:1；棋盘坐标 × **1.25** = 视口坐标。
- **PopupMenu 是独立窗口，`game_screenshot` 截不到它**。触发菜单项要用：
  `evaluate_expression("get_tree().root.find_children('*','MenuButton',true,false)[0].get_popup().id_pressed.emit(25)")`
  —— id 25 就是"真实对局（接入引擎）"。
- `evaluate_expression` **不支持 `var` / 多行 / lambda**，表达式要短。
- `run_interactive` **不能传自定义命令行参数** → `--capture-real` 那条通道 MCP 跑不了。
  **分工：自动化审计定位（`--capture-real`），MCP 做真实鼠标的最终确认。**

---

## 7. 建议的下一步（按性价比）

1. **查清 §4.3 的 `move=BROKEN[2,0,2]`** —— 唯一还活着的落点问题。
2. **裁决 §4.1 的候选面分歧**（邻位 vs 全部空槽），然后提交引擎那处改动并推。
3. **逐条更新 38 条引擎测试**（每条附"为什么这么改"，不要盲改）。
4. §4.4 `PlayableUids` 全标可出 —— 影响玩家体感（拖出去没反应）。
5. §4.5 死牌问题 —— 要么改成只用能打的卡建组，要么接整条效果流水线（大活）。
6. §4.7 的边界护栏 —— 不然下次谁再顺手 `using Orc.Game;` 照样一路绿灯。

---

## 8. 我今天犯的错（别学）

| 错 | 代价 |
|---|---|
| 拿 `--verify-ui` 的绿灯当"能玩"的证据，而它跑的是探针牌组不是真卡对局 | 用户当面说"完全无法游玩" |
| 你说"修改"，我理解成改你刚放进来的 `架构docs/`，其实你要修 bug | 误改 4 处 + 新增整节《偏离记录》，已全部还原 |
| 按抽象名次测落点（自证自洽） | "够不到行尾"这类问题连续溜过两轮 |
| 只验引擎返回的槽位、不验画面顺序 | "数据全对、画面是坏的"溜过两轮 |
| 丢弃 `Complete` 的返回值 | 引擎永久 park，冻结实机审计两分钟 |
| `git push \| tail; echo rc=$?` | 误判推送成功 |
| 无头导入/编辑用错 Godot 二进制 | 报 `No loader found for resource: Main.cs`（工程没坏，是二进制不对） |
| 用测试里没验证过其存在性的方法名（`OrcRefs.ResetKeyCache`） | 编译失败 |

---

## 9. 相关文档

| 文件 | 内容 |
|---|---|
| `架构docs/游戏层对局端到端流程.md` | ★ **权威索引**（引擎作者提供，受众明写"对接本引擎的 AI agent"）。§2 有路由表：**先只读它，按表只打开 1–2 个分文件** |
| `架构docs/04-打出与部署.md` | 预打出→选槽→部署链。⚠️ `:107`/`:24` 与 §4.1 的实现有分歧 |
| `架构docs/05-指挥与交战.md` | 拖拽分派、复验、互伤/伏击/反击、死亡。` :40` 要求"候选＝移动∪攻击并集" |
| `架构docs/06-目标选择（异步倒置）.md` | targeter 两入口、应答三态。**`Complete` 返回 false = 拒绝且请求继续等待** —— §5.1 的依据 |
| `docs/BATTLE_SLAM_REWORK.md` | 拍桌设计铁律（幅度只由防御力决定，不得引入兵种分支） |
| `docs/ENGINE_BRIDGE.md` | 旧桥接文档。`:96` 的"需上游放宽"就是 §4.1 那处改动 |
| `docs/CARD_POOL_REPORT.md` | ⚠️ `:124-131` 说"引擎没有打出指令的入口"是**错的**（§4.5） |
| `.workbuddy/memory/` | 项目长期约定 + 每日工作日志（细节多，查历史从这儿开始） |
