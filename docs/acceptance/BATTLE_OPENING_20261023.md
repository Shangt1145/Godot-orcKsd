# 开局演出补齐（2026-10-23）

## 参照与实现

参照 `docs/research/KARDS_SELECTION_RESEARCH.md` 的 V1 00:05–00:19 记录，并重新查看 `artifacts/research/kards-selection/mulligan_01.jpg`：双方身份与斜置总部、居中“对战”，随后进入横排起手牌及换牌。这次没有重新测量原版逐帧时长或音频，当前时长是项目调校值。

- 在真实换牌请求前增加双方公开总部、昵称的短入场；随后起手牌从己方牌堆逐张错开发出、翻面并落到现有换牌行。
- 入场期间隐藏换牌说明、确认按钮和底部重复的己方手牌；完成后开放点选与确认，没有改战场布局。
- 支持“跳过入场”；程序提前确认也先归位再提交原有 keep UID 列表。演出不读牌库，不执行额外抽牌或决定先后手。
- 同一 match ID 重开面板不重播；只读观战不打开可交互换牌面板。中途观战／回放不会因为普通投影自动启动这段演出。
- 切换对局、退出、减弱动效均清理入场 Tween 与临时卡片；开启减弱动效直接进入原有换牌行。速度设置按现有 AnimClock 缩放。
- 修正原有等待横幅挂在已隐藏换牌面板下的问题；现在它属于独立显示层，离开换牌阶段即关闭。

当前契约的 `ActivePlayerSide` 在 mulligan 阶段固定返回 self（引擎 `UiProjectionProvider.ActiveSideOf`），不能用它判断先手。本次没有伪造 VS 阶段的“先手”标记，正式回合方向仍由 play 阶段真实投影与既有回合演出决定。没有新增玩家头像资源。

## 验证

- `dotnet build H:/g/kards/Kards.Ui.csproj --no-restore -v quiet`：0 错误，2 条原有房间验证 nullable 警告。
- `./tools/run.ps1 -VerifyOpening` / `--verify-opening`：原生窗口验证入场、发牌、翻面、最终归位、跳过、同局重开、切换对局清理、减弱动效、观战门禁；真实引擎验证先手提前确认与后手自然播放后确认，均能进入 play，保留卡与牌数一致。
- 日志：`artifacts/opening-native.log`。实际引擎截图：`H:/g/kards/artifacts/opening-20261023/real-versus.png`、`real-ready.png`；其余图片是视觉夹具，不代表真实开局场面。
- `--verify-input-scheduling` 复验通过：真实鼠标连续出牌、抽牌中出牌、持牌跨重绘、敌方演出期间输入门禁；日志 `artifacts/opening-input.log`。

本次仅修改前端；未推送、未导出发行版，没有据局部开局验证宣称引擎历史战斗阻塞已修复。

## 换牌确认后的连续演出补充

用户指出换牌本身也应有动画。检查发现旧的 `MulliganReturnAsync` / `MulliganEnemyAsync` 没有产品调用，确认按钮会直接清空大卡；引擎换牌是静默替换，不会自动产生普通抽牌事件。此前“沿用已有换回／补入”的描述不准确，现已补接实际流程。

- 被选中的大卡携带红叉印记倾斜向右上方飞离；未选中的牌保持原位，待退牌后再收回底部手牌。
- 收牌完成后只提交一次原有 keep UID 列表，连续确认不会重复提交；退出／换局会取消尚未提交的旧回答。
- 读取引擎返回的实际手牌，以保留 UID 集合及换牌数量识别替换牌，逐张从己方牌堆位置飞入扇形手牌；允许引擎换回相同 UID，不根据卡名猜牌，不额外抽牌。
- 临时卡片与真实手牌连续交接，首回合横幅等待替换牌归位；到达后真实手牌保持显示，避免横幅期间卡牌消失。
- 同时支持全保留、换一张和全换；拒绝后重开换牌会清理旧等待状态。对手换牌次数没有可靠事件时不编造对手换牌演出。
- `--verify-opening` 增加视觉夹具验证：0／1／5 张、红叉保留、保留牌静止、连续确认、实际返回牌面、到达后可见、首回合顺序及退出清理。真实引擎分别验证先手／后手换一张，均到达 `mulligan-replacements-settled`。
- 日志 `artifacts/mulligan-opening.log`；视觉夹具截图 `H:/g/kards/artifacts/opening-20261023/mulligan-rejected-leaving.png`、`mulligan-arrival-1.png`、`mulligan-arrival-5.png`。
- 减弱动效换牌验证通过；`--verify-room --replace-opening` 通过联机真实换一张、服务端返回替换结果、补牌后释放输入及 AI 交回回合，日志 `artifacts/mulligan-room-replacement.log`。普通全保留联机探针也通过（`mulligan-room.log`）。
- 本次修改后 `--verify-input-scheduling` 再次通过，确认新增换牌锁没有影响普通抽牌、连续出牌、持牌跨重绘及敌方演出输入门禁；日志 `artifacts/mulligan-input.log`。

## 连续演出与画面打磨

用户再次指出此前仍停留在功能接通，本轮将开局／换牌作为连续演出来打磨：

- 入场期间隐藏棋盘上的同一总部实例，避免双方总部重复叠放；公开名称采用印刷姓名签，补足标题、分隔、纸色与军绿的层次，不新增虚构头像或军衔。
- 遮罩改为带固定细颗粒的暗角，保持中央木桌材质。换牌说明加独立深色底带，避免总部文字与血量穿到说明后方；确认和跳过使用统一样式，已选数量即时反馈。
- 发牌与补牌使用同一套中心锚点、弧线路径、错峰、翻面、动态阴影及着桌收尾；保留牌收回时修正缩放前后的中心对应，避免交接瞬移。退牌使用抬起后加速离场，红叉随纸片移动。
- 换牌卡轻抬悬停、盖章短促压印；确认后冻结标记，不能在退场中修改。盖章动效只由换牌面板主动开启，没有改变其他抉择面板的默认标记行为；减弱动效不执行盖章缩放。
- 现有纸张／UI 音效跟随每张牌启程、落稳触发，降低增益；沿用已有素材，没有新增录音。本次录屏不包含音轨，不能将录屏当作听觉验收。
- 正常开局时的卡池数量／验证牌组提示改到日志，不覆盖对局画面；操作拒绝等提示保留。

实际引擎的连续无声录屏：`artifacts/opening-polish-20261023/opening-polish.mp4`（约 6.9 秒）；代表帧 `versus.jpg`、`mulligan.jpg`。素材来自 `--capture-opening-polish` 读取 Godot viewport、记录每帧实际时间后编码，没有另行制作演出模型。原始帧与时序在 `H:/g/kards/artifacts/opening-polish-20261023/`。

构建通过（0 错误、2 条既有 nullable 警告）；`--verify-opening` 验证开局／换牌与真实先后手流程，日志 `artifacts/opening-polish-verify.log`；联机换一张及 AI 交回回合通过，日志 `artifacts/opening-polish-room.log`。未推送，未修改引擎。
