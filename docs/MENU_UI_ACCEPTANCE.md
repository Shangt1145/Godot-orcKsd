# A.21：主菜单与对局外界面改版

日期：2026-10-11。版本：`0.0.0-alpha.21`。

本轮按用户澄清，以主菜单、卡组库、收藏、构筑、DIY 和设置等对局外页面为范围。[原版研究](KARDS_MENU_RESEARCH_20261011.md)记录官方材料和实际看过的参考图。

## 交付

- 新增主菜单：左侧“开始／卡牌／DIY 工坊”，中央地图与卡牌展示，右侧当前对战卡组和开始／编辑，下方真实卡库数量。
- 默认启动停在首页；“开始”进入卡组选择，首页也可直接使用当前有效卡组开局。
- 顶部品牌返回首页，卡牌区域共用“我的卡组／收藏／构筑”导航，帮助和设置保留在右侧。构筑、DIY 隐藏侧栏保留完整宽度。
- 卡组库改为可滚动封面网格和右侧详情，新增空封面入口。封面描边表示正在查看，独立文字表示当前对战卡组；查看不激活卡组。保留新建、编辑、复制、确认删除、导入导出、启用和真实开局。
- 收藏顶部组织标题、搜索、筛选、重置和数量；筛选展开后提供原有国别／卡类／兵种／稀有度／词条选项，并显示已应用条件数。卡牌详情保留在右侧。
- 菜单使用单独的军绿／旧纸色主题、方形按钮和橙色主操作；背景、地图和卡背复用现有资产。主展示由 Godot 绘制，未新增下载的视觉素材。
- 设置改为演出和音效分区，音量显示当前百分比，仍使用原来的持久化偏好。
- 战场菜单新增“返回主菜单”；当前实例和待选状态保留，“返回战场”继续同一对局。隐藏战场先检查可见性，避免右键取消后台待选或后台待选拦截菜单按键。
- 去除旧页脚“第二阶段”文案，显示实际版本，并修正窗口标题乱码。

游戏规则、卡池准入、主盟国与张数选项、敌方回合锁、抽牌接管、空隙部署及演出顺序沿用 A.20。此次未修改引擎源码。菜单没有引入未接通的金币、商城、好友、任务、国家进度或联网 PvP。

## 验证

| 检查 | 结果 |
|---|---|
| 短路径 Godot .NET 编译 | 通过，0 警告、0 错误 |
| UI／桥接单元测试 | 258 / 258 通过 |
| `--verify-menu` | 通过：默认首页不自动开局、真实鼠标导航、查看与启用分离、草稿禁止开局、所选卡组原生发牌、同一实例恢复、隐藏抉择不干扰菜单输入 |
| `--verify-decks` | 通过：鼠标增减卡、草稿保存／重载、复制、确认删除、导出、非法／合法导入、自定义张数及不限国家开局 |
| Forward+ 实际渲染 | 1600×900、1280×720 均通过导航与页面边界检查，人工查看主菜单、卡组选择、收藏、筛选、构筑、DIY 和设置；64 字卡组名使用两行省略显示，完整名称保留在悬停提示 |
| 完整 `--verify-ui` | 输入修正后通过：`UI_VERIFY_OK screens=8 cards=315 texture_cache=64/64`；新增名称显示限制后，菜单专项在两种分辨率复验通过 |
| `git diff --check` | 通过 |

菜单场景验收使用隔离的 `res://artifacts` 卡库，生成的展示卡组会在退出前清理；不会改写正常用户卡库。卡组列数按逻辑可用宽度计算，窗口在 1280×720 下按现有 Godot canvas stretch 缩放。

证据：`artifacts/menu/godot-menu-headless.log`、`godot-menu-1600.log`、`godot-menu-1280.log`、`godot-full-ui.log`。最终展示图位于 `artifacts/menu/1600x900`、`artifacts/menu/1280x720`，包含 `home`、`home-long-name`、`deck-selection`、`deck-draft`、`decks`、`collection`、`collection-filters`、`builder`、`diy`、`settings`。

完整回归覆盖既有战场部署、移动、攻击、指令、选择、回合锁、连续输入、抽牌因果、状态与终局、DIY 和构筑。日志中无效 DIY 效果的告警来自既有隔离测试样本，是验证明确拒绝未知效果的预期输出。

复现：

```powershell
& .\tools\prepare.ps1 -SkipAssets
dotnet build H:\g\kards\Kards.Ui.csproj --no-restore --nologo
& 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe' --headless --path H:\g\kards -- --verify-menu
# 图形渲染并保存到指定目录：
$env:KARDS_CAPTURE_DIR = 'H:/Working Folder/OrC-KSD.Godot/artifacts/menu/1600x900'
& 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe' --path H:\g\kards --resolution 1600x900 -- --capture-menu
```

## 主要实现

- `proto/ui/screens/HomeScreen.cs`：首页与实际卡库数据。
- `proto/ui/screens/Main.MenuShell.cs`：菜单壳、一级／二级导航和原对局返回。
- `proto/ui/components/MenuArtwork.cs`、`UiStyles.cs`：本地素材组合与独立主题。
- `proto/ui/screens/DeckLibraryScreen.cs`：封面、选择状态和右侧详情；现有持久化及构筑服务保持原边界。
- `proto/ui/screens/Main.MenuVerification.cs`：真实鼠标和原生开局验收，包含隐藏待选输入复现。
- `Main.cs`、`CollectionScreen.cs`、`DeckEditorScreen.cs`、`BattleScreen.cs`：启动、收藏、编辑主题及菜单导航接入。

这是依据官方结构完成的项目适配；使用本项目的国别标识、素材与现有功能，不声明逐像素复制原版。
