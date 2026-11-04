# 复杂 DIY 路线图：从「能画」到「能跑」（2026-11-03）

> 目标一句话：**玩家在工坊里搭出条件／循环／遍历／选项／只读查询，存盘，开局真跑——不碰一行 csx。**
> 这份文档只回答三个问题：现在到哪了、还差哪几段、先做哪一段。

---

## 1 链路长什么样

```
作者（积木画布）
   │  ① 写 DSL 文档        editor.Commit("handler-dsl/x.hdsl.json", json)
   ▼
handler-dsl/x.hdsl.json        ← 不是资产：无 id、无版本、不进 lock（引擎 D11）
   │  ② 草案引用它          members[].handler = { id, entry, dsl, dslHash? }
   ▼
modings/x.draft.moding.json    ← 作者编辑的**永远是草案**，不是父资产
   │  ③ 摊平               ModingFlattener.FlattenAndWrite(packRoot, draftPath)
   ▼
effects-v4/x.component.json    ← 产物：csx 由 DSL **现场生成**，附 HandlerGeneratedBlock（双哈希＋行号映射）
   │  ④ 装载
   ▼
对局真的执行
```

引擎侧 ①②③④ **全齐**（`AssetEditorSession` 的提交白名单已含 `*.hdsl.json`；`ComponentHandlerAsset.Dsl`；
`ModingFlattener`；`HandlerGeneratedBlock`）。我们这侧只做了「画 + 校验 + 生成 JSON」，**写回一段没接**。

## 2 三张现状表

### 2.1 引擎已经给的（可以直接用，不用等）

| 能力 | 入口 |
|---|---|
| DSL 文档落盘（不是资产，立刻成功、无版本冲突） | `AssetEditor.Commit("handler-dsl/x.hdsl.json", json)` |
| 草案落盘（kind＝Moding，进测试 lock） | `AssetEditor.Commit("modings/x.draft.moding.json", json)` |
| 草案 → 产物 | `ModingFlattener.FlattenAndWrite(packRoot, draftPath)`；`Check(...)` 查「草案落后于父」 |
| 成员描述（视图字段、可见性、seam、mounts） | `AssetEditor.DescribePrefab` → `prefab-description.sample.json` 的形状 |
| 目录（27 节点 ＋ 10 内建 ＋ 可读项） | `HandlerCatalogFacade.GetCatalog()` |
| 选项请求（`choice`） | 已接通（R38），走 targeter 通道，协议零变更 |
| 效果模板库（38 信号 × 4 档 ＝ 102 个） | `EffectTemplateLibrary`（**仓库里 0 个文件**，要 `ORC_WRITE_TEMPLATES=1` 生成） |

### 2.2 我们已有的

| 能力 | 位置 | 状态 |
|---|---|---|
| 积木画布（拖、嵌、删、复制） | `HandlerBlockEditorScreen` | 能用 |
| 工具箱（全部可调用节点，含「跑不起来」分区） | 同上 | 能用，今天 0 个 preview |
| 本地校验（13 码，含 `choice` 文案规则） | `HandlerDslValidator` | 能用，但向量还没接成数据驱动 |
| 目录镜像（37 节点） | `HandlerDslCatalog` | **手抄的快照**，不是引擎给的 |
| 资产提交（card／prefab） | `NativeWorkshop.Commit` | 能用 |
| v3→v4 迁移桥 | `MigrateEffectsToV4` | 能用（每次写入后补桥） |

### 2.3 缺口（按依赖排序）

| # | 缺口 | 影响 | 大小 |
|---|---|---|---|
| G1 | 没有「DSL 文档 + 草案」的写回 | **复杂 DIY 完全走不通**——画完只能导出 JSON，落不了盘 | 中 |
| G2 | 没有草案编辑器（继承父效果、选挂载信号） | 作者不知道自己的效果挂在哪、继承谁 | 中 |
| G3 | 目录/视图字段仍是硬编码 | 引脚类型、`read`／`write` 的合法性靠猜；引擎加节点我们就落后 | 小 |
| G4 | 多输出 `bind` 对象形态编不了 | 只能编辑单输出节点 | 小 |
| G5 | 27 个 check 向量没做成数据驱动 | 校验器与契约可能悄悄漂 | 小 |
| G6 | 模板库 0 个文件 | 「从模板开始」这条入口是空的 | 小（但要引擎生成） |

## 3 四段里程碑

### M1 打通最小闭环（不含 UI 改动）——**已完成（2026-11-03）**

