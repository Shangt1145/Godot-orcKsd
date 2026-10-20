# Astral 单位／指令／反制母版自动切换（2026-10-20）

源模板来自用户指定的 `H:/Working Folder/AstralKards模板2024新年版`。原始 PSD 保持只读。

## 已实现

- 按卡牌 `CardType` 自动选择单位、指令、反制母版；编辑时即时切换，加载和保存后重新打开仍按同一字段选择。
- 单位来自 `Astral单位模板.psd`。指令与反制来自 `Astral指令模板.psd`，分别使用 `类型/指令` 和隐藏的 `类型/反制指令` 图层。
- 导出并使用原始透明边框、费用 K 字样、单位兵种／攻防徽章、指令 !／反制 ? 标识；移除此前遮挡卡名的临时纸面覆盖方式。
- 插画放在真实透明边框下方，卡名、费用和属性作为动态字段覆盖在上方。
- 编辑器的卡牌类别列出三类，预览标题显示当前母版。卡牌列表中的反制显示为反制。
- 微调工具自动跟随当前母版。配置 version 3 分别存储 unit/order/counter，保留旧版单组和 version 2 文件的读取兼容。

## 源文件

- `tools/export_astral_templates.py`：直接提取 PSD 原始图层；依赖 psd-tools 与 Pillow。
- `proto/assets/cardface/astral/`：三个母版、八种单位徽章图层，以及带源文件 SHA-256 的 `sources.json`。
- `CardFace.cs`：母版选择、叠放顺序、三套独立微调状态。
- `CardFaceTuner.cs`：自动选择母版和配置兼容。
- `DiyEditorScreen.cs`：类别选项、母版提示和正确类别名称。

复导出：`python tools/export_astral_templates.py --source "H:/Working Folder/AstralKards模板2024新年版"`。

## 验证与边界

- 编译成功，0 警告、0 错误。
- `--capture-cardface` 在 1600×900、1280×720 均通过：三类自动切换、真实 !／? 徽章像素差异、实际编辑器切换、保存重开类别保留，以及三套参数互不覆盖。
- 保留上一轮多尺寸插画、缩放、拖动、滚轮、关闭丢弃、旧配置与无效配置的检查。
- `--verify-diy` 的原有导入、编辑、保存、构筑及试打流程通过。
- 本次未修改桥接层、引擎和对局绘制组件。反制的规则编译／准入能力沿用现状，不因母版可显示而绕过校验。验收使用的一张随附反制带 `dualAttack` 扩展字段，仍以完整声明草稿保存；不宣称该卡已可实战。
- 未重新打包发布 EXE；已同步并编译短路径测试工程 `H:/g/kards`。

## 截图与日志

`artifacts/design/astral-masters-20261020/after/1600/03b-three-masters-art.png` 为同一插画下三类母版对比。

`03a-three-masters.png` 为定位条纹与类型徽章检查；`09a-editor-counter.png`、`09b-tuner-counter.png` 为反制编辑器和微调工具实机截图。1280 目录包含较小窗口下的同组检查。编译、资源导入、两种分辨率探针和 DIY 回归日志与文件范围检查均保存在证据目录。
