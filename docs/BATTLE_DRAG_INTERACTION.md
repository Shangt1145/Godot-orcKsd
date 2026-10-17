# 对战拖放交互

2026-10-17。依据用户确认：拖到目标后松手；指向部署先确认落点，再确认目标。

## 操作

- 指向性指令：按住手牌后直接拉出箭头，卡牌留在手牌位置；拖到引擎公布的合法目标后松手即打出，不先拖动卡牌、不再要求第二次选目标。无效落点退回手牌。
- 非指向性指令：拖入战场使用区域后松手提交；区域之外退回手牌。抉择、研发、选择手牌仍由原生选择面板处理。
- 指向部署：先拖到引擎公布的部署空隙并松手，然后显示该位置的单位预览，临时排开相邻单位。箭头此时已进入选靶状态，移到合法目标后按下并松手即可确认，也支持再次拖动选靶。无需重新从来源单位拖动超过某个距离。落点阶段遗留的松手不能回答新目标请求。
- 原生引擎要求的战场单目标选择（包括自定义词条主动技能）使用同一箭头交互。多选、手牌引用、定义选择与抉择继续使用面板。
- 目标选择期间屏蔽其他卡牌手势和结束回合；右键或 Esc 请求取消。取消是否接受仍由引擎响应器决定。无效目标松手保留选择，可重新拖动。

## 边界

单位落点预览只改变 Godot 的显示位置和卡牌模式，不提前扣费或修改引擎战场。原生 OrC-KSD 接受最终选择后才结算。候选目标来自原有 OrderTargets 投影或原生 selector，界面不根据效果文字推断目标，也不套用攻击合法性。

自定义词条指令通过原生 `ReadFirstPlayInput` 纯查询公布首次选择，与实际预选使用同一选择步骤遍历和目标解析；不提前执行效果、选择、随机数或付款。桥接同时补齐限定前线/支援阵线的指令预览，复用实际选靶的原生候选查询。先研发或抉择再指向的牌仍按引擎请求顺序交互，多阶段牌后续选择不与第一目标混用。

## 验证

- `dotnet test tests/Kards.Ui.Tests.csproj --filter "FullyQualifiedName~OrderTargetingTests|FullyQualifiedName~TargetHonourTests|FullyQualifiedName~WorkshopTests|FullyQualifiedName~Stage6B2EffectTests|FullyQualifiedName~OfficialCardExpansionTests"`：62 项通过。新增自定义词条指向部署取消/重试、场上主动技能来源与目标回放测试。
- Godot `--verify-orders`：指令拖放、空目标/非法目标/取消不付费、非指向抽牌、面板抉择、反制及演出检查通过。
- Godot `--verify-target-drag`：实际 viewport 鼠标按下、移动、松手事件；单击不提交、无效落点重试、部署预览、落点事件不穿透；完整自定义词条部署通过原生引擎选择与结算。另以 Vulkan 实际渲染运行并检查截图。
- 日志位于 `artifacts/drag-target-tests.log`、`artifacts/drag-target-godot.log`、`artifacts/target-drag-native-ui.log`、`artifacts/target-drag-rendered.log`。

未生成新发布包，未推送这些改动。

## 直接拉箭头修正

用户再次明确指向指令不得先拖动卡牌。已补齐自定义词条及限定阵线指令的拖放目标投影，并在按住指令时直接显示箭头。新增原生自定义指令鼠标验证：拖动中卡牌留在手牌、松手前不提交、松手后直接结算且无第二次选靶请求。日志：`artifacts/direct-order-ui.log`、`artifacts/direct-order-rendered.log`、`artifacts/direct-order-regression.log`、`artifacts/direct-order-native-regression.log`；画面：`artifacts/direct-order-arrow.png`。

引擎配套变动位于另一个工作目录 `H:/Working Folder/OrC-KSD-github/src/Orc.Game/Cards/ProgramKeywordComponent.cs`，新增纯预览并共用选择步骤查询；构建本次 UI 必须包含该配套改动。

修正验证：相关 UI/桥接回归（包含 DesktopAuthoringTests）79 项通过，原生 KeywordProgramTests 18 项通过；Godot `DIRECT_ORDER_ARROW_VERIFY_OK` 确认卡牌保留按住时的位置、一次拖动、松手使用、无第二次选靶。普通单击不打出，并清理临时箭头。

## 指向单位无响应修复

真实示范包“缠斗步兵”部署后，箭头已进入选靶状态；旧代码仍要求新按下后移动超过 8 像素，导致移到目标后普通按下/松手被忽略。修复仅移除已激活选靶状态中的移动门槛，仍要求新按下与松手成对，确认以松手时的引擎候选为准。手牌指令仍需要拖拽，不因此改成单击出牌。

复现日志 `artifacts/unit-target-before.log` 在真实包的点选目标处超时；修复后 `artifacts/unit-target-demo-pack.log` 通过同一操作。`artifacts/unit-target-gestures.log` 同时覆盖点击、拖动、非法目标重试、部署遗留松手、连续两个目标槽位及取消。词条定义与玩家工坊存档未修改；此前审计发现的两个机制问题属于独立事项。
