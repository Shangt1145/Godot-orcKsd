# alpha.5 部署指向重新排查交接（2026-10-25）

## 状态与本轮边界

**问题未解决，不能继续沿用“部署指向已全面验收”的结论。** 用户在 alpha.5 发布后反馈：“部署指向的单位根本就不让我选，仍然是随机指向”，并明确范围是“所有带有部署指向的单位，比如‘部署：对一个单位造成一点伤害’”。用户未提供具体卡 ID，也未确认本地还是联机，因此不能把某一条分支的缺陷直接宣布为全部现场的唯一原因。

用户随后要求：**“查完后写交接文档，不用自己改”**。本轮只做源码、数据和既有测试覆盖面的只读排查，新增本文并在旧交接顶部标记重新打开；未修改游戏/引擎代码，未生成新修复、未打包、未提交或推送。未做新一轮实机复现，也未重跑整套测试。下文区分代码已能证明的缺陷与仍需实际复现的部分。

主仓库检查基线：`8cfd7eab596d01780782de600f17600d25e392ca`；公开版本 `2.0.0-alpha.5`，Android version/code `10`。本轮开始主仓库干净。引擎位于相邻 `../OrC-KSD-github`，HEAD `025d44ebe4a491042a484c452ca264121a95c3cb`；已有未提交修改 `AuthoringTargetContext.cs`、`EffectRuntime.cs` 是上一轮留下的发布补丁内容，本轮未触碰，勿重置。

## 1. 已确认：基础结构编辑的“单个”仍走无头取首个

这条路径与上一轮测试的桌面声明图路径不同，是优先处理项。

调用链：

1. `proto/bridge/Kards.Ui.OrcBridge/OrcEffectEditing.cs:179` 的 `Seed()` 为带目标的动作默认生成 `Sel="one", Side="enemy"`。`Sels` 中用户可见的 `one` 标签是“单个”。
2. `ToParsed()` 对 **没有 DesktopJson** 的旧式/基础结构，经过 `ToDslOp()` → `BuildSelector()`，保留 `DslSelector("one", ...)`。它没有为这个目标生成 `DesktopJson.targets`。
3. 同文件 `Compile()` 的普通分支（约 337 行）进入 `OrcImportService.CompileOrcEffect()`；后者约 242 行使用原生 op 模板生成快照。
4. 原生 `src/Orc.Game/EffectParsing/Templates/ops/damage.csx.tpl:5` 固定调用：

   ```csharp
   var targets = await runtime.SelectAsync(self!, new EffectSelector(/* sel, side, ... */));
   ```

   `pin.csx.tpl:5` 也有相同调用方式；其他单目标动作应逐项审计，不能只修伤害。
5. 原生 `src/Orc.Game/Effects/EffectRuntime.cs:759` 的 `SelectAsync()` 调用 `EffectTargetResolve` 判定器，**不会发起玩家选择器**。
6. `src/Orc.Game/Judicators/EffectTargetResolveJudicator.cs:63–84`：`all/self` 返回集合；`random` 使用随机服务；`one` 最终执行 `cards.GetRange(0, take)`，默认 `take=1`。

因此“单个”在这条编译路径上的实际语义是“自动取候选第一个”，不是玩家选择。用户肉眼描述“随机”不等于代码一定调用 RNG；这里是确定性的自动代选。

另一个同源入口是 `BackendTextAuthoring` 的 `TextCardType` 分支：`BackendTextAuthoring.Compile()` → `CardTextEffectCompiler.Compile()`；原生 `CardTextEffectCompiler.cs:361–363` 把 friendlyUnit/enemyUnit/anyUnit 映射成 `one`，同样复用上述模板。**当前 DIY 文本按钮也有经过 `OrcInspector.CompileText()` → DesktopOrc 的路径，不要错误地把所有文本解析都算成基础后端入口。应检查保存卡牌实际携带的是 DesktopJson、TextCardType、PrefabJson，还是普通 Ops。**

为什么之前修补没有兜住：

- `DesktopTargetContext.ReadOrderTargets()` 和原生 `AuthoringTargetContext.PrepareCard()` 都依赖外部图声明。
- `OrcMatchSession.CreateFromCatalogAsync()` 约 246–254 行装配 DIY 计划时，对已有 `OrcEffects` 只提取其中非空的 `DesktopJson`。
- 普通 `Ops/Sel=one`、基础 `TextCardType` 或没有作者元数据的原生快照，并不会凭空补出图声明。
- `BattleScreen.Orders.cs:10–11` 的 `IsTargetedOrder()` 依赖投影里的 `OrderTargets`；缺少投影时新加的单位定向拖拽分支仍进不去。
- 即使让 UI 带上 TargetUid，无头 `SelectAsync()` 不创建选择器，`AnswerParked()` 也就没有机会回放这个目标。仅补 UI 或只调预打出入口不够。

