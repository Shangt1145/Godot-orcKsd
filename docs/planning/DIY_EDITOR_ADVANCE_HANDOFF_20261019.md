# DIY 编辑器更新：提前交接与后续执行方案 · 2026-10-19

> **交接状态：实现中，尚未完成图形／实机验收，不可据此宣称整轮完成。**
>
> 用户最新要求：“先写一个超前的交接文档，因为你额度不够了”。因此在当前代码状态交接，先停止继续扩展功能。已有代码保留在工作区，没有提交、没有发布。
>
> 本文区分聊天中用户直接要求、设计文档建议、实际源码契约，以及交接者对下一步的建议。设计文档不是比用户指令更高的权限来源。

## 1. 用户目标、已定决策与不可越界项

### 1.1 本轮直接请求

用户指定 `docs/design/DIY_EDITOR_UPDATE_20261019.md`，要求“进行调研并开始工作”。该方案分为：

- P0：逐效果能力门 A、`owner.controls.type` 条件 B。
- P1：对局共享计数器 C。
- P2：注入时机 `injects.band` 的只读展示与校验 D。

方案末尾的三个待决策，已通过聊天提问得到用户明确选择：

**允许中文计数器名称；同时开放计数伤害与增益；能力提示覆盖旧简易效果行。**

不要在下一轮再次询问这些已经决定的事项。

### 1.2 持续有效的边界

1. **不要改对局界面。** 用户曾因误改对局 UI 强烈纠正。当前轮只改 DIY 编辑与桥接能力，没有编辑 BattleScreen/BattleCard 的视觉或布局。
2. 本轮不修改原生引擎仓库代码；UI 只呈现和编辑数据，规则编译与执行留在桥／引擎。
3. 不做 wrapper 可视化编辑，不把仅代码侧能力做成假表单。
4. 不把“能保存”当作“能对局”，不把编译／实例化测试当作真实鼠标操作和帧度量。
5. 未知或未完整适配的原始声明要保留并明确诊断，不猜语义、不中途丢字段。
6. 没有用户要求的新线程／子代理；本轮没有使用子代理。
7. 工作区有大量此前改动，包括用户已有引擎桥改动、文档搬迁、对局交互修复等；**不能整仓 reset、不能按 git status 批量还原对局文件。**

## 2. 环境与工作目录

| 项 | 当前路径／口径 |
| --- | --- |
| 主源码工作区 | `H:/Working Folder/OrC-KSD.Godot` |
| 实际运行／构建短目录 | `H:/g/kards` |
| 原生引擎检出 | `H:/Working Folder/OrC-KSD-github` |
| 桥项目默认引擎根 | `H:/Working Folder/OrC-KSD-github/src/Orc.Game` |
| Godot | `H:/g/tools/godot47/Godot_v4.7-stable_mono_win64/Godot_v4.7-stable_mono_win64_console.exe` |
| UI／桥目标框架 | net8.0 |
| xUnit 测试项目 | `tests/Kards.Ui.Tests.csproj`，net9.0 |
| Shell | Windows PowerShell；中文 Python 输出用 `PYTHONIOENCODING=utf-8` |

编辑主工作区；运行前通过 `tools/prepare.ps1 -SkipAssets` 同步到短目录。源码已同步并构建，**既有 dist 安装包未重新制作**。验证命令采用隔离的 `res://artifacts/...` 数据路径，不能把验证改成写用户的真实卡库。

## 3. 调研结论：设计文件中必须校正的地方

以下均来自本轮读取的当前源码，而不是凭旧记忆假设。

### 3.1 条件曾被触发守卫覆盖

`proto/bridge/Kards.Ui.OrcBridge/OrcImportService.cs` 的 `CompileOrcEffect` 会为 SelfUnit／SelfCard 等触发组合加入宿主守卫。原来 `CloneWithCondition(op, condition)` 直接替换原动作 Condition。

如果只给 UI 增加兵种条件，实际编译后可能只剩“事件指向自己”，新增条件就消失。

**已改为原生 `DslCondition.AllKind` 合取：触发守卫 AND 动作条件。** 仍使用引擎现有编译器，不在 UI 判断兵种数量。

### 3.2 当前引擎没有设计文件列出的部分 band 类型

当前检出的真实枚举为：

