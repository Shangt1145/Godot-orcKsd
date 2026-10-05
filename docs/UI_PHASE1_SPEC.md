# Godot UI 先行开发 · 可执行规格书

> **本文是给 agent 直接开工用的。** 不需要读其他文档，所有必要的接口契约、参照物位置、验收断言都在这里。
> 上游依据：`docs/UI_NOW_OR_LATER.md`（可行性分析）· `docs/GODOT_ORC_ROUTE.md`（2.0 路线）· `docs/GODOT_PORT_CONSTRAINTS.md`（Godot 约束）
> 版本：2026-10-05 · 状态：**可开工**

---

## 0. 任务边界

### 你要做的是

**在 `项目文件夹`（短路径！见 §6）建一个 Godot 4.7 + C# 项目，实现与引擎无关的 30% UI。**

### 你不要做的（越界即返工）

| ❌ 不做 | 为什么 |
|---|---|
| 任何"读取单位攻击力/防御力"的代码 | 数据模型未��。OrC-KSD 的 `CardBase` 只有 `Definition`/`Modifiers`/`Keywords`/`Owner`，**没有 `defense` 这种平铺字段**。现在写必重写 |
| 任何"判断这张卡能不能打出/能不能攻击"的逻辑 | 引擎未就绪，判定规则尚未确定。UI 只能**读 DTO 给的 `canAttack`/`canPlayable`**，绝不自己算 |
| 战斗界面布局（战场格位、支援线、HQ 位） | 依赖引擎的区域/位置模型（`GameEnvironment.GetPositionOf` 等） |
| Inspector / 效果编辑器 | 依赖 `GetCapabilities` 能力表，**OrC-KSD 完全没有** |
| 手牌出牌/拖拽的合法性校验 | 同上 |
| 联机大厅的协议实现 | 需与 2.0 Godot 端对齐，先只留接口 |

### 你要做的是

| ✅ 做 | 规模参照 |
|---|---|
| Godot 项目骨架 + 7 个 screen 导航 | 对齐 `electron/game/index.html`（649 行） |
| 卡牌视觉组件（四数值/词条图标/稀有度/卡图） | 对齐 `electron/game/css/style.css`（3,723 行） |
| **动画层 60 个效果** ⭐ 最高价值 | 移植 `electron/game/js/animate.js`（1,616 行） |
| 事件段播放器 | 按 `EventSegment` 契约 |
| 卡牌图鉴/收藏屏 | 数据源 `dist/win-unpacked/resources/app/game/data/nations/*.json`（11 个文件，315 张卡） |
| UI 视图 DTO 的 C# 定义 | ⭐ **这是你和引擎之间的合同，必须最先定** |

---

## 1. 参照物（唯一真相源，全部只读）

| 需要什么 | 去哪看 | 规模 |
|---|---|---|
| 页面骨架与结构 | `H:\Working Folder\Kards-Desktop\electron\game\index.html` | 649 行，7 个 `screen-*` |
| 视觉样式 | `H:\Working Folder\Kards-Desktop\electron\game\css\style.css` | 3,723 行 |
| 动画语义 ⭐ | `H:\Working Folder\Kards-Desktop\electron\game\js\animate.js` | 1,616 行，**含大量设计意图注释，务必逐条读** |
| UI 交互逻辑 | `H:\Working Folder\Kards-Desktop\electron\game\js\ui.js` | 7,661 行（只读结构，⚠ 不要抄里面的数值读取） |
| 卡池数据 | `H:\Working Folder\Kards-Desktop\dist\win-unpacked\resources\app\game\data\nations\*.json` | 315 张卡 |
| 卡图素材 | `H:\Working Folder\Kards-Desktop\dist\win-unpacked\resources\app\{USG,UN,av76,deran,星盟,牌！Q!!!}\` | **576 张 PNG** |
| 音效素材 | `...\app\game\assets\sfx\` | **53 个 mp3** |
| 效果 DSL 规范 | `...\app\game\data\EFFECT_DSL.md` + `RULES_REF.md` | 29.8KB + 60.7KB |

⚠ **`dist/win-unpacked/resources/app/` 才是完整数据**，`electron/game/` 下**没有** `data/` 目录（已实测）。

### 已实测的素材规格

```
卡图      500×701 PNG（RGBA），文件名 = 卡牌 id 的最后一段（USG/commands/_1.png ↔ 卡牌 id "USG/commands/_1"）
          卡牌 JSON 的 art 字段直接给相对路径：art = "../USG/commands/_1.png"
音效      mp3（ID3v2.4），53 个，按兵种分：artillery1-2 / attack1-3 / bomber1-2 / cruiser1-2 / deploy1-2 / die1-… 
卡池      315 张 = 161 单位 + 148 指令 + 4 反制 + 2 无 cardType
页面      7 个 screen：battle / collection / deck / decklib / editor / help / settings
          对应 ui.js 里 13 个顶层 render* 函数
```

---

## 2. ⭐ 阶段 A（先做这个）：定义 UI 视图 DTO

**这是最重要的一个任务。** 它是你和引擎之间的合同，**必须在写任何 UI 代码之前定下来**。

### 2.1 为什么必须先定

现有 `ui.js` 有 **29 处直接读单位字段**（`ui.js:1509` 等）：

```js
all.forEach(u => { snap.units[u.uid] = u.defense; snap.zones[u.uid] = u.zone; ... });
```

而这些字段的**语义没定义** —— `defense` 是基准值还是 `recomputeAuras` 之后的有效值？OrC-KSD 根本没有这种平铺字段。

**DTO 的首要目的就是把 `recomputeAuras` 的四套状态（`mods`/`tempBuffs`/`permMods`/`dynMods`）挡在 UI 之外。**

### 2.2 文件清单（新建）

```
H:\g\kards\proto\contracts\
  UiViewModel.cs          # DTO 定义（引擎侧投影用）
  IDtoKind.cs             # 身份标识规则
  README.md               # 给上游的提案正文（见 §2.6）
