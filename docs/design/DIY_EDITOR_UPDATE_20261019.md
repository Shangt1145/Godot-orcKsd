# DIY 编辑器更新设计方案 · 2026-10-19

> 目标：把引擎这一轮新增的**可用语法**接进现有 DIY 编辑器，并把「能编辑」与「能进对局」这两件事在界面上分开说清楚。
> 读者：实现这套改动的开发者。所有文件路径、DTO 字段、JSON 键都可直接对照代码。

---

## 1. 结论先行

| 编号 | 要做的 | 为什么必须做 | 工作量 |
| --- | --- | --- | --- |
| **A** | 能力门（每个效果标「可进对局 / 仅草稿」＋原因） | 现在只有保存后一句"部分效果暂不能用于对局"，作者不知道是哪一条、为什么 | 小 |
| **B** | 条件面板加 `owner.controls.type` | 引擎已有原生条件（含 unitTypes/comparison/value 三个序列化维），编辑器**一个控件都没有** | 小 |
| **C** | 对局变量（`EngineVariables` 计数器） | 引擎提供对局级 key→value；编辑器零覆盖，跨卡计数只能写 csx | 中 |
| **D** | 注入时机（`injects.band`）只读展示＋校验 | 预制体已有该字段，编辑器恒发 `injects: []`，作者看不出效果挂在哪一段 | 小 |

**明确不做：wrapper 编辑。** 引擎文档《03-判定器与 wrapper》写明「数据侧不能注册/改写判定器（仅代码侧）」。目前 UI 一行 wrapper 控件都没有不是遗漏，是边界。把它做成「代码侧能力」，给可复制的 C# 片段即可，不做表单。

---

## 2. 现状：两条编译链，一个窄门

编辑器实际有三个入口，背后是两条完全不同的编译链：

```
DiyEditorScreen（卡面：名称/国家/费用/身材/词条/卡图）
   └── DiyEffectEditorScreen
        ├── Tab0 完整 Inspector（NativeEffectInspector + Friendly）
        │     └── DesktopAuthoring/js/*（V8 内嵌）→ 桌面 DSL 图
        │           └── DesktopNativeLowering.Lower(graph)      ← 闸门①
        │                 └── DesktopNativeCodegen.Compile → csx  ← 闸门②
        ├── Tab1 结构行（OrcEffectRow / OrcOpRow）
        │     └── OrcEffectEditing.ToParsed → DslOp
        │           └── OrcImportService.CompileOrcEffect → EffectSnapshot
        └── 右侧原始 DSL（只读）
```

两个闸门决定了「能不能打」：

- **闸门①** `DesktopNativeLowering.Lower` 只认 5 个顶层字段（`effect`/`trigger`/`actions`/`targets`/`selfOnly`），其余一律抛
  `"OrC-KSD 尚无无损执行适配：<路径> = <原文>。完整声明已保留在 Inspector 草稿中。"`
- **闸门②** `DesktopNativeCodegen` 逐 op 手写降级，未覆盖就 `Gap()`。

后果：**完整 Inspector 能画的东西远多于能编译的东西**。作者在友好界面里搭出十条动作，保存时才被告知"暂不能用于对局"，而错误信息统一汇总在一行提示里，定位成本极高。

结构行那条链反而更稳：它只暴露 12 个触发条目 × 17 个 op，全部走引擎原生 DslOp 编译，不会撞闸门②。

**这决定了改动的落点**：新增语法优先加在**结构行链路**（引擎原生、可持久化），而不是继续扩桌面 JS 的 DSL——后者每加一个 op 都要同时改 `primitives.js` / `effects.js` / `compiler.js` / `effect-contract.js`，且仍要过两道闸门。

---

## 3. 设计原则

1. **单一真源在桥，不在 UI。** 触发条目、op 列表、词条、兵种、条件词表都由 `OrcEffectEditing` 的静态目录给出；UI 只读目录渲染控件。新增语法同理。
2. **只暴露能往返的字段。** 已有的反例：阈值过滤因为 `FilterDto` 无该序列化维，编辑器明确不给——这个纪律要延续。
3. **"能编译"才算"能玩"。** 编辑面不承诺执行；但必须当场告诉作者当前这一条卡在哪。
4. **不改引擎。** 本轮所有新增能力都用引擎现成面表达（原生 DslOp，或桥内生成的 csx）。

---

## 4. 设计 A：能力门（Capability Gate）

### 4.1 数据流

新增 `proto/bridge/Kards.Ui.OrcBridge/DiyCapability.cs`：

```csharp
public enum DiyPlayability { Playable, DraftOnly }

public sealed record DiyCapabilityRow(int EffectIndex, DiyPlayability State, IReadOnlyList<string> Reasons);

public static class DiyCapability
{
    // 逐条效果独立探测：复用 OrcEffectEditing.Compile，不另写一套判定。
    public static IReadOnlyList<DiyCapabilityRow> Probe(DiyCardDraft draft, IReadOnlyList<UiCardDefinition> catalog);
}
```

