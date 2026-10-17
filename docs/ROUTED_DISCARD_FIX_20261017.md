# 溃退多余选靶与弃牌动画

## 程序词条说明被重复执行

真实保存的“溃退”程序只定义场上主动撤退技能，没有部署选靶。但 `WorkshopAuthoring.KeywordGraphs` 仍将它的说明文字送入旧解析器：

```text
溃军可撤退到支援线。
→ trigger: deploy
→ action: retreat(t1)
→ targets: unit / any / support
```

此声明又被加入旧版预打出目标捕获，形成程序以外的额外选择；空场时还可能产生原生错误。修复为：具有 ProgramJson 的词条不从 EffectText 或保留的旧 Effects 注册任何旧版选择图。旧版纯文字词条保持原有流程，不按词条名称分支。

验证：使用实际保存的“溃军”，双方支援线已有其他单位，通过 Godot 手势选择部署空隙；成功部署、无选牌请求、无原生错误。工坊文件哈希未改变。新增回归同时覆盖旧 Effects 残留与仅说明文字两种情况。

## 弃牌演出

原生 card.discarded 在销毁卡牌后发出，旧桥接的活引用解析将其过滤，造成弃牌演出步骤丢失。修复仅在弃牌事实的转换中读取死实体的稳定 ID，并优先使用之前的手牌快照；不放宽游戏规则的活引用校验。

卡牌从原手牌位置抬到可见区域，显示弃置提示，旋转缩小淡出。对手使用匿名卡背，并从对手手牌方向离场。支持减弱动态效果；满手爆牌保持独立语义与事件，不冒充弃牌。

## 验证证据

- `artifacts/routed-discard-final-tests.log`：91 项相关回归通过。
- `artifacts/routed-deploy.log`：`SAVED_ROUTED_DEPLOY_OK ... occupied-both-support-lines ... no-card-picker no-native-errors`。
- `artifacts/routed-description.log`：修复前解析说明文字产生额外部署目标的证据。
- `artifacts/routed-legacy-before.log`：修复前程序词条产生额外旧图的回归失败证据。
- `artifacts/discard-ui.log`、`artifacts/discard-rendered.log`：原生弃牌事件驱动，己方/对方、正常/减弱动态四种演出通过。
- `artifacts/discard-self.png`、`artifacts/discard-enemy.png`：实际渲染画面。

修改已同步并构建至 `H:/g/kards`；没有重写玩家工坊，没有生成新的发布 ZIP。
