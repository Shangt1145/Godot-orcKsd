# Android 与 PC 功能对齐（2026-10-25）

本次对齐的是当前开发源码。旧版 APK 需重新安装新版；PC 与手机联机时应使用同一套客户端和服务端源码。

## 已补齐的路径

- 工坊动作目录、自然语言解析、图形效果编辑：Android 使用纯托管 Jint 执行原来的同一套解析源码，PC 保留 V8。取消加载失败后返回空动作目录的降级。
- 自定义效果编译：从 APK 当前架构物化程序集、规则模板、词条效果与插件资产，将标准运行目录指向应用私有缓存。原生 C# 脚本直接使用这些编译引用生成并执行效果。
- 手机创建房间：内置同一套服务端，通过 TCP 连接玩家。保留玩家批准、卡包接收开关、真实内容指纹校验、扩展依赖、自定义词条和卡图同步。词条快照限于本房间卡包，避免此前浏览的扩展包干扰新房间。
- 房间运行通知、保持运行与局域网组播锁；离开房间时释放服务。
- Android 系统文档 URI 的卡包导入导出、卡图及外观图片读取、回放导入。文档导入上限 256 MiB，图片上限 32 MiB。
- Android 返回键先取消当前选择或对话框，再执行页面返回及原有未保存编辑保护；主页退出需确认。
- 离线更新选择 APK 后交给 Android 安装器，保留系统的安装确认和原有应用数据。
- 同一个调试安装包包含 arm64-v8a（手机）和 x86_64（模拟器）。

## 构建与自检

运行 `tools/export-android.ps1`。它会先通过 `tools/prepare-android-platform.ps1` 将受版本管理的 Java 服务和权限配置装配进 Godot Android 模板，再导出 APK，校验产物时间和 APK 结构并输出 SHA-256。

Android 运行自检只在调试构建中读取 `user://verify-android-parity.flag`，通过后生成 `user://android-parity-report.json`。它检查动作目录、自定义效果编译、训练、真实内置房间与两个 TCP 玩家、卡包同步、出牌效果和回放保存。另用应用文件提供器的真实 `content://` URI 检查文档分块读写和 PNG 导入。

存在 `user://android-parity-background.flag` 时，自检在房间开局后停留 30 秒，写出 `user://android-parity-host-port.txt`，可在此时切到后台并从电脑验证连接；随后刷新对局并记录后台检查结果。自检使用独立临时卡包目录，结束后恢复玩家的工坊配置。

## 配套引擎源码

[引擎补丁](../engine-patches/android-parity-20261025.patch) 基于 `2806e0458af3c235fad4ebcfe12a674dc92507b3`，包含当前卡包传输与 Android 内置服务所需的配套改动；不要与旧网络补丁重复应用。当前开发工作副本已直接修改，补丁是供另一份引擎检出使用的副本。

## 验证记录

- 客户端完整回归：614/614。
- 联机工坊专项（含进程内/独立服务端、自定义程序/声明式词条和卡图）：10/10。
- 官方语料 1553 张卡与动作目录：Jint 与 PC V8 输出逐项相同。
- 引擎脚本测试：17/17；服务端测试：127/127；词条相关测试：222/222。
- 最新编译路径调整后再次完整回归：614/614；工坊、保存机制与联机专项：51/51。
- Android 15 / x86_64 实际安装运行：目录、文档 URI 分块读写、PNG 导入、效果编译、训练、自建房与两个 TCP 玩家、卡包同步、出牌效果、后台保持、回放均通过，`android-parity-device-report.json` 的 `ok=true`。
- 电脑经 ADB 转发连接 Android 房间：前台及切到后台后均完成协议握手和 `room.info` 请求；后台服务检查为 `isForeground=true`，离开验证房间后服务已释放。此项是模拟器连接验收，不是公网穿透验收。
- 调试 APK：`dist/android-parity/OrC-KSD-android-debug.apk`，760,992,024 字节，包含 arm64-v8a 和 x86_64。SHA-256：`43300C025F5FB69D0F32143EE294071839D364DC3C680D92206BACBCE12DF45E`。
- 产物构建日志为 `artifacts/android-parity-gradle-final.log`；运行结果、电脑连接响应、后台服务状态与截图均在 `artifacts/android-parity-*`。最后一次 Godot 导出因此前中断任务残留而在清理步骤失败；确认残留任务退出后，用其最新发布资源顺序执行 Gradle `assembleMonoDebug` 成功，生成上述 APK。

返回键、APK 安装入口已接线；本次没有实际覆盖安装另一个版本，也未逐项人工操作所有编辑器页面。arm64 安装包已构建，实体手机尚未测试。

模拟器运行 Android 15 / x86_64，使用主机 GPU。SwiftShader 的 GLES 实现曾触发 Godot 4.7 内置着色器 uniform 上限并显示空白，改用模拟器主机 GPU 后界面正常。尚不能据此确认所有实体手机 GPU、厂商后台策略及公网长局均已验收。

## 接手收尾（同日）

接手时工作区编译不过、测试 4 红，已全部处理：

- **编译阻断**：`NativeEffectInspector.RefreshSourceCheck` 把 `JsonNode` 当序列用了（`effects.Select(...)`）。
  该方法第 115 行已判过 `effects is not JsonArray`，补 `.AsArray()` 即可。这是唯一挡编译的错误。
- **语料基准漂移 19 条**：解析器新增「升为老兵时」「坦克或步兵／空军或步兵」「友方单位和总部」三类语法后，
  `12th_guards_mechanised_vet`、`usace`、`penicillin`、`sneak_maneuver`、`m16_halftrack`、`torrential_rain` 等 19 张卡的编译输出随之变化。
  逐条核对原文：**19/19 全部由这三类新语法触发**，属能力补齐而非回归，基准哈希已按实际结果更新（旧的已备份）。
- **缺口守卫样本失效**：「返回手牌」原本是「引擎面外」的样本，现在由 csx 调 `AuthoringRuntime.ReturnAsync` 补齐；
  以它为样本的 3 个测试（引擎缺口不冒充编译通过／文本模式诚实拒绝／批量导入待核对分栏）因此失效。
  换成仍然无无损适配的两个动作：**返回牌库（`moveCard`）**、**复制手牌（`copyHandCard`）**，守卫继续有效。
- 复核：编译 0 错误，**622/622 测试通过**（含 19 条语料基准逐项比对）。