**做什么**：在 `NativeWorkshop` 里加两个方法——
`CommitHandlerDsl(name, json)` 与 `CommitModingDraft(draftJson)`，再接 `FlattenAndWrite`。
用一张**真样例卡**（选一个自带效果做父）走一遍：写 DSL → 写草案 → 摊平 → 开局 → 打出这张牌。

**验收**：一个测试，断言①产物里 `members[].generated.dslHash` 存在；②开局后打出这张牌，
场上发生 DSL 里写的事（例如 `draw` 2 张）；③`ModingFlattener.Check` 报 up-to-date。

**实际落地**（与计划的差异都在这儿，别照计划去找 `NativeWorkshop` 上的方法）：

- 没加进 `NativeWorkshop`，另起 `HandlerDsl/HandlerDslAuthoring.cs`——那三步是纯文件与引擎 API，
  塞进工坊类就得把整个 Godot 界面拖进测试。
- 五条测试，`tests/HandlerDslAuthoringTests.cs`，全绿：
  ①产物自包含＋双哈希来源标记＋`Mounts` 恰好一条；②装载器接受且 handler 注册进运行期；
  ③`Check` 干净、改过 DSL 变陈旧；④名字带分隔符直接拒绝；⑤**端到端：开局 → 打出 → 手牌真的 +2**。
- 端到端那条逮到一个真坑：草案重复声明父效果已有的挂载 ⇒ 产物 `mounts` 出现两条 ⇒
  `draw 2` 抽 4 张。`BuildDraft` 的缺省值已改成「不声明挂载」，引擎侧建议见
  [`docs/engine/ISSUE_20261103_MOUNT_DUPLICATE.md`](../engine/ISSUE_20261103_MOUNT_DUPLICATE.md)。

**为什么先做这段**：它证明「引擎三段真能连起来」，且完全不碰 UI。跑不通的话，后面所有 UI 工作都是白搭。

### M2 把画布接上去（保存按钮真的保存）——**已完成（2026-11-03）**

**做什么**：积木编辑器的「保存」调 M1 的两个方法；草案的 `extends`／`mounts` 用下拉选
（父效果来自 `DescribePrefab` 清单，挂载信号来自 `callable`／`hostable` 成员表）；
保存后立刻 `Check` 一次，把「草案落后于父」显示成一条警告。

**验收**：在游戏里新建一个效果、搭 3 块积木、保存、退出工坊、重进——画布还原；开局打出这张牌真的生效。

**实际落地**（三处与计划不同，理由写在括号里）：

1. **父效果清单我们自己扫，没走 `DescribePrefab`**。`DescribePrefab` 要按包逐资产问引擎，
   而画布只需要「组件 id ＋ 可挂载成员 ＋ 父已经挂了哪个信号」三样，扫 `effects-v4/*.component.json`
   一次就够，也不必把引擎的编辑面拖进 UI 线程。落地为 `HandlerDslAuthoring.DescribeParents()`，
   返回 `HandlerParentOption(ParentId, Member, MountedTarget)`；**只收 `mountable` 成员**
   （改写一个没有触发点的成员＝保存成功但永远不跑）。
2. **挂载信号没有做成第二个下拉**。父效果已有的挂载会被摊平器继承，再声明一遍就是挂两遍
   （M1 逮到的那个「draw 2 抽 4」）。所以对话框只**显示**「父效果挂在哪个信号上」，不给输入框——
   少一个能写错的地方。真正需要补挂载的场景（父没挂）在清单里标成「（父效果没挂信号）」，
   等有真实需求再加。
3. **保存不拦校验**。本地 `HandlerDslValidator` 只用来提示，最终裁定在引擎（摊平器的错误原话直接显示）。
   多一道自己的闸就会出现「界面说不行、引擎说行」的分叉。

入口放在**工坊 →「词条与效果」→ 用积木搭…**：只有包内效果才走得了这条路（要一个打开的编辑会话），
卡牌草稿那边进不去。保存成功后在「检查结果」栏第一行留下
`已保存：effects-v4/<id>.component.json（产物是最新的）`。

**验收证据**：`--verify-native-workshop` 新增 `block-canvas` 段——从工坊点进去、换成一段只有 `draw 2`
的文档、开保存对话框、确认父效果列表非空、点保存、断言产物文件真的落在临时包里。
headless 与带窗口两种模式都通过，截图见 `block-canvas.png`。

