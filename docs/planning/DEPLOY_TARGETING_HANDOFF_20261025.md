# 部署指向 / 跨段效果 交接（2026-10-25）

> **重新打开（alpha.5 发布后）**：用户确认部署单目标单位仍不能选择目标。下方“已修复/验收通过”为上一轮特定兵团夹具的历史结论，不能视为所有 DIY、原生结构和联机路径均完成。最新只读排查、已确认缺口与接手要求见 [alpha.5 重新排查交接](DEPLOY_TARGETING_ALPHA5_REOPEN_HANDOFF_20261025.md)。用户本轮要求只交接、不改代码、不发版。
>
> **已修复（2026-10-25 23:35，见后续提交）**：真因是**部署指向的候选池里没有这张牌自己**。预打出发生在落位之前，
> `ResolveCandidates` 只查场上单位，空场时「使 1 个友方单位…」连它自己都选不到 → 候选 0 →
> `CancelPrePlay()` → **整次部署被取消**，玩家看到的是"卡丢上去没反应"（不是随机，是根本没发生）。
> 同一批里还有 99 张「有卡面文本、card.json 却没有 effects 组件」的白板卡（第 62 步兵团、喷烟者 42 型都是），
> 指向逻辑写得再对也没有脚本可跑，已一并重生成。
> 现在：空场部署第 62 步兵团 → 候选 1（它自己）→ 选靶 → 落位；先铺一个友方单位时候选 2。

> 接续状态：下述 UI 断点及预打出旁路已修复，图形化调试台已实现。桌面及手机尺寸下的实际输入验证通过；详见 [接续实现与验收](../acceptance/DEBUG_DEPLOYMENT_20261025.md)。本文其余部分保留交接时的现场记录。

> 接手上一个人留下的「部署指向变成随机」和「最后一击后半段被丢弃」。
> 数据层、引擎层已修好并有测试守着；**玩家侧仍然随机的原因在 UI 的落点提交分支**，本轮才定位到，上一轮的修补因此对玩家无效。

---

## 0. 一句话结论

`proto/ui/screens/BattleScreen.cs:628` 那个"指向即打出"的分支只匹配 `CardType is "order" or "counter"`。
单位卡（`部署：使 1 个友方单位获得 +1+1` 这类）**根本进不去**，它走的是下面的 `SelfBand` 分支，提交
`PlayCard(Uid, GapId)` —— 不带 `TargetUid`。可 `BattleScreen.cs:510` 又确实给单位卡画了指向箭头
（`IsTargetedOrder` 对它为 true）。玩家看到箭头、松手却没有带走目标，效果落到引擎自己挑的那个单位上
—— 这就是"指向变随机"。

---

## 1. 用户报的两件事，分别卡在哪一层

| # | 现象 | 数据层 | 引擎层 | UI 层 |
|---|---|---|---|---|
| 1 | 部署指向的效果全部变成随机 | ✅ 已重生成 17 张卡 | ✅ 回指/复用已通 | ❌ **断在 `TryDrop` 分支** |
| 2 | 最后一击后半段被丢弃 | ✅ 已重生成 | ✅ 已通 | ✅ 用例覆盖 |

第 2 条已闭环：`tests/CrossEffectTargetTests.最后一击的回合结束效果消灭的是打出时选中的那个单位`
实测 `attack 1→4`、回合结束后 `line=[]`。

第 1 条只修了"卡脚本说什么"，没修"UI 传什么"。

---

## 2. 已经做完的部分（**别重做，也别回滚**）

### 引擎（`OrC-KSD` 仓库，提交 `025d44e`）

- `src/Orc.Game/Effects/AuthoringTargetContext.cs`
  - 新增 `_recent`：卡 → 目标声明 id → 最近一次真正选中的单位。
  - `SelectAsync` 命中预捕获、`SelectCore` 选靶成功时都写入。
  - 新增 `Recall(source, declaration)`：**只读记录，不发起选靶**（按 `alive` 过滤）。
  - `ValidateTarget` 接受 `reuse` 布尔；带 `reuse` 的声明不进打出前预捕获（否则同一张卡问两遍）。
- `src/Orc.Game/Effects/AuthoringRuntime.cs`
  - `RecallTargets(source, declaration)`，csx 的唯一入口。

### 游戏（提交 `f947239`）

- `DesktopAuthoring/js/orc.js`
  - `head` 分支继承上一段的 `_lastTarget`（`inheritTarget`）；代词命中时懒生成 `{...decl, reuse:true}`。
  - **裸「回合结束时」只在第二段含代词时才当触发头**（`PRONOUN`）。这一点很关键，见 §6。
- `DesktopNativeCodegen.cs`：`reuse` 声明生成 `AuthoringRuntime.RecallTargets(...)`。
- `OrcMatchSession.cs` / `TargetChoices.cs` / `VerifiedCardPool.cs`：日志埋点（提交被拒、选靶请求、
  非交互时机收到选靶、卡池核验明细）。
