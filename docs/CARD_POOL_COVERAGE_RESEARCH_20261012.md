# 卡池覆盖率调研（2026-10-12）

版本基线：UI `ff0cd53`（alpha.22 收口），引擎 `7540b43`（＝`origin/main`，无新版本）。

## 结论先行

审核支持集从 21 张扩到 **约 80 张**不需要动引擎：**45 张卡的效果只含引擎已有原生 op 模板的非交互原语**，走"源声明 → 引擎 DSL 转换 → 逐卡审核准入"即可；另有 16 张需要接交互选靶、14 张无效果只需审核准入。真正的引擎缺口只有 summon/develop/hqEnchant 等少数原语。

## 现状链路（谁挡住了谁）

`VerifiedCardPool.Compile` 的准入顺序（`proto/bridge/Kards.Ui.OrcBridge/VerifiedCardPool.cs`）：

1. `CardPoolCompiler.TryCompile` 结构编译：类型/国籍/词条/数值，任何未知都拒绝（不静默裁剪）
2. token → GeneratedOnly（84 张）
3. 源声明存在且签名一致，否则 SourceChanged
4. **源声明带 effects 且不在 `SourceEffectAssembly.Supported`（硬编码 12 个 ID）→ Unsupported** ← 最大瓶颈
5. 必须在 `Resources/verified-cards.json`（内嵌 SHA-256 审核清单）里 → 否则 Unreviewed

两条可复用的现成能力：
- `SourceEffectAssembly`：人工审核的源 DSL 适配器（12 张指令/反制），支持交互选靶（TargeterManager）与 chooseOne
- `DiyCardCompiler`（A.19）：引擎原生 DSL 编译链（EffectCompilationDriver + 内嵌引擎 op 资产），但只暴露非交互面（self/all 目标）

## 量化缺口（315 张 = 315 个"为什么进不了牌组"）

| 类别 | 张数 | 根因 / 去向 |
|---|---|---|
| token 仅生成 | 84 | 设计如此，不进正常牌组 |
| 已审核支持 | 12（+9 单位=21） | 现状 |
| **批次 1：纯非交互转换** | **45** | 效果只含 draw/buff/damage/destroy/pin/silence/costMod/gainKredits/addToHand/shuffleIn 等原语，目标全是 self/all —— 引擎 op 模板全覆盖，转 DSL 即可 |
| 批次 2：需交互选靶 | 16 | 带 ref 选靶或 chooseOne，需复用 SourceEffectAssembly 的 TargeterManager 流 |
| 无效果卡 | 14 | 只差审核准入（verified-cards.json + 签名） |
| 缺引擎 op | 21 | summon(8)/develop(6)/hqEnchant(4) 等，需引擎侧扩展，UI 不可伪装 |
| 未知词条 | 54 | 关键词映射表没有对应项（部分引擎也没实现） |
| 国籍外集合 | 52 | misc/天气/星盟/自定义/进攻模式，引擎 Faction 枚举无家可归 |

批次 1 样本：`av76/command/-6/-8/-9/-10/-14/-16/-20`、`deran/command/_1/_2/_3/_4/_5/_10/_15/_16` 等。

## 实施方案（批次 1，完全不动引擎）

在桥接层新建**源效果 → 引擎 DSL 转换器**（复用 `DiyCardCompiler` 的编译链与内嵌 op 资产）：

```
源卡 effects(JSON) → 结构识别（trigger: deploy/order → hook: unit.deployed/card.played）
  → op 映射（源词表 → 引擎 DslOp）→ EffectCompilationDriver 编译
  → 完整编译才准入（failures/needsCsx/placeholder 任一存在即拒绝）
  → 签名入 verified-cards.json → SourceEffectAssembly.Supports 放行
```

硬约束（继承 6B 约定）：
- 无法完整转换的卡显式 Unsupported，不静默裁剪
- 逐卡语义审核（对照卡面文本），SHA-256 锁源声明
- 转换器只在导入/桥接层，UI 与契约不感知
- "必须含指令"等无依据组牌限制维持现状，支持集扩大后再谈

## 验收口径

- 全量单测（当前 258/258）不回归，批次 1 每张卡有真实引擎编译/实例化断言
- `--verify-ui` 全绿；抽 3–5 张代表卡进真实对局验证效果发生（像素级证据按实机铁律）
- 卡池报告 JSON 更新，Verified 计数从 21 → 目标 ≥ 60（批次 1 + 无效果卡）

## 后续批次

- 批次 2（交互选靶）：把 TargeterManager 选靶流接进 DSL 转换产物，或扩展 SourceEffectAssembly plan 词表
- 批次 3（引擎缺口）：summon/develop/hqEnchant 需引擎新增模板，走上游补丁流程（参考 6D2 先例）
- 词条/国籍两大 blocker：先盘点 54 张未知词条的分布，引擎已实现但映射缺失的补映射表；引擎没有的进批次 3
