# 宿主侧对接报告 —— v4 引擎

> 对象：OrC-KSD 引擎团队
> 日期：2026-10-29
> 引擎基线：`v4` @ `9510cdb`（已合入 `origin/main` @ `9f36e09`）
> 宿主：`OrC-KSD.Godot` @ `ee1c685`，版本 2.0.0-beta.1
> 撰写方：宿主（Godot 客户端）侧

---

## 0. 一句话结论

**引擎这边没有"挡住发版"的问题。** 合并 `0a50f43`（成员带序 / 动作缝 / 死亡否决拆分）之后宿主零回归，
引擎全量 2133/0。

下面 6 条按"对宿主造成的实际损失"排序，前两条是**契约问题**（会让客户端写出非法声明却不报错），
中间三条是**能力缺口**（宿主已经明确拒绝并给出可读原因，不生成哑弹卡），最后一条是**工具链**。

每条都写了"怎么验"，都可以在 5 分钟内复现或否证。**如果哪条你判定不是引擎的问题，请直接说，
我会把它挪到宿主侧的欠账清单里**——这份报告的目的是分账，不是甩锅。

---

## 1. 【P0·契约】目录给出的默认值，按原样拼出来不是合法声明

### 现象

`method="catalog"` 返回的 `vocabulary.ops[op].fields` 里，每个字段都带 `initial`（默认值）。
客户端"照文档办事"——把某个 op 的所有字段按 `initial` 拼成一份声明——得到的东西**正常化器不接受**。

实测（`contract.ops[op].params` 与 `vocabulary.ops[op].fields` 逐字一致，所以这不是客户端读错了表）：

| op | 按目录默认值拼出来的声明 | 结果 |
|---|---|---|
| `heal` | `{"op":"heal","target":"self","attack":1,"defense":1,"amount":1}` | 拒绝：`heal.attack` 无适配 |
| `changeAttribute` | 含 `attack`／`defense`／`keyword`／`opCostMod`／`side`／`amount` 六个"兄弟变体"字段 | 拒绝：`字段 attack` 无适配 |
| `drawOne` | `{"op":"drawOne","side":"self","target":{…},"as":"drawn","count":1}` | 拒绝：`字段 count` 无适配 |
| `conditional` | `{"op":"conditional","condition":{"op":"and","items":[]},"then":[],"actions":[],"else":[]}` | 接受 |

### 根因（我们这边的判断，请确认）

字段表是**按原语汇总的并集**，不是逐变体的参数表；而正常化器是**按"哪些字段在场"来认变体**的。
于是：

- 填全 ⇒ 形状撑坏 ⇒ 认不出变体 ⇒ 拒绝（上表前三行）；
- 只填必填 ⇒ 变体认得出，但**全字段都标 `optional` 的 op**（`conditional`／`repeat`／`setVar`）就塌成
  `{"op":"conditional"}`，OrC 侧对缺失字段的 `undefined` 再 `JSON.parse` 一次 →
  `SyntaxError: "undefined" is not valid JSON`。

也就是说：**同一个 op，按目录填全不行、填少也不行，中间那个"刚好能过"的字段组合没有写在任何地方。**

### 造成的损失

宿主侧 161 个 DIY 动作里，**22 个曾经能跑的动作被这条打成"预览"**（`dealDamage`／`heal`／`move`／
`drawOne`／`summon`／`pin`／`fight`／`damageHQ`…）。我们最后是用"先试精简形状、过不了编译器再退回全量"
的两级判据绕过去的——**这是绕过，不是修好**：真正的契约仍然没写清楚。

### 希望引擎侧做的（二选一，或都做）

1. **目录按变体给字段表**：`vocabulary.ops[op]` 暴露 `variants: [{ fields: [...], initial: {...} }]`，
   或者至少给每个字段标上是"本变体"还是"同原语其它变体"。
2. **正常化器接受超集**：字段在场但值等于该字段的默认值时，视为"未给"，不参与变体判定。

第 2 条更省事，也能顺带把"客户端多带一个无关字段就炸"这个坑填掉。

### 怎么验

```
# 目录里 heal 的字段表
curl -s -X POST <orc-endpoint> -d '{"method":"catalog"}' | jq '.vocabulary.ops.heal.fields[].name'
# 期望：只列本变体字段；实际：target attack defense amount 四个

# 填全之后是否被拒
# 宿主侧等价断言（本地即可跑）：
dotnet test tests/Kards.Ui.Tests.csproj --filter "FullyQualifiedName~DiyOpCatalogTests"
```