现成的同类原生样例：`../OrC-KSD-github/samples/card-pack-demo/cards/demo.grenadier.card.json`，注释写“部署，对敌方一个单位造成 1 点伤害”，脚本实际使用 `runtime.SelectAsync(... EffectSelector("one", "enemy", ...))`。这也是审计导入/扩展包不能只看文字的证据；本轮未把该样例导入运行。

## 2. 已确认：联机命令无法同时保存部署空隙和效果目标

`proto/bridge/Kards.Ui.OrcBridge/RemoteMatchFeed.cs:368`：

```csharp
_desiredGap = command is PlayCard { GapId: not null };
_desiredTargetRef = command switch {
    PlayCard play => play.GapId ?? play.TargetUid,
    // ...
};
```

当 UI 已正确提交 `PlayCard(uid, TargetUid: target, GapId: gap)` 时，只保存 gap，**target 当场丢失**。

同文件约 646 行只在 `!_desiredGap || selector=="slot"` 时消费该单一字段。效果选择器先到时不能用玩家指定的目标；slot 先到时消费完字段，之后也没有目标可回放。无论先后，都无法完整表达两个独立输入。

约 675 行的 `AutoResolveTargets=true` 会自动答候选第一个。但本轮搜索到的显式赋值均在 `Main.RoomVerification.cs` 的探针中，**没有证据证明普通房间误开了这个开关**，不能把它当成已定位的产品根因。

`RoutePlay()` 约 399 行的旧 `SupportIndex` 分支仍使用 `play.unit`，无 slot 的分支使用 `play.unit_pre`；须检验前者是否绕过预打出。普通 UI 当前交的是 GapId，不能混淆旧兼容分支和普通入口。

## 3. 已确认：本地遗漏位置的兼容调用会自动回答所有选择

`OrcMatchSession.cs:865–873` 的注释声称自动策略只用于“无指向部署”，实现却是：

```csharp
var automatic = _automaticChoice;
_automaticChoice = _pendingGapId is null;
try { return Report(await Match.PlayManager.BeginUnitPrePlayAsync(unit, ct)); }
finally { _automaticChoice = automatic; }
```

没有 GapId/SupportIndex 的调用会把整个预打出期间都设置为自动选择。`AnswerParked()` 约 588 行会调用 `OrcTargeterBridge.AutoRespond()`；后者约 106 行选第一个存活候选，效果目标与位置没有分开。

**范围限制：** 普通战场拖拽目前带 GapId，不能仅凭这处缺陷解释用户全部正常拖拽现场。但现有不少测试直接 `new PlayCard(uid)`，会被这个兼容策略掩盖交互缺失。后续修改必须让“自动选空隙”与“自动选效果目标”分开，不能把所有预打出选择一并自动化。

## 4. 不应再次误判的事实

- `BattleScreen.TryDrop()` 约 633–644 行已经有单位 + IsTargetedOrder 的分支，会一起传 TargetUid 和 GapId。**不要再次只加这段就宣布修好。** 它成立的前提是该卡真的有 OrderTargets 投影。
- 本地显式 GapId / SupportIndex 已经过 `BeginUnitPrePlayAsync()`；上一轮对应修补存在。
- 当前内置包 `17th_infantry_regiment` 的执行脚本已调用原生 `AuthoringTargetContext.SelectAsync()`，并有声明图。它与没有声明图的基础结构卡不是同一条执行链。
- 扩展包一些旧脚本调用 `Kards.Ui.OrcBridge.DesktopTargetContext.SelectAsync()`，包装器内部仍调用原生交互通道。搜索脚本时必须区分该名字与 `EffectRuntime.SelectAsync()`，不能因为同名就说扩展包全是无头选靶。
- H39 sWG 的桌面元数据保留 deploy，但原生实际 hook 是 `unit.damage.dealt`；现有 `HasInteractivePlayEffect()` 的过滤有其回归背景，不要为增加投影而无条件撤销。
- 现有跨段“其”复用、Recall、不重复询问、候选查询不触发 targeted 通知等行为应保留，不能为当前修复回滚。

## 5. alpha.5 的验收为什么不够

