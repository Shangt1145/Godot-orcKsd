# 引擎侧问题：R39 之后，仓库里没有一个能被 `extends` 的资产

**状态**：**已提交** → https://github.com/hitman-OLD6/OrC-KSD/issues/3（我们已在客户端侧规避，不影响出货）
**日期**：2026-11-04
**触发**：`4b2aa7f feat: 存档导出与槽位/内联预制体模型…`（R39 落地）

---

## 1 现象

R39 把覆写资格改成「父在链上把该成员声明成 `slot: { type: "handler" }` ∧ 未 sealed」，
`authorize-moding`／`abstract` 读到即拒绝。这条本身清楚，但配套的两件事没跟上：

1. **`samples/` 里没有任何资产声明过空位。**
   `grep '"slot"' samples/*/effects-v4/*.component.json` → 0 命中。
   于是 `samples/card-pack-diy` 的六个组件**一个都不能当父**——它作为「DIY 参考实现」已经过期。
2. **模板库只存在于代码里，不存在于磁盘上。**
   `EffectTemplateLibrary.WriteAll(root)` 能生成 104 份 `template.<信号>.<档>.component.json`，
   但只有 `ORC_WRITE_TEMPLATES=1` 的生成测试会调用它；仓库里没有落地的 `effect-templates/`。

结果：任何客户端照示例写第一份 moding，摊平就会撞上

```
成员 'onPlayed' 不是空位：父资产 'diy.fire-support.effect' 没有在链上把它声明为 slot
(slot <成员名>: handler)，因此不允许被覆写（R2 已被 R39 修订）。
```

这句话是对的，但它出现的位置离作者的动作很远——作者只是「选了一个效果，点了保存」。

## 2 我们怎么规避的

`HandlerDslAuthoring`（客户端）：

- 父清单**不再扫包**，直接取 `EffectTemplateLibrary.Derive()`（104 条，成员恒 `onHook`）；
- 保存前 `EnsureTemplateLibrary()`：模板库目录不存在就 `WriteAll` 整批生成到
  `<base>/effect-templates`（**包根的兄弟目录**，刻意不放进 `packs/`——`PackageDiscovery`
  会把 `packs/` 下每个带 manifest 的目录当卡包，同 id 直接抛）；
- 摊平传 `templateRoot`，跨包继承只走「不饱和的模板」这条口子。

产物是自包含的，模板只在摊平时被读一次，所以模板库缺失只影响「能不能改」，不影响「已发布的能不能跑」。

## 3 建议（按优先级）

### A. `samples/` 落一份模板库（成本最低，收益最大）

`WriteAll` 已经能生成，加一个 `ORC_WRITE_TEMPLATES=1` 的生成测试写进
`samples/effect-templates/` 即可。顺手把 `samples/card-pack-diy/README.md` 里
「挑一个效果当父」改成「挑一个模板填空位」，否则示例与实现继续对不上。

### B. `ModingFlattener.Check` 补 `templateRoot`

现在 `Check(packRoot, draftPath)` 只 `LoadPackAssets(root)`。父是模板时：

```csharp
if (catalog.Contains(parentId)) { …比对父新增成员… }   // ← 恒为 false，整段跳过
```

于是「父新增了成员而产物没有」这条**静默不检查**，作者看到的是「草案与产物一致」。
`Flatten` 已经有 `templateRoot` 参数了，`Check` 保持一致即可（签名向后兼容）。

### C. 装载期对「声明了 `mountable` 但没 `slot`」给一条提示

现在的失败点在摊平期。如果在装载／描述面就能说「这个成员不能被积木填」，
编辑器可以把不可选的原因显示在下拉里，而不是等作者保存后才看到异常。

## 4 我们不做什么

按老规矩，**引擎仓库一行不改**。上面三条都是建议；我们这边已经能工作，
A 与 B 只是让「下一个客户端」不用重新踩一遍。
