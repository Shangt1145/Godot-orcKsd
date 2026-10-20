# DIY 卡包 / 扩展包设计——引擎架构对照（2026-10-12）

用户设计意图：**DIY 内容分两层——卡包负责添加新词条、新单位类型等词汇表能力，并附带一些单位；
扩展包必须基于某个卡包，只能添加单位。**

本文把该设计映射到 OrC-KSD 引擎的既有扩展点，标出"现在就能做 / 需引擎补丁"的分界。

## 一、引擎已有的现成能力（不用发明）

引擎上游"端到端创作卡牌工具链"（批 1–7，HEAD `8b0ae98`）已交付：

| 能力 | 引擎落点 | 对设计的意义 |
|---|---|---|
| 卡包分发形态 | `CardDataLoader.LoadDirectory` 扫 `*.card.json`；效果库 `*.prefab.json`；示例包 `samples/card-pack-demo/`（≥2 卡目录＋1 库目录＋README） | **"卡包"概念引擎已定型**：纯数据目录、csx 自包含、零 C# 注册即可跑 |
| 分发预检器（批 3） | dry-run 校验器，声明↔注册对比，Error/Warning 双零口径 | **扩展包依赖检查的现成载体**：基包未启用 → 扩展包引用的词条/效果 id 预检报 Error |
| csx 动态效果（批 4） | `CSharpScriptEvaluator`：白名单＋黑名单关键字＋5s 超时弱沙箱；已验证词条 csx 行为引用（`*.csx.prefab.json`） | 卡包可以**纯数据携带新词条的行为**（数据壳效果绑定 / csx） |
| 词条开放扩展注册 | `KeywordRegistry.Register(id, factory, isBattleKeyword)` 进程级全局，"开放扩展注册（测试/后续批次可注册自定义标识）"；`DeclareEffectBindings` 数据化绑定 | **新词条有正式扩展点**，且"注册先于使用"——正好对应"扩展包必须基于卡包"的时序 |
| 效果装载时序 | 预检 → 装载 → 注册 → Match 初始化 → 使用（示例包 README 硬性文档化） | 卡包先注册词条/效果库，扩展包的卡才能引用——依赖方向天然成立 |

## 二、设计的架构映射

```
卡包（pack）
├── pack.json            包清单：id、名称、依赖（卡包无依赖）、词条声明、版本
├── cards/*.card.json    附带单位/指令（六组件 schema，引擎批 7 已定型）
├── effects/*.prefab.json  效果库（csx / 数据壳）
└── keywords/            新词条：标识 + 组件形态（csx 行为引用 / 数据壳绑定）
        ↓ 注册（KeywordRegistry + PrefabManager + CardEffectRegistry）
扩展包（expansion）
├── pack.json            依赖：[某卡包 id]
└── cards/*.card.json    只有单位；可引用基包词条/效果 id
```

- **依赖规则**＝"注册先于使用"的引擎时序（KeywordRegistry 单源）：
  扩展包声明的词条/效果 id 必须在（引擎内建 ∪ 基包已注册）集合内，预检器照实报错。
- **"扩展包只能加单位"**＝分发层约束（pack.json 声明 + 预检 + UI 导入校验），引擎无感。

## 三、分界：现在就能做 vs 需要动引擎

### ✅ 引擎已支持，UI 侧即可实现

1. 卡包/扩展包目录格式、包清单、依赖声明与预检（复用批 3 预检器 + 批 7 形态）
2. **新词条**（大部分）：
   - 数据壳效果绑定（`DeclareEffectBindings` + prefab）——如"部署时抽 1 张"类
   - csx 行为引用——沙箱内可表达的逻辑（已验证三样本全链等价）
3. 扩展包单位引用基包词条/效果（装载链现成）
4. UI：卡包管理页（导入/列表/依赖提示/启停）、DiyCardCompiler 感知卡包词条

### ⚠️ 需要引擎补丁（走 6D2 先例：本地改→推上游）

1. **新单位类型**：`UnitType` 是 C# 枚举（`src/Orc.Game/Cards/UnitType.cs`），**无法运行时注册**。
   要支持卡包加兵种，需引擎把 `typeCategory` 的单位类型数据化（字符串标识＋注册面，类比 KeywordRegistry 的做法），
   或提供 UnitType 扩展注册面。这是卡包设计里唯一的硬引擎缺口。
2. **判定器改写类词条**（如伏击那种"改写伤害结算"的深逻辑）：csx 沙箱表达不了或不该表达 →
   需 C# 入程序集。纯数据卡包装不下；要么引擎内建该词条，要么卡包支持附带已编译程序集（安全性需单独评估）。

### 📌 约束继承（6B 既有约定）

- 未注册词条/未知效果 id → fail-fast 或显式拒绝，绝不静默裁剪
- 卡包内容逐包审核准入（沿用 verified-cards.json 的 SHA-256 锁源思路）
- UI 只消费投影，规则判定全归引擎

## 四、建议实施顺序

| 批次 | 内容 | 依赖 |
|---|---|---|
| P-包1 | 包格式 + 预检 + 装载注册链接通（复用引擎批 3/7），先做"只带单位+效果"的卡包 | 无，纯 UI/桥接 |
| P-包2 | 新词条注册面接通（csx 行为 / 数据壳绑定），扩展包依赖检查 | P-包1 |
| P-包3 | UI：卡包管理页、DIY 编辑器感知卡包词汇 | P-包2 |
| P-引1 | 引擎补丁：UnitType 数据化/注册面（卡包加新兵种） | 用户授权动引擎 |

与"批次 1 卡池覆盖（45 张纯非交互转换）"相互独立，可并行排期。

## 五、实施发现（P-包1/2/3 当日落地，2026-10-12 补记）

按"不动引擎"约束实施 P-包1→3 后，两条引擎硬约束浮出水面（均已按 fail-fast 口径处理）：

1. **卡牌 JSON 无法声明自定义词条**：引擎卡数据体的 `keywords` 组件走 `CardAttributeMap`
   （静态内建英文标识→中文词条映射；internal static），未命中归 `UnmappedAttributes` **静默留痕**。
   因此卡包新词条当前的正确用法＝**注册词汇表＋效果绑定，经"授予类效果"进入对局**
   （如 csx 效果发放词条；授予链在授予时查 KeywordRegistry，任意已注册标识均可），
   不能直接写在卡牌数据里。要在卡牌数据声明自定义词条，需引擎把 CardAttributeMap 数据化（引擎补丁，后置）。
2. **静默留痕升级为显式拒绝**：PackSystem 对装载结果检查 `UnmappedAttributes`，
   非空＝拒绝装载（否则卡牌会"看起来有词条、实际没有"——规则静默缺失）。

落地形态（`proto/bridge/Kards.Ui.OrcBridge/PackSystem.cs` + `PackLibrary.cs` + `proto/ui/screens/PackScreen.cs`）：
包清单 `pack.json`（id/name/kind=pack|expansion/dependsOn/keywords）；分发预检双零门；扩展包只能加单位卡＋
效果引用须在（本包∪基包）集合内；词条注册＝KeywordRegistry 开放注册面＋PlainKeywordComponent＋DeclareEffectBindings；
UI 卡牌 id 命名空间 `pack/<包id>/<卡id>`；进程不变量＝**启用中的卡包属于进程词汇表**，所有对局漏斗
（CreateNativeAsync）自动附挂启用包的效果库（KeywordEffectAssembly 装配期对全部声明绑定核验所需）。
