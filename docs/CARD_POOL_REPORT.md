# 真卡池编译报告

> 由 `tools/card_pool_probe.py` 生成（读 `proto/data/nations/` 的 315 张卡，编译成引擎定义）。
> 目的：把「UI 停止自造卡牌」这件事变成可核对的数字，而不是一句claim。

## 结论：315 张里 150 张（48%）可以直接上桌

```
catalog=315 compiled=150 rejected=165
    56x set '星盟' is not a faction
    24x set '天气附加' is not a faction
    14x keywords the engine does not implement: guard
    11x set '进攻模式' is not a faction
     8x set 'misc' is not a faction
     7x keywords the engine does not implement: deployment
     5x keywords the engine does not implement: valor
     4x keywords the engine does not implement: impact
     4x set '天气' is not a faction
     3x keywords the engine does not implement: exile
     3x keywords the engine does not implement: shock, impact
     2x keywords the engine does not implement: shield
     2x keywords the engine does not implement: tsekep
     2x keywords the engine does not implement: guerrilla
     1x keywords the engine does not implement: lightArmor
     1x keywords the engine does not implement: valor, exile
     1x keywords the engine does not implement: guard, shield
     1x keywords the engine does not implement: lightArmor1, charge4
     1x keywords the engine does not implement: deployment, pin, shield, magnetic
     1x keywords the engine does not implement: deployment, shield
     1x keywords the engine does not implement: veteran, guerrilla, confiscate
     1x keywords the engine does not implement: 游击
     1x keywords the engine does not implement: guerrilla, deployment
     1x keywords the engine does not implement: deathrattle
     1x keywords the engine does not implement: 老兵
     1x keywords the engine does not implement: impact, deployment
     1x keywords the engine does not implement: shock, exile, impact
     1x keywords the engine does not implement: valor, tsekep
     1x keywords the engine does not implement: veteran, impact, valor
     1x keywords the engine does not implement: airdrop
     1x keywords the engine does not implement: lightArmor2
     1x keywords the engine does not implement: guard, lightArmor2
     1x keywords the engine does not implement: shield, sponge
     1x set '自定义' is not a faction
    e.g. UN/unit/-10
    e.g. UN/unit/-9
    e.g. USG/units/_16
    e.g. UN/unit/-4
    e.g. UN/unit/-13
    e.g. deran/units/_13
    e.g. USG/units/_6
distinct=150
```

## 两类拒绝，性质完全不同

### 一、缺国籍映射 — 103 张（可修）

引擎的 `Faction` 只有 7 个值（德/苏/美/英/日/法/意），且 `factionCost` 是**必填槽位**
（国籍与部署费绑定）。而下列内容包不是国家：

| set | 张数 | 是什么 |
|---|---|---|
| 星盟 | 56 | 机制卡 |
| 天气附加 | 24 | 机制卡 |
| 进攻模式 | 11 | 模式卡 |
| misc | 8 | 杂项 |
| 天气 | 4 | 机制卡 |
| 自定义 | 1 | — |

**这不是 UI 能绕的**：引擎架构里「无国籍卡」没有位置。
要么引擎加一个中立/无阵营选项，要么这些卡只能做成 token 或不進對局。

### 二、引擎词条未实现 — 62 张（要改引擎）

| 词条 | 张数 | 状态 |
|---|---|---|
| guard | 14 | 引擎注释明说「官方 guard 走未实现留痕」 |
| deployment | 7 | 未注册 |
| valor | 5 | 未注册 |
| impact | 4 | 未注册 |
| exile | 3 | 未注册 |
| shock | 3 | 未注册 |
| shield | 2 | 未注册 |
| tsekep | 2 | 未注册（拼写疑为 tse-kep 飞艇） |
| guerrilla | 2 | 未注册 |
| lightArmor | 1 | 未注册 |
| 游击/老兵 | 2 | **JSON 里已是中文，但词表无此词条** |

引擎词条表只认 14 个中文标识：闪击/奋战/烟幕/伏击/被压制/被抑制/动员/钳击/预报/免疫/重甲/情报/无法被压制/无法被抑制/亡计。
而 JSON 里是英文 id，共 31 种。**已建立 9 条映射**（blitz/fury/smokescreen/ambush/mobilize/pincer/armor + 三个中文别名），
带数值后缀的（armor1/armor2）折叠到基础词条并保留最大值——引擎禁止重复词条，且会 fail-fast。

## 设计决定

**未知词条一律拒整张卡，不静默丢弃。** 丢掉词条等于交给引擎一张行为与印刷不同的卡，
这比不提供这张卡更糟。 有 14 张，正是这种情况。

## 复现

```bash
python tools/card_pool_probe.py
```
