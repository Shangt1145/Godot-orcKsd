# 引擎更新后的只读检查与 DIY 界面优化 · 2026-10-19

## 检查范围与基线

用户要求先检查引擎更新和已有 UI 改动，不修改这些适配，再继续优化 UI。检查时引擎 HEAD 为 `b5e0a5c`，工作树干净；UI HEAD 为 `54e9433`，存在此前多轮和其他人的大量未提交改动。没有拉取、重置、提交或发布。

在任何 UI 优化前，记录了 UI／桥／测试及引擎共 531 个现有 C# 源文件的 SHA-256。基线位于 `artifacts/design/diy-ui-current-20261019/before-hashes.json`。最终比对仅有五个现有 UI 文件变化；引擎、整个桥接层、对局源码和现有测试源码均未变化。`Main.cs` 的新增内容只有专用布局验收入口两行。完整结果为同目录 `scope-check.json`。

## 检查发现

1. 新引擎的 `423b057` 引入 Action 契约／实现注册、伤害管线及 `Orc.Foundation.Actions`，`CommandCard` 更名为 `OrderCard`。
2. 当前 UI 桥已有相应适配，包括 OrderCard 引用、OrderCastView 的载荷绑定，以及对手回合／输入投影等改动。本轮保留原样。只读检查后的构建通过，7 项 DIY 更新专项测试通过。
3. 当前原生 `Orc.Game/Cards/EffectSystem.cs:328` 已将 band 名传给 InjectByFramework；旧交接文档中的“只支持空 band”结论已过时。
4. **仍有一处桥接遗留：** `DiyInjectionCatalog.cs:21` 及 `:37` 仍把所有非空 band 标为仅草稿。其 Schemes 已更新为真实 ActionTriggerBands／ActionHandleBands，但准入逻辑尚未同步。这可能误拦当前引擎可接受的声明。按本轮“检查不改适配代码”的边界，记录问题，没有改桥、删测试或在 UI 绕过准入。界面仍如实显示桥返回的状态。未来修复应按目标触发器的真实 band 方案验证，不能把所有已知 band 无条件放行。
5. 以上检查不等同于全引擎代码审计，也不把现有测试通过视为全部新机制已接入。

## UI 改动

- 效果搭建分为“图形搭建／结构编辑／编译结果／对局计数器”四页。移除结构编辑页常驻的编译 JSON 侧栏，释放实际编辑宽度。
- 计数器引用、共享语义提示和批量重命名移入独立页，不再占据条件与动作表单的首屏。
- 结构参数分成生效条件、作用对象、对局计数器、数值与期限。字段按可用宽度排列，隐藏无关项。
- 图形搭建把生效条件从时机卡片中分出，按时机→条件→动作阅读；左侧导航宽度随窗口变化。
- 点击“仅草稿”后，当前效果的错误原因在标题下方按完整编辑宽度展开，避免撑宽标题工具条。
- 标题显示当前卡牌名称；数值修改同步刷新动作摘要。
- 未知结构动作以保留参数的只读提示呈现，读取时不再默默变成目录首个动作。
- 没有改动引擎机制、编译门、对局布局或对局交互代码。

现有文件改动仅限：

- `proto/ui/screens/DiyEffectEditorScreen.cs`
- `proto/ui/screens/NativeEffectInspector.Friendly.cs`
- `proto/ui/screens/NativeEffectInspector.Capabilities.cs`
- `proto/ui/components/DiyCapabilityBadge.cs`
- `proto/ui/screens/Main.cs`（仅新增布局探针分派）

新增 `Main.DiyLayoutVerification.cs` 为专用实际操作与截图探针，使用验证隔离存储。

## 验证

- 常规 UI 构建：0 warnings / 0 errors。
- DIY 更新、内置包修改、工坊、DIY 卡牌四组测试合计 **48 项通过**。
- `--verify-diy` 通过，包含真实鼠标保存、编辑重开、导入导出、隔离无效声明、构筑及原生部署；故意无效卡产生预期警告。
- `--capture-diy-layout` 在 **1280×720、1600×900** 均通过：实际打开编辑器、修改条件并保存重开、展开单条错误、计数器批量改名、检查编译报告、只读注入展示和未知动作原参数保留。
- 全套 `--verify-ui` 通过，退出码 0，最终输出 `UI_VERIFY_OK screens=10 cards=531 texture_cache=64/64`；覆盖菜单、收藏、素材、对局展示、选择、原生交互、DIY 和构筑回归。故意无效声明产生的警告属于测试预期。

截图与日志：`artifacts/design/diy-ui-current-20261019/`，其中 before/1280 是优化前实机截图，after/1280 和 after/1600 是优化后截图。没有用旧截图代替本轮结果。

运行方式：

```powershell
.\tools\prepare.ps1 -SkipAssets
dotnet build H:\g\kards\Kards.Ui.csproj --nologo
& 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe' --path H:\g\kards --resolution 1280x720 -- --capture-diy-layout
```

源码和调试运行目录已更新，既有 dist 安装包未重新发布。
