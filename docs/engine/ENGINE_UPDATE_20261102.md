# 引擎更新调研：能力分区、信号守卫与效果模板库（2026-11-02 版）

> 调研对象：`hitman-OLD6/OrC-KSD` `main` 分支 `299d93d`（2026-11-02 04:05 UTC / 12:05 北京时间）
> 对照基线：`d366c85`（2026-11-01 07:19 UTC），即上一轮调研文档 `ENGINE_UPDATE_20261101.md` 的对象
> 调研方式：`git fetch` 仍连不上 `github.com:443`（代理 `127.0.0.1:14082` 无进程监听），
> `api.github.com` 也失败；改从 `codeload.github.com` 取 `299d93d` 的 `tar.gz`（4.8 MB）解包，
> 与本地留存的 `d366c85` 全树做**字节级 diff**
> 结论一句话：**handler DSL 的语法一个字没改，但目录多了一条硬规矩——「哪些东西现在跑不起来」必须诚实标出来；
> 而我们上一版刚发的积木编辑器正好违反了它（`choice` 被当成普通积木摆着）。**

---

## 1 这次更新是哪两个提交

| 提交 | 时间（UTC） | 内容 |
|---|---|---|
| `52f41b3` | 11-02 04:05:13 | feat: 效果模板库与信号守卫，DSL 生成器支持 guard |
| `299d93d` | 11-02 04:05:19 | docs: 契约包补充 guard 语法与目录 schema |

两条提交相差 6 秒，实质是一次「代码 + 文档」的成对提交。改动面比上一轮大：

- 契约包 4 个文件（`SPEC.md`、`FRONTEND-GUIDE.md`、`catalog.schema.json`、`prefab-description.sample.json`）
- `src/` 下 20 个文件修改、3 个新文件（`SignalGuards.cs`、`EffectGrantService.cs`、`EffectTemplateLibrary.cs`）
- 新增 10 个测试文件（`SignalGuardTests`、`GuardGenerationTests`、`EffectTemplateLibraryTests`、
  `EffectTemplateFlattenTests`、`TemplateDescriptionTests`、`EffectGrantTests` 等，合计 1151 行）

**没动的**（这点很重要，决定了我们要改多少）：

| 文件 | 状态 |
|---|---|
| `schema.json`（DSL 文档语法） | **未改** —— 没有新字段、没有新原语 |
| `EXCLUSIONS.md` | **未改** |
| `catalog.snapshot.json` | **未改**（仍是 27 节点、无 `stage`、无 `builtInOps`） |
| `vectors/check`（24 个）、`vectors/gen` | **未改** —— 一条新向量都没加 |
| 13 个错误码 | **未改**（`HandlerDslContract.cs` 里的小写字面量集合完全相同） |

也就是说：**能写出来的东西没变，变的是「写出来的东西能不能跑」这件事必须被说出来。**

---

## 2 契约唯一的硬变更：§12.11 能力分区

### 2.1 新增的两张表

目录对「每一个可拖的东西」给出分区，取值 `alpha`（这版真能跑）／`preview`（目录里有、现在跑不起来）：

| 对象 | 承载 | 说明 |
|---|---|---|
| 有引脚的调用原语（27 个扫描节点） | 节点上的 `stage` / `problem` | 缺省 `alpha`，**不写进快照**；`preview` 才落盘并带 `problem` |
| 内建 op（10 个：`sequence`／`branch`／`loop`／`while`／`iterate`／`choice`／`let`／`write`／`ask`／`script`） | 顶层 `builtInOps` 数组 | 前端硬编码这些 op、目录里没有引脚条目 |

为什么内建 op 要单独一张表（`HandlerDslContract.PreviewOps` 的注释写得很直白）：
`choice` 的运行时实现 `ChooseAsync` **本身在排除清单里**（`HandlerDslExclusions.cs:99-100`），
扫描器根本扫不到它，节点级标注无处可挂。

### 2.2 两条硬规矩

1. **不裁剪**：`preview` 条目照样出现，只影响排序与提示。藏起来会让作者以为这版没这个能力，
   转而去别处绕——比「标出来但说明原因」糟糕得多。
2. **判据是显式登记，不是自动探针**。理由：`choice` 编译能过、运行才炸，用「编译一次」判会误判成 `alpha`；
   多数节点又需要真实对局上下文才能跑，探针会大面积误判。登记必须由**行为测试背书**，
   引擎补上能力后测试先红，逼人回来删登记。

