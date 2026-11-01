# 引擎更新调研：Handler DSL 契约 2026-11-01 版

> 调研对象：`hitman-OLD6/OrC-KSD` `main` 分支 `d366c85`（2026-11-01 07:19 UTC / 15:19 北京时间）
> 对照基线：`f05dd0e`（2026-10-31 13:59 UTC），即我们上一轮逐条核过的那版契约
> 调研方式：本机 git 直连 GitHub 被挡（代理 502、加速器未运行），改从 `codeload.github.com`
> 取 `tar.gz` 全量解包，再用本地 `origin/main`（= `f05dd0e`）逐字 diff
> 结论一句话：**我们提的 D1／D2／D3／D4／D8 全部被采纳，D6／D7 也顺带答了；剩下 D5（中文标题）没动。**

---

## 1 这次更新是哪两个提交

| 提交 | 时间（UTC） | 内容 |
|---|---|---|
| `62fcabf` | 11-01 07:19:45 | feat: Handler DSL 类型校验与目录扫描完善，prefab 描述对齐（14 文件，+558/−58） |
| `d366c85` | 11-01 07:19:50 | docs: 契约包同步 DSL 校验规则与新增向量（+3206/−144） |

`62fcabf` 的改动主线（提交信息 + 实测 diff 一致）：

- `readables` 由扁平表改为「实体种类 → 可读项」两级结构
- `read.of` 从只收 `self`／`viewer` 放开为可接受任意实体表达式
- 新增 `[HandlerDslPure]` 注解与 `unitsOf` 只读查询节点
- `HandlerDslTypeChecker` / `DslExpression` 补 `sum` 数值列表、引脚子类型交集、读目标路径、
  `script` 输入输出完整性等校验
- `PrefabDescriptionTests` 新增预制体描述样例导出测试

---

## 2 我们提的 8 条决策，现在的状态

| 编号 | 事项 | 状态 | 依据 |
|---|---|---|---|
| **D1** | `readables` 不分 owner（契约自相矛盾） | ✅ **按方案 A 采纳**，但分组维度改成「实体种类」而非我们写的「self／viewer」 | SPEC §12.8 新增折算表 |
| **D2** | `read.of` 读不到被遍历实体 | ✅ **按方案 A 采纳**：`{"of": {"var":"t"}, "path":"defense"}` | SPEC §4.5 第 5 条；gen 向量 009 |
| **D3** | 没有只读查询原语 | ✅ **采纳 `unitsOf`**；`firstOf`／`randomOf` 明确不收录 | SPEC §12.10；gen 向量 010 |
| **D4** | 引脚没有 `subtypes` | ⚠️ **判定已实现，但真实目录仍然零条声明** | catalog.snapshot 实测 `subtypes` 出现 0 次 |
| **D5** | 目录没有中文 `title` | ❌ **未动**（快照节点键仍只有 name/source/hasAwait/hasSideEffect/inputs/outputs） | 实测 |
| **D6** | `choice` 运行期抛异常 | ✅ **已实现**：`HandlerDslGenerator.EmitChoice` 生成 `ChooseAsync` + `switch` | 源码实测 |
| **D7** | 合并基线 ＋ facade 签名 | ✅ **已在 FRONTEND-GUIDE §5 写明**（见下） | 文档 + 源码核对 |
| **D8** | `DescribePrefab` 样例 | ✅ **已提供** `prefab-description.sample.json`（2 成员／39 可挂载目标／75 可包装成员） | 契约包 |

### D1 采纳的方式和我们建议的不同（而且是更好的）

我们建议按 `self`／`viewer` 分组。他们按**实体种类**分组：

```jsonc
"readables": {
  "unit":   { "attack": "number", "defense": "number", "opCost": "number",
              "deployCost": "number", "name": "string", "zone": "string" },
  "hq":     { "hqHealth": "number", "name": "string" },
  "player": { "kredits": "number", "maxKredits": "number", "hqHealth": "number" }
}
```

