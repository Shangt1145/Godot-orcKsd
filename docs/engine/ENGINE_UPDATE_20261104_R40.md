# 引擎更新 2026-11-04（晚）：R40 把「重复挂载」判成错误

两个提交、17 个文件、`+1377 / -23`：

| 提交 | 内容 |
| --- | --- |
| `2bc0928` | 挂载声明唯一性校验与预制体展开对齐（内核＋编辑面＋对局＋5 组新测试） |
| `192c04b` | 契约包补挂载归属字段，DIY 文档同步 handler 触发说明 |

这一版**不是新功能**，是对我们提的 issue #4 的修复，附带一处 DTO 破坏性变更。

---

## 1 一句话结论

> 以前：同一条挂载声明两遍 → 静默翻倍（`draw 2` 抽 4）。
> 现在：展开期 `structure` 拒绝，点名「谁已经声明过、谁又声明了一次」。

R40 的判定点是 `PrefabCatalog.Build`——摊平／资产装载／`inline`／`named` 实例化三条路径唯一的交汇处，末端（`MountPlanner`／`AssetPackLoader`）不另加判据。**不提供 override 通道**：挂载没有可改的参数，「覆盖」与「删掉子那条」结果等价。

---

## 2 对我们的影响：零改动，一条回归测试

我们的草案**缺省不声明挂载**（继承模板），所以这条校验对我们本来就无痛。上一轮之所以这么设计，正是踩过 #4 那个坑（`draw 2` 抽 4 张）。现在它从「我们的自觉」变成了「引擎的强制」，是好事。

新增回归测试 `草案重复声明模板已有的挂载会被点名拒绝而不是效果翻倍`，钉三件事：

1. 重复声明 → 摊平失败，错误原文含 `重复声明挂载`、目标地址、**两个**声明者 id（模板与草案）；
2. 失败**不落产物**（半份产物会让「重试」变成「改一份已经存在的东西」）；
3. 同一成员挂到**不同**信号仍然合法（产物 `mounts` 两条）——防止以后收紧过头把「一个成员多个触发点」也堵死。

第 3 条的信号不硬编码：从 `EffectTemplateLibrary.Derive()` 里挑一个**宿主需求相同**的其它信号，否则会撞「挂错宿主」的另一条校验，测不到这里想测的东西。

真实错误原文（`structure` 错误项）：

```
资产 'demo.dup.draft' 无法展开（预制体 'demo.dup.draft' 重复声明挂载 'engine#card.played'
→ 'onHook'：已由资产 'template.card.played.any' 声明，资产 'demo.dup.draft' 又声明了一次
（同一 (target, member) 在整条继承链上至多一条；挂载没有可改的参数，故不提供 override 通道，R40.1／R40.3）。）。
```

观感上套了两层括号（外层摊平器包装、内层展开异常原文），已在 issue #4 里提了一句，不影响可用性。

---

## 3 DTO 破坏性变更：`MountedTo` 从字符串数组变成对象数组

```csharp
record PrefabMountReference(string Target, string Member, string DeclaredBy);

IReadOnlyList<PrefabMountReference> MountedTo;   // 原：IReadOnlyList<string>
PrefabWrapSummary { Target, Phase, Seam, DeclaredBy }   // 多一个位置参数
```

**我们没被影响**：`DescribeParents()` 的挂载目标一直是从模板 spec 推的（`"engine#" + spec.Signal`），没有读 `MountedTo`；`ViewFieldsOf` 只用 `ViewFields`。编译零报错即为证。

`DeclaredBy` 是**内存态元数据**（不参与相等／哈希／序列化，落盘即丢），三层链 `T ← A ← 草案` 上报 `T`（真正写下那条声明的资产），不是「直接父层」。

### 为什么引擎要加这个字段（值得记下来）

契约文档的原话：作者严格照着 `DescribePrefab` 的结果回写草案时，会把父已有的挂载再声明一遍，而错误里说的「父资产」在旧 DTO 里根本读不到——作者无从下手。

纪律写得很直白：**展开表只读展示，草案表增量追加；把展开表原样回写草案＝`structure` 失败，属预期行为**。

我们不受这条影响，因为画布**根本不编辑挂载**（保存对话框只显示「父已挂哪个信号」，不给第二个输入框）。这是当初刻意的取舍，现在回头看省掉了一整类坑。

---

## 4 仍然没修的（#3 的 B 项）

`ModingFlattener.Check(packRoot, draftPath)` 依旧没有 `templateRoot`，`Flatten` 有：

```csharp
if (catalog.Contains(parentId))   // ← 父是模板时恒为 false，整段跳过
{
    // 「父新增成员而产物没有」= ParentAhead
}
```

后果：**父是模板时，「父新增成员」这条陈旧检查静默不跑**，只剩 `dslHash` 比对。

对我们界面的具体含义：「产物是最新的」这句话，对模板父来说只覆盖了 DSL 那一半。模板只随引擎版本变，所以现实中很难踩到——但口径不完整就该说清楚。已在 `ISSUE_20261104_TEMPLATE_GAP.md` 里标着，不催。

---

## 5 我们改了什么

| 位置 | 改动 |
| --- | --- |
| `tests/HandlerDslAuthoringTests.cs` | 新增 1 条（17 条），见 §2 |
| `docs/engine/ISSUE_20261103_MOUNT_DUPLICATE.md` | 状态改「已修复（#4 closed）」，补引擎的最终实现位置 |
| issue #4 | 回帖验证记录并关闭 |

**引擎仓库一行没改**。合并 `192c04b` 时 `Match.cs` 有一处**注释**冲突（对方加了 patch 标签），按引擎那一版解决，没有改写任何逻辑。

---

## 6 验证

- `HandlerDslAuthoringTests` **16 → 17 条**全绿；`HandlerDslEditorTests` 39 条全绿。
- 全量 **773 条 → 771 通过 / 2 失败**（`CrossEffectTargetTests`、`MatchDebugTests`，与基线同因，未新增）。
- 未跑 UI 探针：这一版没有触碰目录面、摊平公开签名或保存对话框，探针覆盖的三条路径（`block-canvas`／`block-replace`／目录来源）行为不变。
