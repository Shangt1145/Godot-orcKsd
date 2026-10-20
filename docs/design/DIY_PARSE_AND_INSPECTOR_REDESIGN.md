# DIY 自动解析 + Inspector 重做方案

> 目标读者：Kards.Ui 维护者。范围：「规则文本 → 效果」的自动解析链，以及 DIY 效果编辑器（Inspector）。
> 基线：引擎 `6d72eac`；本文只描述**方案**，不含实现。

---

## 0. 结论先行

| 项 | 现状 | 目标 |
| --- | --- | --- |
| 解析 | 走 **V8 桌面 JS DSL**（`DesktopOrc.Compile` → `DesktopNativeLowering` → 逐 op 编译） | **只走**后端 `CardTextEffectCompiler`（引擎卡包管道同款） |
| Inspector | `DiyEffectEditorScreen.cs` **727 行**手写 UI，字段与 op 硬编码 | **模板驱动**：31 个触发模板 + 21 个 op 模板自动生成表单 |
| 能力面 | V8 几乎任意（嵌套/条件/变量/wrapper） | 引擎口径：**9 op × 7 选靶 × 3 期限**（收窄，但不漂移） |
| 文本↔效果 | 单向（文本→效果）；Inspector 改完文本不更新 | **双向**：新增 op→文本生成，改完立刻回写 |

**核心取舍**：用户要求"自动解析必须基于后端引擎"。代价是能力面收窄到引擎 `CardTextEffectCompiler`
支持的语法（9 op / 7 选靶 / 3 期限）；换来的是**解析结果与引擎卡包管道逐字节一致**，
DIY 卡不会再出现"UI 说能编译、进引擎跑不起来"。

---

## 1. 现状（实测）

### 1.1 解析链（现在）

```
卡面文本
  → OrcInspector.CompileText            (proto/bridge/Kards.Ui.OrcBridge/OrcInspector.cs)
    → DesktopOrc.Compile                (V8 跑 DesktopAuthoring/js)
      → OrcCompileResult { Effects[].DesktopJson }
        → DesktopNativeLowering.Lower   (只认 5 个顶层字段；未覆盖即 Gap)
          → OrcImportService.CompileOrcEffect(逐 op) → EffectSnapshot
```

问题：
- **不是后端引擎**：`DesktopOrc` 是前端自带的 V8 运行时，语法与引擎 `EffectParsing` 各走各的。
- **静默 Gap**：`DesktopNativeLowering` 未覆盖的字段直接丢，结果"能编译但没效果"。
- **双份真源**：卡包文本由引擎 `CardTextEffectCompiler` 解析，DIY 文本由 V8 解析，两边语法漂移。

### 1.2 后端能力边界（`Orc.Game/EffectParsing/CardTextEffectCompiler.cs`，354 行）

| 维度 | 支持 |
| --- | --- |
| op | `draw` `gainPoint` `gainSlot` `damage` `buff` `pin` `silence` `costMod` `destroy` |
| 选靶 | `self` `friendlyUnits` `enemyUnits` `friendlyUnit` `enemyUnit` `anyUnit` `bothUnits` |
| 期限 | `""`（持续） `turnEnd` `nextOwnerTurnStart` |

API 只有两个：`Parse(text, cardType) → CardTextEffectDraft[]?`、`Compile(cardType, drafts, id) → EffectSnapshot`。
`Parse` 返回 `null` = 语法漂移（生成器与解析器不同步），**这是引擎的正式口径**。

### 1.3 Inspector（现在）

`DiyEffectEditorScreen.cs` 727 行：每个 op 的表单字段手写，新增 op 要改 UI 代码；
诊断信息只有一句异常文本，无法定位到具体 op；改 op 不回写卡面文本。

### 1.4 模板体系（已存在，可直接当数据源）

- `EffectParsing/Templates/ops/*.csx.tpl`：**21 个 op 模板**，首行注释即参数 schema：
  `// [buff] attack={{attack}} defense={{defense}} until={{until}} selector(sel={{sel}} side={{side}})`
- `EffectParsing/Templates/*.tpl.json`：**31 个触发模板**，含 `hooks` / `slots` / `root.mainTrigger`

**结论**：Inspector 不需要新造 schema，从模板提取即可。

