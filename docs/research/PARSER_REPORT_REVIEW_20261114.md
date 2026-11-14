# 两份 DIY 文本解析报告审阅（2026-11-14）

审阅对象：用户提供的“前端延迟回合开始句式无法解析”“前端缺少对敌方总部造成伤害的触发句式”。报告基于 beta.2，当前 beta.11 源码依然能复现。本文仅记录实际复现及修复边界；不把未经实现的语法建议混入已验证的 beta.11 发行包。

## 当前复现

通过真实 DesktopOrc.CompileGraph 调用，并分别使用桌面 V8 与 ORC_MANAGED_JS=1（Android 同源 Jint 路径）：

| 输入 | 当前结果 |
| --- | --- |
| 部署：下个友方回合开始时，失去两个指挥点 | complete=false，unparsed-text；没有应用半截立即扣费效果 |
| 部署：下个敌方回合开始时，抽一张牌 | complete=false，unparsed-text |
| 闪击／本单位对敌方总部造成伤害后，获得 +1 攻击力。 | 闪击能识别，整句 complete=false，触发前缀缺失 |
| 本单位攻击敌方总部后，获得 +1 攻击力。 | 语法 complete=true，trigger=afterAttackHQ |

证据：artifacts/parser-report-reproduction.log、parser-report-reproduction-managed.log。

## 报告之外的执行缺口

绕开文本解析，直接给 OrcEffectEditing.Compile 提交对应的声明图：

- deploy → nextTurnStart(self, drawOne(self))：拒绝准入，`OrC-KSD 尚无无损执行适配：动作 nextTurnStart`。
- afterAttackHQ → drawOne(self)：拒绝准入，`OrC-KSD 尚无无损执行适配：trigger = afterAttackHQ`。

证据：artifacts/parser-report-native-reproduction.log。因此旧 Desktop JS 模拟器里存在 OPS.nextTurnStart 和 afterAttackHQ，不等于 OrC-KSD 原生对局已接通这些能力。当前真正需要修复的是“识别 + 原生执行适配 + 对局回归”，不是一两行正则。

## 延迟回合开始句式

报告的切段根因成立：时间状语在复合句切分前没有被识别。必须在切段前保留整个延迟结构，不能用会吞掉时间条件的后端兜底把效果变成立即执行。

建议补丁的阵营重写还需要进一步验证：仅对第一层 action.side 做 self→enemy 替换不能覆盖 loop、if 等嵌套动作；显式写“我方/友方”的内层对象也不应未经语义确认就改成敌方。目标何时选择、施动卡离场后是否继续结算、只执行一次及回合时点都需要原生测试。指挥点与指挥点槽必须分开验证。

## 对敌方总部造成伤害句式

缺少触发前缀属实。报告建议复用 afterAttackHQ 只能表达“攻击总部并造成伤害”，不能自动等价于“本单位通过任意来源对总部造成伤害”。句子中出现伤害数字时，也不能在正则里吃掉数字而不记录或验证。

应明确选用攻击后触发，或连接引擎实际伤害事件并按来源单位、受伤总部和阵营筛选；保留伤害阈值等条件。既有 damaged、hqDamaged 等规则不得被新前缀抢占。

## 后续验收

先补原生能力并测试触发时点、阵营和一次性结算，再补文本识别，最后跑整包语料及 V8/Jint 对照。至少包括友方/敌方延迟抽牌、资源和资源槽、嵌套动作、非法内层句子整句拒绝、攻击总部造成零伤害不触发、效果伤害与攻击伤害的区别，以及当前常规触发不回归。

本次 beta.11 只发布默认房间开局的效果制品格式修复；这两项解析问题没有标记为已修复。