- `Orc.Cards.OrderFlowBands`：Counter、Resolve、Finalize。
- `Orc.Cards.AttackFlowBands`：Counter、Resolve、Finalize。
- `Orc.Cards.DamageFlowBands`：PreApply、Apply。
- `Orc.Core.DefaultBands`：Default。

没有检出设计文件中的 `ActionTriggerBands`／`ActionHandleBands`，因此没有照抄 Initiate／Commit／Rewrite／Veto 等未来词表。

更重要的是，当前 `src/Orc.Game/Cards/EffectSystem.cs` 的 `ApplyFrameworkInjects`（约 311 行）会对**所有非空 BandName**记录隔离并跳过。这比设计文件所说“有效具名 band 可执行”更窄。

**当前实现：保留并展示原声明；空白 band 显示默认；未知 band 给明确错误；即使 Counter 是真实枚举成员，也提示当前预制体注入路径不支持，标为仅草稿。**

不要为了让 P2 看起来通过而新增引擎能力，或把“词表中存在”误判为“当前注入路径支持”。

### 3.3 变量不是存档恢复系统

已确认 `src/Orc/Core/EngineVariables.cs`：对局实例级、强制同 key 同运行时类型、不进入快照、不回滚、不发信号。

**新引擎实例缺失计数器，按 0 读取；同一实例读取快照并不会自动清空变量。** 界面已加入提示。当前测试证明变量不在 `SnapshotJson.SerializeAll` 输出、不同引擎实例隔离；没有新增真正的读档系统，不能声称已经做了完整 save/load 往返。

### 3.4 文档的编辑器现状描述有部分过时

实际完整 Inspector 已是 Godot 原生的 `NativeEffectInspector` 及 Friendly 表单，并非网页 UI。DesktopAuthoring 仍用于语法目录／声明处理，不要据文档重建一个 Web 编辑器。

`OrcEffectEditing.Compile` 原先逐 op 编译时，多个同触发效果可能复用相同 prefab id。本轮加入效果索引／op 索引，保证每个结构动作预制体身份独立；这需要额外做旧功能回归。

### 3.5 条件的“往返”证据要分层

`unitTypes/comparison/value` 保存在编辑 DTO 和原生 DSL JSON 中。最终已编译的 EffectSnapshot 主要存生成后的 csx 守卫，不会仍然保留这三个 DSL 字段。

当前测试分别验证 DSL JSON 三维完整、草稿导出导入、深拷贝，以及编译后代码含真实 OwnerUnits 统计与宿主守卫。不要用“最终快照 JSON 仍有 unitTypes”作为错误验收标准。

## 4. 当前已落地的文件与行为

### 4.1 DTO、存储与编译

`proto/contracts/UiDiyCard.cs`：

- `OrcOpDraft` 增加 `CondUnitTypes`、`CondComparison`、`CondValue`、`Counter`、`AmountFromCounter`。
- `AmountFromCounter` 实际采用 `bool`，旧草稿缺字段默认 false。
- 新增 `OrcOpDraft.Snapshot()`，深拷贝条件兵种数组。
- `OrcEffectDraft.Snapshot()` 改为逐 op Snapshot，不再只复制数组壳。
- `OrcEffectDraft.PrefabJson`：导入的原生 EffectSnapshot JSON，与 DesktopJson 区分，原文保留、只读展示。

`proto/core/DiyCardLibrary.cs`：

- 对新条件数组做受限形状检查。
- 允许带 PrefabJson 且没有结构 Ops 的效果。
- 校验 PrefabJson 为对象且长度受限。

`proto/bridge/Kards.Ui.OrcBridge/OrcEffectEditing.cs`：

- Conditions 目录新增 `owner.controls.type`，比较词表由桥给出。
- BuildCondition 校验兵种、比较符和数量，并传入引擎三维字段。
- ToDraft 保存三维参数；Summarize 有中文条件摘要。
- Ops 目录新增 counter.add/sub/set。
- 计数器／计数数值通过 OrcCounterCodegen 生成 csx，并将 parsed effect 的 NativeGenerated 置 true。
- Compile 的预制体身份加入 effectIndex、opIndex。
- 支持只读原生 PrefabJson：先做 band 诊断，再用引擎 PrefabJson.Deserialize 读回。

`proto/bridge/Kards.Ui.OrcBridge/OrcImportService.cs`：

- 原触发守卫与动作条件改为原生 all 合取，避免条件丢失。
- 本轮没有给现有模板盲目增加自由 injects 表单参数。

