# 草案与父效果声明同一条挂载 → 产物挂两遍，效果翻倍

- 日期：2026-11-03
- 引擎检出：`716d7d3`（`5475abbf` R38 + `9b62d965` 文档）
- 触发者：本仓库 M1 端到端测试（`tests/HandlerDslAuthoringTests.摊平出来的牌在真实对局里真的生效`）
- 状态：**已提交** → https://github.com/hitman-OLD6/OrC-KSD/issues/4
  （我们这边已规避；引擎侧未改，本仓库不改引擎源码）

## 1. 现象

一个只做 `draw 2` 的草案摊平后，在真实对局里打出来**抽了 4 张**。
产物本身完全合法：通过 `AssetSchema.Read`、装载器接受、handler 注册成功、哈希自洽。

唯一异常藏在 `asset.mounts`：

```json
"mounts": [
  { "target": "engine#card.played", "member": "onPlayed" },
  { "target": "engine#card.played", "member": "onPlayed" }
]
```

两条一模一样 ⇒ 同一个 handler 挂了两遍 ⇒ 每次 `card.played` 触发两次 ⇒ `draw 2` 变成 `draw 4`。

## 2. 定位

> **2026-11-04 更新**：R39 把合并搬进了共用核，行号变了，问题原样搬过去：
> `src/Orc/Core/Model/Prefabs.cs:544-545`（`new List<MountDeclaration>(parent.Mounts)` ＋
> `mounts.AddRange(definition.Mounts)`）。下面保留当初摊平器里的那一版，便于对照。

`src/Orc.Packages/Editing/ModingFlattener.cs:302`（R39 前）：

```csharp
var mounts = parent.Mounts.Concat(draft.Mounts).ToArray();
```

父效果的挂载与草案的挂载**纯拼接**，不去重。所以「父效果已经有 `engine#card.played → onPlayed`，
草案里又写一遍同样的声明」时，产物里就出现两条。

这不是引擎漏了什么继承规则——继承是对的（产物自包含，必须带父的挂载）。
问题在于**重复声明没有代价**：作者或 AI 顺手写一遍，产物照样合法。

## 3. 为什么难查

1. 产物合法：没有错误码、没有警告、`flatten --check` 报「最新」。
2. 装载成功：`AssetPackLoader.Load` 只按声明逐条挂载，重复条目对它不是异常。
3. 行为翻倍而不是归零：`draw 2` 抽 4 张看起来像「DSL 的 count 语义理解错了」，
   排查方向会被引到表达式求值上（我们就是这么被引偏了一次）。
4. 只有端到端（开局 → 打出 → 数手牌）才看得出来；单测装载器和产物结构都绿。

## 4. 建议（引擎侧，任选其一）

| 方案 | 位置 | 代价 | 说明 |
| --- | --- | --- | --- |
| ① 按 `(target, member)` 去重 | `ModingFlattener.cs:302` | 最小 | `parent.Mounts.Concat(draft.Mounts).DistinctBy(m => (m.Target, m.Member))`。语义最直白：声明两次等于声明一次。 |
| ② 判成错误 | 同上，或 `HandlerDslValidator` | 中 | 明确告诉作者「这条挂载父效果已经有了」，比静默去重更难踩。需要新错误码或复用 `pin-*` 口径。 |
| ③ 装载期警告 | `AssetPackLoader` 挂载段 | 小 | 重复条目留痕，不改变行为。至少让「效果翻倍」有迹可循。 |

倾向 ①＋③：去重保证行为正确，警告保证作者知道自己写了冗余声明。
② 会打断「父效果没有、草案补一条」这个合法用法之外的一切重复写法，对 AI 生成的草案不够宽容。

## 5. 我们这边的规避

`proto/bridge/Kards.Ui.OrcBridge/HandlerDsl/HandlerDslAuthoring.cs`：

- `BuildDraft(..., mounts)` 的缺省值从「补一条 `engine#card.played → 该成员`」改成 **空**
  ——父效果的挂载会被继承，草案不该重复声明。
- 参数保留：父效果**没有**该成员的挂载时，作者仍可显式补一条。
- `tests/HandlerDslAuthoringTests.DSL文档与草案可提交_摊平产物自包含且带生成标记` 增加断言：
  产物 `Mounts` 必须**恰好一条**，且是 `engine#card.played / onPlayed`。

这条断言的作用不是防引擎，是防我们自己再把缺省值改回去。