- `tests/CrossEffectTargetTests.cs`：两个用例，断言到"单位真的死了""属性真的加了"。
- 17 张卡定向重生成（见 §5 的命令）。

---

## 3. 下一刀：UI 落点提交（**这是现在的真问题**）

### 3.1 现场

```
proto/ui/screens/BattleScreen.cs:623   private void TryDrop(Vector2 at, DeploymentPose? releasedPose = null)
proto/ui/screens/BattleScreen.cs:628   if (InteractionCard(_selected) is { Zone: "hand", Definition.CardType: "order" or "counter" } order)
proto/ui/screens/BattleScreen.cs:638       if (IsTargetedOrder(_selected)) { ... Submit(new PlayCard(_selected, TargetUid: target.Uid)); }
proto/ui/screens/BattleScreen.cs:659   else if (SelfBand.HasPoint(at) && ...)   // ← 单位卡走这里
proto/ui/screens/BattleScreen.cs:510   if (card.Mode == Hand && IsTargetedOrder(card.Uid)) UpdateOrderAim(card, _pressAt);  // ← 单位卡也画箭头
proto/ui/screens/BattleScreen.Orders.cs:14  OrderTargetAt() 只认 View.Zone != "hand" 且落在矩形内的候选
```

### 3.2 为什么箭头画得出来却不起作用

- `OrderTargets` 的来源（`OrcMatchSession.cs:255`）是三路合并：
  `_effects.ReadOrderTargets`（只认 `OrderCard`）+ `_desktopTargets.ReadOrderTargets`（**全手牌 + deploy**，
  上一轮改的）+ `KeywordOrderTargeting`。
- 所以 17th 这类单位卡确实出现在 `OrderTargets` 里 → `IsTargetedOrder` = true → 拖拽起手画箭头。
- 但 `TryDrop` 的第一层 `if` 用 `CardType` 挡住单位卡，箭头在松手时被丢弃。

### 3.3 改法（**注意：落点和指向是两个字段，别用一个顶**）

单位卡需要同时给出：

1. **落点** `GapId` —— 它自己站哪（`DeploymentGapAt(at.X)`，支援线/前线）；
2. **目标** `TargetUid` —— 效果作用在谁身上（箭头指向的那个单位，可能不是落点）。

`PlayCard` 记录已经有四个字段：`PlayCard(Uid, SupportIndex, TargetUid, GapId)`，
`OrcMatchSession.SubmitCoreAsync` 里 `_pendingSelection = play.TargetUid is { } s ? [s] : null`，
所以**只要 UI 把 TargetUid 传上来，整条链不用改**。

建议的判定顺序（先指向、再落点）：

```
拖的是有 OrderTargets 的手牌卡（不管 order 还是 unit）：
  指向目标 = OrderTargetAt(uid, at)            // 落在单位上
  若为 null：退化为"箭头最近指向"（若 UI 有记录），仍为 null 则按普通部署/普通指令走
  order/counter：PlayCard(Uid, TargetUid)
  unit：需要落点 → GapId = DeploymentGapAt(at.X)；两者都有 → PlayCard(Uid, GapId, TargetUid)
```

### 3.4 需要一起看的两处

- `BattleScreen.cs:479` 的提示文案："拖出箭头指向单位，松手打出；右键取消" —— 单位卡的语义不同，
  落点还得管，提示要分开写，否则玩家以为只要指单位。
- `OrderTargetAt` 只认 `View.Zone != "hand"`。单位卡箭头指向**场上单位**没问题；
  但如果指向的是"刚要落地的自己"，它当时还在 hand，取不到。

---

## 4. 还没确认的疑点（**先验再改，别猜**）

1. **`CallDeferred` 时序**：`proto/ui/screens/Main.cs:567` 用 `Callable.From(...).CallDeferred()` 投递选靶面板，
   而 `OrcMatchSession.SubmitCoreAsync` 的 `finally`（`:811`）里有 `CancelTargetChoice()`。
   部署是 `await` 引擎完成（选靶会阻塞到有人应答），理论上不会提前取消，但**必须在真实 UI 复现一次**再看。
2. **预捕获到底发生没有**：`AuthoringTargetContext` 注册在 `UnitCard.PrePlayTrigger` 上，
   而部署走的是 `PlayManager.PlayUnitAsync(unit, gap)`。要确认这条路径跑不跑 PrePlay；
   不跑的话，每次都是 csx 现场 `SelectCore`（会弹面板），也就解释了"为什么部署完才问"。
3. **候选口径**：`ResolveCandidates` 用 `EffectSelector("all", side, null)`，`side` 缺省是 `both`，
   含不含 HQ、含不含自己，要按卡面文本对一遍（`使 1 个友方单位` 不该把 HQ 算进去）。

---

## 5. 命令与工具（照抄能跑）

