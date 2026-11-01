# Handler DSL v1 · 客户端实现交接说明

日期：2026-10-31
提出方：Godot 客户端（`Shangt1145/Godot-orcKsd`）
依据：`contracts/handler-dsl/v1/`（SPEC／FRONTEND-GUIDE／EXCLUSIONS／`catalog.snapshot.json`／19 条 check 向量），`origin/main @ f05dd0e`
引擎侧现状：本机检出在 `v4 @ 169e575`，落后 `origin/main` 3 个提交

> 这份文档按**你需要知道的**组织，不按「我做了什么」组织。
> 第 2 节是我们逐条比对契约向量的结果——**先看那个，它说明我们是不是真的跟你们一致**。
> 第 3 节是要你决策的 8 件事，其中 D1／D2／D3 会直接决定 DIY 的表达力上限。
> 第 4 节是建议你补的 4 条契约向量（都是我们实现时踩到的坑）。
> 配套的「请求清单」在同目录 `ENGINE_CHANGES_REQUESTED_20261031.md`，本文不重复它的细节，只给结论与优先级。

---

## 0 一句话

客户端已按契约做完一个**可运行的积木式 handler 编辑器**：13 个错误码、36 个节点（10 内建 ＋ 26 目录）、
导出格式、`else` 省略、`bind` 双形态约束全部对齐；`HandlerDslEditorTests` 19 条全绿。
实现过程中发现 **1 处契约自相矛盾**（`readables` 不分 owner）、**2 处表达力缺口**（读不到被遍历实体、
没有只读查询原语）、**3 处我们自己跟契约不一致**（已按契约修正，见 §2 的 015／017／`bind`）。

---

## 1 你可以立刻验证的东西

### 1.1 跑测试

```bash
dotnet test tests/Kards.Ui.Tests.csproj --filter "FullyQualifiedName~HandlerDslEditorTests"
# 已通过! - 失败: 0，通过: 19，已跳过: 0
```

这 19 条覆盖：目录快照形状（36＝10＋26，`damage` 引脚顺序）、样例导出逐字节比对、`else` 空省略、
`script` 的 `inputs`/`outputs` 必填、10 个错误码的**判定 + 节点路径**、绑定类型沿声明点传递、
`list<entity>` 接单实体引脚、实体子类型交集、以及契约向量 015／017 的复刻。

### 1.2 看界面

```bash
Godot_v4.7-stable_mono_win64_console.exe --path . -- --capture-hdsl
# → artifacts/hdsl-editor.png
```

截图里那份样例任务的 DSL 是：

```jsonc
{ "schemaVersion": 1, "actions": [
  { "op": "ask", "kind": "unit", "side": "enemy", "prompt": "选择一名敌方单位", "bind": "targets" },
  { "op": "iterate", "over": { "var": "targets" }, "as": "t", "actions": [
    { "op": "branch",
      "test": { "op": "ge", "args": [ { "read": { "of": "viewer", "path": "kredits" } }, { "lit": 5 } ] },
      "then": [ { "op": "damage", "target": { "var": "t" }, "amount": { "lit": 6 } } ],
      "else": [ { "op": "damage", "target": { "var": "t" }, "amount": { "lit": 3 } } ] },
    { "op": "draw", "count": { "lit": 1 } } ] } ] }
```

### 1.3 代码在哪

| 文件 | 作用 |
|---|---|
| `proto/bridge/Kards.Ui.OrcBridge/HandlerDsl/HandlerDslCatalog.cs` | 目录镜像（`HandlerCatalogNodeDto` 的形状），36 个 spec ＋ 9 个可读项 |
| `.../HandlerDslDocument.cs` | 值（5 变体）／节点／文档；`HandlerType` 静态类型 |
| `.../HandlerDslValidator.cs` | 13 码校验器，带节点路径 |
| `.../HandlerVocabulary.cs` | 中文词表（**临时方案**，见 D5） |
| `proto/ui/screens/HandlerBlockEditorScreen.cs` | 积木编辑面 |
| `proto/ui/components/BlockShape.cs` | 积木绘制（缺口／凸榫／C 形脊线） |
| `tests/HandlerDslEditorTests.cs` | 19 条 |

---

## 2 契约一致性：19 条 check 向量逐条对照

**方法说明（重要）**：客户端目前**没有 JSON → 模型的装载器**（只有导出），所以这 19 条是**逐条人工比对**，
不是自动化跑出来的。补装载器是我们自己的待办（§6.4），不是你的问题。

