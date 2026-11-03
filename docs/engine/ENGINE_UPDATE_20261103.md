# 引擎更新调研：choice 接通了，我们的「还不能用」角标成了谎（2026-11-03 版）

> 调研对象：`hitman-OLD6/OrC-KSD` `main` 分支 `9b62d965`（2026-11-03 02:17 UTC / 10:17 北京时间）
> 对照基线：`299d93d`（11-02 04:05 UTC），即上一轮 `ENGINE_UPDATE_20261102.md` 的对象
> 调研方式：本地检出仍在 `169e575`（**未动**）；`git fetch` 依旧连不上 `github.com:443`，
> 改从 `codeload.github.com` 取 `9b62d965` 的 `tar.gz`（4.9 MB）解包，与留存的 `299d93d` 全树做字节级 diff
> 结论一句话：**我们 11-02 提的 issue #2 被实现了（R38）——`choice` 不再是预览态，它现在真的能跑；
> 于是我们昨天刚发的 beta.3 里那个金色「还不能用」角标，从「诚实的提示」变成了「说反了」。已改。**

---

## 1 这次更新是哪两个提交

| 提交 | 时间（UTC） | 内容 |
|---|---|---|
| `5475abbf` | 11-03 02:17:53 | feat: choice 标签校验与靶通道呈现对齐，审计链导出完善 |
| `9b62d965` | 11-03 02:17:59 | docs: 契约包补 choice 标签规则与调试工具说明 |

又是相差 6 秒的成对提交。改动面：

- 契约包 6 个文件：`SPEC.md`（3 行）、`EXCLUSIONS.md`（1 行）、**新增 3 个 check 向量**
- `src/` 下 12 个文件（`KeywordEffectActionFace.cs` 一个人就 +166 行）
- 新增 2 个测试文件：`ChoiceActionFaceTests.cs`、`ChoiceTargetingChannelTests.cs`（173 行）

**没动的**（决定我们要改多少）：

| 文件 | 状态 |
|---|---|
| `schema.json`（DSL 文档语法） | **未改** —— 没有新字段、没有新原语 |
| `catalog.snapshot.json` | **未改**（仍 27 节点、无 `stage`、无 `builtInOps` 键） |
| `catalog.schema.json` | **未改**（`builtInOps` 仍是可选字段，语义照旧） |
| `FRONTEND-GUIDE.md` | **未改** |
| `vectors/gen`（21 个） | **未改** —— 生成出来的代码一个字不变 |
| 13 个错误码 | **未改**（`choice` 的标签规则**复用** `pin-unknown`，没有第 14 个码） |

也就是说：**能写出来的东西还是那些，变的是「`choice` 现在真能跑」以及「跑不起来时怎么失败」。**

---

## 2 核心变化：`choice` 从 preview 变 alpha

### 2.1 契约怎么说

`SPEC.md` §12.11 那张表里，内建 op 那一行的备注被改成了：

> **R38 起 `choice` 为 `alpha`**（有桥走 `target.submit`、无桥明确失败），该表当前为空

**注意「该表」指的不是 `builtInOps`，是 `PreviewOps` 登记表。** 这两张表极易混成一个：

| 表 | 内容 | 现在 |
|---|---|---|
| `HandlerCatalogDto.BuiltInOps` | **恒十项**（`sequence`…`script`），每项带 `stage` | 十项都在，**全部 alpha** |
| `HandlerDslContract.PreviewOps` | 「语法合法但跑不起来」的登记 | **空表** |

引擎的导出逻辑是 `BuiltinOps.Select(op => PreviewOps 里有 ? preview : alpha)`，
所以 `PreviewOps` 一空，十项就全是 alpha。我第一次读 SPEC 时把「该表当前为空」读成了
`builtInOps` 空表，被引擎自己的守护测试纠正：

```csharp
var choice = Catalog().BuiltInOps.Single(o => o.Op == "choice");
Assert.Equal("alpha", choice.Stage);
Assert.Null(choice.Problem);
```

### 2.2 实现：复用既有 targeter 通道，协议零变更

`ChooseAsync` 从「一行 `throw`」变成了完整实现，但**没有新建子系统**：

- 选择器用既有的 `SelectorTemplates.Option`（`candidates` 空、`identifiers` 非空）
- **标识集＝选项文案**（`labels`），不是序号——回填靠文案定位分支
- 提交口径与引用类选择器**完全一致**：`target.submit` 的 `picks` 是 **1 起序号**
  （`TargeterBridgeAdapter.Submit` 里新增了一条「按 `identifiers[index-1]` 解标识」分支）
- `pending` 多了一个 **`prompt`** 键，且**只在有文案时出现**——`ask` 的输出逐条不变（这是同轮的验收项）

对我们意味着：**前端不必区分两种选择器**。我们已有的 ask 选靶提交流程，原样就能应答 choice。

### 2.3 失败语义：只有一种异常，七种原因

`EXCLUSIONS.md` 里 `choice` 那行整段重写。要点：

| 情形 | 行为 |
|---|---|
| 无桥（没装配 targeter） | 抛 `InvalidOperationException`，消息前缀 `choice-failed[bridge-not-assembled]:` |
| 玩家取消 / `FailAll`（房间轮换、终局） | 抛 `choice-failed[cancelled]:` —— 两者**故意不可区分**，要分辨得查 `TargetingTrace` |
| 非法选择 | `choice-failed[invalid-selection]:` —— **不重试、不取首项** |
| 选择器回传了不在文案集里的标识 | `choice-failed[fault]:` |
| `ct` 被取消 | **`OperationCanceledException` 原样穿透**，绝不折成 `InvalidOperationException` |