---

## 2. 目标架构

```
             ┌──────────────── 卡面文本 ────────────────┐
             │                                          │
       (解析) ▼                                          ▲ (生成，新)
   CardTextEffectCompiler.Parse              OpTextWriter.Generate
             │                                          │
             ▼                                          │
     CardTextEffectDraft[]  ◄──── Inspector 表单编辑 ───┘
             │                          ▲
             │ (编译)                    │ 模板驱动
             ▼                          │
   CardTextEffectCompiler.Compile   OpTemplateCatalog(21) + TriggerTemplates(31)
             │
             ▼
      EffectSnapshot → 卡文件 effects 组件 / 对局 AddEffect
```

三条铁律：
1. **解析与编译只调用后端**，前端不实现任何语法解析。
2. **Inspector 的表单结构从模板生成**，新增 op 不改 UI 代码。
3. **任何"引擎不支持"都必须显式呈现**，禁止静默降级或丢弃。

---

## 3. 解析层

### 3.1 主链替换

`OrcInspector.CompileText` 改为：

```csharp
// 伪代码
var drafts = CardTextEffectCompiler.Parse(text, cardType);
if (drafts is null) return Unsupported("引擎不支持的语法：<定位到的片段>");
var snapshot = CardTextEffectCompiler.Compile(cardType, drafts, compileId);
```

V8（`DesktopOrc` / `DesktopNativeLowering`）**保留但不再进入解析主链**，仅用于读取旧卡的
`DesktopJson`（见 3.3）。

### 3.2 能力边界与错误

| 情况 | 行为 |
| --- | --- |
| 文本可解析 | 正常出效果，Inspector 显示结构化 op |
| 文本不可解析（`Parse` 返回 null） | **不报"解析失败"了事**：标出无法识别的子句，提示"引擎不支持该语法"，给出「手动搭建」入口 |
| 语法合法但编译失败（`Compile` 抛） | 错误挂在对应 op 卡片上，高亮该行 |
| 文本为空 | 空效果集（合法，白板卡） |

**新增**：一份「引擎支持语法」速查（9 op × 7 选靶 × 3 期限 的中文对照），直接在编辑页展示，
用户写文本时就知道边界，不用撞墙。

### 3.3 旧卡兼容

已保存的 DIY 卡若带 `DesktopJson`（V8 产物）：
- **只读保留**，不自动重解析（重解析可能丢能力）。
- 编辑页顶部提示"这是旧版效果（V8 语法），改用引擎语法可获得一致性"，提供**一次性转换**按钮（尽力解析，失败保留原样）。

---

## 4. Inspector 重做

### 4.1 三栏布局

```
┌────────────┬────────────────────────────┬──────────────┐
│ 效果列表    │ op 卡片流                   │ 卡面预览      │
│            │                            │ + 文本        │
│ ▸ 部署      │ ① 抽牌  [数量 2 ▲▼]        │ + 诊断        │
│ ▸ 阵亡      │ ② 伤害  [选靶 enemyUnits]  │              │
│ + 添加触发  │    [数量 1 ▲▼]             │ 实时文本：    │
│            │ + 添加 op                  │ 部署：抽2张… │
└────────────┴────────────────────────────┴──────────────┘
```

- 左：效果列表，每条 = 一个触发（31 选 1）+ op 序列；拖拽排序。
- 中：op 卡片，参数控件由模板 schema 生成。
- 右：卡面（复用 `CardFace`）+ 生成的规则文本 + 诊断列表。

### 4.2 表单生成规则

从模板首行注释提取 `[op] p1={{p1}} p2={{p2}} ...`，参数类型由一份**类型旁表**决定：

```json
// proto/ui/data/effect-params.schema.json（新增，一次性）
{
  "amount": "int", "count": "int", "attack": "int", "defense": "int",
  "until": { "enum": ["", "turnEnd", "nextOwnerTurnStart"] },
  "sel": { "enum": ["self","friendlyUnits",...] },
  "side": { "enum": ["friendly","enemy","any"] },
  "field": "string", "name": "string", "filter": "string"
}
```

映射：`int`→SpinBox，`enum`→OptionButton（中文标签），`string`→LineEdit，缺省→LineEdit。
**新增 op 时只补旁表，不碰 UI 代码。**