| 向量 | 期望 | 我们 | 备注 |
|---|---|---|---|
| 001 `else-empty` | `graph-shape` | ⚠️ 不报 | 我们导出时**省略**空 `else`，不产出这种文档；校验器不会把它判成 `graph-shape`（刻意放宽，见 §5.1） |
| 002 `entity-pin-fed-number` | `pin-type` | ✅ | |
| 003 `expr-op-unknown` | `expr-op-unknown` | ✅ | 除法/取模/幂一律拒 |
| 004 `loop-missing-times` | `loop-unbounded` | ✅ | |
| 005 `bind-dual-form` | `bind-scope` | ⚠️ 不适用 | 我们只产出字符串形态，产不出对象形态（§5.2） |
| 006 `node-unknown` | `node-unknown` | ✅ | |
| 007 `pin-unknown` | `pin-unknown` | ✅ | |
| 008 `pin-missing` | `pin-missing` | ✅ | |
| 009 `write-forbidden` | `write-forbidden` | ✅ | 只读／不在视图里都拒 |
| 010 `ask-in-loop` | `ask-in-loop` | ✅ | |
| 011 `schema-version` | `schema-version` | ✅ | 路径 `$` |
| 012 `expr-arity` | `expr-arity` | ✅ | |
| 013 `expr-type` | `expr-type` | ✅ | `count` 接 `number` |
| 014 `entity-pin-fed-list` | `pin-type` | ✅ | 并给出「改用逐个处理」的提示 |
| 015 `bind-view-name-clash` | `bind-scope` | ✅ **本次修正** | 之前漏了「绑定名不得与视图字段同名」 |
| 016 `var-out-of-scope` | `bind-scope` | ✅ | |
| 017 `loop-times-not-number` | `loop-unbounded` | ✅ **本次修正** | 之前错报成 `pin-type` |
| 018 `graph-shape-then-missing` | `graph-shape` | ✅ | |
| 019 `pin-unknown-read-path` | `pin-unknown` | ✅ | 复用码，不新增 |

**顺带修掉的第三个不一致**：目录节点的 `bind`（SPEC §3 的 `"bind"?: "x" | { … }`）之前没被声明成引脚，
我们把自己的合法文档报成了 `pin-unknown`。现在 24 个有输出的动作节点都补上了 `bind`（可选，类型＝首个输出类型）。

---

## 3 需要你决策的（按影响排序）

| # | 事项 | 影响 | 现状 |
|---|---|---|---|
| **D1** | `readables` 不分 owner | **契约自相矛盾**，作者会看到无意义选项 | 新增发现 |
| **D2** | `read.of` 读不到被遍历实体 | **DIY 表达力少一半**（「若目标防御≤2」写不出） | 原 P0-1 |
| **D3** | 没有只读查询原语 | 「每个我方单位 +1 攻击」写不出 | 原 P0-2 |
| **D4** | 引脚没有 `subtypes` | 子类型判定永不触发（代码已就绪） | 原 P1-9 |
| **D5** | 目录没有中文 `title` | 作者看到 `damage`／`fetchFromDeck` | 原 P1-3 |
| **D6** | `choice` 运行期抛异常 | 作者一点就炸 | 原 P1-5 |
| **D7** | 合并基线 ＋ 3 个 facade 签名 | 决定我们能否开始接真接口 | 原 P2-7 |
| **D8** | `DescribePrefab` 样例 | 决定 `write` 候选是否对得上 | 原 P2-8 |

### D1 `readables` 不分 owner —— 这条是契约内部矛盾，建议优先看

**现状**：SPEC §12.8 给的是**一张扁平表**，但表里混了两类东西：

| 类别 | path |
|---|---|
| 实体属性 | `attack`／`defense`／`opCost`／`deployCost`／`name`／`zone` |
| 玩家属性 | `hqHealth`／`kredits`／`maxKredits` |

而 `read.of` 只有 `self`／`viewer` 两个取值（§4.5，`HandlerDslReader.cs:56` 硬校验）。

**后果**：下面两种写法**都能通过校验**（path 存在、类型都是 `number`），但语义不成立：

```jsonc
{ "read": { "of": "self",   "path": "kredits" } }   // 单位没有「点数」
{ "read": { "of": "viewer", "path": "attack"  } }   // 玩家没有「攻击力」
```

校验器只查 path 在不在表里，查不出「这个 path 配这个 owner 不成立」。运行期拿到什么取决于实现，
契约没有定义——**这是 §12.8 与 §4.5 之间的缝**。