```

### 2.3 DTO 完整定义（照此实现）

```csharp
namespace Kards.Ui.Contracts;

/// <summary>UI 视图投影契约。引擎负责从内部状态投影，UI 只读本类型。
/// 关键约定：所有数值字段都是「修饰后的有效值」，不是基准值。</summary>
public sealed record UiCardView(
    // ── 身份 ──────────────────────────────────────
    string Uid,                // 对局内稳定标识。语义：单次对局内不变，跨对局可复用。UI 用它做节点 key
    string CardId,             // 卡牌定义 ID，如 "USG/commands/_1"。语义：永久稳定

    // ── 身份信息（可缓存，不随对局变）─────────────
    string Name,
    string NameEn,
    string CardType,           // unit | order | counter
    string UnitType,           // infantry | tank | artillery | fighter | bomber | landcruiser | cruiser | spacefighter | …
    string Set,                // 英国 | 美国 | 德国 | 苏联 | 日本 | 法国 | 意大利 | 波兰 | 芬兰 | 澳新军团 | 自定义国家原值
    string Rarity,             // token | iron | bronze | silver | gold | common
    string ArtPath,            // 相对路径，如 "../USG/commands/_1.png"
    string Text,               // 卡面原文（Inspector 与图鉴用）
    IReadOnlyList<string> Keywords,   // 词条 id 列表，顺序稳定
    IReadOnlyDictionary<string,int> KeywordValues, // 参值词条的数值（如 armor=3），无参值则为空
    int Cost,                  // 部署费（基准，UI 展示用）
    int BaseAttack,            // ⚠ 基准攻击力。展示用。有效值用 EffectiveAttack
    int BaseDefense,

    // ── 战斗状态（引擎每步投影）───────────────────
    int EffectiveAttack,       // ★ 含光环/修饰/期限修正后的有效值
    int EffectiveDefense,
    int EffectiveOpCost,       // 操作费有效值
    int Health,                // HQ 血量；单位恒等于 EffectiveDefense（保持字段形状统一）
    string Zone,               // frontline | support | hq | hand | deck | discard | counter
    int SlotIndex,             // 阵线内位次；不在场上为 -1
    bool IsHq,
    bool IsVeteran,

    // ── 判定（引擎算，UI 绝不自己算）──────────────
    bool CanAttack,            // 此刻能否发动攻击
    bool CanMoveAndAttack,     // 此刻能否移动后攻击
    bool CanBeTargeted,        // 是否可被选中
    IReadOnlyList<string> BlockedReasons,  // 不能操作的原因（UI 用于灰显 + tooltip）

    // ── 运行时标记 ────────────────────────────────
    bool IsSuppressed,         // 抑制中（词条被清空）
    bool IsSilenced,           // 静默（效果关闭）
    bool IsToken               // 衍生卡/衍生单位
);

/// <summary>一局对战的全量可见投影。UI 的唯一数据来源。</summary>
public sealed record UiMatchView(
    int Turn,
    string Phase,              // mulligan | play | over
    string ActivePlayerSide,   // self | enemy
    int SelfKredits,
    int SelfMaxKredits,
    int EnemyKredits,          // 可见性由引擎决定（引擎不给就不给 0）
    int EnemyMaxKredits,
    int SelfHandCount,
    int SelfDeckCount,
    int SelfCounterCount,
    IReadOnlyList<UiCardView> SelfHand,      // ⚠ 顺序即显示顺序，引擎须保证与规则一致
    IReadOnlyList<UiCardView> SelfLine,      // 前线 + 支援线，Zone 区分
    UiCardView? SelfHq,
    IReadOnlyList<UiCardView> EnemyLine,     // ⚠ 只含可见信息
    UiCardView? EnemyHq,
    UiCardView? SelectedTarget,              // 当前选中/待选择目标
    IReadOnlyList<UiCardView> PendingChoices // 手牌/抉择的候选（若引擎在等 UI）
);

/// <summary>可见信息等级。引擎按此决定 EnemyLine 给不给详情。</summary>
public enum Visibility { Full, Silhouette, Hidden }

