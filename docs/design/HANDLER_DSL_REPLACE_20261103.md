# 改一个已有的脚本效果：为什么不是真覆盖

> 2026-11-03。对应 `HandlerDslAuthoring.Save` 的 `HandlerDslSaveMode.ReplaceExisting`。
> 前情见 [`COMPLEX_DIY_ROADMAP_20261103.md`](../planning/COMPLEX_DIY_ROADMAP_20261103.md) 的 M1／M2。

## 1 作者要什么

工坊里有一个手写的脚本效果（`effects-v4/x.component.json`，csx 文本），作者想用积木重写它。
期望只有一条：**包里引用这个效果的卡，从现在起用的是积木版本**。他不在乎 id 长什么样。

## 2 引擎不让真覆盖

`ModingFlattener.ResolveProductId`（`src/Orc.Packages/Editing/ModingFlattener.cs:872`）：

```
产物 id 不能等于父 id（'{parent}'）：moding 产物必须是新 id、不得覆盖父（Constraints「id 唯一」）。
```

三条理由都成立，不是保守：

1. **读写同一个文件**。摊平的输入是父（`extends` → `catalog.Resolve`），输出是产物。
   `FlattenAndWrite` 的原子性只保证「失败不写产物」，不保证「失败能救回父」。
   父被写坏＝这个效果彻底没了，而且没有副本。
2. **继承链会变成自引用**。产物是自包含的（`extends: null`）。父一旦被替换，下次摊平的
   `extends` 就指向**自己**，父的 `mounts` 与成员的 `handler` 会被再合并一遍——
   M1 那个「draw 2 抽 4」的翻倍，这次会发生在整份资产上。
3. **R11 第 5 条**：同 id 两副面孔（草案与产物）＝资产声明错误。

所以「真覆盖」要改引擎。按规矩没动，建议写在
[`docs/engine/ISSUE_20261103_PRODUCT_ID_OVERWRITE.md`](../engine/ISSUE_20261103_PRODUCT_ID_OVERWRITE.md)。

## 3 等价实现：派生 id ＋ 改写引用

| 步骤 | 做什么 | 为什么 |
| --- | --- | --- |
| ① 产物 id | `<父 id>.dsl`（`ReplaceProductIdOf`） | 确定性。作者不填，也不该填 |
| ② 摊平 | 草案 `extends` 仍是父，`product.id` 是派生 id | 父始终是输入，永远不被写 |
| ③ 改写引用 | 包内 `cards/*.card.json` 的 `components[effects].prefabs[]` 里父 id → 派生 id | 这一步才是「覆盖」的全部意义 |
| ④ 父保留 | 不动 `effects-v4/<父 id>.component.json` | 它是摊平输入，也是「还原」的退路 |

作者看到的结果与真覆盖一致：卡用的是积木版本。包里多了一个效果（父），但它在
`DescribeParents()` 里仍然可用——**作者随时能再改一次**。

### 3.1 为什么幂等

二次保存走**同一份** DSL（`handler-dsl/<父 id>.dsl.hdsl.json`）、**同一份**草案、**同一个**产物 id。
引用在第一次就改完了，第二次 `RetargetReferences` 返回 0 处——这不是失败，是幂等。
`x.dsl.dsl` 是这条设计要防的唯一事故，测试 `覆盖模式二次保存落在同一份产物上_引用不再变化` 钉住它。

### 3.2 为什么产物不进父效果清单

`DescribeParents()` 跳过**任一成员带 `generated`** 的组件：产物是「结果」不是「基线」。
不跳过的话，作者第二次会选产物当父 → 派生 `x.dsl.dsl` → 再选 → `x.dsl.dsl.dsl`，
而且每一层都把上一层的挂载再合并一遍。

### 3.3 引用改写走 `Commit`，不直接写文件

卡牌是资产：提交要过引擎的 schema 校验、要 bump 版本、被拒时要原样恢复。
直接 `File.WriteAllText` 会造出「界面说改了、引擎没记账」的资产。
被拒的那张卡会进 `Retarget.Failed`，保存结果里点名——**它仍然用旧效果**，作者必须知道。

顺序是刻意的：**先摊平，再改引用**。反过来会出现「卡指向还不存在的效果」的中间态，
那个中间态比「产物是孤儿」糟得多。

## 4 载入上次的图（覆盖的前提）

不载入，作者第二次进来看到的是样例文档，一保存就把上次的图换成样例——那不是编辑，是数据丢失。

所以补了 `HandlerDocument.FromJson`（`HandlerDslDocument.cs`），是 `ToJson` 的严格逆：

- 三类字段靠 **JSON 形状**区分，不靠目录：数组＝子动作、对象＝值引脚、字符串＝文本引脚。
  这样目录里没声明的字段也不会被吃掉（与 `ToJson` 的「额外声明照写」对称）。
- `choice` 的 `options`（`[{label, actions}]`）单独处理，它不是普通动作数组。
- `read.of` 的**实体表达式**形态要保住：写成 `self` 就把「读被遍历的那个单位」变成了「读自己」，
  图看起来没变、跑起来全错。
- 不认识的东西一律抛 `FormatException`，不猜。猜错的图会让作者在不知情的情况下改掉结构，
  而保存是覆盖式的。

界面上的联动（`SyncReplaceMode`）：勾上「覆盖这个效果」→ 产物 id 输入框禁用并显示派生 id →
有旧 DSL 就载入、没有就提示「保存后引用它的卡会改用它」。取消勾选时**换回一个新的随机 id**，
否则会以新建模式写出与覆盖模式同名的产物。

## 5 验证

- `HandlerDslAuthoringTests` 9 → 13 条：
  - `覆盖已有卡牌效果_卡牌引用改指产物而父效果保留`（引用真的改了、父还在）
  - `覆盖模式二次保存落在同一份产物上_引用不再变化`（幂等、id 不增殖、第二次的哈希进了产物）
  - `覆盖产物不再出现在父效果清单里`
  - `覆盖之后真实对局里打出走的是积木版本`（端到端，且**没有手工改卡牌 JSON**）
- `HandlerDslEditorTests` 36 → 39 条：样例文档往返稳定、`choice` 往返、不合法输入抛错。
- `--verify-native-workshop` 新增 `block-replace` 段：勾选后断言 id 被派生且不可编辑、
  保存后产物存在、父仍在、报告说改了卡时卡里真的指向新 id。headless 与带窗口都通过。

## 6 仍未做

1. **一个效果只能被覆盖一个成员**。草案只声明一个成员，第二次覆盖另一个成员会写同一份产物，
   把前一次的声明挤掉。多成员要等「草案里放多个成员」这条（引擎侧没有障碍，是界面没做）。
2. **覆盖不做备份**。父保留＝可还原，但如果作者删了父文件，产物仍然能跑（自包含），
   只是再也改不动了（没有基线）。是否需要「把父移到 `effects-v4/_replaced/`」可以再议。
3. **改引用会重排卡牌 JSON 的格式**（`JsonNode` 重写统一缩进）。语义无损，但 diff 会变大。
   如果要保原格式，得走文本级替换——那会在作者手写注释（JSON 没有注释）之外引入新的猜测。