```powershell
# 环境：引擎源码（ServerLauncher/引擎项目都从这里找）
$env:ORC_ENGINE_ROOT='H:\Working Folder\OrC-KSD-github'

# 单卡 / 任意文本的解析探针（打印 graph + csx，落 artifacts/official-card-probe.json）
dotnet run --project tools/OfficialCards -- --probe final_push
dotnet run --project tools/OfficialCards -- --probe --text '使 1 个友方单位获得 +3 攻击力。友方回合结束时，将其消灭。'

# 定向重生成（**必须带 --only**，原因见 §6）
dotnet run --project tools/OfficialCards -- --refresh --only a,b,c --write

# 回归
dotnet test tests/Kards.Ui.Tests.csproj            # 需 ORC_ENGINE_ROOT，当前 624/624
tools\run.ps1 -Verify                              # UI_VERIFY_OK screens=12 cards=521

# 实机：单卡部署（真实鼠标，图形窗口）
$env:DEPLOY_CARD_ID='pack/builtin/17th_infantry_regiment'
& 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe' `
  --path 'H:\g\kards' '--' --verify-deploy-card
```

`--verify-deploy-card` 会打印 `DEPLOY_TARGET_REQUEST selector=... slots=orcTarget1 ...`，
`orcTarget1` 这个 slot 名是新解析产物的指纹（旧产物是 `default`）。

**但它不能证明"玩家能选"**：脚本自己订阅了 `runner.TargetRequested` 并用 `DragTargetChoiceAsync` 作答，
绕过了"UI 没传 TargetUid 时怎么办"这个真问题。**验收必须看 §7。**

---

## 6. 坑（会白扔半天的那种）

1. **卡包是生成物**：`proto/data/` 在 `.gitignore` 第 4 行。这轮 17 张卡的修复**不在 git 里**，
   换机器/重装必须重跑 `tools/build_builtin_packs.py`（= `OfficialCards --refresh --write`）。
2. **全量 `--refresh --write` 会加 229 张新卡**（`previous=102 added=346 total=448`）。修单卡行为永远用 `--only`。
3. **重生成前先看美术**：`shadow_strike` 受管但没有 `proto/art/kards/shadow_strike.png`，
   重生成会把它塞进 `exp-bloodandiron` 卡池，`--verify-ui` 直接报 `Pack artwork missing`；
   删卡文件后又报"卡包声明了影子卡"，必须连 `display-texts.json` / `desktop-effects.json` 的条目一起删。
4. **`dist` 是指向 `G:\orc-dist` 的 Junction**：`tools/prepare.ps1` 的 `robocopy /E` 会跟进去抄几十 GB，
   表现是同步 **exit 1 且一个字都不输出**。已在排除列表加 `dist`（本轮改的）。
5. **沙箱 safe-delete**：`Remove-Item` 走回收站，回收站操作失败会连坐整条同步；必要时 `[IO.File]::Delete()`。
6. **让 `basicParse` "更成功"会换掉兜底路径**：`edge_of_the_empire`（「…。回合结束时，抽 3 张牌。」）
   一旦把裸「回合结束时」认成触发头，第一段会产出 `moveCard`（codegen 未适配）→ 整卡编译失败。
   所以现在只在第二段含代词时才认。
7. **测试基准**：`tests/fixtures/desktop-orc-hq-extensions.json` 是逐项人工审核的漂移清单。
   这轮加了 `final_push` / `honor` / `shadow_strike`，测试断言已从写死的 `13` 改成 `reviewed.Count`。

---

## 7. 什么才算真的修好（验收口径）

只认这三种证据，任何一条不满足就是没修完：

1. **不传 TargetUid 也能选到目标**：真实界面里拖一张部署指向卡 → 出现可点的目标高亮/箭头 →
   点某个友方单位 → 松手 → **效果正好落在那个单位上**（不是候选第一个）。
2. **拖到空地也要能选**：落点是空的（战线槽位），目标在别处 —— 不能因为落点没单位就退化成"随机"。
3. **取消要干净**：右键取消后卡回手牌、不扣费、不产生半截效果。

推荐做法：加一个和 `--verify-deploy-card` 同级的实机脚本，**不给 `TargetUid`**，
只用真实鼠标模拟"拖卡 → 点目标 → 松手"，然后断言目标单位的 `EffectiveAttack`。
现有 `Main.DeployCardVerification.cs` 已经是真实鼠标通道，扩一个"指向"分支即可。

---

## 8. 欠账（这次没动）

- 117 张受管卡里只有 17 张重生成，**其余仍是旧解析器产物**；全量换代要单独一轮（会连带 229 张新卡进池）。
- 「回合结束时，抽 3 张牌」这类**无代词**的回合结束段仍未支持（要先给 `moveCard` 等补执行适配）。
- 卡池 `--verify` 只核验到 24 张（`verified=24 / packs=207`），效果覆盖仍然很薄。
- 引擎侧 `orderTargets` 恒为空（`McpService.Projection.cs`）这件事**没变**，UI 侧仍在自建候选。
