# 引擎更新 2026-11-04：R39 把「父」换成了模板

两个提交、95 个文件、`+12741 / -971`：

| 提交 | 内容 |
| --- | --- |
| `4b2aa7f` | 存档导出与槽位／内联预制体模型，手牌转移与效果模板库扩展 |
| `6c96c16` | 术语表补存档与槽位，契约目录快照同步 |

对我们不是「多了一堆新功能」，而是**写回链路的父变了**。

---

## 1 一句话结论

> 以前：挑一个**具体效果**当父，覆写它的成员。
> 现在：挑一个**模板**（声明了空位的资产）当父，把空位填上。
> 具体效果的成员**不允许**再被覆写——R39 把 `authorize-moding` 这个开关整个删了。

这不是保守化，是换了模型：抽象性由「存在未填空位」推导，覆写资格由「父声明了 `slot: { type: "handler" }` ∧ 未 sealed」决定。`abstract` 与 `authorize-moding` 读到即**拒绝**（不静默忽略），口径写在 `AssetSchema.RejectAbolishedFields`。

---

## 2 三处直接影响我们的改动

### 2.1 覆写授权门（`Members.cs:594`）

```csharp
if (Slot is null)
{
    throw new PrefabLoadException(Structure,
        $"成员 '{Name}' 不是空位：父资产 '{parentId}' 没有在链上把它声明为 slot "
        + "(slot <成员名>: handler)，因此不允许被覆写（R2 已被 R39 修订）。");
}
```

**我们的 9 条摊平用例全红**，报的就是这句。示例包 `samples/card-pack-diy` 的六个组件一个空位都没有，所以**整个包里没有任何东西能当父**。

### 2.2 模板库成为唯一合法的父（`EffectTemplateLibrary`）

按「信号 × 守卫档」派生，`ORC_WRITE_TEMPLATES=1` 生成。实测 **104 份**：

```
template.card.played.any
template.card.played.self
template.card.played.owner-same
template.card.played.owner-different
…（每个非加载期信号 × 4 档）
```

每份的形状固定：

- 成员恒为 `onHook`，`slot: { type: "handler" }`、`handler: null`（＝未填＝模板）
- `mounts: [{ target: "engine#<信号>", member: "onHook" }]`（挂载由模板给，草案别重复声明）
- `view.fields` ＝该信号的载荷视图（`card.played` 是 `Card`／`Player`／`Source`）
- `requires-host`／`active-when` 按信号推导，**草案要照抄**（写错时点＝装载成功、永远不跑）

`ModingFlattener.Flatten(packRoot, draftPath, dsl, templateRoot)` 的第四个参数就是它：模板库**只并进目录**，跨包继承只开在「不饱和的模板」上（R33 第 4 条）。

### 2.3 摊平器重写（`ModingFlattener.cs` `-400/+69`）

合并、填空位、饱和判定全部收敛进 `PrefabExpander.Expand`（`Orc.Core.Model`），声明面只留读草案／产物 id／陈旧比对／原子写盘。**公开面没变**：`Flatten`／`FlattenAndWrite`／`Check` 签名兼容（只是多了可空的 `templateRoot`），`ModingFlattenResult` 字段不变。

所以我们这边不是重写，是换父。

---

## 3 目录面：+3 个节点

`catalog.snapshot.json` 的 `catalogRevision` 变了，节点 27 → 30：

| 节点 | 入参 | 出参 | 备注 |
| --- | --- | --- | --- |
| `attackByEffect` | attacker／target／source? | `boolean` | `hasAwait`、有副作用 |
| `attackCandidates` | attacker | `list<entity>` | **纯查询**（`hasAwait=false`，折叠可复制） |
| `exchange` | first／second／source（**全必填**） | 无 | 交换的发起方决定归属判定 |

已有节点的引脚一处没动。我们的 `引擎目录与本地镜像一一对应` 首跑就红——**这正是它存在的意义**（漂移报警器），补进镜像后转绿。

---

## 4 其他变化（暂时不影响我们，但要记着）

- **存档面**：`SaveExporter`／`SaveEvaluation`／`SaveContext`／`SaveModing`／`OrchestrationRestore`／`Slots`／`InlinePrefabTable`／`PrefabExpansion`。`PrefabDefinition` 多了 `slots`／`origin`／`save`，删了 `authorizesModing`／`isAbstract`。
- **内联预制体**：卡牌的 `effects` 组件除了 `prefabs: string[]`，还支持 `inline: [...]`（v3 快照）。**`pack/builtin` 全走 `inline`**——这直接影响「覆盖模式」：没有字符串引用可改，覆盖在 builtin 上**确实没有目标**（见 §6）。
- **手牌转移**：`HandTransferService`／`HandSelection`，多了一套转移/排除语义。
- **效果运行时**：`EffectRuntime.ActionExtensions`（+344）扩了动作面；`HandlerDslExclusions` 新增排除口径。

---

## 5 我们改了什么

| 位置 | 改动 |
| --- | --- |
| `HandlerDslAuthoring` | 父清单改读 `EffectTemplateLibrary.Derive()`；新增 `TemplateRoot`／`EnsureTemplateLibrary()`；`Flatten` 传模板库；`BuildDraft` 新增 `requiresHost`／`activeWhen`（照抄模板）；`Save` 先校验「父必须是模板」 |
| `HandlerDslAuthoring` | 覆盖模式新增 `RetargetFromId`：产物 id 从**被替换的效果**派生（不是模板），引用也由它改指 |
| `HandlerDslAuthoring` | 新增 `DescribeReplaceableEffects()`：覆盖模式的目标下拉（被卡引用的效果 ＋ 已有积木版本的原效果） |
| `HandlerDslCatalog` | 镜像补 3 个节点 |
| `HandlerBlockEditorScreen` | 保存对话框：模板下拉（缺省「打出时」）＋「替换哪个效果」下拉；两个下拉带 `metadata`（id 不靠 label 解析） |
| `Main.NativeWorkshopVerification` | `block-canvas`／`block-replace` 改按 `Name` 取控件、按 metadata 取 id；无目标时断言「老实拒绝」 |
| `HandlerDslAuthoringTests` | 16 条（父改模板、新增「父必须是模板」「覆盖必须说明替换谁」「模板库缺失自动生成」） |

---

## 6 遗留（已写进 issue）

1. **`ModingFlattener.Check` 没有 `templateRoot`**：模板父的草案，`Check` 只能比对 `dslHash`，
   「父新增了成员而产物没有」这一条**静默跳过**（`catalog.Contains(parentId)` 为 false）。
   见 `docs/engine/ISSUE_20261104_TEMPLATE_GAP.md`。
2. **示例包零个可扩展资产**：`samples/card-pack-diy` 现在既不能当父、也不是模板库；
   任何客户端第一步都得自己生成模板库。
3. **`inline` 效果无法被「覆盖」**：`pack/builtin` 的卡用的是 v3 内联快照，没有 `prefabs` 字符串可改。
   要支持得做「内联效果 → 积木产物」的迁移，属独立一段。