**前端现在的表现**（因为契约没给区分依据，只能照单全列）：取值菜单把 9 个 path 同时挂在
「我方」和「自己」两个分组下，作者会看到「读取 我方.attack」「读取 自己.kredits」这种选项。

**最小改法**（二选一，都是增量，不动 `schemaVersion`）：

```jsonc
// 方案 A：按 owner 分组
"readables": {
  "self":   { "attack": "number", "defense": "number", "opCost": "number",
              "deployCost": "number", "name": "string", "zone": "string" },
  "viewer": { "hqHealth": "number", "kredits": "number", "maxKredits": "number",
              "name": "string", "zone": "string" }
}

// 方案 B：每项声明适用 owner
"readables": {
  "attack": { "type": "number", "of": ["self"] },
  "kredits": { "type": "number", "of": ["viewer"] },
  "name":   { "type": "string", "of": ["self", "viewer"] }
}
```

改了之后：校验器能拒掉 `self.kredits`（`pin-unknown`，复用码），取值菜单能按 owner 过滤。
**在你决定之前，我们保持现状**（全列），因为猜一个过滤规则只会把合法选项藏起来。

### D2 `read.of` 读不到被遍历实体

`iterate over targets as t` 之后，没有任何表达式能读 `t` 的 `attack`／`defense`／`opCost`：

```jsonc
// 现在写不出来：若该单位防御 ≤ 2，则抽一张牌
{ "op": "iterate", "over": { "var": "targets" }, "as": "t", "actions": [
  { "op": "branch",
    "test": { "op": "le", "args": [ /* t.defense 怎么写？ */ , { "lit": 2 } ] },
    "then": [ { "op": "draw", "count": { "lit": 1 } } ] } ] }
```

`t` 只能当实体传给动作引脚，进不了比较；`script` 也救不了（v1 脚本没有值通道，见 D2 附）。

**最小改法**（推荐 A）：

```jsonc
// A：read.of 接受实体表达式（语义最直白）
{ "read": { "of": { "var": "t" }, "path": "defense" } }

// B：新增 of 取值 + 名字字段
{ "read": { "of": "var", "name": "t", "path": "defense" } }
```

两者都要动 §4.5 的封闭集与 `ReadTargets`。若想保持 v1 封闭集不变，也可只加
`"of": "entity"` ＋ `"expr"` 字段，旧文档不受影响。**前端已经预留了位置**：`HandlerValue.Read`
只存 `of` 与 `path`，`of` 改成接受任意表达式是局部改动。

### D3 没有只读查询原语

26 个目录节点**全是动作**。`iterate.over` 的来源只有两种：`ask`（必须弹窗问玩家）、
或视图里恰好声明了 `entity[]` 字段。所以「每个我方单位 +1 攻击」要么逼作者弹一次选靶，要么靠运气。

**这不是界面能绕过的**——界面再怎么设计也造不出一个不存在的原语。

建议加 1~3 个只读节点（按 §12.9 的合成节点注入即可，`hasAwait=false`、`hasSideEffect=false`、无 `bind`）：

| 节点 | 引脚 | 返回 |
|---|---|---|
| `unitsOf` | `side`(self/enemy/any)、`zone`? | `list<entity>` |
| `randomOf` | `from`(list<entity>)、`count`(number) | `list<entity>` |
| `firstOf` | `from`(list<entity>) | `entity` |

它们**必须无副作用**，否则 FRONTEND-GUIDE §8 的折叠规则会把它们判成不可复制。

### D4 引脚没有 `subtypes`

`catalog.snapshot.json` 的 26 个节点里没有任何引脚声明 `subtypes`，所以 §7.6 的
「两侧都有子类型集且交集为空＝`pin-type`」**永远命中不了**——引脚那一侧永远是「未知」。

前端已经实现了 `ask.kind` → `list<entity>` 的子类型、`iterate.as` 的元素类型传递、以及交集判定
（`HandlerDslEditorTests.实体子类型交集为空是引脚类型错误` 用一份自造目录验证过）。**只等真实数据。**

**需要你定**：是否有动作的实体引脚应当限定子类型（只收 `unit`／只收 `hq`）？
有就给一份带 `subtypes` 的目录样例；没有就明确说「v1 不设」，我们把它降级成防御性保留。

### D5 目录没有中文 `title`

