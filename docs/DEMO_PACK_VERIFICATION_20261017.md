# 机制示范包实际数据核查

2026-10-17。核查当前开发版，读取真实用户工坊保存的 `pack/diy-demo-mechanics`，未运行种子工具或重写工坊。

## 结论

本次要求的拖放交互在真实包上正确实施，但示范包不能判为整体正确：另有两个可复现的机制问题。

| 核查对象 | 实际验证 | 结果 |
|---|---|---|
| 全部 11 张卡、11 个词条 | 从实际 diy-packs.json / diy-cards.json 加载，逐卡原生编译验证 | 通过；这不代表所有词条行为已逐一验收 |
| 装填 | 手牌按住后直接拖箭头；拖动中牌留在手牌，松手一次选靶并打出 | 通过；无第二次选择请求 |
| 装填生成的发射 | 原生实例携带 damage=2，直接拖箭头选敌军，松手打出 | 通过；敌军防御 3→1，伤害等于记录值 |
| 缠斗步兵部署 | 先拖至落点，松手后显示该位置的单位预览；第二次拖箭头选敌军 | 通过；两个阶段各自确认，原生链接建立 |
| 直接加入卡组的发射 | 未经装填生成，无 KeywordProgramData；仍显示可打出 | 不通过；提交结果 Applied，消耗 2 点、移出手牌，却造成 0 伤害 |
| 缠斗的周期战斗 | 双方单位均在支援阵线，建立链接后推进敌方及己方回合开始 | 不通过；敌军防御仍为 1，缠斗步兵防御仍为 3，没有交战 |

## 两个问题的处理方向

1. **无记录的发射应在使用前拒绝。** 包目前将“发射”与普通卡一样放入卡池，允许直接构筑，但它依赖装填写入实例记录。需要生成专用卡的准入策略，或明确所需实例输入并在候选/提交阶段一致拒绝缺失记录的使用。不能将缺失值默认为零并消耗卡牌。
2. **缠斗的周期攻击没有满足交战条件。** 保存程序的 attack 动作为 `bypassRange=false`，而原生效果攻击仍复验合法攻击目标；支援线步兵不能正常攻击敌方支援线。这与本次“不交战”的复现相符。应明确缠斗对距离、守护、拦截等限制的豁免范围，在程序/原生交战入口修复，并补上双方支援线的真实回合用例；不能改成直接扣血代替交战。

本次未改动词条定义或玩家存档。未对其余词条逐一完成全场景行为验收。

## 证据与复跑

- 验证入口：Godot `--verify-demo-pack-targeting`，实现见 `proto/ui/screens/Main.DemoPackVerification.cs`。
- 原生无界面运行日志：`artifacts/demo-pack-targeting.log`。
- Vulkan 实际渲染与鼠标事件日志：`artifacts/demo-pack-targeting-rendered.log`，错误输出为空。
- 实际画面：`artifacts/demo-pack-load-arrow.png`、`artifacts/demo-pack-fire-arrow.png`、`artifacts/demo-pack-grapple-target.png`。
- 最终审计标记：`SAVED_DEMO_PACK_AUDIT_COMPLETE targeting=pass findings=2 workshop-files-unchanged`。
- 构建成功，零警告、零错误；本次没有导出或发布新游戏包。

输入路径：`%APPDATA%/Godot/app_userdata/OrC-KSD · 卡牌指挥部/`。验证前后文件 SHA-256 一致：

```text
diy-packs.json  0DF57609EDE6DCA8B0F6F80D5E38595372BEFDAD27FF4B88D2A8CE133DD32273
diy-cards.json  2407F4600C7DB593DE065DF920B29FF1470296721CD548ECD6A99E3B18A80A89
```
