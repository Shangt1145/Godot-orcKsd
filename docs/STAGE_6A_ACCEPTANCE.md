# 6A 客户端可靠性：实现与验收

日期：2026-10-07。当前引擎可支持的客户端修复已落地；严格的段完成历史快照仍受上游接口限制，未标记为通过。6B–6D 尚未实施。本轮不发布、不改版本。

## 本轮实现

- 无效目标或槽位取消当前请求并显示拒绝原因，不再自动改选 HQ、首个目标或其他槽位。失败不扣费、不移动卡牌，后续合法操作可以继续。
- 前线分开处理引擎候选槽位与画面插入位置，接受后才应用排列；支援线按槽位及 HQ 位置绘制。修复中心坐标浮点误差和前线高亮标记垂直位置。
- 一个 host 消费事件段。每次桥接动作及对手动作完成后冻结状态，稍后分发；没有事件段的换牌、阶段及资源变化也会刷新。
- PresentationPlayer 串行播放冻结载荷。伤害进入 UiBoardImpactsPresentation，与同段抽牌和其他步骤共存；CombatReady 不再覆盖完整表现。
- 每局使用唯一 MatchId。退出或重开取消引擎调用、换牌等待及表现队列，解绑订阅；旧 runner 回调不能写入新局。
- 引擎调用自身互斥，尚未完成的引擎请求返回 Busy；动画不持有命令锁。真实对局可连续提交操作，结算不等待上一段动画。结束回合检查行动方，真人热座不运行自动对手。
- 输入读取最新适配器投影；默认演示对战和真实引擎对战均可连续操作，画面按冻结历史快照 FIFO 播放，普通点击、拖动、取消手势及拒绝操作不会打断动画。槽位占用和行动候选取最新投影，避免后续操作重复使用动画画面里的旧空位。
- 修复部署重影：专用部署序列负责部署卡牌时，通用布局只移动其他卡牌，不再同时生成该单位的移动副本，也不会提前显示落地卡牌。
- 画面重建避开仍按住的拖拽手势，布局移动计入当前动画完成边界。多个前线落点分别保存卡牌左右关系，拒绝只清理对应手势；重新开局和视角切换重建基线。
- 卡池编译、组牌和带引擎回调类型的初始化签名收进桥接。单测检查源码及 contracts/core 引用，Godot 检查实际 UI 程序集引用。
- 手牌可尝试集合显式带 `HandPlayabilityKnown=false`，UI 不显示确认可打出的光圈。普通真实对局正常发牌，固定起手只用于指定审计。
- 图形运行发现兵种名大小写导致音效路径告警，已统一到现有小写文件名。

## 实测结果

| 验证 | 结果 | 证据和范围 |
|---|---|---|
| contracts/core/bridge/Godot/tests 顺序构建 | 通过 | 0 警告、0 错误；使用 BuildProjectReferences=false，未重建外部引擎源码 |
| .NET 回归 | 112/112，无跳过 | 原有 99 项 + 13 项可靠性断言；非法选择、所有权、并发退出、快照、混合步骤、观察者、能力限制、FIFO、嵌套冻结、架构边界 |
| Godot 无界面验证 | 通过 | artifacts/stage6a-headless.log：资源、交互、战斗、表现、反馈、换牌、真实引擎及程序集边界 |
| Godot 图形窗口完整验证 | 通过 | artifacts/stage6a-graphical-verify.log：UI_VERIFY_OK，无运行时错误或音效路径大小写告警 |
| 连续真实操作与 FIFO 动画 | 通过 | artifacts/nonblocking-graphical-verify.log：两次鼠标拖动在第一段动画完成前结算，槽位不同；无效命令保留队列；结束回合读取最新状态；两段部署依次播放并收敛到引擎投影 |
| 拖拽跨越动画完成时刻 | 通过 | 同一图形日志：第二次验证按住卡牌 1.5 秒跨过上一段落地效果，未丢失手势；四个单位最终投影和 FIFO 顺序通过 |
| 部署单一画面与默认入口连续输入 | 通过 | artifacts/deployment-focused.log：1280×720 图形窗口，双方 × 三档速度 × 减弱动效共 12 组逐帧检查；默认入口两次拖牌与动画期间结束回合通过；四张部署中/落地截图已查看 |
| 完整 Godot 回归及实际运行副本 | 通过 | artifacts/deployment-full-verify.log：UI_VERIFY_OK；artifacts/deployment-runtime-verify.log：H:/g/kards，1600×900 图形窗口，12 组部署检查及默认/真实输入验证通过 |
| 1280×720 图形落点 | 通过 | artifacts/stage6a-graphical-1280.log 与对应截图目录 |
| 1600×900 图形落点 | 通过 | artifacts/stage6a-graphical-1600.log 与对应截图目录 |
| 真实牌组鼠标审计 | 部分覆盖，不能算完整通过 | artifacts/real-audit/audit.txt：7/10 项；左右与行尾部署、攻击已测；没有合法移动候选，三个移动手势未覆盖 |

