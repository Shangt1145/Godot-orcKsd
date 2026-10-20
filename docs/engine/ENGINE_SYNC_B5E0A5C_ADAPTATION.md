# 引擎 b5e0a5c 升级适配记录（UI 侧）

> 日期：2026-10-19
> 引擎基线：`cce4ddf` → `b5e0a5c`（218 文件 / +17642 −1692）
> **立场：不改引擎源码。改名、迁移、语义对齐一律由 UI 侧完成。**
> 结果：**编译 0 错误 0 警告；单测 31/31；`--verify-ui` rc=0、32 个 VERIFY_OK。**

---

## 一、纯改名

| 旧 | 新 | 处理位置 |
|---|---|---|
| `CommandCard` | `OrderCard` | proto 6 个文件 14 处 |
| `PlayManager.BeginCommandPrePlayAsync` | `BeginOrderPrePlayAsync` | `OrcMatchSession`、`OrcOpponentDriver`、`RealMatchReproTests` |
| `AttackFlowBands` / `DamageFlowBands` | `ActionTriggerBands` / `ActionHandleBands` | `DiyInjectionCatalog.Schemes`、`DiyEditorUpdateTests` |

## 二、新增前置条件

建对局前必须注册动作实现集，否则 `CommandManager` 构造即抛「未注册动作实现」：

- bridge 增加 `Orc.Foundation.Actions` 的 `ProjectReference`；
- `OrcMatchSession.CreateNativeAsync` 入口调用 `FoundationActions.Register()`（幂等）。

## 三、`AddActiveHandler` → `ActiveEffect`（已跑通）

### 引擎侧事实（读源码所得）

1. `OrderCard.AddActiveHandler` 已删除；主动效果改为 `AddEffect(ActiveEffect<TView>)`，
   由 `OrderFlow` 的 Resolve 段按登记序执行（`OrderAction`：`card.Effects.OfType<ICastAction>()`）。
2. `OrderFlow.OnResolve` 传给 `ICastAction.CastAsync` 的载荷**只有三个键**：`Source` / `Target` / `Amount`。
3. `ContextViewBinder.Create<TView>` 会对**所有非 `[Optional]` 的视图属性**做必填校验
   （`EnsureRequired`：键必须存在且非 null）。
4. `PlayManager.ResolveOrderTarget` 只从捕获箱里取**第一个可解析为 `Card` 的值**。
   指向只有三种：**单选择**（玩家选一个）、**全部指向**（所有单位）、**随机指向**（随机一个）。
   后两种由效果自己遍历/抽取，不依赖这个目标；所以只有"单选择"需要靠它补回。

### 踩坑与解法（本次最花时间的部分）

| 坑 | 现象 | 原因与解法 |
|---|---|---|
| 用 `CardTriggerView` 接 | handler 完全不执行，`Argument` 恒 null | 载荷键是 `Source/Target/Amount`，与 `CardTriggerView` 属性名对不上，一个都绑不上 |
| 用引擎的 `OrderFlowView` 接 | 同样不执行，且**没有任何报错** | 它有必填的 `Orders`，施放载荷里没有该键 → `KeyNotFoundException` → 被 `Trigger.InvokeAsync` 的契约兜底**静默吞掉** |
| 捕获箱交整个 `Selection` | `The given key 't1' was not present in the dictionary` | `ResolveOrderTarget` 不认复合对象；改为只交第一张 `Card` |
| chooseOne 选项丢失 | `Value cannot be null (Parameter 's')`、分支不执行 | 选项没有通道，UI 侧自己留：`_pendingCapture` 保存预打出的完整选择，施放时取回 |

### 最终写法

- `OrderCastView`（`proto/bridge/Kards.Ui.OrcBridge/OrderCastView.cs`）：
  `[ContextView]` + `[Read] Source` / `[Optional][Read] Target` / `[Mutate] Amount`，
  **只声明载荷里真实存在的三个键**，不借用引擎视图。
