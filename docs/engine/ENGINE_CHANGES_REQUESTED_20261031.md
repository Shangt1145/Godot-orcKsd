# 引擎侧需要改的东西（客户端请求清单）

日期：2026-10-31。提出方：Godot 客户端（`Shangt1145/Godot-orcKsd`）。
依据：`contracts/handler-dsl/v1/`（SPEC／FRONTEND-GUIDE／EXCLUSIONS／catalog.snapshot，`origin/main @ f05dd0e`）。

> **本文件只列请求，不代替实现。** 客户端不会去改 `src/`、不改契约包、不 merge `v4`。
> 每条都写清「为什么界面绕不过去」和「最小改法」，方便你判断值不值得做。

---

## 0 先说结论：三条最值钱

| 优先级 | 事项 | 卡住什么 |
|---|---|---|
| **P0** | `read.of` 支持实体表达式 | 所有「若目标防御 ≤ N」这类条件**写不出来**，DIY 表达力直接少一半 |
| **P0** | 提供「查某方所有单位」的只读原语 | 「每个我方单位 +1 攻击」这种最普通的效果只能靠弹窗选靶 |
| **P1** | 目录加 `title`（中文显示名） | 作者看到的是 `damage`／`fetchFromDeck`／`rerunAuras`，与「傻瓜式」目标冲突 |

其余是配合项与确认项。

---

## P0-1 `read.of` 只能 self／viewer，读不到被遍历实体

**现状**：SPEC §4.5 把 `read.of` 封闭为 `self`／`viewer`；`HandlerDslReader.cs:56` 的
`ReadTargets = { "self", "viewer" }` 硬校验。`view` 取的是当前执行帧的视图字段。

**后果**：`iterate over targets as t` 之后，没有任何表达式能读 `t` 的 `attack`／`defense`／`opCost`。
下面这条最常见的效果在 v1 写不出来：

> 对目标造成伤害；若该单位防御 ≤ 2，则抽一张牌。

`t` 只能当实体传给动作引脚，进不了比较。`script` 也救不了（见 P1-3：v1 脚本没有值通道，`t` 进不去）。

**最小改法**（二选一）：

```jsonc
// 方案 A：read 增加实体表达式形态（推荐，语义最直白）
{ "read": { "of": { "var": "t" }, "path": "defense" } }

// 方案 B：新增第三种 of 取值 + 名字字段
{ "read": { "of": "var", "name": "t", "path": "defense" } }
```

两者都要动 §4.5 的封闭集与 `ReadTargets`，属**破坏性/扩容级**变更（按 §1 需升 `schemaVersion`，
或在 v2 里做）。若你倾向先做增量，也可以只加 `"of": "entity"` ＋ `"expr"` 字段，旧文档不受影响。

---

## P0-2 没有「查询某方所有单位」的只读原语

**现状**：目录 26 个节点**全是动作**，没有 `all`／`random`／`first` 这类纯查询；
`EXCLUSIONS.md` 把 `OwnerUnits` 明确排除（理由：它是查询不是动作，且隐式宿主映射会出错——这个理由成立）。

**后果**：`iterate.over` 的来源只剩两种：
1. `ask`（**必须弹窗问玩家**）；
2. 视图里恰好声明了 `entity[]` 字段（取决于预制体的视图声明，作者不一定有）。

所以「每个我方单位 +1 攻击」在 v1 要么逼作者弹一次选靶，要么依赖视图声明碰巧给了列表。
**这不是界面能绕过的**：界面再怎么设计也造不出一个不存在的原语。

**建议**：新增 1~3 个只读查询节点（不进 `[HandlerDslActionFace]` 扫描也行，做成 §12.9 那样的合成节点）：

| 建议节点 | 引脚 | 返回 |
|---|---|---|
| `unitsOf` | `side`(enum: self/enemy/any)、`zone`?(enum) | `list<entity>` |
| `randomOf` | `from`(list<entity>)、`count`(number) | `list<entity>` |
| `firstOf` | `from`(list<entity>) | `entity` |

按 §12.9 的约定注入即可：`hasAwait=false`、`hasSideEffect=false`、无 `bind`、生成码不经 `EffectRuntime`。
它们**必须无副作用**，否则 FRONTEND-GUIDE §8 的折叠规则会把它们判成不可复制。

---

## P1-3 目录没有中文显示名

**现状**：`HandlerCatalogNodeDto` 只有 `Name`（英文 op）与引脚 `Name`；`EnumValues` 也是英文
（`unit`／`enemy`／`Permanent`／`TurnEnd`）。

**后果**：对「不懂代码的玩家」是硬伤。客户端现在自己维护了一份词表
（`HandlerVocabulary.cs`：35 个节点 + 引脚 + 枚举中文），但这份词表**会与目录漂移**——
引擎加节点，界面就多一个英文名；引擎改引脚名，中文名对不上。

**建议**：目录增量加两个可选字段（按 SPEC §1 属**增量**，只升 `catalogRevision`，旧 DSL 照常装载）：

```jsonc
{
  "name": "damage",
  "title": "造成伤害",          // 中文显示名；缺省时前端回退到 name
  "group": "造成与消灭",         // 可选：意图分组；缺省时前端按自己的表分
  "pins": [ { "name": "target", "title": "目标", ... } ]
}
```