### 4.2 能力门

新增 `proto/bridge/Kards.Ui.OrcBridge/DiyCapability.cs`：

- `DiyPlayability`、`DiyCapabilityRow`、`DiyCapability.Probe`。
- Source 区分 structured／legacy／text／card。
- 结构效果逐条复用 OrcEffectEditing.Compile，并检查动态效果实例化结果。
- 旧 Effects 简易行逐行复用桥编译。
- 卡面级准入错误独立诊断，使用剥离真实效果后的合法基线隔离卡面错误。
- 不做自动修复，不用自造规则预测编译结果。

新增 `proto/ui/components/DiyCapabilityBadge.cs`：

- 标准 Godot Button／Label，不通过 `_Draw` 画徽标。
- 可对局／仅草稿；点击后就地展开原因。

新增 `proto/ui/screens/NativeEffectInspector.Capabilities.cs`：

- 将能力状态映射到原生 Friendly Inspector 的效果列表和当前效果标题。
- 更新状态时不重新创建整个输入表单。

### 4.3 计数器

新增 `proto/bridge/Kards.Ui.OrcBridge/OrcCounterCodegen.cs`：

- 名称 Normalize：Trim＋Unicode FormC，长度 1–40，允许中文／字母数字／空格／下划线／连字符。
- key 固定 `diy.counter.<规范化名称>`，类型固定 int，缺失按 0，生成时字符串通过 JSON 字面量转义。
- add 防 int 溢出；sub 最低 0；set 明确设值。
- damage 读取计数值；buff 将攻击／防御增减参数作为计数的倍数，走原生 BuffAsync，并支持原有期限。
- 真实伤害、选靶、增益全部经引擎 EffectRuntime；没有 UI 模拟伤害。
- CountersIn 从结构 ops 反推，Rename 批量改引用且不修改原快照。
- 作者提示说明：本局双方同名计数器共享、不随读档或回放恢复。

**当前有一项明确的参数限制：** 计数伤害／增益在 one／random 选靶下，若同时指定 Faction 过滤或 ExcludeSelf，会明确拒绝。当前原生 EffectSelector 不直接携带这两维，不能先选后过滤假装等价。all／self 路径可处理它们。下一轮应考虑把这个限制在 UI 控件上更清楚地表达，或查找完整原生选靶接口后补齐；不要静默删除原参数。

### 4.4 注入只读视图

新增 `proto/bridge/Kards.Ui.OrcBridge/DiyInjectionCatalog.cs`：

- Schemes 按当前真实引擎枚举列出。
- Read 兼容快照对象和编译产物数组，提取 target／band／event／priority。
- 只读摘要“注入 → 目标 · 区段 · 事件 · 优先级”。
- 对非空 band 给当前引擎限制提示；未知 band 额外指出拼写问题。

`BuiltInWorkshopCatalog.cs`：

- 原生包卡若没有 Desktop 图但拥有内联预制体，会尝试从包内容的 InlinePrefabs 按声明 id 原样载入 PrefabJson。
- 不应为了恢复结构可编辑性而丢弃原生预制体的 injects。
- 外部效果库未能完整载入时仍要明确保留不可执行诊断，不擅自降为无效果卡。

### 4.5 编辑器界面

`proto/ui/screens/DiyEffectEditorScreen.cs`：

- 增加头部“效果 N 条 · 可进对局 N · 仅草稿 N”。
- 卡面错误独立一行。
- 结构效果、原生 Friendly 效果和旧简易行都接能力徽标。
- 保存提示区分全可用、部分效果仅草稿、卡面准入错误。
- 编辑标签始终可见，结构行不再只能由隐藏兼容探针访问。
- OrcOpRow 增加兵种 chips＋添加兵种、比较下拉、数量输入。
- 共享条件控件放在动作参数之前。
- 增加计数器名与“数值来自计数器”，上方列出派生引用及重命名按钮。
- PrefabJson 原文只读展示，附注入时机和红色诊断。
- 暴露 StructureRows／OpRows 等 internal 控件用于后续 UI 验证。

`NativeEffectInspector.Friendly.cs`：

- 列表和当前标题旁增加徽标。
- 遇到原生 owner.controls.type 条件可只读显示三维参数。

`DiyEditorScreen.cs`：

- 带原生 PrefabJson 的完整草稿允许保留；不能因不能执行而丢弃原文。

