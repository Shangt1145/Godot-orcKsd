# `choice` 运行期无条件抛异常：建议复用既有 targeter 提交通道

> 拟提交到 `hitman-OLD6/OrC-KSD`
> 标题：**`choice` 节点运行期无条件抛 NotSupportedException——建议复用既有 targeter 提交通道实现选项呈现**
> 提交日期：2026-11-02　　基线：`299d93d`

---

## 现象

任何含 `choice` 的 handler DSL 效果，执行到该节点**必抛** `NotSupportedException`，该节点之后的效果全部不执行。

- 实现位置：`src/Orc.Game/Cards/KeywordEffectActionFace.cs:121` `ChooseAsync`
- 整段实现只有两个 null 检查 ＋ 一个 `throw`，没有分支、没有降级
- 已在 `HandlerDslContract.PreviewOps` 登记为 `preview`，目录导出后 `builtInOps` 里 `choice` 的 `stage=preview`
- 生成器照常出码（`HandlerDslGenerator.EmitChoice`：`await ChooseAsync(...)` ＋ `switch`），校验器也放行（13 个错误码一条不报）

也就是说：**能画、能校验、能生成 csx、装载能过，一跑就断。** 这符合 R36「宁可炸不可错」的设计意图，
但结果是作者只能搭图、不能发布——`choice` 现在是唯一一个卡在这个状态的节点。

### 最小复现

契约包自带的 `vectors/gen/005-choice-ask-iterate.gen.json` 就是最小形态：

```json
{
  "schemaVersion": 1,
  "actions": [
    {
      "op": "choice",
      "prompt": "选择一项：\"抽牌\" 或 \"放弃\"",
      "options": [
        { "label": "抽 1 张", "actions": [{ "op": "draw", "count": { "lit": 1 } }] },
        { "label": "放弃",   "actions": [{ "op": "stop" }] }
      ]
    }
  ]
}
```

生成出的 csx 里是：

```csharp
var __choice_0 = await Orc.Game.Cards.KeywordEffectActionFace.ChooseAsync(
    __self, "选择一项：\"抽牌\" 或 \"放弃\"", new string[] { "抽 1 张", "放弃" }, ct);
switch (__choice_0) { case 0: ... case 1: ... }
```

`ChooseAsync` 第一行有效语句就是 `throw`，所以 `switch` 永远不会进入。

---

## 关键观察：这条通道其实已经存在了

`choice` 缺的是「问前端要一个玩家裁决」。而同目录下的 `ask` **已经在做同一件事**：

```csharp
// AskAsync（KeywordEffectActionFace.cs:72）
return await runtime.SelectAsync(host, new EffectSelector("one", side, zone), ct);
```

`EffectRuntime.SelectAsync`（`EffectRuntime.cs:793`）按选择器分流：

| `sel` | 走法 |
|---|---|
| `all`／`self` | 确定性求值，不产生往返 |
| `random` | 对局随机服务（生产对局未开启＝明确错误） |
| 其余（`one`／`any`／`choose`） | **`SubmitAsync` → 提交通道** |

`SubmitAsync`（`EffectRuntime.cs:880`）取 `OwnerOf(viewer)?.TargeterManager.Bridge`，
桥接存在就交付一个会话（`ITargeterBridge.BeginTargeting` → `ITargeterSession.NextAsync`），
前端拉取、回填、引擎继续；**桥接不存在则抛 `InvalidOperationException`，绝不代选**（R2）。

`AskAsync` 再在外面包一层：捕获 `InvalidOperationException`（＝环境缺通道），
才按作者声明的 `fallback`（`empty`／`first`）降级；**有通道但玩家主动取消＝返回空集、不走 fallback**。

**结论：交互通道、队列、失败语义、审查记录都已经有了，而且已经在跑（`ask` 就是证据）。
`choice` 不需要新子系统，只需要在这条通道上增加一种「问题类型」。**