理由写在 SPEC 里，而且是对的：`read.of` 放开成实体表达式之后，「调用者视角」这个维度就站不住了
——`{"var":"t"}` 既不是 self 也不是 viewer，但 `t` 是单位就得能读 `attack`。种类是唯一与 `of` 形态无关的维度。

折算表（前端必须照这个实现）：

| `of` 形态 | 种类集合 |
|---|---|
| `"self"` | `unit ∪ hq`（宿主是单位还是总部静态未知） |
| `"viewer"` | `player` |
| 实体表达式 | 由静态子类型决定；未知 ⇒ `unit ∪ hq` |

组合不成立（`self` + `kredits`、`viewer` + `attack`）＝`pin-unknown`，错误路径指向 **`.read.path`**。

> SPEC 自己补了一句很关键的说明：**这不是新语义**。运行期 `ReadValue` 早就按 owner 严格分区
> （`of=self` 读 `kredits` 会抛 `NotSupportedException`），v1 早期留成扁平表就留出了一条
> 「校验通过、运行期抛异常」的缝。分组只是把既有事实搬到元数据。

### D3 只给了一个原语，另外两个明确不做

`unitsOf` 的引脚与标记：

| 节点 | 引脚 | 输出 | 标记 |
|---|---|---|---|
| `unitsOf` | `side`（`string`，取值集 `self`／`enemy`／`any`，**必填**）、`zone`（`string`，可选） | `result`：`list<entity>` | `hasAwait=true`、`hasSideEffect=false` |

两点值得记住：

1. **`hasAwait=true` 是对的**，因此折叠规则判它**不可复制**——复制一份等于在两个时刻各查一次，
   要共享就写 `let`。这不是限制，是 §8.6 的本意。
2. `hasSideEffect=false` 靠新增的 `[HandlerDslPure]` 显式声明。默认规则把一切 `Task` 方法判成有副作用，
   对动作正确、对异步查询过头。SPEC 特别警告：**标错比不标更糟**（前端会允许复制一个实际不可复制的节点）。

`firstOf`／`randomOf` 明确不收录，理由写得很实在：空集时返回什么、随机两次结果不同，
在 v1 尚无自洽答案，宁可不加。

### D4 为什么还是零条

`catalog.schema.json` 里 `subtypes` 是合法引脚字段（`unit`／`hq`／`card`，`minItems: 1`），
判定也真的在跑（check 向量 021 用一份自造目录验证），但**真实目录一条都没标**：

```text
catalog.snapshot.json 里 "subtypes" 出现次数：0
27 个节点、全部引脚，带 subtypes 的：0
```

所以子类型交集判定在真实目录上**永远不会命中**（引脚侧永远「未知」＝放行）。
这是数据缺口，不是契约缺口——前端已经实现且用自造目录验证过，只等引擎在动作面上补 `[HandlerDslSubtype]`。

### D7 的签名（可以直接照抄）

```csharp
// 目录：无参构造＝扫描引擎本程序集的动作面
var catalogFacade = new HandlerCatalogFacade();
int revision = catalogFacade.CatalogRevision();      // ⚠ 是**方法**，不是属性
HandlerCatalogDto all = catalogFacade.GetCatalog();  // 同步、缓存、同 revision 同实例

// 编辑器：需要 registry ＋ 可写根策略，且**先开会话**
var editor = new AssetEditor(registry, policy, options: null);
EditorSessionInfo session = editor.BeginSession(new EditorSessionRequest(/* 包根等 */));
PrefabDescription desc = editor.DescribePrefab(packRoot, prefabAssetId, draftPath: null);
EditorCommitResult r = editor.Commit(relativePath);            // 文件指针通道
EditorCommitResult r2 = editor.Commit(relativePath, content);  // 内容通道
```

`DescribePrefab` 与 `Commit` 都是实例方法，**必须在会话内**调用。`DescribePrefab` **不抛异常**
——父未注册／包不存在／草案缺失都走 `Description.Diagnostics`。