**上述新 UI 都尚未做截图验收。不要引用前一轮截图证明本轮界面正确。**

## 5. 已执行的验证与准确结论

### 5.1 当前轮构建

执行了：

```powershell
.\tools\prepare.ps1 -SkipAssets
dotnet build H:\g\kards\Kards.Ui.csproj --nologo
```

最后一次完整 UI 构建通过：0 warnings／0 errors。

### 5.2 当前轮新增单测

新增 `tests/DiyEditorUpdateTests.cs`，执行：

```powershell
dotnet test H:\g\kards\tests\Kards.Ui.Tests.csproj --filter FullyQualifiedName~DiyEditorUpdateTests --nologo
```

**7 项全部通过，耗时约 2 秒。** 测试项目仍有之前的 WorkshopTests xUnit2031 分析器警告。

七项分别证明：

1. 每条效果独立可用性；一个可编译、一个未知动作；旧简易行与卡面错误分开。
2. owner.controls.type 的 DSL 三维往返、数组深拷贝、ORCDIY1 兼容、编译代码含宿主守卫。
3. 中文计数器名、名称字面量边界、批量重命名、生成脚本可实例化。
4. 变量不在实体快照输出中；新引擎实例读为 0；旧实例变量没有被快照读取自动清除。
5. band 目录与当前四种实际枚举逐项一致；未知 band／当前不支持的具名 band 被阻止；默认注入原文得以保留。
6. **真实原生对局 API 测试**：单位 +2 计数→按计数增益，2/10 变 4/14；另一张指令再 +1，按计数造成 3 点伤害，防御变 11。
7. **真实原生对局 API 测试**：兵种条件未满足不执行；另一单位部署后，原宿主不会错误响应其事件。

这里的“真实原生对局 API”不是 Godot 鼠标录制，不等于设计文档要求的 capture-real 帧度量。

### 5.3 还没有执行的验收

- 没有为本轮新界面拍任何截图。
- 没有跑本轮 `--verify-ui`。
- 没有新增／执行 `--verify-diy-syntax` 或 `--capture-diy-syntax`；这些入口**尚不存在**。
- 没有跑本轮的 `--capture-real`，也没有本轮计数器的真实拖拽／伤害帧证据。
- 没有在本轮代码后重跑此前的 41 项 DIY／工坊相关测试。
- 没有在本轮代码后重跑旧 DIY 图形操作／原生 Inspector 验证。
- 没有为导入包的 injects 做完整 Godot 界面验收。
- 没有完整存档读入系统测试；只能宣称变量不进入现有快照且新引擎实例独立。
- 没有提交／发布／重打包。

## 6. 下一轮应先做的事情（按依赖顺序）

### 第一步：先确认接续状态，别重做已有功能

1. 读本文和设计文件。
2. 查看关键新文件与 git diff，确认用户是否又有新改动。
3. 构建并先复跑新增 7 项测试；出现问题先按源码排查，不改引擎兜底。
4. 用下一节的 UI 探针方案，补齐实际视觉和数据写回证据。

### 第二步：补一个独立的 DIY 语法验收入口

建议新增 `proto/ui/screens/Main.DiySyntaxVerification.cs`，在 Main 的参数分派中加入：

- `--verify-diy-syntax`
- `--capture-diy-syntax`

注意：目前只有建议，**文件和分派尚未写**。

建议探针流程：

1. 使用现有验证隔离数据，创建测试工坊包。
2. 从卡牌编辑器实际点击“打开效果编辑器”，确认 `_active == "diyfx"`。
3. 结构条件面用真实控件操作：选 owner.controls.type，添加 infantry／tank chips，改比较值；断言主卡草稿同步，再保存、重开、导出／导入。
4. 打开一张含一条可编译 Desktop 图、一条不支持图的草稿；检查两处徽标、点“仅草稿”展开原因，保存后文案有具体数量。
5. 旧简易行也检查徽标，避免只给新链路做能力门。
6. 打开计数器单位，修改 CounterName、勾选 AmountFromCounter；使用派生引用区的“重命名引用”按钮，断言各 op 引用全部更新。
7. 保存单位与计数伤害指令，校验后进入构筑目录。
8. 打开带默认 injects 的原生预制体，检查只读目标／事件；打开错误 band 版本，检查红色原因。
9. 在 1600×900、1280×720 截图，确认不会因为原因展开／兵种 chips／计数器区把表单撑出窗口。

