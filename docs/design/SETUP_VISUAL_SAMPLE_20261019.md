# 作战准备页样板 · 2026-10-19

> 历史样板：现已退出主要开战流程，首页直接开战。当前改版范围见 MENU_UI_OVERHAUL_20261019.md；对局界面保持原样。

本轮把实际 Godot 作战准备页改为地图桌面、双方卡组与纸质出击简报。准备页隐藏全局导航及页脚，返回指挥部后恢复。保留当前卡组验证、进入真实对局与更换卡组入口。未启用模式不再展示；内置对手执行参数放入可展开详情。

实现：
- proto/ui/screens/SetupScreen.cs：页面与原生 Godot 绘制组件；沿用本地地图、卡背素材。
- proto/ui/screens/Main.cs：准备页导航显隐。
- proto/ui/screens/Main.MenuMotion.cs：实际截图、详情及返回/更换卡组点击检查。

验证：
- dotnet build H:/g/kards/Kards.Ui.csproj：0 warnings / 0 errors。
- --verify-menu：MENU_VERIFY_OK，包含主页进入准备页、点击开始进入已保存卡组的原生对局、减弱动效与导航回归。
- --capture-setup：1600×900 与 1280×720 均通过 SETUP_CAPTURE_OK，检查页面边界、详情展开收起、返回导航恢复与更换卡组入口。
- 人工查看实际截图的默认及详情状态，检查文字与按钮可读性。

截图：artifacts/design/implemented/setup-1600.png、setup-details-1600.png、setup-1280.png、setup-details-1280.png。

本轮是开战准备页的第一张样板；首页、收藏、构筑、工坊与战场尚未套用新视觉。构建副本已按 prepare.ps1 同步到 H:/g/kards；本轮未生成新的发布包。