- `SourceEffectAssembly.SourceOrderCast : ActiveEffect<OrderCastView>`。
- `CaptureAsync`：`_pendingCapture = selection`，`CaptureBox.Capture(第一个目标卡)`。
- `TakeCastSelection`：按 plan 的 target id 回填目标 + 从 `_pendingCapture` 取回选项；取完清空。
- `Dispose` 清空 `_pendingCapture`。

## 四、其他两处适配

- `Hq.ApplyDamageAsync` 被收为 `internal`（R11 收口，唯一调用方 `MatchActionServices` 也是 internal）。
  `Stage6AReliabilityTests` 用反射驱动同一数值路径造测试状态，仅为测试需要，不是产品路径。
- 边界测试放行 `Orc.UI.Contracts`：中立视图契约程序集（项目既有约定），只有引擎运行时程序集才算越界。

## 五、先手随机连带

多条用例断的是「先手 4 张起手」，先手随机后抽到后手变 5 张：

- `Stage6AReliabilityTests.ReadyAsync` → `firstPlayerIndex: 0`
- `Stage6C2Tests.SingleHumanMulligan…` → `firstPlayerIndex: 0`

## 六、当前状态

| 项 | 结果 |
|---|---|
| 编译 | 0 错误 0 警告 |
| 单元测试（相关 31 项） | **31 / 31 通过** |
| `--verify-ui` | **rc=0，0 ERROR，32 个 VERIFY_OK**，末行 `UI_VERIFY_OK screens=10 cards=531 texture_cache=64/64` |

## 七、仍然存在的引擎侧缺口（已绕过，非阻塞）

- **`chooseOne` 选项**：没有通道传下来，靠 UI 侧 `_pendingCapture` 传递，只在「预打出 → 施放」同一次连续调用里有效；
  若将来出现预打出与施放分离的路径，需要重新设计。
- **单选择的多个槽位**：一张卡若要依次选多个目标（不是"全部指向"），引擎通道只带得回最后一个；
  目前 reviewed 卡没有这种形态，等出现再处理。

## 八、阻塞级引擎回归（UI 无法绕过，2026-10-19 晚实测）

**现象**：产品路径点「开始对战」直接失败，提示
`真实对局未启动：对局尚未进入'进行'态：管理器群不可用（须先成功完成 Initialize）。`

**完整堆栈**（来自 `%APPDATA%\Godot\app_userdata\OrC-KSD · 卡牌指挥部\logs\godot.log`）：

```
Match.Initialize (Match.cs:711)
→ PlayerManager.LoadDecksAsync (PlayerManager.cs:167)
→ CardBase.LoadAsync → LoadComponentPhaseAsync (CardBase.cs:279)
→ BuiltInPlugin.RegisterBuiltInCardComponents (BuiltInPlugin.cs:297)
→ KeywordManager.GrantCore (KeywordManager.cs:284)
→ ProgramKeywordComponent.Mount → AttachDeclarations (ProgramKeywordComponent.cs:84)
→ match.Players            ← RequireReady 抛错（Match.cs:857）
```

**根因**：b5e0a5c 给 `Match.Players` 等管理器属性加了就绪门禁（`State == Preparing` 即抛），
但 `ProgramKeywordComponent.AttachDeclarations` 在**卡加载阶段**（Initialize 内部）就要遍历双方玩家——
加载发生在 Preparing 态，等于自己把自己锁在门外。

**为什么 `--verify-ui` 没抓到**：验证卡池里没有触发 `targeting.forbid` 程序词条的卡；
用户真实卡组/卡包里有。**验证卡池 ≠ 用户卡池**，这类"只有真数据才触发"的回归要靠实机玩才能暴露。

**为什么 UI 无法适配**：这是引擎内部组件挂载逻辑，不在任何接口边界上。
建议引擎侧二选一：
1. `ProgramKeywordComponent` 延迟到进入"进行"态后再 `AttachDeclarations`；
2. 给装配链开一个内部通道（如 `Match.TryGetPlayersRaw()`），跳过门禁。
