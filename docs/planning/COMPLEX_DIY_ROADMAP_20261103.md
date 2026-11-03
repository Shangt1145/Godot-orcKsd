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

### M1 打通最小闭环（不含 UI 改动）

**做什么**：在 `NativeWorkshop` 里加两个方法——
`CommitHandlerDsl(name, json)` 与 `CommitModingDraft(draftJson)`，再接 `FlattenAndWrite`。
用一张**真样例卡**（选一个自带效果做父）走一遍：写 DSL → 写草案 → 摊平 → 开局 → 打出这张牌。

**验收**：一个测试，断言①产物里 `members[].generated.dslHash` 存在；②开局后打出这张牌，
场上发生 DSL 里写的事（例如 `draw` 2 张）；③`ModingFlattener.Check` 报 up-to-date。

**为什么先做这段**：它证明「引擎三段真能连起来」，且完全不碰 UI。跑不通的话，后面所有 UI 工作都是白搭。

### M2 把画布接上去（保存按钮真的保存）

**做什么**：积木编辑器的「保存」调 M1 的两个方法；草案的 `extends`／`mounts` 用下拉选
（父效果来自 `DescribePrefab` 清单，挂载信号来自 `callable`／`hostable` 成员表）；
保存后立刻 `Check` 一次，把「草案落后于父」显示成一条警告。

**验收**：在游戏里新建一个效果、搭 3 块积木、保存、退出工坊、重进——画布还原；开局打出这张牌真的生效。

### M3 让目录与视图变成真的

**做什么**：`HandlerDslCatalog` 改成 `HandlerCatalogFacade.GetCatalog()` 的投影（保留镜像作为离线兜底）；
视图字段改用 `DescribePrefab` 的真实 `viewFields`；补多输出 `bind` 对象形态。

**验收**：删掉镜像里一个节点，界面跟着少一个（证明不是读本地表）；给成员加一个视图字段，
`write` 的下拉里立刻出现它。

### M4 把契约钉死

**做什么**：把 27 个 check 向量做成数据驱动测试；模板库生成出来（引擎侧 `ORC_WRITE_TEMPLATES=1`），
接一个「从模板新建」。

**验收**：向量全绿；模板列表非空。

## 4 先做哪一段

**M1**。理由只有一条：它是唯一能证明「这条路真的通」的段落，而且不依赖任何 UI 决策。
M2 之后的每一步都要先看到 M1 绿了才有意义。

## 5 已知的坑（提前说，别到时候才发现）

1. **v3/v4 双轨**：编辑面只索引 v3 `effects/*.prefab.json`，装载面只认 v4 `effects-v4/*.component.json`，
   中间靠 `MigrateEffectsToV4` 补桥。复杂 DIY 走的是**草案 → 摊平**这条路，产物直接落 v4，
   不要顺手去改 v3 的规则 DSL——那套表达不了 handler。
2. **草案必须有父**：`asset.extends` 必填，没有它就不是草案而是组件资产。所以「从零做一个效果」
   得先有一个可 `extends` 的父（自带效果，或一个只声明 `requires-host` 的空壳父）。
3. **`ask` 的 prompt 仍是死的**：引擎 Q7 明确把 `ask.prompt` 排除在外，作者填的那句对 `ask` 无效。
   界面上要么如实说，要么等引擎补。
4. **DSL 不是资产**：不要给它编 id、不要期待版本号——`Commit` 立刻成功、lock 不动，这是设计不是 bug。
