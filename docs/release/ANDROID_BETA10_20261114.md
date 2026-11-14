# Android 2.0.0-beta.10 补发（2026-11-14）

APK：OrC-KSD-2.0.0-beta.10-android.apk，762,319,179 字节，arm64-v8a 与 x86_64，versionCode 21。沿用原签名，可直接覆盖安装。

SHA256：346827F9F92A0F31E2F783B98D4694A445C64A52AE450CC66245DD03206DCB7F。

## 更新

下载 APK 后点击安装；系统询问时允许当前文件管理器或浏览器“安装未知应用”。直接覆盖原版，不要卸载或清空数据。也可在游戏“离线更新”入口选择新版 APK，由 Android 系统安装器确认。Android 使用完整 APK，不使用 Windows .orcupdate，也没有桌面式退出自动打补丁。

## 修复

Android 内嵌服务端在创建对局时，默认 Roslyn 脚本求值器读取 /System.Private.CoreLib.dll 失败。现通过 ServerOptions → McpServiceOptions → MatchOptions 传入已有 NativeScriptHost 内存程序集适配；独立桌面服务端缺省路径不变。

配套引擎补丁：docs/engine/v4-mobile-server-script-evaluator-20261114.patch，基于引擎 c950b2ab6971c4381bc9de97f3d5bbac51bb505c。此补丁随 UI 源码保存，构建须应用；未向引擎远端推送。

## 验证与边界

- x86_64 Android 模拟器（宿主 GPU）：beta.3 保留数据覆盖 beta.10，主页正常，测试卡组、DIY 卡及校验文件保留；未使用 pm clear 或卸载。
- 新旧 APK 签名证书一致，versionCode 从旧版递增至 21。
- Android 探针：文档 URI 读写与图片、目录、编译、训练部署抽牌、房间创建、网络效果执行、回放全部通过（ok=true）。
- 测试存档保留验证与功能探针分开执行：探针不支持目录中已有额外 DIY 卡，临时归档自建测试卡，完成后已恢复。不是要求玩家清数据。
- 原生脚本隔离与联机测试 10 项通过；工坊及教程入口在模拟器截图确认。
- 历史的覆盖安装后立即退出，在本次 beta.3→beta.10 路径未复现，不能据此宣称查明历史退出根因。
- arm64 已构建并签名，尚未在实体手机验证。模拟器软件渲染有 uniform 限制，改用宿主 GPU 验证。

Windows beta.10 产物和构建标识不变；Android 使用后续移动端适配提交，单独打 android/v2.0.0-beta.10 标签追溯。