宿主侧现有记账：161 个动作 / **62 个能跑** / 99 个预览。其中 **97 个**的失败原因是"这个动作没有降级分支"
（宿主欠账，见 §7），**只有 2 个**是引擎能力缺口（§3）。

---

## 2. 【P0·契约】进程级注册表没有复位口，同进程内会互相污染

### 现象

三处进程级静态状态，注册进去就出不来：

| 类型 | 现有 | 缺 |
|---|---|---|
| `Faction`（`Cards/TagData.cs`） | `RegisterCustom(id, major)` → 写进静态 `ConcurrentDictionary` | **没有任何注销/清空** |
| `KeywordRegistry` | `Register` / `RegisterOrReplace` / `Unregister(keyword)` | 只能逐条删，**没有"回到内置集"** |
| `CardEffectRegistry` | 实例级（这个没问题） | — |

对照：`ActionImplementationRegistry.Reset()`（`Actions/ActionImplementationRegistry.cs:122`）**已经有了**。
所以这不是"引擎不支持这种模式"，是另外两处没跟上。

### 造成的损失

1. **宿主 711 条单测里稳定红 2 条**，且这两条与它们测的功能无关：
   - `CrossEffectTargetTests.最后一击的回合结束效果消灭的是打出时选中的那个单位`
   - `MatchDebugTests.DebugCommandsRespectCommandLockAndCanCancelAParkedDeploymentWithoutSpendingResources`

   两条都是"前面某个用例注册过的东西还在"。我们只能加
   `[assembly: CollectionBehavior(DisableTestParallelization = true)]` 压住并行，串行仍然红。

2. **引擎自己也有一次瞬时失败**：合并 `9f36e09` 后跑全量，见过一次
   `Command_Targeter_Never_Offers_Support_Line_Interior_Slots` +
   `C1_Covert_Unit_Is_Rejected…` 两条红；用干净的上游工作树跑 1116/1116 全绿、合并后单独重跑也全绿，
   再跑全量 2133/0。**判为同源的注册表残留型 flake**——不是合并引入的，但同样说明这个状态没有复位手段。

3. 历史上更严重的一次：`Faction` 在 `CardDefinition` 构造时硬校验，导致**独立服务端子进程拒绝整个
   自定义阵营卡包**，直到卡包能自描述（本地补丁 `affcedb` 的 `factions.json`）才好。

### 希望引擎侧做的

```csharp
// 形状建议：与 ActionImplementationRegistry.Reset() 对齐
public static void ResetCustom();                 // Faction：清空自定义阵营，保留内置 11 个
public static void ResetToBuiltIn();              // KeywordRegistry：回到内置集（含 Order/BattleKeywords）
```

或者给一个统一的测试钩子 `EngineRuntime.ResetProcessWideState()`，内部依次调三者。

### 怎么验

```
cd <engine-root>
dotnet test 2>&1 | tail -5          # 期望 2133/0
# 反证：把这两条单独跑
dotnet test --filter "FullyQualifiedName~CrossEffectTargetTests|FullyQualifiedName~MatchDebugTests"
```

---

## 3. 【P1·能力】运行期给目标挂效果，没有入口

### 现象

宿主侧 `bindEvent` / `grantEffect`（"给某张卡挂一段效果程序，离场时撤销"）在 v4 里没有对应入口，
现在**明确拒绝**并给出原因：

```
OrC-KSD 尚无无损执行适配：给目标临时附加效果（bindEvent／grantEffect）：
v4 没有运行期挂载效果快照的入口。完整声明保留可编辑。
```

### 影响面

宿主 DIY 目录里 **2 个动作**卡在这里（占 99 个预览的 2%）。数量小，但语义上重要——
"亡语：给某个单位附加一个效果"是玩家最常写的一类复杂机制，也是 `Kards` 原版卡里很常见的一档。

### 希望引擎侧做的

一个 `EffectRuntime` 入口即可，形状类似：

```csharp
Task<IDisposable?> AttachEffectAsync(Card host, string effectProgramId, EffectDuration duration, CancellationToken ct);
```

要点是**生命周期跟着宿主走**（宿主离场/死亡自动撤销），这一点引擎的 `AuraDeclaration` 已经做对了，
可以照抄。

### 怎么验

宿主侧现有判据（会断言"必须给出可读原因"，而不是生成哑弹卡）：

```
dotnet test tests/Kards.Ui.Tests.csproj --filter "FullyQualifiedName~ComplexDiyTests"
```

引擎侧补上入口后，这条会从"必须拒绝"变成"必须能跑"，宿主会同步改判据。

