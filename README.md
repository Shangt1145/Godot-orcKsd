# OrC-KSD.Godot · 对战 UI

> 版本规则见 `docs/VERSIONING.md`，当前版本见根目录 `VERSION`（应用内页脚同步显示）

Godot 4.7 .NET + C# 原型，入口为 `proto/scenes/Main.tscn`。

当前源码在 `H:/Working Folder/OrC-KSD.Godot`，实际构建和运行副本在 **`H:/g/kards`**。只在源码目录编辑，使用脚本同步；短路径中的 `.godot`、bin、obj 不会复制回源码。参考项目与引擎源码均保持只读。

## 启动

启动脚本兼容 Windows PowerShell 5.1 和 PowerShell 7；`.ps1` 使用带 BOM 的 UTF-8，确保中文素材目录名能正确解析。编辑脚本时保留该编码（`.editorconfig` 已配置）。

首次准备卡池与素材：

```powershell
& 'H:\Working Folder\OrC-KSD.Godot\tools\prepare.ps1'
```

打开原型：

```powershell
& 'H:\Working Folder\OrC-KSD.Godot\tools\run.ps1'
```

`run.ps1 -Editor` 打开 Godot 编辑器；`-Test -Verify` 执行消费层测试、战斗交互检查和场景启动检查；`-VerifyEffects` 检查各类演出生命周期；`-Capture` 生成战斗棋盘、瞄准、手牌详情、小窗口、图鉴与演示场景截图后退出。

工具路径默认使用本次下载的 `H:/g/tools/godot47/Godot_v4.7-stable_mono_win64/`，可以通过 `-Godot` 指定其他 4.7 .NET 可执行文件。工具包来自 [Godot 官方 4.7 下载页](https://godotengine.org/download/archive/4.7-stable/)。

本机 `H:/Godot_v4.7-stable_win64.exe` 是普通版，探针检查 `ClassDB.class_exists("CSharpScript")` 为 false；下载的 .NET 版为 true。`OS.has_feature("mono")` 不是本机可靠的判定方法。

## 已接入

- 7 屏导航；对战页铺满窗口，通过右上角齿轮进入图鉴/设置。卡组、牌库、编辑器为占位页。
- 对战第二阶段：按原版分离大号橙色指挥点、K 与横线下槽数；使用 image_gen 桌面、卡背、HQ 地图和爆炸纹理。保留第一阶段部署/移动/手牌操作，增加五类兵种攻击、受击、反击、死亡和 HQ 演出。详见 `docs/BATTLE_STAGE2.md`。
- 对战第三阶段：我方及对手抽牌、连续抽牌、指令揭示/停靠/退场、多目标同时命中与最终重排；外部载荷驱动，隐藏信息与中断清理受保护。详见 `docs/BATTLE_STAGE3.md`。
- 对战第四阶段：抽牌移动中带惯性翻面到屏幕中央停留；反制在手牌内武装、解除与触发，治疗/强化/压制解除/费用变化反馈。详见 `docs/BATTLE_STAGE4.md`。
- 315 张定义的图鉴、5 维筛选、文本搜索、分页、点击/长按详情。
- 卡牌组件：素材、基础/有效数值、词条参值、稀有度边框、国别信息、静默/抑制/老兵状态、目标与攻击高亮。
- 25 组动画演示，8 种攻击兵种，3 档拍桌防御值、速度档、减弱动效、音效与偏好持久化。
- mock 事件段消费、历史值复制、混合多槽位请求、首次拒绝后的重试和取消。
- 操作手势：**一律拖拽**提交行动，点击只负责选中与查看预览，右键/Esc 取消。
  - 手牌：整张卡跟随光标拖到我方支援线部署。
  - 场上单位：**卡牌留在原位**，拖出的是箭头（移到前线＝移动箭头，指向敌方＝攻击箭头），松手提交。
  - 落点即插槽：支援线与前线都按松手位置决定插在哪个单位的左边或右边（前线是双方共用的一排）。
- 部署落地**拍桌**：按防御力分三档（≤2 轻 / 3–5 中 / ≥6 重），空中兵种时长 ×1.25；中/重档带扬尘。参数为表现估值，非原版实测。
- 独立的 net8 契约与消费层；Godot 主项目为 net9。

当前源码 `animate.js` 实际有 **63 个不同导出项**，含常量与工具，见 `docs/ANIMATION_PORT.md`。这些是动画移植初版，原版逐帧手感验收仍需记录在 `docs/ACCEPTANCE.md`。

## 素材与内存

源数据在发行包 `dist/win-unpacked/resources/app`，prepare 脚本复制 7 个图片目录及根目录图片：576 张 PNG、53 个 mp3、11 个 nations JSON。其中 `_meta.json` 不含 cards。

实际卡池是 161 单位、150 指令、4 反制；39 张卡没有 art，使用文字卡面。另有部分自定义指令含非空基础攻防，读取器保留原值，不擅自修正源数据。

图鉴原始纹理采用 VRAM Compressed；图鉴每页 18 张，缓存最多 64 个纹理资源，渲染默认 Forward Plus。原始卡面、音效和卡池从本地发行包准备。四张新生成对战纹理保存在 `proto/assets/battle/`，源码同步时一并复制；完整提示词与生成记录见该目录的 `ASSETS.md`。

## 契约与后续接入

`proto/contracts/README.md` 是给上游的提案草稿，尚未发送。**本项目只做 UI，主引擎架构尚未完成。** UI 消费值对象；真实引擎对象、规则与 Ref 映射留在适配层。演示攻击通过独立本地夹具驱动；外部模式只发请求并消费 `UiCombatResolution` 和 `UiPresentationResolution`，不计算伤害或胜负。尚无生产引擎连接、联机、卡组合法性或存档协议；后续继续起手、选择、回合反馈及专用状态素材。

引擎状态读面、可见性与历史值投影确认后，再用真实适配器替换 mock。不能将当前事件段当作存档，也不能从日志活引用延迟推断当时的数值。