### 4.3 诊断与定位

后端 `Compile` 的异常消息往往只带 op 名（如 `[buff] attack=...`）——解析出 op 名后
**定位到具体 op 卡片并高亮**，而不是弹一个全局红字。

### 4.4 文本反向生成（新增）

写一个 `OpTextWriter`：draft[] → 中文卡面文本（9 op 的模板句）。
用途：
1. Inspector 改完 op → 立即回写 `EffectText`，用户可读；
2. **闭环校验**：`Generate → Parse → 与原 draft 比对`，不一致即为漂移（进测试）。

### 4.5 与现有 DIY 编辑页的关系

`DiyEditorScreen` 保留（卡牌属性/卡图/词条/风味文本），只把「效果」区替换为新的 Inspector 入口；
`DiyEffectEditorScreen`（727 行）**整体替换**为 `EffectInspector`（模板驱动）。

---

## 5. 分阶段落地

| 阶段 | 内容 | 验收 |
| --- | --- | --- |
| **P0** | 解析切后端：`OrcInspector.CompileText` 改调 `CardTextEffectCompiler`；不可解析时给明确提示 | 同一段文本，DIY 解析结果与引擎卡包管道**逐字节一致**（测试比对 `EffectSnapshot`） |
| **P1** | 提取模板 schema：启动时读 21 op 模板 + 31 触发模板 + 类型旁表 | 打印出完整 op/触发清单，无缺项 |
| **P2** | 新 Inspector 三栏 UI（列表/卡片/预览），替换 `DiyEffectEditorScreen` | 9 op × 典型参数全部可编辑并保存 |
| **P3** | `OpTextWriter` 反向生成 + 闭环校验 | `Generate(Parse(text)) ≈ text`（允许措辞差异，op 序列必须一致） |
| **P4** | 旧卡兼容与一次转换；能力边界速查表；文档 | 旧 DesktopJson 卡不丢、不崩；新卡全走引擎链 |

**P0 是第一优先级**：不依赖任何 UI 改动，就能消除"双份真源"。

---

## 6. 验收标准

1. **一致性**：`DIY 解析(text) 的 EffectSnapshot` == `卡包管道解析同一 text 的 EffectSnapshot`。
2. **边界**：V8 能解析但引擎不支持 → 界面明确说"引擎不支持"，不产出半成品效果。
3. **模板驱动**：删掉所有 op 表单代码后，仅靠模板仍能编辑全部 21 个 op。
4. **闭环**：Inspector 任意组合编辑 → 保存 → 重开 → 文本与 op 序列不变。
5. **回归**：`--verify-diy`、`--verify-workshop`、`--verify-ui` 全绿；既有 DIY 卡可正常试打。

## 7. 风险与取舍

| 风险 | 说明 | 应对 |
| --- | --- | --- |
| 能力收窄 | V8 可表达条件/嵌套/wrapper，引擎文本语法只有 9 op | 明确告知边界；复杂效果走「组件化 op 序列」或后续推动后端扩语法，不私自扩展前端 |
| 旧卡不兼容 | 旧卡带 `DesktopJson` | 只读保留 + 一次性转换，不做静默重解析 |
| 模板参数类型不准 | 注释头只有名字没有类型 | 类型旁表人工维护（21 op，一次性成本低）；新增 op 时补一行 |
| 文本生成措辞与用户原文不一致 | 反向生成是模板句 | 以 op 序列为准，措辞差异不视为错误；只在 UI 提示"文本已按效果重写" |

---

## 附：本方案依据的实测事实

- `OrcInspector.CompileText` 走 `DesktopOrc.Compile`（V8），链路见 `proto/bridge/Kards.Ui.OrcBridge/OrcInspector.cs:41`。
- 后端 `CardTextEffectCompiler`：`src/Orc.Game/EffectParsing/CardTextEffectCompiler.cs`，354 行，
  `Parse`/`Compile` 两个 public 入口，9 op / 7 选靶 / 3 期限。
- op 模板 21 个、触发模板 31 个，首行注释即参数 schema。
- 现有 DIY 效果编辑器 `DiyEffectEditorScreen.cs` 727 行，字段硬编码。
