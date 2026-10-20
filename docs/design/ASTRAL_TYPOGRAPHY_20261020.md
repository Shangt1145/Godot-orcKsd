# Astral 卡面字体与位置（2026-10-20）

卡面文字由 PSD 原始文字元数据驱动：字体、字号、填充色、基线、水平缩放、字距和行距由 `tools/export_astral_typography.py` 提取到 `proto/assets/cardface/astral/typography.json`。源模板和源字体均只读。

## 模板排版

以下字号均为 500×701 卡面的设计像素，已计入 PSD 的 0.5 文字变换。

| 字段 | 字体 | 常规字号 | 变体 |
| --- | --- | --- | --- |
| 费用 | Roboto Black | 76 | 两位数 51 |
| 行动费用 | Roboto Black | 30.5 | 紧凑布局 21；0、9 等实际数值均正常显示 |
| 攻击／防御 | Roboto Black | 58 | 两位数采用模板的 41 字号，并居中同一盾形 |
| 单位卡名 | 思源黑体粗体 | 33 | 过长时在标题范围内缩放／省略 |
| 指令／反制卡名 | 思源黑体粗体 | 48 | 有词条时 46 |
| 词条 | 思源黑体粗体 | 30 | 使用中文名称及参数 |
| 单位正文 | 思源黑体常规 | 24 | 原始行距 37.5 |
| 指令／反制正文 | 思源黑体常规 | 20 | 有词条时按对应文字层的 31 字号；原始行距 35 |
| K 标识 | Franklin Gothic Demi Cond | 31 | 两位费用时 21，并按模板切换位置 |

- 采用 PSD 点文字的真实基线与居中方式，不再以 Label 文字框中心近似。
- 有词条／无词条的正文起点分别从相应文字层读取。
- 文字按 PSD 原始分辨率整形，再随卡面同比例缩放；取消旧的最小 8px 字号钳制。
- 正文在模板参考线以内换行，长文本缩小到下限后省略。存储的完整卡名和规则不变，编辑器卡面下方仍显示原文。
- 移除母版里烘焙的固定 K，改由相应文字样式动态绘制，支持个位与两位费用的不同位置。

## 文件

- `proto/ui/components/CardFaceTypography.cs`：字体、整形、换行、基线绘制和受限溢出。
- `proto/ui/components/CardFace.cs`：使用新的动态文字层。
- `tools/export_astral_typography.py`：提取排版元数据及 PSD 缓存文字参考图。
- `tools/export_astral_templates.py`：导出母版时移除固定 K，交给动态文字层。
- `proto/assets/cardface/fonts/`：补入原文件夹中的 Roboto Black、思源黑体常规和 Franklin Gothic Medium Cond。
- `proto/ui/screens/Main.CardFaceTypographyVerification.cs`：实际字体渲染与 PSD 缓存文字边界对照。

## 验证

- 编译成功，0 警告、0 错误。
- 1600×900、1280×720：均出现 `ASTRAL_TYPOGRAPHY_VERIFY_OK` 和 `CARDFACE_VERIFY_OK`。
- 对照源 PSD 的文字缓存与 bbox，验证单位、指令、反制的卡名、费用、攻防、词条、正文。单位费用和行动费的边界一致；单位卡名、攻防有约 1px 字形边缘差异；跨引擎正文边界允许少量抗锯齿与字体度量差异，不宣称 Photoshop 逐像素相同。
- 另外检查两位数、行动费 9、有词条排版、长标题及长正文的边界。
- 原有 DIY 导入、保存重载、构筑、试打回归出现 `DIY_UI_VERIFY_OK`。
- 文件范围对照只改变既有 `CardFace.cs`、`Main.CardFaceVerification.cs`，另新增文字绘制组件与验证探针、导出脚本和资源。桥接层、引擎和对局绘制代码未改。

证据目录：`artifacts/design/astral-typography-20261020/`。`reference/` 为源 PSD 缓存文字；`after/1600/typography-ink.json` 记录逐字段边界；`after/1600/03b-three-masters-art.png` 为更新后的完整卡面。

源码与 `H:/g/kards` 测试工程已更新；没有重新导出发布 EXE。