> **这条正好打在我们的老做法上。** `proto/bridge/Kards.Ui.OrcBridge/DiyCapability.cs` 的
> `DiyOpCatalog` 就是探针式判据——每个动作拿默认声明走一遍 `DesktopNativeLowering.Lower`，
> 抛异常就进「预览」。那套对 DIY 旧动作还行（失败＝编译期失败），但对 handler DSL 的 `choice`
> 会**判成 alpha**，正是引擎点名要避免的误判。新契约要求的是「登记表 ＋ 行为测试」，不是探针。

### 2.3 今天到底有几个 `preview`

实测（`grep -rn "HandlerDslPreview" src/`，排除注解定义本身）：**零个节点**标了 `[HandlerDslPreview]`。
`HandlerDslContract.PreviewOps` 里也只有一条：

```csharp
["choice"] = "选项呈现需前端桥接（S10）尚未接入：编译能过，运行期一律抛 NotSupportedException。"
    + "接通交互通道后删掉本条。",
```

`KeywordEffectActionFace.ChooseAsync`（`KeywordEffectActionFace.cs:121`）**无条件抛**：

```csharp
throw new NotSupportedException(
    "choice 节点的选项呈现需前端桥接（S10）：本机没有交互通道时不做替代选择。");
```

所以现状是：**`choice` 是唯一一个「能画、能校验、能生成 csx、一跑就炸」的东西。**
`stage`/`problem` 这套机制是为将来准备的，今天只需要标这一条。

### 2.4 前端指南给的具体做法（§5.5）

```text
展示顺序：alpha 在前，preview 在后（各自沿用原来的分组/命名顺序）
preview 条目：置灰 + 角标，hover 显示 Problem 原文
拖到画布上：允许（作者可以先把图搭出来），但保存前给出阻断性提示
```

注意最后一条的措辞：**允许拖上去，只是保存前拦**。这和「校验期报错」是两件事——
13 个错误码里没有、也不该有「用了 preview」这一条。它属于界面规则，不进契约。

### 2.5 DTO 签名（会破源码兼容的地方）

```csharp
// 注意：BuiltInOps 是**无缺省值**的末位参数 —— 直接 new 这个 record 的代码会编译失败
public sealed record HandlerCatalogDto(
    int CatalogRevision,
    IReadOnlyList<string> ScannedFaces,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Readables,
    IReadOnlyList<HandlerCatalogNodeDto> Nodes,
    IReadOnlyList<HandlerBuiltinOpDto> BuiltInOps);

// 这两个有缺省值，构造端不受影响
public sealed record HandlerCatalogNodeDto(
    string Name, bool HasAwait, bool HasSideEffect, string? FaceType,
    IReadOnlyList<HandlerCatalogPinDto> Inputs, IReadOnlyList<HandlerCatalogPinDto> Outputs,
    string Stage = "alpha", string? Problem = null);

public sealed record HandlerBuiltinOpDto(string Op, string Stage, string? Problem);
```

`catalog.schema.json` 里 `builtInOps` 和 `stage`/`problem` **都是可选**（顶层 `required` 仍是
`schemaVersion`／`catalogRevision`／`nodes`），所以旧快照仍然合法——这也解释了为什么
`catalog.snapshot.json` 没跟着改。**代价是：快照验证不了 §12.11，别拿它当分区真源。**

---

## 3 组件资产层：模板库（`abstract` / `label` / `description`）

这一层跟 handler DSL 无关，是**卡包资产**的扩展。但它是「效果模板库」的承载，界面迟早要吃。

### 3.1 新字段

| 层级 | 字段 | 语义 |
|---|---|---|
| 资产 | `abstract: true` | 本资产是模板，不是可装配组件；被卡牌 `components` 引用＝装载期拒绝（「模板不可实例化」） |
| 资产 | `label` / `description` | 展示文案；未声明＝前端回退显示 id |
| 成员 | `abstract: true` | 该成员必须由子实现（`PrefabMemberDescription.RequiresImplementation`） |
| 成员 | `guard` | 守卫档：`any`／`self`／`owner-same`／`owner-different` |
| 成员 | `label` | 展示名；未声明＝回退成员名 |

### 3.2 `authorize-moding` 变成三态

原来是 `bool`（缺省 `false`），现在是 `bool?`：

- **未声明**（`null`）＋ `abstract: true` ⇒ **推导为 true**
- 显式 `false` ＋ `abstract: true` ⇒ **构造时抛 `ArgumentException`**（「抽象资产必须可被覆写」）
- 普通资产不写该字段（写入器加了 `&& !asset.Abstract`，模板文件里根本不出现这个键）

