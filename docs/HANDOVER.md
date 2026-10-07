# 交接文档 — 对战 UI（OrC-KSD.Godot）

交接日期：2026-10-06。当前版本：`0.0.0-alpha.11`。

> **本次交接状态**：拍桌部署动作已按原版逐帧核验重做并交付（见 `BATTLE_SLAM_REWORK.md`）。
> P9（受击接真实伤害 + 攻击弹道）的改动**已从工作区剥离、未提交**，原因见§3。

> ✅ **已全部推送**（2026-10-06 19:56，`601ae1a..cdae090`）。若未来再遇推送失败，
> 先查 `git config --local --list | grep http.ssl` 是否还指向
> `artifacts/combined-ca.pem`（Watt Toolkit CA 过期时需重新生成，方法见
> `.workbuddy/memory/MEMORY.md` §网络），再确认 Watt Toolkit 加速已开。

## 1. 项目是什么

`H:\Working Folder\OrC-KSD.Godot`：Godot 4.7 .NET (C#) 的 KARDS 风格对战 UI。引擎在 `H:\Working Folder\OrC-KSD`（S1145 维护），UI 通过桥接层消费引擎真值——**UI 不算规则、不猜状态、不泄露隐藏信息**。

- 构建副本：`H:\g\kards`（`tools/run.ps1` 自动同步源码过去构建）
- 引擎干净引用副本：`H:\Working Folder\OrC-KSD-github`（worktree，detached；`Kards.Ui.OrcBridge.csproj` 的 `OrcEngineRoot` 指向它）
- ⚠️ 主引擎仓库 `OrC-KSD` 工作区有 S1145 的 330 个未提交改动，**绝对不要动**；以 GitHub 为准时用干净副本

## 2. 已完成

| 提交 | 内容 |
|---|---|
| `cdae090` alpha.11 | **部署拍桌按原版逐帧核验重做**：`SlamStyle(defense)` 成为单一真源（防御力=身材，唯一输入）；删除兵种/家族分支（空军 ×1.25、舰船水花）与旧"跳起→砸下"曲线；新增 `--capture-slam` 录制钩子 + 5 支核验工具。详见 `BATTLE_SLAM_REWORK.md` |
| `0229db6` | **拍桌尘土可见性修复**（P10）：alpha 上限 .5→.72、环心上移 8%、新增 `Age` 属性让步进驱动显式推进淡出（此前帧时钟不推进导致环只有 .5×Diameter 且几乎不可见）。已量化验证（落地区域帧间亮度差） |
| `93b011c` | **瞄准箭头短距离自交修复**（P10）：距离 20–44px 时杆长为负导致 Godot 三角化失败，936 组几何中 144 组报错；改用三点楔形后 936/936 通过。见 `ACCEPTANCE.md` §渲染期缺陷 |
| `601ae1a` alpha.10 | **P8 换牌面板**：`MulliganSelect` 请求停泊等 UI、`ChooseMulligan` 命令通路、红 ✕ 印记面板（`BattleScreen.Stage5.cs`）、"敌方正在选择起手牌"横带；headless 保留自动应答（`InteractiveMulligan=false`） |
| `c787d44` | `run.ps1` 补齐引擎 Roslyn 运行时闭包：Godot 只拷项目程序集，引擎 a24ef2d 起需要 `Microsoft.CodeAnalysis.*`（csx 效果脚本），用 `dotnet publish` 解析闭包后补拷到 `.godot\mono\temp\bin\Debug` |
| 更早 | P0–P7 桥接七步（详见 `docs/ENGINE_BRIDGE.md`）：真实对局可读、命令映射、拖拽手势、拍桌、受击编排（diff 兜底版）、对手 AI 驱动、认输/终局、最近空槽回放 |

文档索引：`ENGINE_BRIDGE.md`（桥接全程+引擎缺口）、`KARDS_SELECTION_RESEARCH.md`（原版交互调研，含 2026-10-06 完整对局录像逐帧核验：换牌面板、瞄准灰化、回合横幅等）、`BATTLE_STAGE5.md`（换牌面板交付）、`VERSIONING.md`。

## 3. P9 已完成（作为 P11-1 的一部分）

**旧结论「testhost 崩溃、疑似引擎栈溢出」已证伪三次**，最终真因是测试自身写错：

- `BeginUnitPrePlayAsync` 会在 `SingleSelect` 槽位上 **park 等玩家选择**
  （`PlayManager` 类注释里写明了会进交互）。
- 探针的 `OrcTargeterBridge` 只记录请求、从不调 `OrcTargeterBridge.AutoRespond` 提交答案
  → 引擎无限等待 → testhost 挂起。**不是引擎 bug。**
- 更早两次误判（"Initialize 之前访问 Players"、"引擎目标解析挂死"）也都是探针自身缺陷。

**P9 未按原计划恢复，而是作废重写**：旧实现基于 `stat` 差值倒推伤害，
而引擎新信号 `unit.damage.dealt{Unit, Card, Amount}` 直接携带攻击者与真实伤害。
重写时还发现更深的问题：**`card.damaged` 信号从未被消费**（翻译器只认 `card.stat.changed`），
导致每次攻击 HQ 都产生 0 个 impact。现已修复，见 P11-1（提交 `ca6e172`）。

**教训**：写测试前先看 `tests/OrcBridgeTests.cs` 里同类测试怎么写，照抄就不会错；
别自己发明调用序列。诊断前读引擎源码（含注释）+ grep 项目内已有辅助方法，再写探针。

## 4. 引擎能力与UI 消费状况（引擎已同步到 `40bb95b`）

- `GameUpdates` 共 45 条信号，UI 已消费：`card.damaged`、`unit.damage.dealt`、
  `slot.gained/lost/changed`、`point.gained/lost/changed`（P11-3，**已按来源处理避免重复播放**）、
  `counter.triggered`、`unit.combat.survived` 尚待消费。
- 效果解析流水线（词表 JSON + 模板 + csx）：纯引擎内部，UI 无 API 面。

### 真正的缺口：支援线与钳击 🔴

引擎有完整钳击体系，但 **UI 完全看不到**：

- `Battlefield.PlayerASupportLine` / `PlayerBSupportLine`：各 4 槽，仅 `[0]` 被 HQ 占位
- `PincerSystem` / `PincerKeywordComponent`：配对关系在 `internal PincerPair`
- **`GameUpdates` 里钳击信号为 0**；`PincerRules` 唯一入口 `TryFormPairAsync` 是 `internal`

结论：**钳击配对关系目前在 internal 边界内，桥接层读不到**。
P12 若要播钳击演出，需要引擎侧暴露查询面或发信号——**开工前先确认，别假设**。

UI 侧现状：`BattleScreen` 只渲染 HQ 占位的前置槽，其余 3 个前置位没有视图，
`MoveUnit(uid, "supportline", slot)` 没有任何发出路径。**整类前置卡牌在 UI 里没法用。**

### 其余待办

- 交互式选择第二段（指向性指令拖拽打出、部署带指向自动弹箭头——原版实例待补证，
  见 `KARDS_SELECTION_RESEARCH.md` §6）
- 多选候选面板（`PendingChoices` 仍走 `AutoRespond` 取第一个合法候选）
- 换牌补抽飞入演出（`MulliganReturnAsync` / `EnemyAsync` 已写好未接）
- 🔴 **`TurnBannerAsync` 是死代码**：有定义零调用点，回合横幅从未播放过。
  接上或删掉，别留着假装有

## 5. 操作手册

```powershell
# 全套（同步源码到 H:\g\kards + 构建 + 可选测试 + Godot 运行）
powershell -File tools\run.ps1 -Test -Verify        # 端到端验证（headless）
powershell -File tools\run.ps1 -Capture             # 截图到 H:\g\kards\artifacts
powershell -File tools\run.ps1 -Editor              # 打开编辑器
# 只跑单测
dotnet test tests\Kards.Ui.Tests.csproj -v q --nologo
# 引擎切换
dotnet build -p:OrcEngineRoot='H:\...你自己的副本\src\Orc.Game'
```

- 提交身份：`git -c user.name='OrC-KSD UI' -c user.email='ui@local' commit ...`（本机无全局身份）
- **网络（2026-10-06 实测）**：本机HTTPS 代理 `127.0.0.1:5373` 对 `github.com:443`
  的 CONNECT 隧道一律 502，**git push 目前不可用**；同一代理下其他域名（腾讯 COS）
  正常。`curl --noproxy '*' https://github.com` 可返回 200，但 git 直连会超时——
  git 与curl 的连接方式不同，不能靠清空 `HTTP_PROXY` 绕过。SSH 22 端口可达但本机无 key。
  代理恢复后 `git push origin main`。
- `tools/run.ps1` 会吞输出（PowerShell 工具层取不到回显）。需要看验证输出时改为手工：
  同步文件到 `H:\g\kards` → `dotnet build` →
  `Godot_v4.7-stable_mono_win64_console.exe --headless --path H:/g/kards -- --verify-ui`
- 跑测试前先清残留：`taskkill //F //IM testhost.exe`（多个后台跑会互锁文件，表现为"跑不动"）
- 验收基线（全绿标志）：`MULLIGAN_VERIFY_OK ...`、`BRIDGE_VERIFY_OK ...`、`UI_VERIFY_OK screens=7 cards=315 texture_cache=64/64`、`已通过 41`
- 同名单卡牌库注意：换出去的牌可能立刻被抽回（uid 合法重现），不变量断言用手牌数/牌库数，不用 uid 缺席
