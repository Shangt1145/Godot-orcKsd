# OrC 当前进展核查

日期：2026-11-05。范围：本地 Godot 仓库、实际引用的引擎、近期实现与专项测试；未 fetch、未发布、未执行全量回归或真机界面测试。

## 当前结论

Godot `main @ ba23afc`，相邻引擎 `v4 @ ce498aa`。版本文件仍为 `2.0.0-beta.3`，发行说明日期为 11 月 2 日；11 月 3—5 日源码已补齐复杂 DIY 保存与运行链，不能把新版源码能力当作已发布能力。

## 已核对的进展

1. 引擎新增独立 `Orc.HandlerDsl` 工程，包括读取、类型检查、校验、目录扫描及代码生成。积木前端输出结构化 DSL，后端生成 csx。
2. `HandlerBlockEditorScreen` 已有保存入口。`HandlerDslAuthoring` 提交 DSL、草案，再生成自包含 v4 组件；现有效果可通过新产物和卡牌引用改写替换，保留原效果。
3. R39 后，父资产需公开 handler 空位。客户端从引擎模板库取得 104 项模板，按需生成共享模板文件，并继承模板的挂载、上下文和生效时机。
4. 目录与可读字段已接引擎。扫描节点与内建控制结构属于不同层；内建结构的引脚仍由本地契约实现，扫描失败仍有快照回退，不应称为完全消除手工映射。
5. 多输出绑定已补齐；27 条契约 check 向量形成 29 个测试用例，修复了五处前后端校验分歧。
6. 引擎 R40 将重复挂载改为结构性拒绝，避免“抽 2 变抽 4”。客户端已有回归测试。
7. 最新保存提示明确：先落编辑会话草稿，回工坊点击“应用修改”后才用于新对局。

## 本次实际测试

命令均针对 `tests/Kards.Ui.Tests.csproj`，首次使用 `--no-restore` 构建；后续使用 `--no-build --no-restore`。

| 筛选 | 通过 | 失败 | 跳过 |
|---|---:|---:|---:|
| `FullyQualifiedName~HandlerDsl` | 91 | 0 | 0 |
| `FullyQualifiedName~NetworkWorkshop\|FullyQualifiedName~RemotePresentation` | 14 | 0 | 0 |
| `FullyQualifiedName~CrossEffectTargetTests\|FullyQualifiedName~MatchDebugTests` | 2 | 2 | 0 |

合计本次 109 项：107 通过、2 失败。不是全仓结果。Handler DSL 包含真实开局、打牌并断言手牌净变化的端到端用例，超出序列化／编译检查；仍不等于所有复杂组合或双端对战已验证。

失败仍为跨效果回指的回合结束消灭未发生，以及调试指令的候选断言失败。本次复现，不重新归因。

## 重要边界

- 联机测试通过不能解释为 DIY 联机完整：其中明确验证客人独有包不能自动传输、同 id 不同规则未按内容校验，以及部分自定义词条服务端不执行／内嵌服务端拒绝开局。它们是缺口记录用例。
- 新 Handler DSL 的单机成功，不能推导其所有复杂效果已在远端运行；需要单独的服务端端到端证据。
- 旧 JS 文字解析仍由 `OrcInspector.CompileText → DesktopOrc` 调用，新积木走 Handler DSL；尚不能宣布文字与积木已统一。
- 当前 DSL 排除清单仍有边界，例如结构化光环描述参数不能直接映射为普通节点。应按现有入口和替代路径评估，不能以全部节点 alpha 推断任意效果可做。
- v3 内联效果不能直接被新的替换流程覆盖；已有覆盖设计还记录了多成员编辑限制。
- 保存仍出现模板、目标效果、产物 id 等技术概念；运行链已打通不等于新手体验已验收。
- 模板样例落盘、目录快照缺 `builtInOps` 等引擎契约问题仍有记录；客户端已有部分规避。

## 文档时效

`COMPLEX_DIY_ROADMAP_20261103.md` 顶部旧现状表仍称没有写回、目录硬编码，后文已经补记完成；README 和 beta.3 说明描述发行时状态。应结合源码和后段补记读取，不把前段旧表当当前结论。

## 判断

项目已从“编辑器可以搭图”前进到“后端编译、资产保存、真实对局执行”有专项验证的阶段。后续主要风险是发布落差、旧新创作链并存、跨端规则与包一致性，以及普通作者的操作负担。与 KDE 比较时，可把这一闭环作为实力证据，但不能据此宣称整体领先。

主要证据：`HandlerDslAuthoring.cs`、`HandlerDslCatalog.cs`、`HandlerBlockEditorScreen.cs`、`HandlerDslAuthoringTests.cs`、`HandlerDslVectorTests.cs`、`NetworkWorkshopTests.cs`；路线图、R39/R40 更新记录及 11-02 发行说明。