---

## 4. 【P2·能力】光环只能"描述式"，没有逐候选谓词

### 现象

`EffectAuraFilter`（`Effects/EffectRuntime.cs:50`）是 6 个字段的描述式谓词：

```csharp
public sealed record EffectAuraFilter(
    string? Side, string? Faction, string? UnitType,
    string? Keyword, bool ExcludeSelf, string? Zone);
```

没有"逐候选跑一段程序"的位置。所以宿主**拒绝**这些形态并写明原因：

- 光环带条件（"友方单位获得 +1，只要你的总部生命 ≥ 10"）
- 光环带 `where` 逐候选求值
- 光环 `set` 模式（只有常量增量）
- 光环包含多个动作

### 一条对我们自己的批评（不是你们的问题）

`EffectAuraFilter` 的 `Faction`／`UnitType`／`Keyword` 三个字段，**宿主当前一个都没用**——
我们的 `DesktopNativeCodegen.Aura()` 只发了 `Side`／`Zone`／`ExcludeSelf`。
也就是说"光环只惠及某兵种/某词条"这类形态，**引擎今天就支持，是宿主没接**。这条我们自己改，不占你们排期。

### 希望引擎侧做的

只针对"带条件的光环"：如果 `EffectAuraFilter` 能接受一个 `Func<Card, bool>` 或一个 csx 谓词片段，
宿主就能把 `where` 无损翻过去。其余形态我们继续拒绝，不硬翻（硬翻会丢掉条件，玩家看到数值不对却查不出原因，
比"不支持"更糟）。

### 怎么验

```
dotnet test tests/Kards.Ui.Tests.csproj --filter "FullyQualifiedName~ComplexDiyTests"
# 其中「拒绝的形态给出可读原因」这条覆盖「光环带条件」
```

---

## 5. 【P3·可见性】`KeywordProgramRuntime` 的匹配入口是 `internal`

### 现象

```csharp
// src/Orc.Game/Cards/ProgramKeywordComponent.cs:502
internal static Match MatchFor(LogicEngine engine)
internal static Match? TryMatchFor(LogicEngine engine)
```

宿主需要回答"这个单位当前是否具备某个关键词程序效果"（用于 DIY 卡的效果预览与合法性提示），
现在只能自己重写一份判定，**必然与引擎漂移**。

### 希望引擎侧做的

把 `TryMatchFor` 提成 `public`（`MatchFor` 会抛，`TryMatchFor` 不会，宿主只需要后者）。
如果担心暴露 `Match` 的太多面，包一层只读视图也行：

```csharp
public static bool TryHasKeywordProgram(Card card, string keyword, out string? programId);
```

### 怎么验

```
cd <engine-root>
grep -n "internal static Match" src/Orc.Game/Cards/ProgramKeywordComponent.cs
```

---

## 6. 【P4·协议】在线对局的效果执行：引擎不代选目标（R2）

### 现象

`EffectRuntime.SubmitAsync` 在没有前端桥时抛 `TargeterFailureReason.BridgeNotAssembled`：

```
效果选靶 sel=… 失败：BridgeNotAssembled（引擎不代选，R2）。
```

这条设计**我们认同**（引擎不自作主张选目标是对的）。但它意味着：
**服务端跑一段 DIY 效果时，需要选靶的动作无法完成**，除非客户端在线应答。

### 影响

宿主侧在线对局的 DIY 效果目前跑不起来，有两层原因：
- 第一层（宿主）：我们没有把"选靶请求"从服务端下发到客户端并回传的通道接完；
- 第二层（协议）：**谁选靶、怎么下发、超时怎么办、双方状态不一致时以谁为准**——这需要协议层的决定，
  不是宿主单方面能定的。

### 希望引擎侧做的

不需要改代码，只需要一个**协议答复**：服务端发起选靶时，引擎期望的请求/应答形状是什么，
以及"应答超时"的语义（中止这一段效果？还是回滚整条打出链？）。
宿主侧的 `ctx.Interrupt()` 现在是"中止整条打出链并回滚"，我们倾向沿用，但想确认这是引擎的意图。

### 怎么验

```
# 宿主侧现状（会失败，是已知缺口）
dotnet test tests/Kards.Ui.Tests.csproj --filter "FullyQualifiedName~NetworkWorkshop|FullyQualifiedName~RemotePresentation"
```

---

## 7. 【P5·工具链】构建输出槽位是跨仓库共享的可变状态

### 现象

`Directory.Build.props` 把 `ArtifactsPath` 固定到 `artifacts/slot`，布局是
`artifacts/slot/bin/<工程名>/<config>/`。