`ComponentAsset` 的属性 `AuthorizesModing` 仍是 `bool`（读入时就把推导值落定），所以**读的一方不用改**；
构造签名从 `bool authorizesModing = false` 变成 `bool? authorizesModing = null`，命名实参调用仍然编译通过。

### 3.3 保留前缀 `template.`

`AssetSchema.TemplateIdPrefix = "template."`，`AssetSchema.IsTemplateId()` 是唯一判据。
装载链（`AssetPackLoader`）见到该前缀的资产 id 直接拒绝——模板只作编辑期只读层，不参与对局装载。

> 注释里特意强调：与 `ComponentDisposalPolicy.ReservedPrefix`（`framework.`，管**成员名**）
> 形态同类但**命名空间不同**，**不得复用同一个判定函数**（否则 `framework.` 会误伤资产 id、
> `template.` 会误伤成员名）。这种「长得像但别复用」的坑值得记住。

---

## 4 信号守卫（guard）

### 4.1 它解决什么问题

信号是**全局广播**：`engine#unit.deployed` 上任何单位部署，都会跑到每一条挂在该信号上的边。
以前作者只能靠 `read` 条件自己筛；现在模板可以声明守卫，由**生成器**在作者效果体之前合成过滤代码——
作者不可见、不可编辑、不可删除。

### 4.2 四档

| 档 | 文本名 | 语义 | 可用条件 |
|---|---|---|---|
| `Any` | `any` | 不生成过滤代码 | 恒可用 |
| `Self` | `self` | 宿主卡与载荷里的主体卡字段**引用相等** | 信号在 `Subjects` 白名单里登记 |
| `OwnerSame` | `owner-same` | 宿主归属玩家与载荷玩家字段相等 | 信号在 `Players` 白名单里登记 |
| `OwnerDifferent` | `owner-different` | 不相等（`Owner` 为 null ＝按「不同主」） | 同上 |

**档的可用性只由白名单决定**，不按载荷顺序或字段类型推断（D27）。原因写在 `Members.cs`：
载荷里 `Player`／`Deck`／`Position`／`Source` 全是 `entity`，按「第一个实体字段」推断会把回合五连
判成可用而比较对象恒不命中——静默失效。

白名单实测（`KernelSignals.cs` ＋ `GameSignals.cs`）：

- `Subjects`：内核 4 条（`card.placed`／`card.destroyed`／`card.data`／`effect.removed`）
  ＋ 游戏层 22 条（`card.*` 11 条、`unit.*` 9 条、`counter.*` 2 条）＝ **26**
- `Players`：内核 0 条 ＋ 游戏层 **19** 条

### 4.3 宿主交集推导

多抽象成员模板的 `requires-host` 取**交集**，且校验「声明的宿主不宽于交集」：

- 声明更宽 ⇒ 拒绝（否则「某成员在声明宿主上永不命中」，正是要消灭的静默失效）
- 交集为空 ⇒ 拒绝（如 `unit.*` ＋ `counter.*`：一个组件装不到两种互斥宿主上）

单档推导（`SignalGuards.DeriveHostKind`）：`self` 档按信号前缀（`unit.`→Unit、`counter.`→Counter、其余→Any）；
`owner-*`／`any` → Any。注意 `HostKind` 没有独立的 `Card` 值（`Any` ＝ Unit|Order|Counter|Hq），
所以 `card.*` ＋ `self` 不收紧宿主。

### 4.4 代码合成位置与产物的洁净

生成器（`HandlerDslGenerator.Emit`）的语句顺序变成：

```text
Func<dynamic, Context, CancellationToken, Task> <entry> = async (view, ctx, ct) =>
{
    var __self = (Orc.Cards.Card)view.Host;   // 有守卫或有 self 读取时
    <守卫代码>                                  // 新增：紧跟在宿主绑定之后
    var __runtime = ...ResolveFor(__self);     // 需要运行时门面时
    if (__runtime is null) { return; }
    <作者的效果体>
};
```

作者改不掉它：DSL 没有 return／删除语句，绑定名也不得以 `__` 开头。

**产物里不留 `guard`**：守卫已烘进 csx 文本，产物若保留该字段，再次摊平会重复加守卫。
装载链见到 `members[].abstract` 也直接拒绝（「摊平期已强制实现，产物不该带抽象位」）。

---

## 5 效果模板库

### 5.1 派生规则（先规则后枚举）