/// <summary>UI → 引擎 的操作请求。⚠ 具体传输方式取决于 §6.3 的桥接设计，此处仅定义意图。</summary>
public abstract record UiCommand;
public sealed record PlayCard(string Uid) : UiCommand;
public sealed record MoveUnit(string Uid, string ToZone) : UiCommand;
public sealed record AttackUnit(string AttackerUid, string DefenderUid) : UiCommand;
public sealed record RetreatUnit(string Uid) : UiCommand;
public sealed record EndTurn : UiCommand;
public sealed record ChooseMulligan(IReadOnlyList<string> KeepUids) : UiCommand;
```

⚠ **⚠️ 选目标不走 `UiCommand`！** 已核实 OrC-KSD 的真实机制（`ITargeterBridge` / `ITargetingResponder`）：

- 引擎**主动问 UI 要候选**：`CollectCandidatesAsync(context)` → UI 返回完整 uid 列表
- 引擎**通知 UI 开始交互**：`BeginInteraction(desc, responder)` —— ⚠ **`void`，不是 Task**
- UI **回调应答**：`responder.Complete(requestId, 按槽位组织的选择)` / `Cancel(requestId)`
- ⚠ `Complete` 返回 **`false` 时请求继续等待**（不是失败终局），UI 要能重试或引导取消

**完整签名与实现见 §6.3.1。** 这里不设 `SubmitTarget` 记录类型，避免 agent 照着造一个不存在的通道。

### 2.4 三条铁律（违反即返工）

1. **UI 不碰引擎对象。** 只读 `UiMatchView` / `UiCardView`。引擎侧负责投影。
2. **UI 里不写规则判断。** 「如果攻击力大于 3 就高亮」这种代码**一律不许**——判断由 DTO 的 `Can*` 字段给出。规则只有一份，UI 抄一份就会漂移。
   自查：`grep -rn "if.*>.*attack\|if.*<.*defense\|defense\s*[<>]" proto/ui/` 应无命中。
3. **数值一律用 `Effective*`。** 需要显示基准值时用 `BaseAttack`/`BaseDefense` 并**明确标注**（如卡牌详情页的"基础 2 / 当前 4"）。

### 2.5 身份标识规则（照此实现，别自创）

```
Uid     对局内稳定。节点 key 用它。跨对局不复用。
CardId  永久稳定，等于 nations/*.json 的 key，形如 "USG/commands/_1"。
        ★ 素材寻址：artPath = cardId 映射到 ../<nations 根>/<子目录>/<末段>.png
        ⚠ 同名多实例不能按 CardId 合并（MEMORY 已记的坑）。
```

### 2.6 ⭐ 额外产出：给上游的 DTO 提案

新建 `contracts/README.md`，把 DTO 定义写成给 **Shangt1145（OrC-KSD 上游）** 的正式提案，说明：

- 为什么需要状态读面（他们只定义了段消费 `TakeSegments` 和即时回调 `OnImmediateUpdate`，**从未定义状态读面**）
- 为什么 `Effective*` 必须是有效值（KARDS 的 `recomputeAuras` 语义）
- 为什么 UI 不能自己算判定（规则只有一份）
- 需要他们补的最小查询面：`CanAttack` / `CanPlayCard` / 有效值读取 / 可见性等级

⚠ **这符合第四条移植纪律**（改动可单独摘出来给上游评审）。**不要单方面定完就开干** —— 提案发出去等回复，可能省掉后面 20 人日的返工。

---

## 3. 阶段 B：Godot 项目骨架

### 3.1 项目创建（在短路径）

⚠ **必须在 `H:\g\kards\`**（短路径）。原因：`H:\Working Folder\Kards-Desktop\` 下**不能可靠跑 .NET 子进程**（`dotnet test` 报 `hostfxr 0x80070005`），已实测排除路径深度/长度/空格，是该目录的进程级拦截。

```bash
mkdir -p H:/g/kards
# 用已装的 Godot 4.7 .NET 版创建项目
H:/Godot_v4.7-stable_win64.exe --path H:/g/kards --headless --quit   # 首次初始化
```

⚠ **不要用中文路径，不要放在 `H:\Working Folder\` 下。**

### 3.2 项目结构

```
H:\g\kards\
  project.godot
  proto/                     # 本规格书的交付范围
    contracts/               # ← 阶段 A
    ui/                      # ← 阶段 C/D/E
      screens/               # 7 个 screen
      components/            # 卡牌组件、按钮、列表
      anim/                  # ← 阶段 D：动画层
      data/                  # DTO 消费层
    scenes/
  .godot/                    # 导入缓存（会很大，建议 gitignore）
```

### 3.3 csproj 要求

```xml
<Project Sdk="Godot.NET.Sdk/4.7.0">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>   <!-- Godot 4.7 Android 导出要求 .NET 9 -->
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

⚠ **`net9.0` 不是随意选的**：Godot 4.7 导出 Android 要 .NET 9，而 OrC-KSD 是 `net8.0`。**net9 应用引用 net8 库是官方支持的向下兼容，本项目已实测通过**（含反射）。

### 3.4 屏幕导航（对齐 index.html）

```
TopBar（常驻）
 ├─ screen-battle       战斗
 ├─ screen-deck         卡组编辑
 ├─ screen-collection   图鉴收藏
 ├─ screen-decklib      牌库
 ├─ screen-editor       ★ Inspector（阶段 F，本期只留占位）
 ├─ screen-help         帮助
 └─ screen-settings     设置
```

参照 `index.html` 的 `show(screen)` 逻辑（`ui.js:3475`）：

```js
function show(screen) {
  document.body.classList.toggle('in-battle', screen === 'battle' && !!S.state && !S.state.over);
  $$('.screen').forEach(s => s.classList.toggle('active', s.id === 'screen-' + screen));
  ...
}
```

⚠ 注意 `in-battle` 类控制**战场专属样式**（如隐藏某些顶栏元素）—— Godot 里对应一个 `Theme` 变体或一个 `Control.visible` 开关。

---

## 4. 阶段 C：卡牌视觉组件

### 4.1 卡牌组件必须支持的字段

```
卡名（中文 / 英文）· 四数值（费用 / 攻击 / 防御 / 操作费）
词条图标（带参值时显示数字，如「重甲 3」）
稀有度边框 · 国别标识 · 卡图（500×701 缩放到组件尺寸）
卡面原文（长按/详情展开）
状态叠加：抑制（灰化）· 静默 · 可攻击高亮 · 被选中 · 目标候选框
```

### 4.2 参照物

`style.css` 3,723 行。关键类名（去源码里找对应规则）：

```
.card            卡牌基础
.card.dying      死亡淡出（动画层会加这个类）
.card.slam       部署拍桌
.entering        入场
.counter-chip    反制牌影
.fx-fast / .fx-faster   速度档覆盖（CSS 变量乘时长）
```

⚠ **`fx-fast`/`fx-faster` 是 CSS 变量注入**，Godot 里改成全局 `AnimationSpeedScale`（见 §5.1）。

### 4.3 卡图导入

```bash
# 从发行包复制卡图到 Godot 项目（576 张，约 165MB）
cp -r "H:/Working Folder/Kards-Desktop/dist/win-unpacked/resources/app/USG" H:/g/kards/proto/art/
# 重复：UN / av76 / deran / 星盟 / 牌！Q!!!
```

⚠ **纹理压缩**：Godot 导入时选 `VRAM Compressed`。⚠ 但**若用 `gl_compatibility` 渲染器，`GPUParticles2D` 会静默失效** —— 粒子效果必须用 `Forward Plus` 或改用 CPU 粒子。这是本项目动画层的前提条件。

---

## 5. ⭐ 阶段 D：动画层（最高价值，优先做）

### 5.1 先建速控与减弱动效（对应 A.ms / A.reducedMotion）

`animate.js` 的两个基础装置必须先移植：

```csharp
public partial class AnimClock : Node
{
    public enum Speed { Normal, Fast, Faster }   // 1 / 0.6 / 0.32
    public Speed Current { get; private set; } = Speed.Normal;

    /// 缩放一个毫秒数，下限 30ms（防止极快档把动画压成 0 帧）。
    /// ⚠ 下限 30ms 来自 animate.js:40 的原注释：「免得极快档把动画压成 0 帧」
    public double Ms(double ms) => Math.Max(30, ms * Scale);

    public double Scale => Current switch
    { Speed.Fast => 0.6, Speed.Faster => 0.32, _ => 1.0 };

    public bool ReducedMotion { get; private set; }   // 见下

    public override void _Ready()
    {
        // 尊重系统偏好，但不改变行动/结算时钟 —— animate.js:56-57 原注释：
        // "Geometry, sound and readable card reveals stay intact; travel and debris stop."
        ReducedMotion = OS.GetName() switch { _ => _reducedMotionFromSettings() };
    }
}
```

⚠ **`reducedMotion` 的语义要照抄**（`animate.js:56-60`）：减弱动效**只停"位移与碎屑"，不停"几何、音效、可读揭示"**。别做成"全关"。

⚠ **速度档要持久化**：现有实现存 `localStorage['kg_fx_speed']`，Godot 里存 `user://fx_speed.cfg`。

### 5.2 动画时长基准表（照抄 `animate.js` 的 `ANIM`）

```csharp
public static class AnimTiming
{
    public const double Fast = 120;   // 快速
    public const double Base = 260;   // 基准
    public const double Slow = 420;   // 慢速
    public const double Fly  = 420;   // 手牌飞向棋盘
    public const double Lunge = 150;  // 攻击冲刺
    public const double Die  = 380;   // 死亡淡出
    public const double Move = 360;   // 单位在阵线间移动（支援线 ↔ 前线）的平滑位移
}
```

⚠ **这七个值是手感基线，不要拍脑袋改。** 改之前先去 `animate.js` 确认上下文。

### 5.3 动画效果清单（`animate.js` 60 个导出，逐个移植）

`animate.js` 共 60 个 `A.xxx` 导出。按用途分组：

#### 组 1 · 基础装置（先做）

| 源 | 功能 | Godot 实现 |
|---|---|---|
| `ms` | 时长缩放（下限 30ms） | `AnimClock.Ms()` |
| `setSpeed` / `speedScale` / `speedName` / `SPEEDS` | 速度档 | `AnimationSpeedScale` 全局 |
| `reducedMotion` | 减弱动效 | 见 §5.1 |
| `sleep` | Promise 化延时 | `await ToSignal(GetTree().CreateTimer(sec), "timeout")` |
| `kill` / `card` / `toEls` / `rectOf` / `layoutRectOf` | DOM 工具 | Godot 无 DOM → 改用节点坐标 |
| `isAnimating` / `interrupt` | 动画互斥与打断 | ⚠ 保留语义：`lunge` 打断互斥（`animate.js:141`） |

#### 组 2 · 布局与位移（FLIP 的 Godot 等价）

| 源 | 功能 | 移植要点 |
|---|---|---|
| `flip` / `flipByUid` | **First-Last-Invert-Play**：记录旧位置 → 改结构 → 反推位移播放过渡 | ⭐ 见 §5.4，这是整个动画层的骨架 |
| `arcMidOf` | 跨阵线移动走抛物线弧 | Godot 用 `Tween` + `QuadraticBezier` 手工算贝塞尔 |
| `glide` | 平滑位移 | 直线 `Tween` |
| `turnBanner` | 回合横幅 | `Label` 动画 |

⚠ **`flipByUid` 的核心设计必须保留**（`animate.js:296-310`）：
- 变更前按 `data-uid` 记下"旧坐标 → 旧节点"
- mutate 后用**同一个 uid** 找"新节点"，反推位移
- 跨阵线移动**优先走抛物线弧**，不是生硬直线
- 弧线是"**绕过**"而不是"**穿过**"：中点沿垂直方向推开，推出量随长度增长但设上限
- 击点时机从 `dur-120` 改成 **`dur`**（位移走完才落地，才符合"落地"语义）

#### 组 3 · 战斗演出

| 源 | 功能 | 关键参数/语义 |
|---|---|---|
| `lunge` | 攻击冲刺 | 时长 `ANIM.Lunge` 150ms；⚠ 保留调用契约"最后一发抵达时结算一次，演出不产生额外伤害" |
| `impact` | 命中 | 弹着材质**由攻击方兵种决定**（炮弹/炸弹/枪弹/能量），`animate.js:106`；有 target 则按**目标兵种**选弹着（打装甲=金属、打散兵坑=泥土） |
| `muzzleFlash` / `muzzlePoint` | 枪口闪光 | 挂在单位自身角上，卡面**后腿**无关 |
| `tracer` | 弹道 | |
| `shockRing` | 冲击环 | 炮弹/炸弹/能量才有 |
| `sparkBurst` | 迸射粒子 | 纯 DOM+CSS → `GPUParticles2D` |
| `dustPuff` / `smokePuff` / `wakePuff` | 扬尘/烟/浪花 | **落地反馈按兵种分材质**：地面扬土、舰船推浪、飞机只留一道尾迹 |
| `slam` | **部署拍桌** | ⭐ 力度**由防御力分级**：`def<=2` 轻拍 / `3..5` 中拍 / `>=6` 重拍；轻拍音小、重拍更沉；`fam==='air'` 时长 ×1.25 |
| `die` | 死亡 | ⚠ **必须在元素被移除「之前」调用**，标记 `.dying`，等动画播完再由调用方清理（`animate.js:909`） |
| `burnCard` | 烧牌 | |

⚠ **死亡动画的时序坑**（`animate.js:909-925`）：`die()` 先标记 → 播完 → 才移除。**Godot 里同理**：`await die(); queue_free();`，别直接 `queue_free()` 然后 animate。

#### 组 4 · 界面反馈

| 源 | 功能 | 关键语义 |
|---|---|---|
| `pulse` | 脉冲 | |
| `floatAt` / `floatValue` / `kreditFloat` | 飘字 | HUD 数字跳动（指挥点/上限/牌库/反制数）——**原版回合开始时指挥点是跳字 +N** |
| `drawToHand` | **对手抽一张指挥官的标准演出** ⭐ | 三段式（`animate.js:1033-1037`）：① 从屏幕上沿落下、停在"指挥官"、缩放"迎接" ② 模糊化到画面右侧 ③ 停留 `holdMs` 后继续向左淡出屏幕。**全程 Promise 化，调用方 `await` 完再走下一步** |
| `flyCard` / `flyFromEl` | 通用飞牌 | |
| `slideInFromRight` | 右侧滑入 | |

⚠ **`drawToHand` 是"对手抽牌"的唯一演出**，注意它是三段式且要 `await`。**这在 Godot 里最容易被写成"播完就忘"导致时序错乱。**

#### 组 5 · 状态/机制演出

| 源 | 功能 | 语义 |
|---|---|---|
| `orderFx` | 分类演出 | 在棋盘上铺一层"效果层"，再按类别撒粒子/震屏 |
| `intelScan` | 情报扫描 | 在指定区域扫过一道青色光带（"看到对手手牌"的可见反馈） |
| `counterSet` | 反制「埋设」 | 在反制堆上盖一个**琥珀色封印环** |
| `counterFire` | 反制「触发」 | 琥珀色爆闪 + 冲击环 + 飘字 |
| `counterReveal` | 反制触发 | 把那张反制牌从"反制"堆飞出来亮一下卡面（**原版触发反制会展示卡面**） |
| `holdCounter` | 反制挂起 | 一枚小牌影从手牌飞向"反制"堆，落点盖一个封印环（**比只让数字 +1 有实感**） |
| `veteranUp` | 老兵升级 | 金色光环 + 向上金光（**替换原单位时用，别再用"拍桌"**） |
| `deckShuffle` | 洗入卡组 | 几张卡影从上方飞进牌库 + 牌库脉冲 |
| `aimShot` | 瞄准箭头 | ⭐ 曲线跟随，见 §5.5 |

#### 组 6 · 工具

| 源 | 功能 |
|---|---|
| `collectOps` | 收集一棵效果树里所有 op 名（条件/抉择/循环都是嵌套对象） |
| `classifyOps` | 按 op 名分类演出 |
| `attackProfile` | 攻击方兵种 → 弹着材质 |
| `familyOf` / `kindOf` / `hitKindOf` | 兵种族判定 |
| `budgetOk` | 性能预算检查 |
| `isAnimating` | 动画进行中判定 |

### 5.4 ⭐ FLIP 在 Godot 里的实现（动画层的骨架）

`animate.js` 的 FLIP 是整个动画层的地基（`animate.js:223-296`）。**Godot 没有 CSS transform 残留的概念，但机制可 1:1 复刻**：

```csharp
public partial class FlipMover : Node
{
    private readonly Dictionary<string, Vector2> _before = new();

    /// 变更前记录每个 uid 的「布局位置」（扣掉残留 transform）
    public void SnapshotPositions(IEnumerable<string> uids, Func<string, Control> resolve)
    {
        foreach (var uid in uids)
        {
            var c = resolve(uid);
            if (c is null) continue;
            _before[uid] = c.Position;   // ⚠ Godot 里没有"残留 transform"问题，直接用 Position
        }
    }

    /// 变更后：按同一个 uid 找新节点，反推位移并播放过渡
    public async Task PlayAsync(IReadOnlyDictionary<string, Control> after,
                                bool arc = false, double durMs = AnimTiming.Move)
    {
        foreach (var (uid, node) in after)
        {
            if (!_before.TryGetValue(uid, out var from)) continue;
            var to = node.Position;
            var delta = from - to;
            if (delta.LengthSquared() < 0.01f) continue;   // 零位移 = 静止，跳过

            if (arc) await PlayArcAsync(node, from, to, durMs);   // 跨阵线 → 抛物线
            else      await PlayLineAsync(node, from, to, durMs); // 同行 → 直线
        }
        _before.Clear();
    }
}
```

⚠ **必须保留的三条语义**（来自 `animate.js` 注释）：

1. **零位移 = 静止，跳过**（`animate.js:294`）。**不要为了"有动画"而强行动画** —— 原版就是这样：新线上完全没有移动的单位会自己滑进去。
2. **同行走直线、跨阵线走弧线**（`animate.js:292, 307`）。
3. **弧线是"绕过"不是"穿过"**：控制点沿垂直方向推开，`推出量随长度增长但设上限`（`animate.js:307-310`）。⚠ Godot 的 `Tween.InterpolateValue` 走线性，要自己按 `t` 算贝塞尔。

### 5.5 ⭐ 瞄准箭头的曲线跟随

`animate.js:431-494` 有个精妙实现：**不再拖动单位卡牌，而是从单位中心向光标画一条动态弯曲曲线**，返回句柄 `{ set(x,y), aim(on), destroy() }`。

Godot 等价（`Line2D` + 每帧重算控制点）：

```csharp
public partial class AimArrow : Line2D
{
    /// ⚠ 弓形弯向：opts.bend 固定给 1/-1（让箭头绕开自己那一排）—— animate.js:467
    /// ⚠ 曲线控制点：把中点沿「垂直方向」推开，推出量随长度增长但设上限 —— animate.js:487
    /// ⚠ 箭头朝向：贴在终点，朝曲线末端切线方向 —— animate.js:494
    /// ⚠ 弹性：句柄内部保存「当前终点」，每次 set() 把目标终点写入，
    ///   由一个 rAF-ish 的定时器「插值跟随」 —— animate.js:437-439
    public void SetTarget(Vector2 cursor) { /* 插值跟随，不瞬移 */ }
    public void Aim(bool on) { /* 淡入淡出 */ }
}
```

⚠ **"插值跟随"（缓冲）不能省** —— 瞬移会丢掉箭头的弹性手感，这是原版特意做的。

### 5.6 动画层的验收断言

```
✅ flipByUid 的零位移跳过：同线无移动的单位，必须静止（不产生任何 Tween）
✅ slam 按防御力分级：def=1 / def=4 / def=7 三种，力度/音量/时长必须不同
✅ slam 空中兵种时长 ×1.25
✅ die() 在移除之前：动画播完才 queue_free
✅ lunge 不产生额外伤害：调用前后单位血量一致
✅ drawToHand 三段式且可 await：Play → 停"指挥官" → 右移模糊 → holdMs → 左淡出
✅ reducedMotion 只停位移/碎屑：几何位置、音效、卡面揭示必须仍然发生
✅ 速度档 three 档：1 / 0.6 / 0.32，且 ms(20) 恒等于 30（下限）
✅ crossLine 走弧线：同线走直线，跨线走抛物线，且弧线"绕过"不"穿过"
```

---

## 6. 阶段 E：事件段播放器

### 6.1 OrC-KSD 的 `EventSegment` 契约（已核实）

```csharp
public sealed class EventSegment
{
    public long Sequence;                    // 段号，引擎实例内单调递增，自 1 起
    public DateTimeOffset Timestamp;
    public IReadOnlyList<LogEntry> Entries;  // 本段全部条目（含冒泡 = 本段完整因果，含报错）
    public IReadOnlyList<EventStream> Children; // 本段新增的顶层子树（挂载时序，递归含嵌套）
}
```

⚠ **三条消费纪律**（来自 `docs/ORC_KSD_MIGRATION_RESEARCH.md` §6.2 与 `EventSegment.cs` 注释）：

1. **线性列表读 `Entries`，因果树读 `Children` —— 不能两边都合并再执行**（会重复展示）。
2. **段队列无上限且消费会移除 → 必须持续取段**。工具关闭时释放订阅。
3. **`Sequence` 只在当前引擎实例内单调。重建测试实例要同时重置标识。**

⚠ **另有两个已核实的实现限制**：
- **段只含本动作新增内容**，不能替代初始读状态、可恢复存档或跨端快照协议。
- **日志只读 ≠ 载荷是历史快照**。`Data` 可持卡牌/位置直接引用，延后读取会看到后续状态。⚠ **要展示"当时数值"必须由适配服务另存值**（这正是 DTO 要解决的）。

### 6.2 播放器的两条通道

```
段拉取    TakeSegments() / TryTakeSegment(out seg)   ← FIFO，取走即移除；一个引擎建议一个取段者再分发给各面板
即时回调  OnImmediateUpdate(cb) → IDisposable       ← void 委托、同步调用、全部 Emit 都收、须自行过滤；回调要短小，卸载时 Dispose
```

⚠ **即时口是 `void` 委托且同步调用** —— 耗时回调会拖慢引擎。**不要在回调里 await 或做重活**（用入队，在 UI 自己的循环里处理）。

⚠ **不要用 `Subscribe`（原 await 通知口）等 UI 动画** —— 它仍会 await，不能拿来等动画。

### 6.3 ⭐ 时序陷阱（最易翻车处）

**选目标必须先 `BeginInteraction` 展示并应答，动作才继续。**

⚠ 如果 UI 等最终事件段才弹选择框 → 引擎 `await` 选择、UI 等事件段 → **双方互等死锁**。

#### 6.3.1 OrC-KSD 的真实接口（⚠ 已核实签名，照此实现，不要自创）

⚠ **`ITargeterBridge` 有两个入口，不止一个**（`Orc-KSD/src/Orc.Game/Targeting/ITargeterBridge.cs`）：

```csharp
public interface ITargeterBridge
{
    /// ① 候选收集：执行时向流程提供"前端当前可交互的完整引用列表"
    ///    ⚠ 语义＝【完整列表，含不满足业务规则者】。未返回项与细筛淘汰项的置黑由【前端】自行负责
    ///    ⚠ 含收集需求的请求每次【恰调用一次】，出队执行时调用（保证候选新鲜度，不在构造期/出队前收集）
    ///    ⚠ 约定前端提交【干净列表】（不得含 null）
    Task<IReadOnlyList<object?>> CollectCandidatesAsync(TargetingCollectionContext context);

