# 动画导出映射

参考：`H:/Working Folder/Kards-Desktop/electron/game/js/animate.js`，2026-10-05 实读。正则提取并去重后是 **63 项**，规格的 60 项已滞后。

下表表示实现入口，不代表逐帧视觉已与 Web 版完全一致。演示场景用于核对，Godot 运行探针用于检查完成、打断、状态恢复与释放。

| 源导出 | Godot / C# 入口 |
|---|---|
| `ANIM` | `AnimTiming` 七个常量 |
| `SPEEDS`, `setSpeed`, `speedName`, `speedScale`, `ms` | `AnimClock.Speed`, `SetSpeed`, `Current`, `Scale`, `Ms` |
| `reducedMotion` | `AnimClock.ReducedMotion` |
| `KIND`, `kindOf`, `familyOf`, `hitKindOf`, `attackProfile` | `AnimationEngine.KindOf/FamilyOf/HitKindOf/AttackProfile` 与兵种映射 |
| `sleep` | `AnimationEngine.Sleep`；主线程帧时钟，可打断 |
| `interrupt`, `isAnimating` | `Interrupt`, `IsAnimating`；归还视觉所有权 |
| `rectOf`, `layoutRectOf`, `toEls`, `tf`, `card`, `kill` | 同名 C# 方法；布局外壳与动画 Visual 分离 |
| `flip`, `flipByUid`, `arcMidOf` | `Flip`, `FlipByUid`, `ArcMidOf`；按 MatchId+Uid 匹配重建节点 |
| `arrow`, `aimShot` | `AimArrow`, `Arrow`, `AimShot`；阻尼跟随、曲线末端切线箭头 |
| `slam`, `glide` | `Slam`, `Glide`；三档拍桌，移动反馈在完整 Move 时长后发生 |
| `dustPuff`, `smokePuff`, `wakePuff`, `sparkBurst`, `shockRing` | 同名 C# 方法；带预算的 2D 图元 |
| `lunge`, `impact`, `muzzlePoint`, `muzzleFlash`, `tracer` | 同名 C# 方法；兵种射击、炮口、弹道与命中材质 |
| `floatAt`, `floatValue`, `kreditFloat`, `turnBanner`, `pulse` | 同名 C# 方法 |
| `flyCard`, `flyFromEl`, `drawToHand`, `slideInFromRight`, `burnCard` | 同名 C# 方法；飞牌、真实卡滑入、烧牌 |
| `playOrderCard`, `playOpponentOrder` | 同名 C# 方法；中央亮牌、敌左/己右停靠、停留后侧向退出 |
| `collectOps`, `classifyOps`, `CAT_CN` | `EffectClassifier.CollectOps/ClassifyOps/CategoryLabels` |
| `orderFx`, `intelScan`, `counterSet`, `counterFire`, `veteranUp`, `deckShuffle`, `counterReveal`, `holdCounter` | 同名 C# 方法 |
| `budgetOk` | `BudgetOk`；56 个临时图元预算 |

## 与规格不同但与源码一致的语义

- `drawToHand` 是旧飞牌接口；三段式演出属于 `playOrderCard`，`playOpponentOrder` 为兼容别名。
- `glide` 只做移动完成后的落地音效与碎屑，不负责位移。位移归 `flipByUid`。
- 射击中，地面卡片主要后坐，空中使用独立飞机剪影；卡牌不会冲到目标身上。
- `lunge` 不修改生命值。最后一发到达后只调用一次给定 onHit；实际规则结算留在适配服务。
- 同线直线、跨线弧线；小于 40px 的位移不弯曲；法线偏移 `min(28, length*0.1)`。
- Godot 的容器与 Position 仍可能被动画修改，因此不能直接将任意节点 Position 当作稳定布局坐标。卡牌有独立布局外壳，祖先震屏偏移也需从测量中排除。

## 初版视觉限制

粒子、命中光斑、指令底色、空气/舰船材质以原生 2D 图元表达，未逐个复刻所有 CSS 关键帧和材质。死亡中的精细舰船翻沉/飞机烟轨、指令呼吸光晕及完整特效混音仍需对照 Web 版打磨。这里明确区分可运行的演出语义与最终视觉一致性。