实现要点：

- 逐条效果单独编译（现有 `Compile(string cardId, IReadOnlyList<OrcEffectDraft>)` 已经是逐条隔离，单条失败不拖垮其余）。
- `Error is null` → `Playable`；否则 `DraftOnly`，`Reasons` 收该条的错误文案。
- 卡片级失败（名称重复、费用非法等）单独一条汇总行，不摊到每个效果上。

### 4.2 界面

在 `DiyEffectEditorScreen` 的头部加一行汇总：

```
效果 3 条 · 可进对局 2 · 仅草稿 1
```

在每条效果标题右侧加徽标（沿用 `MenuStyles` 配色，不引入新色）：

| 状态 | 文案 | 颜色 |
| --- | --- | --- |
| Playable | `可对局` | `MenuStyles.Gold` |
| DraftOnly | `仅草稿` | `#dca58b`（现有错误色） |

徽标点击展开该条的 `Reasons`。保存按钮下方文案改为按状态分支：

- 全绿：`卡牌已保存，可以用于对局。`
- 有草稿：`已保存。其中 N 条效果尚不能进对局（点「仅草稿」看原因）。`

### 4.3 不做的事

不做「自动修复」。闸门报的是**结构不支持**，不是参数越界，猜一个修法只会制造第二种错误。

---

## 5. 设计 B：条件面板加 `owner.controls.type`

### 5.1 引擎侧契约（已存在，无需改动）

`Orc.Game.EffectParsing.Dsl.DslCondition.OwnerControlsType = "owner.controls.type"`，携带三个维度：

| 属性 | 序列化键 | 语义 |
| --- | --- | --- |
| `UnitTypes` | `unitTypes: string[]` | 兵种集合（空＝条件不成立） |
| `Comparison` | `comparison: string` | `>=` / `>` / `<=` / `<` / `==` |
| `Value` | `value: int` | 比较右值 |

`DslJson.ConditionDto` 三个键都已就位，编译为**真实 csx 守卫**（不是占位）。

### 5.2 DTO 改动

`proto/contracts/UiDiyCard.cs` → `OrcOpDraft` 追加三个可空字段（旧草稿反序列化不受影响）：

```csharp
/// <summary>条件：宿主控制的指定兵种集合（仅 owner.controls.type）。</summary>
public string[]? CondUnitTypes { get; init; }
/// <summary>条件：比较运算（>= > &lt;= &lt; ==）。</summary>
public string? CondComparison { get; init; }
/// <summary>条件：比较右值。</summary>
public int? CondValue { get; init; }
```

`Snapshot()` 里要 `CondUnitTypes?.ToArray()`，别让数组引用逃逸——现有 `Keywords`/`OrcEffects` 已经这么做，照抄。

### 5.3 目录与编译

`OrcEffectEditing.Conditions` 追加一项：

```csharp
new(DslCondition.OwnerControlsType, "宿主控制的兵种数量…")
```

`BuildCondition` 增加分支：

```csharp
kind == DslCondition.OwnerControlsType
    ? new DslCondition(kind, null, null, op.CondUnitTypes ?? [], op.CondComparison ?? ">=", op.CondValue ?? 0)
    : ...
```

`Summarize` 追加人类可读摘要：`条件 宿主控制 ≥ 2 个步兵`。

### 5.4 参数面

`OrcOpInfo` 已是 UI 显隐的唯一依据。这里**不加新字段**——条件控件属于所有 op 共用，直接挂在 `OrcOpRow` 的既有参数网格里，显隐条件：

```
Vis(条件 == "owner.controls.type")
```

三个控件：

- 兵种：沿用 `OrcEffectEditing.UnitTypes` 生成多选（Godot 无多选下拉；用「已选 chips + ＋添加兵种」的小行，和现有 `fKeyword` 的下拉风格区分开）。
- 算子：`>= > <= < ==` 五项下拉。
- 数值：`SpinBox(0, 20)`。

---

## 6. 设计 C：对局变量（`EngineVariables`）

### 6.1 借用引擎的什么

`LogicEngine.Variables`（`Orc.Core.EngineVariables`）是**对局级** `key → object` 数据袋：`Set` / `TryGet<T>`；同 key 换类型会被拒；不进快照、不发信号、**不参与回滚**。

引擎文档明说这是 csx 才有的入口。所以本设计的实现方式是：**桥内生成 csx**，走现有 `new DslOp("csx", script: code)` 通道（`DesktopNativeLowering` 已经在用），并置 `NativeGenerated: true`（否则 `CompileOrcEffect` 会把 csx 当"需要补全脚本"报错）。