**仍未做**：「草案落后于父」目前只在保存后显示，不是保存前拦截。
（「改一个已有脚本效果」已由下面的 M2.5 补上。）

### M2.5 改一个已有脚本效果——**已完成（2026-11-03）**

**问题**：M2 只能「新建」。作者想用积木重写一个已有的效果时，产物 id 不能等于父 id
（引擎 `ResolveProductId` 明确拒绝，理由见
[`ISSUE_20261103_PRODUCT_ID_OVERWRITE.md`](../engine/ISSUE_20261103_PRODUCT_ID_OVERWRITE.md)），
于是「改」退化成了「再造一个，然后自己去改卡上的 `effects.prefabs`」。

**做法**：派生 id ＋ 改写引用，等价于覆盖。

1. 产物 id ＝ `<父 id>.dsl`（确定性，作者不填）。确定性是关键：二次保存落在同一份产物上，
   不会变成 `x.dsl.dsl`。
2. 摊平后扫 `cards/*.card.json` 的 `components[effects].prefabs[]`，把父 id 改成派生 id，
   逐张走 `Commit`（卡是资产：要过 schema 校验、要 bump 版本、被拒要原样恢复）。
3. 父效果**保留**——它是摊平输入，也是「还原」的退路。
4. `DescribeParents()` 跳过带 `generated` 的产物：它是「结果」不是「基线」，
   列出来就会派生 `x.dsl.dsl`。

**顺带补的一条**：`HandlerDocument.FromJson`（`ToJson` 的严格逆）。没有它，作者第二次打开画布
看到的是样例文档，一保存就把上次的图换成样例——那不是编辑，是数据丢失。

**验收**：`HandlerDslAuthoringTests` 13 条（含「覆盖之后真实对局里打出走的是积木版本」，
且这条**没有手工改卡牌 JSON**）；`--verify-native-workshop` 的 `block-replace` 段。
设计说明见 [`HANDLER_DSL_REPLACE_20261103.md`](../design/HANDLER_DSL_REPLACE_20261103.md)。

### M3 让目录与视图变成真的——**目录与视图已完成（2026-11-03），多输出 bind 未做**

**做什么**：`HandlerDslCatalog` 改成 `HandlerCatalogFacade.GetCatalog()` 的投影（保留镜像作为离线兜底）；
视图字段改用 `DescribePrefab` 的真实 `viewFields`；补多输出 `bind` 对象形态。

**验收**：删掉镜像里一个节点，界面跟着少一个（证明不是读本地表）；给成员加一个视图字段，
`write` 的下拉里立刻出现它。

**实际落地**：

1. **目录＝引擎投影，镜像退成兜底**。`HandlerCatalog.Default` 调
   `new HandlerCatalogFacade().GetCatalog()`，`HandlerCatalog.FromEngine(dto)` 单向投影；
   扫描失败（反射找不到类型／程序集被裁／缺 Roslyn）才退回 `Snapshot`，并且**照实说**
   （`DefaultFallbackReason` → 工具箱头部一条红字「目录来自本地镜像」）。
   三处刻意的不对称：
   - 内建 op 的**引脚仍取本地表**（引擎的 `BuiltInOps` 只报能力分区，没有引脚），stage 取引擎；
   - 扫描节点的引脚**一律 `Value`**：引擎的 `EnumValues` 是取值建议，不是「写成裸字符串」的授权
     （`unitsOf.side` 在 DSL 里是 `{"lit":"enemy"}`，标成文本枚举会把产物写坏）；
   - 子类型集照抄（引擎已把列表约束挂在元素上）。
2. **对照测试**：`引擎目录与本地镜像一一对应` 逐节点比引脚（名／类型／必填／枚举取值／实体子类型）、
   比 `hasAwait`／`hasSideEffect`、比可读项、比内建 op 分区。**首跑即全绿**——说明镜像当初是准的，
   现在这条测试是漂移报警器，引擎改了什么这里先红。
3. **视图字段来自父成员声明**：`HandlerDslAuthoring.ViewFieldsOf(parentId, member)` →
   `AssetEditor.DescribePrefab`（引擎会做继承展开、草案覆写、模板合并，我们不解析第二遍）。
   保存对话框里**换父效果就换视图**，`read`／`write` 的候选立刻跟着变。
   读不出来＝保持现状 + 一条警告，不清空（清空会让画布上每个读取都变红，看起来像作者的图坏了）。