`EffectTemplateLibrary.Derive()`，五条：

1. 取全部信号（内核 4 ＋ 游戏层 35 ＝ 39），**剔除 `SignalTiming.Load`**（唯一一条＝`card.load`）
2. `any` 档恒出
3. `Subjects` 有登记 ⇒ 出 `self`
4. `Players` 有登记 ⇒ 出 `owner-same` ＋ `owner-different`
5. `id = "template." + signal + "." + guard`；`label` ＝ 信号中文名（非 any 档再加「（档中文名）」）；
   `description` ＝ 该信号的 `Notes`

**条数核对**：38 个信号 × any ＝ 38，＋ `Subjects` 26，＋ `Players` 19×2 ＝ 38，
合计 **102**。与源码注释里的「102 只是此刻的实测值」一致（测试按规则重算，不硬编码）。

### 5.2 它是生成物，而且**没入库**

```
ORC_WRITE_TEMPLATES=1  →  EffectTemplateLibrary.WriteAll(templateRoot)
                       →  <templateRoot>/effects-v4/template.<signal>.<guard>.component.json
```

实测：仓库里 `template.*.component.json` **0 个**。`WriteAll` 会先删掉不再派生的旧文件再逐条写，
守护测试「按规则重算再逐条比对」——手改任一文件、少一条、多一条、改名都会红。

> **对我们意味着**：现在**没有任何模板文件可读**。想在界面上列模板，得先跑一次生成，
> 或者由引擎把生成物随发行版带出去。这是个数据缺口，不是契约缺口。

### 5.3 模板根路径与第 7 个诊断码

`AssetEditor` 的选项新增 `TemplateRoot`（`EditorModels.cs`，init 属性，可空）。
配置后 `DescribePrefab`／`Flatten` 都把模板库当**只读依赖层**并进目录。

新增诊断码 **`template-root-missing`**，与 `parent-missing` 分开的理由写得很对：
「父未注册」是资产问题，「模板库未配置」是配置问题，**修复动作完全不同**，同码会让前端永远不知道该配路径。

模板根只影响两件事：
- `DescribePrefab` 能解析 `template.` 前缀的 id
- 摊平的 `extends` 允许指向模板库里的**抽象**资产（指向普通资产仍按跨包拒绝）

### 5.4 新增的编辑 API

```csharp
// 之前只有 DescribePrefab / Commit，现在多一个（不写盘，落盘仍由 Commit 统一处理）
ModingFlattenResult AssetEditor.Flatten(string packRoot, string draftPath,
                                        Orc.Core.HandlerDslEnvironment? dsl = null);
```

---

## 6 运行期授予（`EffectGrantService`，R37）

与界面无关，但记一笔免得以后翻不到：给已经在场的卡凭空加一份效果，句柄式撤销。

```csharp
GrantHandle GrantComponent(...)   // 返回句柄
handle.Revoke()                   // 幂等：宿主离场与主动撤销可能同时命中
```

`GrantLifetime` 只有两档：`UntilDestroyed`（缺省）、`UntilLeaving`（非死亡离场＋死亡都撤）。
注释解释为什么不做「到回合结束」：`one-shot` 与效果自己调 `Revoke` 已经能表达，再加一档
等于让引擎承担时序编排。

`UntilLeaving` 同时接 `card.leaving` 与 `card.dying`／`card.died`——三者互斥，只接前者会让
「被打死」的卡把授予的效果带进弃牌堆。

---

## 7 对我们客户端的影响

### 7.1 必须改（这次就做）

| # | 事项 | 现状 | 契约要求 |
|---|---|---|---|
| **A1** | 工具箱把 `choice` 当普通积木 | `HandlerDslCatalog.cs:154` 建了 `choice`，`ToolboxRow` 无任何分区概念 | §12.11「不裁剪 ＋ 置灰 ＋ 角标 ＋ hover 显示 Problem」 |
| **A2** | 目录模型没有 `stage`/`problem` | `HandlerNodeSpec` 只有 name/await/effect/pins/bodies | FRONTEND §5.5 |
| **A3** | 没有 `builtInOps` 表 | 十个内建 op 混在 `Nodes` 里，靠 `BuiltIn` 布尔区分 | 目录顶层单独一张表 |
| **A4** | 文档里用了 `choice` 不提示 | 校验通过、JSON 照出 | 「保存前给出阻断性提示」 |

A4 的落点是**界面警告**，不是新错误码——13 个码是封闭的，别自己加第 14 个。