### 6.2 面向作者的概念：计数器

不暴露 `Key`/`Type` 的自由表单，只暴露**一个计数器**：

- 标识：作者填一个中文名（如「阵亡数」）；
- 桥内映射为 `diy.counter.<slug>`，类型固定 `int`，缺失按 0 读；
- 前缀与固定类型由桥强制，避免同 key 换类型导致 `InvalidOperationException`。

DTO 追加两个可空字段（`OrcOpDraft`）：

```csharp
/// <summary>计数器标识（仅 counter.* 与"数值来自计数器"的 op）。</summary>
public string? Counter { get; init; }
/// <summary>数值来源是否为计数器（damage/buff 的 amount 从变量读，而非常量）。</summary>
public bool AmountFromCounter { get; init; }
```

### 6.3 三项能力（P1 范围）

| 作者写法 | 生成的 csx（示意） |
| --- | --- |
| 计数器「阵亡数」+1 | `var v=ctx.Engine!.Variables; v.TryGet<int>("diy.counter.阵亡数",out var c); v.Set("diy.counter.阵亡数",c+1);` |
| 计数器 −1 | 同上，`c-1`，最低钳到 0 |
| 按计数器造成伤害 | `foreach(var t in await runtime.SelectAsync(...)) { v.TryGet<int>(...); await runtime.DamageAsync(t, c, orcSelf, ct); }` |

**P1 只对 `damage` 与 `buff` 两个 op 开放"数值来自计数器"**。其余 op 的数值仍走引擎原生 DslOp——宁可少给，也不给一个"看起来能选、保存后变草稿"的控件。

### 6.4 不新增持久化字段

不存「本卡声明了哪些计数器」。变量索引从 **ops 反推**：

```csharp
IReadOnlyList<string> CountersIn(OrcEffectDraft[] effects) =>
    effects.SelectMany(e => e.Ops)
           .Where(o => o.Op.StartsWith("counter.") || o.AmountFromCounter is not null)
           .Select(o => o.Counter!).Distinct().ToArray();
```

界面上在「条件与变量」区展示本卡用到的计数器列表，支持**重命名**（批量改写 ops 里的引用）。这样草稿 schema 不膨胀，导入导出也不用升版本号。

### 6.5 明确告知的副作用

变量不进快照 ⇒ 读档 / 回放 / 悔棋不会恢复计数器。这一点必须写在作者可见的提示里，不能只写在文档：

> 对局变量只在本局内存里累计，不随读档或回放恢复。

---

## 7. 设计 D：注入时机（`injects.band`）

### 7.1 现状

`OrcImportService.CompileOrcEffect` 合成的预制体里 `injects = Array.Empty<object>()`，恒空。结构行编辑面没有"注入"概念，所有效果都是自带触发器。

### 7.2 引擎语义（按文档）

| 字段 | 取值 |
| --- | --- |
| `target` | 宿主具名触发器（如「部署触发器」）；未登记则回退对局级注册表 |
| `band` | 目标触发器带专门 band 方案时填成员名（如 `Counter` / `Resolve`）；空＝默认区段 |
| `event` | 本效果内的事件 id |
| `priority` | 注入后 band 内优先级 |

目标触发器无专门方案、或名不是成员 ⇒ **隔离记录**（不抛、不生效）——这个"静默不生效"正是最需要 UI 兜住的地方。

### 7.3 P2 只做两件

1. **只读展示**：若预制体 JSON 里 `injects` 非空（导入的包、手写声明），在效果卡上显示
   `注入 → 部署触发器 · Counter 段`；结构行编辑器不覆盖、不破坏。
2. **band 词表校验**：把 band 名做成桥内常量目录（`Counter` / `Resolve` / `Finalize` / `Initiate` / `Commit` / `Rewrite` / `Veto` / `Apply` / `default` / `disposing`），
   加一条单测，断言与引擎 `OrderFlowBands` / `ActionTriggerBands` / `ActionHandleBands` 的成员**逐项一致**（引擎加成员时这条测试会红，提醒同步）。

**不做自由文本填写 band。** 写错不报错的字段，做成输入框就是把 bug 外包给用户。

---

## 8. 交互与视觉

沿用项目既有语言，不引入新配色：

- 效果卡仍是 `Card()` 容器：`MenuStyles.Panel` 底 + `MenuStyles.Line` 1px 边 + 2px 圆角。
- 段落顺序调整为四段，条件与变量在动作**之上**（先定前提再定动作，和现有「时机→目标→动作」的阅读顺序一致）：

```
① 什么时候触发   （现有 FriendlyTiming）
② 生效条件       （现有 condition + 新增 owner.controls.type）
③ 对局变量       （新：本卡计数器列表 + 重命名）
④ 要做什么       （现有 FriendlyActions）
```