`HandlerCatalogNodeDto` 只有英文 `Name`，`EnumValues` 也是英文（`unit`／`enemy`／`TurnEnd`）。
对「不懂代码的玩家」是硬伤。前端现在自己维护 `HandlerVocabulary.cs`（35 个节点的中文名、引脚名、枚举名），
但**这份词表会与目录漂移**：引擎加节点，界面就多一个英文名；引擎改引脚名，中文名对不上。

建议目录增量加两个可选字段（增量，只升 `catalogRevision`）：

```jsonc
{ "name": "damage", "title": "造成伤害", "group": "造成与消灭",
  "pins": [ { "name": "target", "title": "目标" } ] }
```

`[HandlerDslNode]`／`[HandlerDslPin]` 已有 `name`，再加 `title` 即可。

### D6 `choice` 运行期不可用 ＋ `options` 结构

- `EXCLUSIONS.md` 写明 `ChooseAsync` 抛 `NotSupportedException`（等前端桥接 S10）。前端已把 `choice`
  **从调色板隐藏**。需要确认：S10 谁做、什么时候做？短期不做的话，建议契约里标成「暂不可用」。
- `options` 是结构体数组 `[ { "label": …, "actions": [...] } ]`，不是裸动作数组。v1 界面不编辑它，
  所以如果哪天要开放 `choice`，需要一个「标签 ＋ 子体」的编辑形态——请确认这个结构是最终形态。

### D7 合并基线 ＋ 三个 facade 签名

本机引擎检出在 `v4 @ 169e575`，本地 2 个未推送提交（`affcedb`／`a134486`），
落后 `origin/main` 3 个提交（`2f101a8` HandlerDsl／`e211ede` 契约包／`f05dd0e` 交接说明）。

客户端 `Kards.Ui.OrcBridge` **直接 `ProjectReference` 引擎工程**，没有 `Orc.HandlerDsl` 就编译不过，
所以现在用的是镜像 DTO ＋ 内置目录快照。

**需要你定**：
1. `v4` 上那两个提交进 `main` 还是丢弃？（决定我们合哪个基线）
2. `HandlerCatalogFacade`／`AssetEditor.DescribePrefab`／`Commit` 的**签名是否就是 FRONTEND-GUIDE §5 写的那样**？
   我们会照那个签名写适配层，签名一变就返工。

### D8 需要一份 `DescribePrefab` 的真实样例

界面要显示「这个成员能改哪些视图字段」（`write` 候选、`write-forbidden` 的判据都靠它）。
现在客户端用的是 3 个假字段（`Card`／`Defense`／`Units`）。

**请求**：一份真实卡包 ＋ 一份 `DescribePrefab(packRoot, prefabId, draftPath)` 的**输出 JSON 样例**（脱敏也行）。

---

## 4 建议补的 4 条契约向量

都是我们实现时**真踩到**的：契约有明文规定，但没有向量守着，实现者容易读漏。

### V1 `sum` 接 `list<entity>` → `expr-type`

§5.4 说 `sum`／`max`／`min` 只接受 `list<number>`。现有 013 只覆盖了「`count` 接 `number`」，
**没覆盖「集合元素类型不对」**。我们自己的第一版就漏了这条（`list<number>` 与 `list<entity>` 互判通过）。

```jsonc
{ "op": "damage", "target": { "view": "Target" },
  "amount": { "op": "sum", "args": [ { "view": "Units" } ] } }
// Units: list<entity>  →  期望 expr-type @ actions[0].amount.args[0]
```

### V2 引脚 `subtypes` 交集为空 → `pin-type`

§7.6 有明文，无向量。需要一份**带 `subtypes` 的目录夹具**（现在所有引脚都没有）：

```jsonc
// catalog: { "name": "killHq", "inputs": [ { "name": "target", "type": "entity",
//            "subtypes": ["hq"], "required": true } ] }
{ "op": "ask", "kind": "unit", "prompt": "选单位", "bind": "us" },
{ "op": "iterate", "over": { "var": "us" }, "as": "u", "actions": [
  { "op": "killHq", "target": { "var": "u" } } ] }
// ask.kind=unit → u 是 entity(unit)，与 {hq} 交集为空
// → 期望 pin-type @ actions[1].actions[0].target
```

### V3 `read.of` × `path` 组合非法 → `pin-unknown`

若采纳 D1：

```jsonc
{ "op": "write", "field": "Defense", "value": { "read": { "of": "self", "path": "kredits" } } }
// 期望 pin-unknown @ actions[0].value.read.path（self 没有 kredits）
```

### V4 `script` 缺 `inputs`／`outputs` → `pin-missing`

