# 对战 UI 第三阶段：抽牌、指令揭示与多目标结算

日期：2026-10-05。本阶段只实现 UI 演出，主引擎架构与正式规则不在范围内。

## 已完成

- 我方抽牌：卡背从右下牌堆抬起 → 移动中翻面进入屏幕中央 → 短暂停留 → 缓动进入扇形手牌。第四阶段按用户反馈增加连贯翻面、弧线与惯性转角，见 `BATTLE_STAGE4.md`。连续抽牌依次播放，已有手牌提前让位。
- 对手抽牌：卡背从上方进入对手扇形手牌；播放与快照冻结时均丢弃误传的对手卡牌身份。
- 指令：从我方手牌或对手手牌方向进入中央，完整卡揭示后停靠右侧，等待目标结算，再淡出退场。隐藏指令只显示卡背，不进入可见历史。
- 指令离开手牌时邻卡缓动让位；指挥点与牌堆数量直接绑定外部提供的已提交结果。
- 多目标：对传入的目标同时播放命中、伤害盾与死亡烟尘；效果保存在目标原位置，卡列在演出结束后按最终投影重排。不推算伤害，不决定目标或死亡。
- 齿轮菜单增加六个演示：抽牌、连续抽牌、对手抽牌、指令、多目标指令、对手指令。外部状态模式下全部禁用。
- 空军与太空战机起飞前移由 32 缩为 20，额外抬升由 18 缩为 12；维持 0.45 秒缓入缓出起飞。太空战舰不浮空，炮击后坐距离由 7 加至 18，用 0.12 秒后坐和 0.30 秒回位。

使用现有桌面、纸卡、完整卡面和第二阶段的烟火纹理，未新增素材或改变棋盘风格。

## 原版参照

沿用 [原版调研](KARDS_BATTLE_RESEARCH.md) 的 `deploy-order-observed-01.png` 指令揭示与退场，以及群体移除分解图。官方 [手牌说明](https://support.kards.com/hc/en-us/articles/360026500332-Battlefield-elements-Hands) 确认我方手牌公开、对手手牌隐藏；[指令说明](https://support.kards.com/hc/en-us/articles/360026754851-Orders) 确认指令揭示与结算后的弃置语义。这里仅据其指导可见性与演出顺序，未实现官方规则。

延续原版完整纸卡揭示、扇形手牌和局部命中语言。现有宣传片证据不支持原版精确缓动与毫秒参数，当前参数仍为可调估值。

## UI 载荷与中断

新增候选载荷 `UiPresentationResolution`：有序的抽牌/指令表现步骤、适配器给出的目标前后值与伤害，以及最终 `After`。`Main.PresentSequenceAsync` 冻结嵌套集合后播放；只消费外部结果，不主动抽卡、扣费、选目标或计算结算。示例数据转换仅在独立的 `BattleDemoAdapter` 中。

```csharp
await main.PresentSequenceAsync(resolvedPresentation, afterActions);
```

这是 UI 演示与未来适配的候选接口，不要求尚未完成的主引擎采用对应内部架构，也没有建立新的生产事件队列。

Esc/右键、切页和偏好变化打断时，直接消费已收到的 `After`；新的全量投影或演示复位使旧任务失效。临时卡牌、Tween、烟火和输入锁全部清理。错误 MatchId 的载荷不播放。隐藏目标不参与可见命中特效。

## 验证

```powershell
& 'H:\Working Folder\OrC-KSD.Godot\tools\run.ps1' -Test -Verify
& 'H:\Working Folder\OrC-KSD.Godot\tools\run.ps1' -Capture
```

`VerifyPresentationAsync` 覆盖六个场景、连续抽牌、隐藏身份、揭示和命中时中断、旧任务失效、外部模式、错配对局、隐藏目标、快照冻结、减弱动效及效果最终归零。既有消费层测试、部署/移动与战斗场景检查保留。

日志为 `artifacts/battle-stage3-verify.log`、`artifacts/battle-stage3-capture.log` 与最终场景检查 `artifacts/battle-stage3-scene-final.log`；截图为 `artifacts/battle-stage3-*.png`，第四阶段另补抽牌途中翻面帧，运行副本输出在 `H:/g/kards/artifacts/`。

后续阶段仍包括起手换牌、爆牌/弃牌、反制、状态变化、选择与回合反馈，以及不同指令的专用视觉覆盖。当前指令命中统一使用既有爆炸效果，只适用于本轮伤害演示；治疗、增益等效果需要各自的后续表现，不能沿用伤害爆炸。