`proto/ui/screens/Main.DebugDeploymentVerification.cs:29–42`：

- 唯一部署效果用例是 `pack/builtin/17th_infantry_regiment`（给友方 +1/+1），没有覆盖用户举的部署单体伤害。
- `_verificationRun=true`、`manualOpponentForVerification=true`、固定起手，调试台加资源/召唤两个目标。
- 验证了这张已迁移到声明图的卡的拖拽、空地部署再选靶、取消；没有证明普通保存卡组中所有不同表示形式的 DIY 卡都能走同一路径。
- `tests/CrossEffectTargetTests.cs` 部署用例也用同一张兵团，并直接传 TargetUid；可验证目标生效，不能代替所有卡的玩家输入验收。
- `tests/DiyInspectorTests.cs:49–57` 的 `Sel="one"` 用例仅比较编译产物参数发生改变，没有验证是否弹出选择和命中玩家选的第二个目标；该文件实际部署测试是部署抽牌。
- 房间探针使用 AutoResolveTargets 的结果不能证明玩家能手动选择。

因此之前测试通过与用户仍不能选目标并不矛盾。旧交接和 `docs/acceptance/DEBUG_DEPLOYMENT_20261025.md` 的“完成”只能作为这些特定夹具的历史记录，不能再作为全覆盖结论。

## 6. 给接手者的实施与验收要求（本轮没有实施）

先复现，拿到实际卡的完整定义与命令/选择链，记录：卡 ID、保存格式、编译脚本调用的目标 API、OrderTargets 投影、最终 PlayCard 两字段、是否出现选择器、是否自动应答。用户给的效果至少分别从“当前文本解析新建”和“基础结构单个目标”两种入口创建；再测试已有保存卡/导入包。不能用调试替身卡替代这些入口。

修复方向：

1. 为原生/基础 DSL 的手动单目标建立真正的交互语义，接入预打出目标捕获和最终执行复用；审计 damage/buff/pin/silence/destroy 等所有相关动作。明确 one、random、all、自身、跨段回指的区别。不要全局把所有无头查询改成弹窗，回合监听/AI/群体查询等场景另有语义。
2. 从真实可执行的目标声明生成 UI 可选目标，而非只依赖某一种编辑器附带的图元数据。对没有声明图的已有原生快照，明确兼容/迁移方案，不能假装 UI 已支持。
3. 联机端分开保存和消费部署 gap 与效果目标，覆盖两个选择器的先后顺序、取消、重试、非法/失效目标，成功与失败均清理意图。
4. 本地兼容空位置调用只可默认位置，不可代选效果目标。

最低验收矩阵：

| 维度 | 必须覆盖 |
| --- | --- |
| 卡来源 | 当前文本解析、普通 Ops/Sel=one、TextCardType 保存卡、含/不含作者元数据的导入快照、官方/扩展包声明图 |
| 场景 | 普通本地保存卡组、真实房间双方各出一次；Windows 和 Android 实际安装产物 |
| 玩家操作 | 至少两个合法目标，主动选第二个；空地部署后再选；直接指目标；取消 |
| 断言 | 未选择不自动命中；选第二个仅第二个扣血；第一个不变；部署位置不丢；取消的费用和手牌符合预打出约定；下一张不继承旧目标 |
| 反例 | random 仍随机、all 仍群体、无候选行为有明确规则、受保护/错误阵营目标不能选、AI 不被玩家弹窗阻塞、跨段“其”仍复用 |

未能确认的现场：用户实际选的是哪一张已保存卡/哪种表示、是否联机、所运行安装产物是否确为 alpha.5、普通入口触发的第一处断点。不要在没有这些证据时用“只是旧包/没更新”解释用户反馈。

## 7. 后续发布与环境注意事项

本轮用户只要求交接，**不要因先前的双端发布请求在本轮自行改代码或发布**。后续得到继续实施指令后再按 `docs/release/VERSIONING.md` 处理版本及双端验收。

- GitHub 网络按用户指示走 Watt Toolkit；hosts 的 127.0.0.1 映射属于该代理，不要删。不要误切系统里另一套 12000 代理。
- 引擎修改发布时通过 `docs/engine-patches/` 累积补丁留存；未授权推送上游引擎仓库。
- 当前 dist 实际指向 G 盘且空间紧张，新产物优先放 H 盘；alpha.5 产物在 `H:/g/orc-releases/2.0.0-alpha.5`。
- 已发布 alpha.5 的 Windows/Android 测试报告是历史证据，不能替代本次新增故障用例。只改交接无需重新打包。
