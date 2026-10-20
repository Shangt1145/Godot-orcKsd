# Godot 内置 OrC / Inspector · alpha.36

2026-10-16。用户确认的边界：整个游戏基于 Godot，不能借用网页；游戏后端必须基于 OrC-KSD。

## 运行方式

DIY 工坊 → 打开效果编辑器，默认使用中文效果搭建。普通页面按“什么时候触发 / 要做什么”展示；结构树、原始字段和 JSON 收在高级工具中。界面全部由 Godot 原生控件组成。没有浏览器、WebView、HTML UI、本地 HTTP 服务或 Node 子进程。

原版 JS 语法编译链通过随包的 Microsoft.ClearScript.V8 7.5.1.1 在游戏进程内运行，保留 Unicode 正则与原版解析行为。只向脚本提供 JSON，不暴露主机对象、文件、进程、网络 API。JS 用于编译声明、校验和元数据，不创建或推进 Desktop 对局。对局继续使用 OrC-KSD 的 Match、EffectCompilationDriver、EffectRuntime 和 TargeterManager。

目前随包运行时是 Windows x64，与本项目当前交付平台一致；其他平台需补齐对应 ClearScript 原生包。用户无需安装 Node.js 或复制 Kards-Desktop。

## 易用界面与通用性

- 默认展示可读的效果概述、触发时机和顺序动作卡片；对象、属性、数值使用中文控件。
- 动作选择器由完整原版 vocabulary 生成，支持分组和搜索；必填参数直接显示，可选参数按需添加或恢复默认。
- 数值先直接填写，再按需切换属性读取、变量、计数、算式、记忆或集合表达式；嵌套条件、流程、目标选择和子效果使用同一组递归编辑器。
- 词条使用独立勾选窗口，参数由用户按需指定，不要求编辑内部字段名。
- InspectorPresentation 只提供标签、摘要和控件提示；它不判断规则合法性，不执行游戏逻辑。字段推断继续来自原版 editorCatalog.infer。
- 不按卡牌 ID、卡名或某几个效果示例分支实现 UI。全部 161 种动作、全部声明参数（含可选参数）逐项打开表单，检查原声明与未知嵌套字段保持不变。
- 目标声明写入最近的子效果作用域，避免给单位绑定新效果时，玩家选择的目标错误地串到外层。
- 旧格式、未知扩展和过深组合仍可在高级工具编辑；打开普通表单不补写默认值，也不裁剪字段。

## 编辑面

| 面 | 实现 |
|---|---|
| 自动解析 | 完整原版编译链，保留效果图、卡面字段、词条及参值、诊断；不退回缩减句式表 |
| 动作目录 | 原版 contract / editorCatalog 的全部 161 项，搜索、原语默认值、可选参数与别名 |
| 完整结构 | 时机（含行为及前/中/后阶段）、目标声明、条件、变量、表达式、查询、筛选、嵌套动作与子效果，逐字段编辑 |
| 通用数据 | 任意对象字段增删/改名/类型切换，数组增删/排序/复制；未知字段、null、嵌套对象和数组无损保留 |
| 卡面 | 卡面字段及 kwMap / kwValues；词条下拉和参数编辑；卡牌引用下拉使用实际卡池 |
| 辅助 | 4 个原版常见示例、表达式/谓词模板、完整 JSON、复制粘贴、80 步整份声明撤销/重做、原版 12 课教程的文本版 |
| 保存 | 编辑实时写回卡牌草稿，保存直接在 Godot 主线程调用 DiyCardLibrary 原子写盘；未应用的无效 JSON 阻止保存和完成 |
| 标记 | 手动“我已实测，标记通过”保存声明的 effectStatus；该标记不绕过 OrC-KSD 规则准入 |
| 批量导入 | 原版 CSV 引号处理、单卡/数组/官方记录规范化；完整声明持久化；手写 effects 优先于卡面文字 |
| 兼容 | 旧有限结构与简易行留在兼容标签；原有声明不会被隐式转换或清空 |

完整效果以 OrcEffectDraft.DesktopJson 为真源，卡面扩展以 DesktopCardJson 保存。InspectorDocument 的历史保存整份 JSON；不向有限动作 DTO 投影。文本追加与示例追加使用原版目标重编号逻辑。

## 来源与复现

本机 `H:/Working Folder/Kards-Desktop/electron/game` 的实际工作树文件移入 DesktopAuthoring。逐文件源 SHA-256 固定在 source-manifest.json；源工作树部分文件尚未纳入提交，不能只用 Git HEAD 标识。

解析、contract 和无损模型源码嵌入程序集。原版 HTML、CSS、effect-editor 与 inspector-help 仅为迁移参考，不嵌入运行时；它们不承担任何游戏界面。自建浏览器工作台与 HTTP 服务已移除。inspector-tutorial.txt 是原版教程正文的静态文本抽取。

`tools/sync-desktop-authoring.ps1` 固定来源。`tools/check-desktop-parity.cjs` 是开发期 Node 对照工具，游戏运行不调用它。`--write-reference` 从独立原项目生成 `tests/fixtures/desktop-orc-reference.json`，供 .NET 对游戏进程内解析结果逐条核对完整 JSON 的 SHA-256。

```powershell
node tools/check-desktop-parity.cjs
# 只在审核源版本后更新对照样本：
node tools/check-desktop-parity.cjs --write-reference
dotnet test tests/Kards.Ui.Tests.csproj --filter FullyQualifiedName~DesktopAuthoringTests
& tools/run.ps1 -Verify
# 同步、构建并准备随包依赖后的专项检查：
& 'H:/g/tools/godot47/Godot_v4.7-stable_mono_win64/Godot_v4.7-stable_mono_win64_console.exe' --headless --path H:/g/kards -- --verify-desktop-authoring
# 去掉 --headless 并换成 --capture-desktop-authoring 可生成原生界面截图。
```

## 验证与边界

- 独立原版对照：1553/1553 条完整结果相同，697 条完整解析，双方动作目录 161 项。
- 进程内解析专项：16 项全部通过，包含上述 1553 条完整结果的 SHA-256 对照、嵌套结构、未知字段、CSV、手写效果、变量表达式、真实对局抽两张牌、命中所选第二个目标与取消不扣费。
- Godot 控件专项：真实鼠标添加效果/循环/嵌套动作，选择计算值和条件分支，词条勾选，撤销/重做、卡面字段实时回写、无效 JSON 阻止保存、实际落盘重开、未知字段保留、OrC-KSD 编译；161 种动作全部参数的普通表单无损打开。
- 界面截图覆盖 1600 / 1280 宽度。
- 完整 Godot 场景回归通过：`UI_VERIFY_OK screens=9 cards=414 texture_cache=64/64`；旧简易行的鼠标编辑、保存重开与试打回归通过。
- 全量 .NET 测试：373/395 通过，22 项既有迁移失败；失败名单与移植前一致，不能称为全仓全绿。

161 是可编辑动作数量，不表示 OrC-KSD 已执行全部动作。697 是原版本身完整解析的文本数量，也不代表全部已可实战。原生适配仍未覆盖任意行为时机监听、全部跨回合记忆/绑定效果、完整光环及特殊修正、创建/变形/控制转移、完整开发/抉择池等。这些声明保留可编辑并报告缺口，不自动准入对局。

证据在 artifacts/desktop-authoring：source-parity.json、full-corpus.json、godot-friendly-final.trx、godot-friendly.log / godot-friendly-full-ui.log和 godot-inspector-*.png。探针使用随机 ID，只清理自己新建的测试卡。
