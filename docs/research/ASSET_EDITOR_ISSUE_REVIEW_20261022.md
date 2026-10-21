# 核对：《编辑器未接入资产编辑器（AssetEditor）》

> 核对日期：2026-10-22 · 被核对文档：`前端编辑器验收Issue-20261021.md`
> 核对方式：静态代码核查（`AssetEditor` 调用点、包产物、工具链、样例、文档）
> 结论：**9 条里 4 条已过时或部分过时，5 条仍成立**。

## 0. 一句话

issue 基于 `0.0.0-beta.3`，而 `beta.4` 的 `NativeWorkshop` **已经接入了 `AssetEditor`**
并具备 `BeginSession`／`Promote`／版本字段——所以"完全并行、无法成为引擎资产"这个总判断
不再成立。剩下的真问题集中在**旧兼容路径、产包工具、样例与文档**。

## 1. 逐条对账

| # | issue 的断言 | 现状 | 判定 |
|---|---|---|---|
| 1 | 编辑只写 `user://diy-*.json`，不写 `<包目录>.tmp/` | 原生工坊走 `Editor.Session.TemporaryRoot`；旧 DIY 仍写 `user://diy-cards.json` / `diy-packs.json`（`Main.cs:105/112`） | ⚠️ 部分（双轨） |
| 2 | 无 sidecar / lock / 转正 / 归档 | `AssetEditor` 提供 `BeginSession` / `EndSession` / `Promote` / `SetVersion` / `lock.preview`；`NativeWorkshop.cs:316` 已 `EndSession(Discard)` | ✅ 已过时 |
| 3 | 全仓 `AssetEditor` 命中 0、`editor.*` 调用 0 | `NativeWorkshop.Authoring.cs:22` `new AssetEditor(...)`；`NativeWorkshop.cs:230/241` `BeginSession(Edit/CreateNew)`；`NativeWorkshop.cs:286` `Promote()` | ❌ **不成立** |
| 4 | `manifest.json` 仍是 v1 形态（`dependsOn` / `assets[]`，缺 `schemaVersion:2` / `metadata[]`） | `tools/OfficialCards/Program.cs:96` 仍写 `dependsOn=…, assets=…`，确实没有 `schemaVersion` / `metadata` | ✅ **成立** |
| 5 | 效果模板仍输出 `modings`，无 `wrappers[]`；"后端 `PrefabJson` 已无 `modings` 成员" | 原生路径已用 `wrappers`（`NativeWorkshop.Effects.cs:49`）；旧路径 `DiyCardCompiler.cs:145`、`OrcImportService.cs:220` 仍写 `modings`。但**引擎仍在解析 `modings`**（`Orc.Game/EffectParsing/Templates/EffectTemplateJson.cs:170`，`EffectSnapshot` 同样保留），`wrappers` 是新增能力而非替换 | ⚠️ 部分 + 事实有误 |
| 6 | 无资产版本概念 | `NativeWorkshopPack.Version` 已有；`AssetEditor.SetVersion` 可改；`DiyCardDraft` 仍旧无 `version` | ⚠️ 部分（原生有、旧路径无） |
| 7 | 应用层 ID 不符合 `pack/{包id}/{局部id}` | 原生路径用 `pack/{包}/{局部id}`（`CardPackContent.Prefix`）；旧 DIY 仍 `diy/<guid>`（`UiDiyCard.cs:20`） | ⚠️ 部分 |
| 8 | `samples/demo-card-pack` 仍是旧 `pack.json`，无 `manifest.json` / sidecar | 目录仍是 `README.md` + `cards/` + `effects/` + `pack.json`，`pack.json` 为 `schemaVersion:1` | ✅ **成立** |
| 9 | 仓库自认缺口与过期文档 | `docs/design/DIY_PACK_DESIGN_20261012.md` 仍有 4 处 `pack.json`；`ASSET_MODEL_MIGRATION_ISSUES_20261013.md:128` 仍写"卡包管理 UI 仍未实现（本期有意不做）" | ✅ **成立** |

## 2. 仍成立的问题（按优先级）

### P1 · 产包工具输出 v1 清单（`tools/OfficialCards`）

这是**唯一会污染下游**的一条：它产出的包要被分发、被 lock、被别人加载，
写成 v1 形态等于"自己造不合规的包"。原生编辑器产的是 v2，工具产的是 v1，
两条产线口径不一致，迟早对不上。

要做：补 `schemaVersion:2` + `metadata[]`（列全部 `.meta.json`），生成 sidecar，
去掉 `dependsOn` / `assets[]`。可以用 `AssetMigrationTool.Plan/Apply` 做一次批量迁移。

### P2 · `samples/demo-card-pack` 仍是 `pack.json`

样例是**别人照抄的模板**，它过期比文档过期更严重。需要改成
`manifest.json` v2 + `cards/*.card.json` + 同目录 `*.meta.json` + `lock.formal.json`，
并同步 README。

### P3 · 旧 DIY 双轨仍在写 `user://`

现状是"原生工坊合规 + 旧 DIY 兼容路径不合规"并存。这不是 bug，是迁移中间态，
但需要**明确边界**：旧路径只服务"兼容包"，不再扩展新能力。
相应地把 `modings` 那两处（`DiyCardCompiler`、`OrcImportService`）与 `diy/<guid>` 标注为遗留。

### P4 · 两份过期文档

`DIY_PACK_DESIGN_20261012.md`（`pack.json` 模型）与
`ASSET_MODEL_MIGRATION_ISSUES_20261013.md` §5.3（"卡包管理 UI 未实现"）
都会误导后来者。建议在文首加"已过时"标记并指向新文档，而不是直接删——历史推理有用。

## 3. 不必做的

- **接 `AssetEditor`** —— 已经接了（`NativeWorkshop`），issue 第 3 条描述的改造已不存在。
- **"引擎已无 `modings`"的适配** —— 引擎两侧都还支持，`wrappers` 是增量能力，
  旧 prefab 不会因此失效。真正要改的是"新产出的 prefab 应该带 `wrappers`"。

## 4. 一条建议给 issue 作者

issue 的判定方式是"静态代码核查（未运行探针）"，这在 beta.3 时得出了正确的警号，
但版本一滚动就容易误判。建议今后这类 issue 附带**可执行的判定命令**，例如：

```powershell
# 是否真的没接编辑器会话
rg -n "BeginSession|EndSession|Promote\(" proto/bridge
# 产出的包是不是 v2
Get-Content proto/data/kards/packs/builtin/manifest.json | Select-String schemaVersion
```

第二条在本仓现状是 `2`——也就是说**磁盘上的包已经是 v2**，
只有工具链产出的包还是 v1。这个区分很关键，否则会把"工具要改"读成"整条链要重做"。