**仍未做**：多输出节点的 `bind` 对象形态（`{"status": "s", "card": "c"}`）。当前只能单输出绑定，
`fetchFromDeck`／`pickHandCards` 这类两输出的节点只能取第一个输出。这条要同时改
`HandlerDslDocument` 的序列化、校验器与画布的绑定编辑，单独一段做。

### M3.5 适配 R39：父从「效果」换成「模板」——**已完成（2026-11-04）**

引擎 `4b2aa7f` 把覆写资格改成「父声明了成员空位」，`authorize-moding`／`abstract` 废止。
后果很直接：**具体效果的成员不允许再被覆写**，示例包六个组件一个空位都没有，
所以「挑一个效果当父」这条路整段断掉，9 条摊平用例同时红。

改法不是绕过，是换父：

1. **父清单改读 `EffectTemplateLibrary.Derive()`**（104 份「信号 × 守卫档」，成员恒 `onHook`）。
   模板库不落盘在任何地方，所以客户端自带生成：`EnsureTemplateLibrary()` 在保存前
   `WriteAll` 到 `<base>/effect-templates`（包根的**兄弟**目录，不进 `packs/`——否则会被当成卡包）。
2. **`Flatten` 传 `templateRoot`**（R33 的只读依赖层）：跨包继承只开在「不饱和的模板」上。
3. **草案照抄模板的 `requires-host`／`active-when`**：写错时点＝装载成功、永远不跑。
   这两个值不自己拼枚举名，直接读引擎渲染出来的那一份。
4. **覆盖模式拆成「父（模板）」与「目标（效果）」两个下拉**：产物 id 从**目标**派生，
   引用也由目标改指；`RetargetFromId` 必填，没有目标就拒绝保存（不做「假装改了」）。

顺带把目录镜像补了 3 个节点（`attackByEffect`／`attackCandidates`／`exchange`）——
对照测试首跑就红，这正是它的用途。

**仍未做**：`inline` 效果（`pack/builtin` 那种 v3 内联快照）无法被覆盖：没有字符串引用可改，
要做「内联 → 积木产物」的迁移。另见 `docs/engine/ISSUE_20261104_TEMPLATE_GAP.md`
（示例包零个可扩展资产、`Check` 缺 `templateRoot`）。

### M4 把契约钉死

**做什么**：把 27 个 check 向量做成数据驱动测试；模板库生成出来（引擎侧 `ORC_WRITE_TEMPLATES=1`），
接一个「从模板新建」。

**验收**：向量全绿；模板列表非空。

## 4 先做哪一段

**M1**。理由只有一条：它是唯一能证明「这条路真的通」的段落，而且不依赖任何 UI 决策。
M2 之后的每一步都要先看到 M1 绿了才有意义。

> 2026-11-04：R39 之后「父」只剩模板，M3.5 已完成适配（104 个模板 ＋ 覆盖模式拆成父子两个下拉）。
> 2026-11-03：M1、M2、M2.5、M3（目录与视图）都已绿。
> M1＝写回链路（`HandlerDslAuthoring` ＋ 5 条测试，含开局打出真的生效）；
> M2＝画布保存（父效果清单、保存对话框、`block-canvas` 探针段）；
> M2.5＝改一个已有效果（派生 id ＋ 改写引用，`block-replace` 探针段）；
> M3＝目录与视图改读引擎（对照测试当漂移报警器）。
> 剩下的都是「能力补全」而不是「路不通」：多输出 `bind`、模板库、check 向量数据驱动。

## 5 已知的坑（提前说，别到时候才发现）

1. **v3/v4 双轨**：编辑面只索引 v3 `effects/*.prefab.json`，装载面只认 v4 `effects-v4/*.component.json`，
   中间靠 `MigrateEffectsToV4` 补桥。复杂 DIY 走的是**草案 → 摊平**这条路，产物直接落 v4，
   不要顺手去改 v3 的规则 DSL——那套表达不了 handler。
2. **草案的父必须是模板**：`asset.extends` 必填，而且父必须把成员声明成空位（R39）。
   具体效果组件**不能**当父——覆写会被摊平器以「不是空位」拒绝。可用的父只有
   `EffectTemplateLibrary` 派生的 104 份模板。
3. **`ask` 的 prompt 仍是死的**：引擎 Q7 明确把 `ask.prompt` 排除在外，作者填的那句对 `ask` 无效。
   界面上要么如实说，要么等引擎补。
4. **DSL 不是资产**：不要给它编 id、不要期待版本号——`Commit` 立刻成功、lock 不动，这是设计不是 bug。