没有 fallback（我们提的 Q4 被明确否了：`switch` 没有 default，兜底就等于「生效到错误分支上」）。
失败即整条效果中断 ⇒ `order.cast` 断 ⇒ `order.finalize` 不执行 ⇒ **这张牌仍在手、未扣费**。

已知边界（引擎自己写在注释里）：`ct` 不传给 `RunAsync`，所以「等待中被取消」不会提前中断，
最长等到桥超时（MCP 桥默认 10 分钟）才收场；只有「进入通道前已取消」会被 `ThrowIfCancellationRequested` 挡住。

---

## 3 契约另一处硬变更：选项文案非空且唯一

`SPEC.md` §14.1 的 `pin-unknown` 那行多了半句：

> `choice.options[].label` 为空（含纯空白）或重复

新增三个 check 向量（24 → 27），**全部复用 `pin-unknown`**：

| 向量 | 场景 | 期望 |
|---|---|---|
| `025-choice-empty-label` | 第二个选项 `label: ""` | `pin-unknown` @ `actions[0].options[1].label` |
| `026-choice-duplicate-label` | 两个选项都叫「抽 1 张」 | 同上，且消息要指出与哪个选项重复 |
| `027-choice-blank-label` | `label: "   "` | 同上（口径＝`IsNullOrWhiteSpace`，与运行期一致） |

为什么必须在**校验期**拦：交互通道按**文案**回填被选项。重复 ⇒ `Array.IndexOf` 恒命中第一个
⇒ 「生效到错误分支上」；空 ⇒ `IdentifierSetParameter` 构造期就抛 `ArgumentException`
⇒ 「画得出来、跑起来炸」。这正是 R36 那条硬规矩的翻版。

顺带一个细节：Reader 里「`options` 不得为空」只在**数组本身为空**时才报
（逐项被拒已经各有一条诊断，再补一条会把单错变双错——向量要求单码单路径）。

---

## 4 还有一处顺手改的：审计链 `choiceSelectors`

`docs/game-flow/09-调试工具.md` 加了一行，`AuditChainJson/Text` 各加了字段：

`choiceSelectors`：`viewer` / `prompt` / `labels[]` / `picked` / `label`，
**`picked` 是 1 起序号**（与 `target.submit` 的 `picks` 同口径），文本面一行 `picked=<n>/<total>`。

只记**成功裁决**（取消不落账）。

---

## 5 我们这侧改了什么

| 位置 | 改动 |
|---|---|
| `HandlerDslCatalog.BuiltInOps` | 十项全改 alpha、`choice` 的 `problem` 删掉；注释写清「清空的是 `PreviewOps`，不是这张表」 |
| `HandlerDslCatalog` 快照里的 `choice` 节点 | `Stage` 去掉（缺省 alpha）；注释改为「R38 起能跑，所以校验期必须保证文案非空唯一」 |
| `HandlerDslValidator` | 选项文案三条规则：空／纯空白／重复 → `pin-unknown`；重复消息**指出跟第几个撞了**（`options[0].label`）。空数组仍报 `pin-missing` |
| `HandlerDslEditorTests` | 32 → **35**：改写 1 条（空文案改判 `pin-unknown`）、新增 2 条（纯空白、重复＋定位）、改写分区测试（十项全 alpha、无预览态） |

界面代码**一行没动**：角标、压暗、金色警告全是 `StageOf` 驱动的，登记表一空，它们自然不再出现。
机制留着——下一个「语法合法但跑不起来」的 op 迟早会来。

验证：`HandlerDslEditorTests` **35/35**；全量 **747 通过 / 2 失败**，失败两条仍是
`CrossEffectTargetTests`、`MatchDebugTests`（改动前就在失败的引擎漂移，与本次无关）。

---

## 6 issue #2 的四个问题，答复在哪

| 我们问的 | 引擎的答 |
|---|---|
| 玩家取消怎么走？ | `InvalidOperationException` + `choice-failed[cancelled]`，与 `FailAll` **同形**；对局级 `ct` 取消另走 `OperationCanceledException` |
| 要不要给 `choice` 加 `fallback` 字段？ | **不加**（Q4）。`switch` 无 default，兜底＝错分支 |
| 在线仲裁归谁？ | 走 `TargeterManager` 的全局 FIFO，与 `ask` 同一队列、按调用顺序排队 |
| 超时？ | 桥层（MCP 默认 10 分钟），`choice` 自己不设超时 |

验收口径也按我们提的做了：删掉 `PreviewOps` 里那条 → 守护测试先红后绿。

---

## 7 还欠着的（不在这次范围）

- **`ask` 的 `prompt` 仍然被丢弃**：Q7 明确把它排除在外，所以只有 `choice` 的 `pending` 带 `prompt`。
  我们在编辑器里让作者填的「问一句什么」对 `ask` 依旧无效——这是**引擎的既有缺口**，不是我们漏接。
- **效果模板库仍是 0 个文件**：`ORC_WRITE_TEMPLATES=1` 才会生成 102 个 `template.*.component.json`，
  仓库里一个都没有。模板浏览器还是没东西可读。
- **`choice` 在循环里**仍是 `ask-in-loop` 硬拒，没变。

---

## 8 下一步：选项弹窗现在很便宜

上一轮我说「等引擎定下『选项请求』的 DTO 形状再接弹窗」。现在形状定下来了，而且**协议零变更**：

1. 收到 `notify/target.request`，看 `pending.identifiers` 非空且 `candidates` 空 ⇒ 这是选项请求
2. `pending.prompt` 有就显示，`identifiers` 按序摆成按钮
3. 点了第 n 个 ⇒ `target.submit { sessionId, picks: [n+1] }` —— 跟选靶完全同一条路

唯一的判断题是**视觉**：选项面板要不要复用选靶面板的骨架。这是我能做的部分。