当**另一个仓库**（宿主）以 `ProjectReference` 方式构建引擎工程时，同一个槽位会被两边交替写，
结果是一个**存在但不可运行**的目录：有 `Orc.Server.dll`，缺 `Orc.Game.dll`。
宿主 `ServerLauncher.Runnable()` 现在就是靠"同目录下有没有 `Orc.Game.dll`"来判这个。

我们踩过两次：一次是 `--verify-keyword-movement` 拿到了**假绿**（跑的其实是旧 dll），
一次是发布门禁报 `FileNotFoundException: 找不到 Orc.Server.dll`。

### 建议（不一定要改）

- 槽位目录名带上消费者标识（`<工程>/<config>/` → 或按 `$(MSBuildProjectName)` 隔离已够，
  问题在依赖副本被清）；
- 或者宿主侧在构建后校验"依赖副本齐全"再启动（这个我们已经做了，见 `ServerLauncher.Runnable`）。

优先级最低，写在这里只是让两边对同一件事有同一个说法。

### 怎么验

```
cd <engine-root>
ls artifacts/slot/bin/Orc.Server/*/     # 看是否同时有 Orc.Server.dll 与 Orc.Game.dll
```

---

## 8. 上游同步情况（两个本地补丁仍未吸收）

合并 `9f36e09` 之后逐项核对，两个本地补丁上游**没有等价实现**：

| 本地提交 | 内容 | 上游状态 |
|---|---|---|
| `a134486` | `MatchOptions.ScriptEvaluator`：允许宿主在建局前交入 csx 求值器（按字节加载程序集） | `origin/main` 只有 `Engine.ScriptEvaluator ??= new CSharpScriptEvaluator()`（`Match/Match.cs:187`）——引擎自建默认，宿主无法注入 |
| `affcedb` | 包内 `factions.json` 自描述阵营，逐资产诊断送进读面 | `origin/main` 只在测试里出现 `factions.json` 字样，无读取实现 |

这两条**宿主必需**（前者是单文件发布下加载脚本程序集的唯一途径，后者是自定义阵营卡包能被独立服务端接受的前提）。
如果你们打算在上游自己实现，请告诉我们形状，我们把补丁撤掉跟随上游；否则它们会一直挂在 `v4` 分支上。

---

## 9. 明确**不**推给引擎的（宿主欠账，我们自己改）

写在这里是为了让分账清楚：

1. **99 个预览动作里 97 个是"没有降级分支"**——宿主 `DesktopNativeCodegen` / `DesktopNativeAliases`
   没写映射。其中一批是纯别名（`buff`／`debuff`／`grant`／`deckToHand`／`drawUntil`／`mill`／`hqKeyword`…），
   落到引擎**已有的** 25 个 `EffectRuntime` 入口上即可，不需要引擎改任何东西。
2. **光环的 `Faction`／`UnitType`／`Keyword` 过滤没接**（见 §4）。
3. **在线选靶通道没接完**（§6 第一层）。
4. **`DiyInjectionCatalog.EngineRestriction` 里有一段过期文案**（"非空 band 会被隔离，不会生效"），
   是 `0a50f43` 之前的结论，需要产品确认后改写。

---

## 10. 附：引擎 `EffectRuntime` 现有公开入口（25 个）

宿主把 DIY 动作往这 25 个入口上映射，所以这张表就是"还差多少"的分母：

```
AddToHand  Battle      Buff        CostMod     Damage      DeclareAura
DeployCostMod          Draw        FetchFromDeck           GainPointSlots
GainPoints Grant       Kill        LosePointSlots          LosePoints
Move       Pin         RerunAuras  Reveal      Revoke      SetKeywordValue
ShuffleIn  Silence     Upgrade     Select
```

如果其中有哪个入口你们计划在下个版本**改签名或删掉**，请提前说——宿主有 97 条映射要落在上面。

---

## 复现清单（宿主侧一条命令）

```
cd "H:\Working Folder\OrC-KSD.Godot"
dotnet test tests/Kards.Ui.Tests.csproj --nologo
# 期望：2 失败 / 709 通过 / 711
#   红的 2 条＝§2 的注册表残留，与本次改动无关
# 绿的关键几组：
#   ComplexDiyTests            6 条  复杂 DIY 真引擎判据
#   DiyOpCatalogTests          4 条  161/62/99 记账与内容判据
#   DiyMechanismExecutionTests 4 条  多子句 / 敌方总部 / 对手抽牌 / 光环生命周期
```