`[HandlerDslNode]`／`[HandlerDslPin]` 已有 `name` 字段，再加 `title` 即可，标注语言不用换一套。

---

## P1-4 `script` 在 v1 没有值通道

**现状**：SPEC §3 明确 `inputs`／`outputs` 只是声明，没有「引脚值」通道；正文只能用
`view`／`orcSelf`／`ctx`／`ct` 与前面节点已建立的绑定。

**后果**：客户端把 `script` 当「逃生舱」的期待落空了——它接不住 P0-1／P0-2 的缺口，
也不能把结果交回图里。我们现在只能把它标成「高级，且不能与图交换值」。

**建议**（不着急，但要知道它现在不是逃生舱）：加 `arguments` 字段把绑定传进去，
或至少在 FRONTEND-GUIDE 里把「script 不能兜底」写成显式警告——现在读起来像是可以。

---

## P1-5 `choice` 运行期抛异常

**现状**：`EXCLUSIONS.md`「运行时能力现状」写明 `ChooseAsync` 抛 `NotSupportedException`（等前端桥接 S10）。

**后果**：作者一点就炸。客户端已把 `choice` **从调色板隐藏**（`OpenPalette` 里排除）。

**需要你确认**：S10 的交互桥接由谁做、什么时候做？如果短期内不做，
建议在契约包里把 `choice` 标成「暂不可用」，否则下一个人会以为它能跑。

---

## P2-6 入前线／入支援线还在「延后」区

`JoinFrontlineAsync`／`JoinSupportLineAsync` 返回 `ActionResult?`，`EXCLUSIONS.md` 归到 **B 类（延后）**：
补一个 `[HandlerDslResult]` 拆解就能进目录，本轮没补。

「把单位塞进战场」是核心动作，现在是**只能写脚本**。成本看起来很低，建议优先补。

---

## P2-7 确认：客户端该以哪个基线编译

**现状**：本机引擎检出在 `v4`，本地 2 个未推送提交（`affcedb`／`a134486`），
落后 `origin/main` 3 个提交（`2f101a8` HandlerDsl／`e211ede` 契约包／`f05dd0e` 交接说明）。

**后果**：客户端 `Kards.Ui.OrcBridge` 直接 `ProjectReference` 引擎工程，
没有 `Orc.HandlerDsl` 就**编译不过**。所以客户端目前用的是**镜像 DTO + 内置目录快照**
（`proto/bridge/.../HandlerDsl/HandlerDslCatalog.cs`），先跑通界面。

**需要你定**：
1. `v4` 上那两个提交是要进 `main`，还是丢弃？（决定我们合哪个基线）
2. 合并后 `HandlerCatalogFacade`／`AssetEditor.DescribePrefab`／`Commit` 的**签名是否就是
   FRONTEND-GUIDE §5 写的那样**？我们会照那个签名写适配层，签名一变就要返工。

---

## P2-8 需要一份 `DescribePrefab` 的真实样例

界面要显示「这个成员能改哪些视图字段」（`write` 节点的候选、`write-forbidden` 的判据都靠它）。
现在客户端用的是 3 个假字段（`Card`／`Defense`／`Units`）。

**请求**：给一份真实卡包 + 一份 `DescribePrefab(packRoot, prefabId, draftPath)` 的**输出 JSON 样例**
（脱敏也行），我们照着做渲染与校验；否则接上真接口时大概率字段名对不上。

---

## P1-9 确认：目录里要不要给实体引脚声明 `subtypes`

**现状**：`catalog.snapshot.json` 的 26 个节点里，**没有任何引脚带 `subtypes`**——所有实体引脚都是
裸 `entity`。SPEC §7.3 第 4 条说「`entity` 不设静态子类型，节点目录可为实体引脚声明运行期接受的
子类型集（`unit`／`hq`／`card`）」，§7.6 说「两侧都有子类型集且交集为空＝`pin-type`」。

**后果**：交集判定写好了，但**永远命中不了**——因为引脚那一侧永远是「未知」。客户端已经实现了
`ask.kind` → `list<entity>` 的子类型、`iterate.as` 的元素类型传递，也实现了交集判定
（`tests/HandlerDslEditorTests.cs` 用一份自造目录验证过）。**只等真实数据。**

**需要你定**：是否有动作的实体引脚应当限定子类型（例如只收 `unit`、或只收 `hq`）？
如果有，给一份带 `subtypes` 的 catalog 样例，我们把镜像快照对齐；如果没有、也不打算有，
请明确说「v1 不设」，我们就把这条判定降级成「仅防御性保留」。

---

## 附：客户端已经确认、不需要你改的部分

- 13 个错误码的本地实现**已全部落地**（含 `pin-type` 的实体子类型交集与列表/单实体区分）。
- 目录快照 26 节点与 `catalog.snapshot.json` 逐条对齐（`tests/HandlerDslEditorTests.cs` 守着）。
- DSL 导出格式（引脚顺序、`else` 为空整段省略、单输出 `bind` 写字符串）。
- 客户端**不生成 csx**，只产出 `.hdsl.json`（FRONTEND-GUIDE §2 的硬禁令）。