- 眉标用现成的 `//` 风格小字（`MenuStyles.Dim` 12px），不新造视觉组件。
- 「仅草稿」徽标必须**就在效果标题旁**，不能只在页脚——页脚提示已经证明没人看。

---

## 9. 兼容与迁移

| 项 | 影响 |
| --- | --- |
| 旧草稿反序列化 | 新增字段全部可空，无影响 |
| `DesktopJson` 路径 | 不受影响，仍是无损真源 |
| ORCDIY1 导出码 | 计数器为派生字段，不需要升版本 |
| 引擎升级 | 本方案不依赖本轮改名（`CommandCard`→`OrderCard`），但**编辑器编译前需先完成那次桥改**，否则整个 UI 起不来 |

---

### 9.1 改动文件清单

| 文件 | 改动 |
| --- | --- |
| `proto/contracts/UiDiyCard.cs` | `OrcOpDraft` 加 5 个可空字段（`CondUnitTypes`/`CondComparison`/`CondValue`/`Counter`/`AmountFromCounter`）；`Snapshot()` 深拷贝数组 |
| `proto/bridge/Kards.Ui.OrcBridge/OrcEffectEditing.cs` | `Conditions` 加 `owner.controls.type`；`BuildCondition` 分支；`Summarize` 摘要；`Ops` 加 `counter.add/sub/set` 三项 |
| `proto/bridge/Kards.Ui.OrcBridge/DiyCapability.cs` | **新增**：逐条效果可玩性探测 |
| `proto/bridge/Kards.Ui.OrcBridge/OrcCounterCodegen.cs` | **新增**：计数器动作 → csx（含数值引用分支） |
| `proto/bridge/Kards.Ui.OrcBridge/OrcImportService.cs` | `CompileOrcEffect` 接受 `injects` 参数（P2）；计数器 op 走 csx 通道并置 `NativeGenerated` |
| `proto/ui/screens/DiyEffectEditorScreen.cs` | 徽标 + 汇总行；效果卡四段布局；条件参数控件；计数器列表与重命名 |
| `proto/ui/screens/NativeEffectInspector.Friendly.cs` | `FriendlyValue` 的 `conditions` 分支识别新 kind（只读展示即可） |
| `tests/` | 能力门、条件往返、band 词表一致性、变量不恢复四条用例 |
| `proto/ui/screens/Main.DiyVerification.cs` | 新的 `--verify-diy-*` 探针入口 |

## 10. 分期与验收

| 期 | 内容 | 验收 |
| --- | --- | --- |
| **P0** | 能力门 A ＋ 条件 B | ① `DiyCapability.Probe` 单测：可编译 1 条、不可编译 1 条，状态与原因正确；② 一条 `owner.controls.type` 效果编译后 JSON 含 `unitTypes`/`comparison`/`value`，往返不丢；③ `--verify-ui` 绿；④ 截图确认徽标可见 |
| **P1** | 对局变量 C | ① 计数器 +1 生成的 csx 能实例化（`DynamicEffectFactory` 成功）；② 读档后计数器归零的行为有测试固化（防止有人改成"进快照"）；③ 实机一局：打出卡→计数→按计数伤害，伤害数字与计数一致 |
| **P2** | 注入时机 D | ① band 词表与引擎枚举逐项一致的断言；② 导入一个带 `injects` 的包，效果卡正确显示注入目标；③ 非法 band 在 UI 上被标红（不靠引擎静默隔离） |

三条通用门禁（沿用项目铁律）：

- `--verify-ui` 不算实机测试；进对局的结论必须来自 `-- --capture-real` 的帧度量。
- 改动涉及 `Draw`/多边形/裁剪 的必须图形冒烟（本方案不涉及，但徽标绘制若走 `_Draw` 则适用）。
- 每条结论要有证据文件，别用"看起来对了"结案。

---

## 11. 留给作者的三条硬提示（要进界面，不只进文档）

1. **能编辑 ≠ 能对局。** 徽标就是这句话。
2. **对局变量不恢复。** 读档/回放后从 0 开始。
3. **band 写错不报错、只不生效。** 所以只给词表，不给自由文本。

---

## 12. 待决策（实现前需要拍板）

| # | 问题 | 选项 |
| --- | --- | --- |
| 1 | 计数器命名要不要允许中文？ | A. 允许（`diy.counter.阵亡数`，可读） B. 强制 slug（更安全，但作者要记两个名字） |
| 2 | P1「按计数器造成伤害」是否同时开放 `buff`？ | A. 先只做 damage（最小面） B. damage + buff（多覆盖，代码生成多一条分支） |
| 3 | 能力门是否也覆盖**旧 `Effects`（九宫格简易行）**？ | A. 覆盖（更统一） B. 只覆盖 `OrcEffects`（旧行本就标注"表达不了 21 动作面"） |