---

## 3 契约的硬变化清单（前端必须跟着改）

| # | 变化 | 影响面 |
|---|---|---|
| 1 | `readables`：`{path → type}` → `{kind → {path → type}}` | 目录 DTO、校验器、取值菜单 |
| 2 | `read.of`：只收 `self`／`viewer` → 收字符串字面量**或**实体表达式 | 值模型、序列化、校验器、取值菜单 |
| 3 | `read.path` 不匹配目标种类 → `pin-unknown`（路径 `.read.path`） | 校验器 |
| 4 | 新增节点 `unitsOf`（目录 26 → **27** 个） | 目录镜像、词表、调色板 |
| 5 | 新增注解 `[HandlerDslPure]` | 后端专有，前端只读 `hasSideEffect` |
| 6 | `catalogRevision`：`1142203374` → **`223446220`** | 缓存失效判据 |
| 7 | 新增 `prefab-description.sample.json` | 视图字段／可写字段的真实形状 |
| 8 | check 向量 19 → **24** 条（新增 019～024） | 校验器测试 |
| 9 | gen 向量 8 → **10** 条（新增 009／010） | 后端生成，前端不用管 |

`schema.json`、`catalog.schema.json`、`EXCLUSIONS.md` **本轮零改动**——说明这是一次纯增量，
没有破坏性变更，`schemaVersion` 仍是 1。

---

## 4 新增的 6 条 check 向量（全部是我们建议的那 4 条）

| 向量 | 场景 | 期望 |
|---|---|---|
| `019-pin-unknown-read-path` | `read.path` 不在目录里 | `pin-unknown` @ `actions[0].value.read.path` |
| `020-sum-needs-number-list` | `sum` 收到 `list<entity>` | `expr-type` @ `actions[0].amount.args[0]` |
| `021-pin-subtype-empty-intersection` | 引脚 `subtypes:["hq"]` 收 `entity(unit)` | `pin-type` @ `actions[1].actions[0].target` |
| `022-read-target-path-mismatch` | `self` 读 `kredits` | `pin-unknown` @ `actions[0].value.read.path` |
| `023-script-missing-outputs` | `script` 有 `inputs` 没 `outputs` | `pin-missing` @ `actions[0].outputs` |
| `024-script-missing-inputs` | 反过来 | `pin-missing` @ `actions[0].inputs` |

019／020／021／022 正是我们上一轮建议补的四条，`script` 那条被拆成了 023／024 两条。

---

## 5 客户端已经对齐的部分

改动落在 `proto/bridge/Kards.Ui.OrcBridge/HandlerDsl/` 与 `proto/ui/`：

| 项 | 做法 |
|---|---|
| `readables` 分组 | `HandlerCatalog.Readables` 改成 `{kind → {path → type}}`；新增 `ReadableType(kinds, path)` 与 `KnowsReadable(path)`，用来区分「根本没这个 path」与「这一类读不到」 |
| `read.of` 实体表达式 | `HandlerValue.ReadTarget`（与 `ReadOf` 恰取其一）；工厂 `ReadFrom(target, path)`；导出为 `{"of": {"var":"f"}, "path":"defense"}` |
| 种类折算 | `HandlerCatalog.KindsForLiteral` + 静态子类型；未知退回 `unit∪hq` |
| 错误路径 | 未知 path／种类不匹配一律 `pin-unknown` @ `.read.path`；`of` 不是 entity 是 `pin-type` @ `.read.of` |
| `unitsOf` | 目录第 27 个节点，`hasAwait=true`／`hasSideEffect=false`，`side` 是**值引脚**（`{"lit":"enemy"}`）不是文本枚举 |
| `bind` 正名 | **删掉了上一轮往目录里塞的假 `bind` 引脚**——它是节点级字段（SPEC §3），塞进 `Inputs` 会让导出的目录与快照不一致 |
| `bind` 形态校验 | 无输出写 bind ＝`bind-scope`；多输出写成字符串 ＝`bind-scope`（与后端 `HandlerDslReader.ParseBind` 同判） |
| `ask` 补字段 | 补上漏声明的 `zone?`／`filter?`／`fallback?`——**之前会把合法文档报成 `pin-unknown`** |
| `ask.side` | 改成可选（契约是 `side?`，我之前写成了必填） |
| 词表 | `unitsOf` ＝「找出单位」，新增分组「查询」（青碧 `#2a6068`，刻意离开全部动作色相） |
| 取值菜单 | 按目标种类过滤：不再出现「我方.attack」「自己.kredits」；并为每个可见的 `entity` 绑定开出「读取 ‹f›.防御」 |
| 样例 | 换成 `unitsOf → 逐个处理 → 若 ‹f›.防御 ≤ 2 打 3 否则打 1`——样例本身就是新契约能力的证据 |