每个尺寸覆盖 20 组落点：已有 0、1、2、3、4 个单位 × 左侧、中心、行尾、首张卡右侧。另断言满前线不提交移动、引擎拒绝后不遗留画面重排。判据来自手势前各卡坐标和期望 UID 顺序，没有用布局内部排序表自证正确。

拖拽期间检查标记数量及垂直位置，落地后检查实际绘制顺序。两种尺寸各保存 40 张按住/落地截图，并人工检查代表画面。投影夹具验证输入和画面，桥接断言独立验证真实引擎接受、拒绝、费用和状态；夹具截图不是原版 KARDS 或全卡池兼容证据。

旧抽牌测试的判据也已纠正：敌方匿名手牌位置可在不同回合复用，不能用全局 enemy-slot-N 判断重复。现在按事件段 Sequence 和匿名槽位识别；我方仍按 UID 检查。实际双信号去重未被移除。

## 复现

在项目根目录运行，先确保当前引擎基线的程序集已构建：

```powershell
$projects = @(
  'proto/contracts/Kards.Ui.Contracts.csproj',
  'proto/core/Kards.Ui.Core.csproj',
  'proto/bridge/Kards.Ui.OrcBridge/Kards.Ui.OrcBridge.csproj',
  'Kards.Ui.csproj', 'tests/Kards.Ui.Tests.csproj'
)
foreach ($project in $projects) {
  dotnet build $project --no-restore -p:BuildProjectReferences=false -m:1 --nologo
  if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
dotnet test tests/Kards.Ui.Tests.csproj --no-build --no-restore --nologo

$godot = 'H:/g/tools/godot47/Godot_v4.7-stable_mono_win64/Godot_v4.7-stable_mono_win64_console.exe'
& $godot --headless --path . -- --verify-ui
& $godot --path . --resolution 1280x720 -- --capture-placement
& $godot --path . --resolution 1600x900 -- --capture-placement
```

截图在 artifacts/stage6a-placement-1280x720 与 artifacts/stage6a-placement-1600x900。产物由 .gitignore 排除，可重生成。

## 已知限制与下一步

1. 没有段完成回调时，外部调用积压多段后才 Pump 无法恢复历史终态，host 将 HistoricalStatesVerified 置为 false。本轮验证了限制的检测，没有伪造能力。
2. 随机真实牌组审计的移动覆盖不足。固定矩阵与桥接移动测试通过，整体随机对局仍要完善覆盖。
3. 疲劳、秘密反制、连续效果选择及全量 effects 接入按能力表推进，不在 UI 补规则。
4. 下一步为 6B：审核代表卡完整语义，将本地声明转换为 OrC-KSD 认可的效果装配形式，先处理数据完整性和目标/条件转换，再扩大可用卡池。

引擎版本、已有修改及更新流程见 [ENGINE_INTEGRATION_BASELINE.md](ENGINE_INTEGRATION_BASELINE.md)。

## 实际启动副本

`tools/run.ps1` 和 `tools/open-editor.bat` 默认使用 `H:/g/kards`。本次发现它仍携带旧客户端程序集；已校验 `.kards-ui-workspace` 的归属并同步源码及客户端程序集，UI DLL 哈希与工作区构建一致。未重建或修改引擎。已经启动的 Godot 进程需要重新启动，才能加载更新后的程序集。