可复用工具和入口：

- `Main.DiyVerification.cs`：DiyClickAsync、DiyCaptureAsync 和现有 DIY 保存重开流程。
- `Main.DeckVerification.cs`：DeckClickAsync 已会向上寻找 ScrollContainer 并滚动显示目标。
- `_diyEffects.StructureRows[index].OpRows[index]`：能访问 ConditionPick／ConditionComparison／ConditionNumber／CounterName／CounterAmount。
- `_diyEffects.NativeInspector`：完整 Friendly 表单，能力徽标会按目录索引映射。

### 第三步：补真实拖拽和帧度量，保持对局 UI 不变

设计文件要求进对局结论来自真实鼠标流程。建议在专用探针中启动现有 BattleScreen，或在 `--capture-real` 的专用场景分派中调用它，**不要改 BattleScreen 的视觉实现**。

已有可复用 API：

```csharp
await StartRealMatchAsync(
    interactiveMulligan:false,
    manualOpponentForVerification:true,
    deckSeed:31,
    verificationHand:[unit.Id,order.Id],
    firstPlayerIndex:0);
await _battle.DragAsync(unitUid,_battle.DeploymentGapPoint(0));
```

- StartRealMatchAsync 位于 `Main.cs`，已有 verificationHand 参数。
- `BattleScreen.RealCapture.cs` 已提供 DragAsync。
- `BattleScreen.ActionPortalVerification.cs` 已提供 DeploymentGapPoint。
- `Main.cs` 已有 AwaitBoardAsync／AwaitIdleAsync／ShotAsync。
- 无选靶指令的拖拽参考 `Main.TargetDragVerification.cs` 的 VerifyNoTargetDragAsync；不应假定存在 OrderDropPoint 方法。
- 帧证据同时保存前／后截图、引擎投影值、出牌事件、像素或帧时序度量，而不只输出一句 PASS。

为避免随机先手使测试误判，验证夹具固定 firstPlayerIndex:0。产品先后手随机逻辑不改。

### 第四步：回归与说明

- 新 7 项＋此前 41 项先跑。
- `--verify-diy`、`--verify-ui`、相关 Native Inspector 图形探针。
- UI 双尺寸截图。
- 专项原生拖拽帧证据。
- 整理实施／验收记录，并明确 P2 的实际引擎限制。
- 只有全部必要证据到位，才可说这一轮完成。没有发布授权／需求时，不擅自推送或打 release。

## 7. 已知实现风险与需要复核的具体点

这些是接续者的核对清单，不是已经确认的全部故障。

1. **新布局未经图形验收。** `OrcOpRow` 仍在四列 GridContainer 中加入条件 chips 和计数器控件，可能过挤；错误原因展开也可能提高最小宽度。
2. **能力徽标的索引映射。** Native Friendly 列表只包含 DesktopJson 效果，结构／Prefab 效果在另一页。混合草稿要核对局部索引映射，不能把 A 的错误贴到 B。
3. **不应打断输入。** NativeInspector.SetCapabilities 不重建输入表单；计数器引用区用签名避免每次定时校验都销毁正在编辑的重命名输入框。要用实机确认。
4. **能力汇总刷新顺序。** Open 会先校验再 NativeInspector.Open，需确认初次打开和切换卡牌时不残留上一张牌的徽标状态。
5. **重复原生 prefab id。** 结构行已加入 effectIndex/opIndex，手工导入的两个 PrefabJson 仍可能使用相同 root.id；需要增加整卡级身份冲突诊断，不能每条绿而整卡注册失败。
6. **仅存档声明的旧简易行优先级。** OrcEffects 存在时旧 Effects 不参与执行，但仍单独显示能力。汇总／文案最好明确“当前优先执行结构效果”，避免作者以为两组会同时运行。
7. **原生预制体的未知字段与注入目标。** band 校验并不能证明插件自定义 target 已登记，也不能替代真实装载；必要时把未证实的目标状态与“可对局”细分说明。
8. **计数器选靶组合限制**见 4.3；不要静默删 Faction／ExcludeSelf。
9. **计数器是双方共享的全局名称。** 同名跨卡共享是用户接受的对局变量方向，但必须保持提示可见，不能让作者误以为自动按玩家隔离。
10. **快照与回滚。** 不要因文案“归零”而在任意读快照时主动清空同一引擎 Variables，那会改变引擎现成语义。
11. **能力门不应发明规则。** 设计文件提“名称重复”等失败只是例子；只有现有编译器／引擎真正拒绝的约束才应作为准入条件。
12. **PrefabJson 只读路径与普通 Inspector 保存。** FromDraft 只向 Desktop Inspector 传 DesktopJson；其他效果由兼容页保留。验证切页、保存、撤销时原始 PrefabJson 未丢。