验证：`HandlerDslEditorTests` **27 条全绿**（原 19 条 + 8 条新的），构建 0 错误。

---

## 6 仍然对不上／还没做的

| # | 缺口 | 为什么现在没做 |
|---|---|---|
| 1 | **多输出节点的对象形态 `bind`** | `fetchFromDeck`／`pickHandCards` 有多个输出，契约要求 `{"status":"st","card":"c"}`。界面只支持单输出字符串，所以现在**如实报 `bind-scope`** 而不是默默放过。要做就得给节点加「按输出名逐个命名」的编辑面 |
| 2 | **`choice` 的 `options[]` 结构** | 引擎已经能生成了（`EmitChoice`），但 `options` 是 `[{label, actions}]`，不是普通动作数组。我们的 `Bodies` 只能存 `List<HandlerNode>`，加进去会导出错误的 JSON，所以调色板里继续隐藏 |
| 3 | **`script` 的 `inputs`／`outputs`** | 契约里是必填数组，缺了报 `pin-missing`（向量 023／024）。我们的导出**总是写空数组**，所以永远造不出这个错误；但没有 JSON 装载器，也接不到外部文件里的这种文档 |
| 4 | **没有 JSON → 模型的装载器** | 仍是客户端最大的验证缺口：24 条 check 向量只能人工比对。这是纯前端工作，不依赖引擎 |
| 5 | **`subtypes` 真实数据为零** | 等引擎在动作面补 `[HandlerDslSubtype]` |
| 6 | **目录没有中文 `title`／`group`**（D5） | 仍在 `HandlerVocabulary` 里本地兜底，会随引擎升级漂移 |

---

## 7 顺手发现的两处文档不一致

FRONTEND-GUIDE 末尾的目录树还是旧数字：

```text
├── vectors/
│   ├── check/*.check.json   ← 你的测试数据（19 条，每码至少一条）   ← 实际 24 条
│   └── gen/*.gen.json       ← 后端专有（8 条），你不用管          ← 实际 10 条
```

不影响正确性（向量文件本身是全的），但会让人以为新向量没进来。建议下次顺手改掉。

---

## 8 下一步

按影响排序：

1. **JSON 装载器**（纯前端，立刻可做）：把 24 条 check 向量变成自动化测试，而不是人工比对。
   这是唯一能证明「我们和后端判得一样」的手段。
2. **多输出 `bind` 的对象形态**：现在 `fetchFromDeck`／`pickHandCards` 的返回值接不住，
   等于这两个节点的输出在界面上不可用。
3. **`choice` 的选项编辑面**：引擎已就绪，只差界面。
4. 接真接口（`HandlerCatalogFacade.GetCatalog()` / `DescribePrefab` / `Commit`）——
   **仍然卡在引擎合并**：客户端 `ProjectReference` 引擎工程，本地检出还没有 `Orc.HandlerDsl`，
   编译不过。签名已经在 D7 里确认，可以先把适配层写出来，等合并后切开关。
