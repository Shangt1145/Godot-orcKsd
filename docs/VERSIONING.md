# 版本号规则

日期：2026-10-05。

## 格式

```
MAJOR.MINOR.PATCH[-预发布标识]
```

| 段 | 含义 | 何时 +1 |
|---|---|---|
| **MAJOR** 大版本 | 架构/契约不兼容的变更 | DTO 契约重定义、引擎桥接面不兼容、需要上游同步改造时 |
| **MINOR** 小版本 | 向后兼容的新功能 | 完成一个阶段（如新增演出阶段、接入新的引擎能力） |
| **PATCH** bug 修复版本 | 只修问题，不新增功能 | 修复渲染/演出/桥接缺陷，不引入新行为 |

## 预发布后缀

| 后缀 | 短写 | 用途 |
|---|---|---|
| `-alpha.N` | `A.N` | 未达发布标准的测试版，N 从 1 起递增（**当前 `A.1` = `0.1.0-alpha.1`**） |
| `-hotfix` | — | 已发布版本上的紧急修复，**三位编号不变**；同一版本第 2 次起为 `-hotfix.2`、`-hotfix.3`… |

**优先级**（由低到高）：

```
1.2.3-alpha.1  <  1.2.3  <  1.2.3-hotfix  <  1.2.3-hotfix.2  <  1.2.4
```

即：alpha 早于正式版；正式版之后的紧急修复排在下一个常规版本之前。

## 规则要点

1. **0.x 阶段契约未冻结**：`0.x.y` 的 MINOR 允许不兼容变更，但必须在 CHANGELOG/阶段文档里写明。**1.0.0 起严格执行上表**。
2. **hotfix 不占号**：修的是已发布版本，编号不变只加后缀；该修复**必须同时合回主干**，并在下一个常规版本（PATCH 或 MINOR）中体现，不重复发号。
3. **alpha 不进 PATCH**：alpha 期间的修复直接体现在当前 alpha（递增 `-alpha.N`）；只有已发布版本才走 PATCH/hotfix。
4. **单一真源**：根目录 `VERSION` 文件。它同步写入 `project.godot` 的 `application/config/version`，UI 页脚直接读该设置显示，避免两处不一致。
5. **打标**：每个对外版本打 `v<VERSION>` 标签（如 `v0.1.0-alpha.1`）。

## 操作

改版本用脚本，不要手改两处：

```powershell
& 'H:\Working Folder\OrC-KSD.Godot\tools\version.ps1' -Get
& 'H:\Working Folder\OrC-KSD.Godot\tools\version.ps1' -Set 0.1.0-alpha.2
```

脚本会校验格式并同时更新 `VERSION` 与 `project.godot`。

## 当前状态

`0.1.0-alpha.1`（短写 `A.1`）—— 引擎桥接 P0/P1 完成，真实对局可读取并推进到 `Play` 相位；玩家输入回路尚未接通，因此是 alpha 而非正式版。