---

## 建议方案（复用，不新建）

1. **在 targeter 协议里增加「选项选择」请求**，与 `ISelectorInstance` 并列——
   前端 `NextAsync` 拉到它时呈现 N 个按钮，回填被选项的索引。
   请求体至少要带：`Prompt`、`Labels`、宿主卡、当前 `Context`（供前端做展示与权限判断）。
2. **`ChooseAsync` 改为走通道**：
   - 有桥 → 走提交通道，等玩家回音；
   - 无桥 → **保持现状**（抛明确异常），与 `ask` 的「无通道＝明确失败」完全一致。
3. **取消语义与 `ask` 对齐**：有桥但玩家取消 → 明确失败（不返回哨兵、不取首项）。
   现在 `ChooseAsync` 的注释已经写清楚「不能返回 −1 之类哨兵，否则会走到错误分支」——这个判断是对的，
   建议取消时也走「失败」而不是「选 0」。
4. **审查与复现**：把「呈现了哪些选项 / 选了第几个」记进审查导出（与 `RandomSelections` 同级）。
   否则重放会分叉，联机也无法对齐。

### 为什么不是「引擎代选一个」

那会制造「装上了但不生效／生效到错误分支」的静默路径，正是 Constraints 明确禁止的形态，
也比现在的「当场炸」更难排查。

### 为什么不是「新建一套 choice 专用桥接」

会多出一套队列、一套失败语义、一套超时与取消处理，和 targeter 通道各写一遍；
而且 `ask` 与 `choice` 在同一次效果里可能先后出现，两套通道的排队顺序无法保证。

---

## 需要引擎侧拍板的点

这几个我们不敢替你们决定，都会影响契约：

1. **取消时 `switch` 走哪条路？** 建议整体失败（与 `ask` 返回空集同构），但需要明确「效果中断」还是「跳过本节点继续」。
2. **要不要给 `choice` 加 `fallback` 字段？** 与 `ask` 对齐（`empty`／`first`）会让作者能在无通道环境里给出替代策略；
   但 `choice` 的「first」语义是「选第一个选项」，容易被误用成「随便走一条」，需要你们判断值不值。
3. **联机／服务端**：选项裁决是否要作为命令入队？已知 DIY 效果在联机服务端不执行，
   如果 `choice` 走本地通道，联机时的裁决归属需要一并定。
4. **超时**：玩家一直不选（挂机／断线）时，是等 `ct` 取消，还是由对局层给一个上限？现在 `ct` 由调用方给，语义上够用，但文档没写。

---

## 验收标准

- **有桥时**：含 `choice` 的卡能正常打出，玩家点选项后走对应分支；`ask` 与 `choice` 混用不串序。
- **无桥时**：仍是**明确失败**，不是静默取首项。
- **删登记**：删掉 `HandlerDslContract.PreviewOps` 里的 `choice` 条目后，那条守护测试先红、实现补上后转绿
  （即你们说的「机制要能自动变绿」，不靠人记得回来改名单）。
- **契约**：`builtInOps` 里 `choice` 的 `stage` 变 `alpha`、`problem` 消失；SPEC §12.11 无需改动。

---

## 我们这边的现状（供参考，不影响本 issue 的取舍）

- 编辑器已按 SPEC §12.11 实现分区：`choice` 在工具箱里置灰＋「还不能用」角标、
  画布块标题后同款角标、检查结果出金色警告并列出原因；**仍然允许插到画布上**（「先搭图，保存前再拦」）。
- 我们**不生成 csx**（SPEC §2.7），生成器在你们这边；桥的接入面在我们这边（Godot UI 的选项弹窗）。
- 只要你们定下「选项请求」的 DTO 形状，我们这边照着接；`choice` 变 alpha 后，
  我们那条断言「`choice` 是唯一预览态」的测试会先红，正好提醒我们回来改分区——不需要你们通知。