    /// ② 交互：Begin 请求描述 → 玩家操作（拖拽/复选/取消）→ 引擎侧 Complete(refs)/Cancel()
    ///    ⚠ 一次 Begin 完成【全部槽位】选择（多槽位场景）
    ///    ⚠ 【void，不是 Task】—— 不 await，靠 responder 回调推进
    void BeginInteraction(TargetingRequestDescription description, ITargetingResponder responder);
}

public interface ITargetingResponder
{
    /// 提交完整选择（按槽位组织）。⚠ 返回 true＝构成终局；false＝【拒绝，不构成终局，请求继续等待】（原因写留痕）
    bool Complete(string requestId, IReadOnlyDictionary<string, IReadOnlyList<Ref<Entity>>> selectionsBySlot);
    /// 混合请求（引用类＋非引用类同请求）用这个重载
    bool Complete(string requestId, IReadOnlyDictionary<string, IReadOnlyList<TargetSelection>> selectionsBySlot);
    bool Cancel(string requestId);
}
```

**由此推出的实现约束**：

| 约束 | 原因 |
|---|---|
| ⚠ **`BeginInteraction` 是 `void`，UI 绝不能 `await` 它** | 它是"通知引擎我要开始交互"，不是"等结果"。UI 弹完框就返回，引擎在等 |
| ⚠ **必须先实现 `CollectCandidatesAsync`** | 引擎会**主动问 UI 要候选列表**（含那些不满足规则的，置黑由 UI 做） |
| ⚠ **`Complete` 返回 `false` 时请求仍在等待** | UI 要能**纠正重试**或 `Cancel`。不能"提交失败就没反应" |
| ⚠ **`requestId` 必须回带**，不匹配＝违规处理（拒绝 + 留痕 + 继续等待） | 所以 UI 要把 `requestId` 存下来随选择一起提交 |
| ⚠ **终局恰好一次**，之后调用幂等忽略 | UI 重复点击不会崩，但也别指望有反馈 |

#### 6.3.2 正确时序

```
引擎侧                                          Godot UI 侧
─────────────────────────────────────────────────────────────
① CollectCandidatesAsync(context)
  │  「你现在可交互的元素有哪些？」
  │  → UI 遍历自己的节点，返回完整 uid 列表
  │  （⚠ 含不满足规则的；引擎不产置黑标记，UI 自己灰化）
  ▼