## 8. 与本轮之前工作有关的背景（不要误回滚）

用户先前要求全面改菜单与 DIY，但明确禁止动对局 UI；随后要求工坊首页两张卡、逐级选择，并提供 KARDS 视频纠正动画。

已有并应保留：

- 工坊首页“卡包／扩展包”，下一组“创建／修改”。
- 正面牌从中央叠放处横向展开，选择后向中央收拢、选中牌置顶，再进入下一组；没有翻背／放大／侧飞。
- 国家候选来自后端全部内置国家，旧四国别名保留；自定义国家入口与图标。
- 卡牌编辑分“属性／规则与词条／卡图／风味”。
- 内置／自带包可进入修改，保留原卡编号、保存本地覆盖、同步图鉴／构筑／原生执行、支持恢复原版。
- `MenuStyles` 与对局旧 `UiStyles` 隔离。

参考文档：

- `docs/design/DIY_WORKSHOP_REWORK_20261019.md`
- `docs/design/BUILTIN_WORKSHOP_EDIT_20261019.md`

旧证据路径（**仅证明前一轮，不证明当前新 UI**）：

- `artifacts/design/workshop-choice-v3/`
- `artifacts/design/builtin-edit-20261019/`

之前较广的 148 项回归中有 6 项既有失败：

- 3 个 ActualNativeMatchReceivesExactCompositionAndSelectedCount 牌数断言。
- 2 个 DIY 先手断言（NotYourTurn）。
- 1 个直接 Match 初始化缺 combat.target.legal 注册。

在仅撤回上一轮装配改动的对照构建中复现过，日志：`artifacts/design/builtin-edit-20261019/baseline-assembly-test.txt`。不要顺手改产品先手或对局规则让旧测试绿。

## 9. 构建和测试命令

```powershell
Set-Location -LiteralPath 'H:\Working Folder\OrC-KSD.Godot'
.\tools\prepare.ps1 -SkipAssets
dotnet build H:\g\kards\Kards.Ui.csproj --nologo

dotnet test H:\g\kards\tests\Kards.Ui.Tests.csproj --filter FullyQualifiedName~DiyEditorUpdateTests --nologo

dotnet test H:\g\kards\tests\Kards.Ui.Tests.csproj --filter 'FullyQualifiedName~DiyEditorUpdateTests|FullyQualifiedName~BuiltInWorkshopTests|FullyQualifiedName~WorkshopTests|FullyQualifiedName~DiyCardTests' --nologo

& 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe' --path H:\g\kards -- --verify-ui
& 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe' --path H:\g\kards -- --verify-diy
```

本轮建议新增的 syntax 参数目前不存在，不能直接照抄当作已有能力。

如果在运行短目录做临时对照构建，务必保存并还原文件，随后执行一次 `dotnet build ... --no-incremental`：之前遇到过对照 DLL 时间比源文件新，普通增量构建误用旧程序集。更推荐另外的对照构建目录，避免污染当前运行产物。

## 10. 可直接用于下一轮的接续提示

> 请先读 `H:/Working Folder/OrC-KSD.Godot/docs/planning/DIY_EDITOR_ADVANCE_HANDOFF_20261019.md`，接着完成 `docs/design/DIY_EDITOR_UPDATE_20261019.md` 的实现和验收。用户已经决定允许中文计数器、支持计数伤害与增益、能力门覆盖旧简易行；不要再次提问。当前已有 DTO、能力门、条件、计数器、原生预制体只读注入视图，并且新增 7 项测试通过，但新 UI 没有截图／实机验收，syntax 探针尚未写。先核对当前源代码和引擎真实契约，再补专用 UI 探针、双尺寸截图、真实拖拽帧证据和旧功能回归。当前引擎对预制体注入只接受空白 band，不要照设计文件虚报具名 band 支持。不要修改对局界面或原生引擎，不要 reset 现有未提交改动。最后准确报告通过项、限制和剩余项。