§3 字段表把 `script` 的 `inputs`／`outputs` 列为**必填**（即使为空数组）。**我们踩过**：
第一版导出 `{"op":"script","body":"…"}` 漏了这两个字段，后端会按 `pin-missing` 拒绝。
现在前端强制补空数组，但契约里没有向量守这条。

```jsonc
{ "op": "script", "body": "return;" }
// 期望 pin-missing @ actions[0].inputs（或 .outputs）
```

---

## 5 我们刻意做的规范化（与后端语义不同，但不会产出非法文档）

这三条请确认可接受；如果哪条你不认同，我们改前端。

### 5.1 空 `else` 一律省略

向量 001 要求显式空 `else` 被拒（`graph-shape`）。前端的做法是：**保存时把空 `else` 整段删掉**，
所以从我们这儿出去的文档永远不含空 `else`。代价是：装载别人手写的含空 `else` 文档时，
我们会静默丢掉它而不是报错。理由是编辑器里「点了添加否则但还没放积木」是一个正常的中间态，
不该在编辑过程中一直飘红。

### 5.2 单输出 `bind` 只写字符串形态

向量 005 把对象形态判成 `bind-scope`（同一语义两种文本）。前端只产出字符串形态；
多输出节点（如 `fetchFromDeck` 的 `status`＋`card`）目前**无法分别绑定**——界面只给一个 `bind` 输入框。
如果多输出绑定是必需的，需要你确认对象形态的规范写法，我们再补界面。

### 5.3 不生成 `sequence`

`actions[]` 本身已经是序列，再包一层 `sequence` 是冗余的。前端从调色板排除了它
（`HandlerCatalog.Palette` 过滤）。如果 `sequence` 有我们没看出来的用途（比如折叠规则的锚点），
请说一声。

### 5.4 不写 `catalogRevision`

§12.7 说它只是诊断、不是门。前端不在文档里写它，避免出现「作者环境 revision 与运行期不同」的噪音。
如果你希望我们写进去（便于排查），一句话就改。

---

## 6 合并后怎么接（我们这边的工作，供你对齐预期）

### 6.1 换目录来源

```csharp
// 现在：内置镜像
private HandlerCatalog _catalog = HandlerCatalog.Snapshot;
// 合并后：运行期查询，revision 变了才重拉（§12.6）
private HandlerCatalog _catalog = await HandlerCatalogFacade.GetAsync();
```

验收：引擎加一个目录节点，界面调色板**自动多一项**，前端不改代码。

### 6.2 换视图来源

```csharp
// 现在：3 个假字段
private IReadOnlyDictionary<string, HandlerViewField> _view = DemoView();
// 合并后
private var _view = await AssetEditor.DescribePrefabAsync(packRoot, prefabId, draftPath);
```

验收：`write` 的字段候选只列 `Writable=true` 的；`write-forbidden` 与真实视图一致。

### 6.3 写回

顺序按 §6 表格第 5 步：**先 DSL 后 moding**，失败可回滚，不产生半截数据。
验收：保存失败时草稿保持原样。

### 6.4 我们自己的待办（不需要你）

写一个 JSON → 模型的**装载器**，这样 §2 的 19 条向量能自动化跑，而不是人工比对。
这是目前客户端最大的验证缺口——补上之后，契约一更新我们就能立刻知道哪里不一致。

---

## 7 附：术语对照

| 契约里的词 | 界面上叫什么 |
|---|---|
| `actions[]` | 积木列表（从上到下就是执行顺序） |
| `branch` | 如果／那么／否则（C 形块） |
| `iterate` | 逐个处理 |
| `loop` / `while` | 重复 N 次／当…时重复 |
| `ask` | 请玩家选目标 |
| `let` | 起个名字存起来 |
| `write` | 修改属性 |
| `read` | 读取属性 |
| `bind` | 结果命名 |
| 预制体成员 | 触发时机（帽块「当这个成员被调用时」） |

---

## 8 联系方式与改动范围

- 本文所有结论都基于 `origin/main @ f05dd0e` 的契约包；契约若更新，请知会一声，我们重新比对 §2。
- **客户端不会去改 `src/`、不改契约包、不 merge `v4`。** 上面 D1~D8 全部等你决定。
- 相关文档：`docs/design/INSPECTOR_SELECTION_20261031.md`（为什么选积木而不是节点图、四层原语模型）、
  `docs/engine/ENGINE_CHANGES_REQUESTED_20261031.md`（逐条请求细节）。
