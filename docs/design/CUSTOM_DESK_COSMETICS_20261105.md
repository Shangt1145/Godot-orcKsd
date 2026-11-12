# 自定义桌布与桌饰

入口：我的卡组 → 自定义外观。

## 使用方式

- 桌布：进入“桌布”，导入 PNG/JPG/WebP，预览后“装备并保存”。桌布跟随当前卡组，只改变本机看到的桌面。
- 图片桌饰：进入“桌饰”导入图片，图片在共享 3D 桌面上显示，支持大小、朝向和点击演出。
- 模型桌饰：进入“桌饰”→“导入 3D 桌饰（GLB）”。需要 GLB 2.0、内嵌纹理，最大 20 MiB。导入时复制到外观库并生成预览图，后续不依赖原始文件。
- 点击演出：自动模式优先播放所选模型动画；没有模型动画时轻晃。另可选择关闭、轻晃、转一圈、抬起落下。可直接点击预览模型，或使用“播放预览”。
- 大小范围 0.25–3，朝向 -180°–180°。桌饰与点击配置仍按主国家共享；桌布跟随当前卡组。

## 战场行为

桌布使用独立的桌面 PlaneMesh，避免 BoxMesh 的六面 UV 图集裁掉自定义图片。桌饰模型和图片均进入共享 World3D；切换视角时重新校准锚点，点击命中使用模型边界投影。卡牌命中优先，桌饰不能挡住正常卡牌操作。减弱动效时战场不播放点击演出。

修复隐藏战场首次显示时，初始化排队的旧桌饰覆盖刚装备物品的问题。新的装备选择会清除该侧旧请求；切换尺寸、朝向和动作也会刷新实例；卸下时立即移除旧实例。

现有 51 份原版 GLB 载荷均没有内嵌动画。因此，轻晃、旋转、抬起是可配置的程序演出，不是已经还原原版专属动作。原版模型未来补齐动画载荷后，可沿用当前动画选择和播放入口。

## 保存与联机边界

`CosmeticProfile.Tablecloth` 保存桌布图片哈希；`Prop` 保存模型哈希、点击模式、动画名、大小和朝向。自定义模型位于外观库 `models/<SHA256>.glb`；图片及模型的编号均校验路径，GLB 不接受外部文件引用。

当前服务端 `cosmetics.set` 仅接受旧的图片资料结构，`cosmetics.asset` 仅接受 PNG。客户端发布时移除新增本地字段，继续同步桌饰预览图，避免破坏旧房间协议。自定义 GLB 及其动作配置目前仅本机完整呈现，对手显示预览图；桌布为本机观感。完整模型同步需要后端增加模型载荷、配置的校验与传输能力，本轮未改动引擎仓库。

## 验证

- `dotnet build Kards.Ui.csproj --no-restore --nologo -v:q`
- `dotnet test tests/Kards.Ui.Tests.csproj --filter FullyQualifiedName~CosmeticLibraryTests`：10 项，包括桌布作用域、模型配置保存重载与移除。
- `dotnet test tests/Kards.Ui.Tests.csproj --filter FullyQualifiedName~DeskCosmeticNetworkTests`：真实双端外观通道，验证新增本地字段不破坏原有协议、预览图可接收。
- `--verify-desk-cosmetics` / `tools/run.ps1 -VerifyDeskCosmetics`：带动画 GLB 导入、缩略图、五分类编辑器鼠标选择、大小／朝向／动画保存、重载、两种镜头点击播放、原版桌饰替换与卸下。Forward+ 与 Compatibility 实机渲染。
- 日志与截图：`artifacts/desk-cosmetics/`。

完整 `--verify-appearance` 的编辑器部分通过，进入房间时被当前服务端缺失的 `pool.transfer.policy` 管理操作阻挡。该问题位于既有房间初始化流程；本轮另用直接连接服务端的外观通道测试验证协议兼容。

**后续（2026-11-12）**：该阻挡已修。`RoomScreen.OpenAsync` 不再把 `pool.transfer.policy` 当硬性前置（v4 服务端没有这个管理操作，调用只会拿到「未知管理操作」并把整个进房流程中断）；同一原因的「接收好友卡包」开关一并移除，改为静态说明「联机不传输卡包：请双方各自安装同一批卡包后再开局」，`_packStatus` 只报本机缺哪个包。`tools/run.ps1 -VerifyRoom` 现可完整跑通（`ROOM_VERIFY_OK server claim ai start local-art ui-mulligan battle play ai-turn-return`）。