出队执行 → BeginInteraction(desc, responder)
  │  「展示 + 等待」
  │  → UI【立即】弹选择框（不 await，不等事件段）
  │  → BeginInteraction 返回 void，引擎在此 await
  ▼  （引擎挂起）
                                            用户点击
                                            → responder.Complete(reqId, slots)
                                              · true  → 引擎继续
                                              · false → 请求继续等，UI 可重试
继续执行...
EndAction() → 产出段
                                            TakeSegments() → 播放动画
```

```csharp
// Godot 侧实现要点
public Task<IReadOnlyList<object?>> CollectCandidatesAsync(TargetingCollectionContext ctx)
{
    // 返回【完整】列表 —— 含不满足业务规则者
    // 未返回项的置黑由前端自己负责
    return Task.FromResult(_units
        .Where(n => n.Visible)
        .Select(n => (object?)n.Uid)
        .ToList());
}

public void BeginInteraction(TargetingRequestDescription desc, ITargetingResponder responder)
{
    // ⚠ void —— 弹框后立刻返回，不要 await
    // ① 立刻弹选择框（❌ 不等事件段）
    var node = ChoiceUi.Show(desc);         // 同步建 UI
    // ② 存 requestId（Complete 必须回带，不匹配＝违规处理）
    var reqId = desc.RequestId;
    // ③ 用户交互 → 提交
    node.Submitted += uids =>
    {
        var ok = responder.Complete(reqId, new Dictionary<string, IReadOnlyList<Ref<Entity>>>
        {
            [desc.Slots[0].Name] = uids.Select(ToRef).ToList()
        });
        // ⚠ ok==false 时请求【继续等待】—— 留在界面上让用户重试，或引导取消
        if (!ok) node.ShowRejectHint();      // 原因引擎已写留痕
    };
    node.Cancelled += () => responder.Cancel(reqId);
}
```

⚠ **OrC-KSD 侧已核实没有 `Task.Run` / `new Thread`**（全异步走 `await`，`lock` 只在配置期）→ **与 Godot 主线程 SynchronizationContext 天然兼容**。⚠ 但 Godot 侧**绝不能用阻塞式 `WaitForResult`**。

---

## 7. 阶段 F：图鉴/收藏屏

纯静态数据，**不依赖引擎**，可独立完成。

```
数据源：dist/win-unpacked/resources/app/game/data/nations/*.json（11 个文件，315 张卡）
卡图：  同目录树 ../{USG,UN,av76,deran,星盟,牌！Q!!!}/  共 576 张 PNG（500×701）
```

⚠ **卡池数据的结构坑（已实测）**：`cards` 是**以卡 id 为键的对象**，不是数组。C# 反序列化要用 `Dictionary<string, CardJson>`，**别用 `List<>`**。

⚠ **`art` 字段是相对路径**（`"../USG/commands/_1.png"`），从 `nations/*.json` 所在目录算起。

筛选维度（数据里都有）：国别 · 卡类（unit/order/counter）· 兵种 · 稀有度 · 词条。

---

## 8. 交付与验收

### 8.1 每个阶段的可验证断言

| 阶段 | 验收方式 |
|---|---|
| **A** DTO | C# 项目能编译；`README.md` 提案已成文；**提案已发出并等回复** |
| **B** 骨架 | Godot 编辑器打开无报错；7 个 screen 可切换；`in-battle` 等价生效 |
| **C** 卡牌组件 | 加载 576 张卡图不 OOM；四种稀有度边框正确；词条参值显示 |
| **D** 动画 | §5.6 的 **9 条断言逐条过**（写成一个 checklist 文件，勾选） |
| **E** 事件段 | ⭐ **必须用 mock**：手写假 `EventSegment` 喂给播放器，验证"Entries 不与 Children 重复执行"、"段按 Sequence 排序"、"Dispose 后不再收回调"。**引擎未就绪时，这是唯一能测的办法** |
| **E2** Targeter 桥 | ⭐ 用 mock 的 `ITargeterBridge` 驱动：验证候选收集返回完整列表、`BeginInteraction` 不阻塞、`Complete` 返回 false 时 UI 留在等待态、错误 `requestId` 被拒 |
| **F** 图鉴 | 315 张卡全部可列出、可筛、可看大图 |

### 8.2 ⚠ 关于自动化测试

**这个项目没有 UI 自动化测试的基础设施**（MEMORY 记：「UI 层零自动化保护」）。所以：

- 阶段 D 的 9 条断言**写成手动 checklist**（一个 `.md`，逐条勾 + 记录时长/数值）
- ⚠ **不要为了"有测试"而新写一套 UI 自动化框架** —— 违反移植纪律第一条（不要新写测试当验收标准）。UI 的验收就是手动 checklist + 目视。
- **但 DTO 消费层要写单元测试**（纯 C#，无需 UI）—— 那部分是可以自动化的。

---

## 9. 已知陷阱清单（全部来自实测或源码注释）

| # | 陷阱 | 出处 |
|---|---|---|
| 1 | 🔴 **不要在 `H:\Working Folder\` 下建 Godot 项目** —— 该目录不能可靠跑 .NET 子进程 | 本文 §3.1，实测 `hostfxr 0x80070005` |
| 2 | 🔴 **`gl_compatibility` 渲染器下 `GPUParticles2D` 静默失效** | `GODOT_PORT_CONSTRAINTS.md` §4 |
| 3 | 🔴 **`die()` 必须在移除元素前调用** | `animate.js:909` |
| 4 | 🔴 **`lunge` 不产生伤害**，最后一发抵达时结算一次 | `animate.js:707` |
| 5 | 🔴 **同线无位移 = 静止**，别强行加动画 | `animate.js:294` |
| 6 | 🟡 **`reducedMotion` 不是"全关"**：停位移/碎屑，不停几何/音效/卡面揭示 | `animate.js:56-60` |
| 7 | 🟡 **`ms()` 下限 30ms**，防极快档压成 0 帧 | `animate.js:40` |
| 8 | 🟡 **弧线要"绕过"不是"穿过"**，推出量随长度增长但设上限 | `animate.js:307-310` |
| 9 | 🟡 **击点时机 = `dur`**，不是 `dur-120` | `animate.js:621` |
| 10 | 🟡 **`Entries` 与 `Children` 不要都执行** | `EventSegment.cs` 注释 |
| 11 | 🟡 **日志的 `Data` 是活引用**，延后读会看到后续状态 | `ORC_KSD_MIGRATION_RESEARCH.md` §6.3 |
| 12 | 🟡 **`nations/*.json` 的 `cards` 是 id 键对象** | 实测 |
| 13 | 🟡 **同名多实例不能按 `CardId` 合并** | MEMORY §6 |
| 14 | 🔴 **`BeginInteraction` 是 `void` 不是 Task**，UI 绝不能 `await` 它 —— 弹框后立刻返回，引擎在等 | 已核实 `ITargeterBridge.cs:34` |
| 15 | 🔴 **必须实现 `CollectCandidatesAsync`**，引擎会主动问 UI 要候选列表（⚠ 含不满足规则的，置黑由 UI 负责） | 已核实 `ITargeterBridge.cs:26` |
| 16 | 🔴 **`Complete` 返回 `false` 时请求仍在等待**，不是失败终局 —— UI 要能重试或引导取消 | 已核实 `ITargetingResponder` |
| 17 | 🟡 **`requestId` 必须回带**，不匹配＝违规处理（拒绝 + 留痕 + 继续等待） | 已核实 `ITargetingResponder` 契约 |

---

## 10. 执行顺序与并行建议

```
第 1 天  ├─ 阶段 A：DTO 契约 + 上游提案        ← 必须最先，且要等回复
         └─ 阶段 B：Godot 项目骨架（可与 A 并行，不冲突）

第 2-3 天 ├─ 阶段 C：卡牌视觉组件
         └─ 阶段 F：图鉴屏（依赖 C 的组件）

第 4-7 天 └─ 阶段 D：动画层（60 个效果）        ← ⭐ 主体工作量，性价比最高
            先做组 1（基础装置）→ 组 4（界面反馈，最独立）→ 组 3（战斗演出）
            最后做组 2（FLIP + 瞄准箭头，因为它们是骨架，要基于前面建好的组件）

第 8 天   └─ 阶段 E：事件段播放器（用 mock 验证）

持续     └─ 提案跟进 + 手感调优（对照 Web 版逐个效果比对）
```

⚠ **动画层建议的内部顺序**：基础装置 → 界面反馈（独立、不依赖战斗状态）→ 战斗演出 → 布局位移（FLIP 需要已有组件才能测）。

---

## 11. 一句话总结

**先定 DTO 合同（并把它提给上游），然后按「骨架 → 组件 → 动画 → 事件段 → 图鉴」推进。**
**动画层是本阶段最能沉淀的资产** —— 它不依赖引擎，且是全项目唯一没有自动化验收、纯靠手感迭代的部分。**越早开做越省。**