### 7.2 不用改

| 事项 | 理由 |
|---|---|
| handler DSL 语法／校验 | `schema.json`、`EXCLUSIONS.md`、向量、错误码全未改 |
| 生成 csx | 我们本来就不生成（SPEC §2.7：前端不得生成 csx）。守卫合成发生在引擎侧，与前端无关 |
| `guard` 字段 | 契约包里 `grep -c guard` ＝ **0**。守卫是组件资产/成员层的字段，不是 DSL 语法，积木编辑器不该出现它 |
| `ComponentAsset.AuthorizesModing` | 属性仍是 `bool`（推导值在构造时落定），读的一方零改动 |

### 7.3 以后要做（记下来，别现在动手）

| 事项 | 依赖 |
|---|---|
| 「从模板新建效果」 | 模板库文件得先存在（现在 0 个） |
| 模板列表／「模板」标记 | `PrefabSummary.IsTemplate` / `Label` / `Description` |
| 「这个成员还没实现」标红 | `PrefabMemberDescription.RequiresImplementation` |
| 接 `HandlerCatalogFacade` 拿真目录 | `HandlerCatalogDto` 现在多了 `BuiltInOps`，构造端要跟着改 |
| `AssetEditor.Flatten` | 接 Commit 那条链时一起做 |

---

## 8 遗留问题与不一致

1. **快照跟不上 schema**：`catalog.snapshot.json` 仍是 27 节点、无 `builtInOps`、无 `stage`。
   两者都是可选字段所以不违规，但**别拿快照验证 §12.11**。上一轮发现的「文档说 check 19／gen 8，
   实际 24／10」还没修，这次又添一处同类漂移。
2. **`builtInOps` 的 `choice` 与节点表的关系没写死**：如果将来给 `choice` 也做了引脚条目（进 `nodes`），
   两张表会不会打架？SPEC 没说。目前 `choice` 只在 `builtInOps` 里。
3. **模板库没有随仓库发布**：`WriteAll` 只在 `ORC_WRITE_TEMPLATES=1` 的测试路径下调用。
   发行版里模板从哪来、放哪个目录，文档没交代。
4. **`stage` 的判据对第三方不可复现**：节点级靠 `[HandlerDslPreview]` 注解（第三方门面也能加），
   内建 op 靠引擎内部字典 `PreviewOps`——前端只能**读**，不能自行推导。这符合「不探针」原则，
   但意味着离线复现目录时拿不到分区。

---

## 9 本轮没做的事

- **没跑引擎测试**：本机 `dotnet test` 需要还原整棵引擎树，且 `github.com` 不通导致 NuGet 可能失败。
  新加的 10 个测试文件只读了源码，没验证过绿。
- **没生成模板库**：没跑 `ORC_WRITE_TEMPLATES=1`，所以 102 条模板只做了**条数推导核对**，
  没看过实际文件长什么样。
- **没动引擎仓库**：全程只读，`H:/Working Folder/OrC-KSD-github` 仍是 `169e575`、工作区干净。

---

## 附：上一轮 8 条决策的最终状态

| 编号 | 事项 | 11-01 状态 | 11-02 状态 |
|---|---|---|---|
| D1 | `readables` 分组 | ✅ 按实体种类 | 未变 |
| D2 | `read.of` 实体表达式 | ✅ | 未变 |
| D3 | `unitsOf` | ✅ | 未变 |
| D4 | 引脚 `subtypes` | ⚠️ 实现但真实目录 0 条 | 仍未变（快照里 `subtypes` 仍 0 次） |
| D5 | 目录中文 `title` | ❌ 未动 | **仍未动**（连续两轮） |
| D6 | `choice` 运行期 | ✅ 已实现 `EmitChoice` | ⚠️ **降级为 `preview`**：`ChooseAsync` 无条件抛，`PreviewOps` 明确登记 |
| D7 | facade 签名 | ✅ 已写明 | 签名又变了（多 `BuiltInOps`） |
| D8 | `DescribePrefab` 样例 | ✅ 已提供 | 样例补了 5 个新字段 |

D6 的变化值得单独说一句：上一轮我判「✅ 已实现」，是看 `EmitChoice` 生成了 `ChooseAsync` ＋ `switch`。
**生成能过 ≠ 跑得起来**——`ChooseAsync` 本身就是无条件抛。这次引擎自己把这件事登记成 `preview`，
等于承认了「生成器的完成度不等于运行期的完成度」。我上一轮的判断错在这里，记一笔。
