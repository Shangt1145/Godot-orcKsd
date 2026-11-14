# Android 自动更新方案讨论（2026-11-14）

当前决定：用户要求安卓只添加更新提示，不添加自动下载、安装等功能。已实现启动后检查，发现有对应 APK 的新版时在页脚显示“有新版 · 查看发布页”；点击打开浏览器，玩家自行下载与覆盖安装。网络失败不阻塞启动，提示不弹窗打断对局或编辑。beta.11 已发布的二进制不变，该改动等待后续版本打包。

以下自动下载与安装内容仅为此前讨论记录，不属于当前实施范围。

## 当前基础

- `proto/ui/screens/Main.AutoUpdate.cs` 启动检查明确跳过 Android，现有版本不会因发布清单变化而自行下载 APK。
- `Main.OfflineUpdate.cs` 已有系统文档选择器手动选 APK 的入口。
- `platform/android/RoomHostService.java` 的 `installApk` 接收 content URI，ACTION_VIEW 调起系统安装器；已有 FileProvider 辅助方法。构建副本在 android/build，必须修改 platform/android 原件。
- `tools/prepare-android-platform.ps1` 已加入 REQUEST_INSTALL_PACKAGES；现有实现尚无“允许此来源”检查、从设置返回继续安装、安装结果追踪。
- `UpdateDownloader` 已具备流式下载、进度、.part 保留、Range 续传、长度和 SHA256 校验，可复用。但移动更新必须强制提供 SHA256，不能沿用缺失校验时继续的桌面兼容路径。
- beta.11 的 applicationId 为 com.orcksd.game，versionCode=22；APK 762,326,550 字节，包含 arm64-v8a 与 x86_64。当前公开包沿用 debug 签名以保留既有覆盖安装兼容性，正式签名迁移须单独设计，不能直接换证书覆盖旧版。

## 第一阶段：自动检查和下载，点击安装

建议优先落地这个方案，保持系统安装确认，兼容范围清晰。

1. 主菜单出现后异步获取静态 Android 清单，检查失败不阻塞游戏。按 Android 的 versionCode 比较，不依赖 beta 字符串排序或桌面 buildId。
2. 主菜单提示版本、更新摘要、下载大小；提供下载、稍后、仅 Wi-Fi 自动下载选项。约 727 MiB 的整包首次不默认使用移动流量。
3. 玩家启用后自动下载到应用私有目录，显示进度，支持取消和断点续传。第一版只承诺前台下载；切到后台或进程终止可暂停，回到游戏继续。若要可靠后台下载，独立接 DownloadManager 或符合当前 Android 要求的下载任务，不复用联机房间前台服务。
4. 校验长度、SHA256、APK 包名、versionCode、签名与本机安装版本一致；任一不符不交给安装器。SHA256 用于完整性校验，真实性仍依赖 HTTPS 发布源和 APK 签名；未来镜像清单应加入签名验证。
5. 下载完成保留“安装更新”入口。对局中不弹安装、不替换；玩家回到菜单主动安装，保存编辑结果并明确提示需要离开房间。
6. Android 8+ 检查 canRequestPackageInstalls；未允许时进入本应用的“允许安装未知应用”设置。将待安装 APK 与校验信息持久化，返回应用后继续，不强制重新下载。
7. 将已验证本地路径经 FileProvider 转为 content URI，临时授予读取权限，启动系统安装器。安装取消仍保留缓存，系统存储不足可清理空间后重试。
8. 下次启动读取实际安装 versionCode，确认成功后清理过期 APK。覆盖安装不卸载、不清空 user://，验证卡组、DIY、设置与导入资源保留。

从 beta.10 / beta.11 进入支持自动更新的新版本仍需要手动覆盖安装一次；旧 APK 没有这段检查逻辑，发布端无法补上。

## 清单与发布接入

可在当前 channel/latest.json 增加可选 android 字段，保持桌面现有 tag/assets 结构兼容，也可以单独发布 android-latest.json。

```json
{
  "android": {
    "versionName": "2.0.0-beta.11",
    "versionCode": 22,
    "applicationId": "com.orcksd.game",
    "url": "https://github.com/Shangt1145/Godot-orcKsd/releases/download/v2.0.0-beta.11/OrC-KSD-2.0.0-beta.11-android.apk",
    "size": 762326550,
    "sha256": "2A396EB48C1262AEE220A0F742335634A65F4D46FFD48231008FA0C3235A76C2",
    "abis": ["arm64-v8a", "x86_64"]
  }
}
```

以上为字段提案。若构建生成清单，还应输出 minSdk、签名证书摘要、更新说明；从 APK 提取并校验，而非手工维护。先上传资产、核对摘要、公开 Release，最后切换频道。镜像下载与 GitHub 使用相同摘要，失败要有可重试提示和浏览器下载退路。

## 第二阶段：安装也自动化

Android 12/API 31 起，PackageInstaller 的 USER_ACTION_NOT_REQUIRED 对满足条件的更新有支持，其中包含应用更新自身。需要 REQUEST_INSTALL_PACKAGES 已授予、声明 UPDATE_PACKAGES_WITHOUT_USER_ACTION、满足相应系统版本的 targetSdk 门槛等条件。不能笼统说普通应用永远无法免确认自更新，也不能承诺所有系统都可静默安装。

如果确实需要此能力，第二阶段引入 PackageInstaller session 和安装状态接收器，在用户明确启用自动安装且游戏处于安全时机时提交。必须处理 STATUS_PENDING_USER_ACTION，退回系统确认；处理进程被系统结束、安装失败、重启后确认版本。首版不为减少一次点击扩大兼容和恢复复杂度。

## 大小优化和长期资源更新

- 第一阶段下载完整 APK，桌面 .orcupdate 不能直接拿来覆盖 Android 安装文件。
- 可先提供 arm64-only 真机 APK，x86_64 留作模拟器；需实测节省大小，更新清单按 ABI 选择，双 ABI 包保留兜底。
- 以后评估 APK 二进制差分：必须以精确原始 APK 为基线，重建后哈希一致并交系统安装，失败退回完整包，另做空间和性能验证。
- 更有收益的长期方向是稳定程序壳与卡图/音频/声明式卡包分开版本化，按内容哈希增量更新；下载到暂存区验证，原子切换，保留上一版并检查引擎/协议兼容。现有内嵌资源和运行时查找路径需要改造，不能仅将 DLL 或脚本改成远程下载就视为完成。
- 若将来上 Google Play，单独采用 Play In-App Updates / Play Asset Delivery，不能把 GitHub 自更新权限原样带入 Play 渠道。

## 验收重点

无网络不拦启动；较旧/相同 versionCode 不提示；Wi-Fi/移动数据策略；中断续传；返回 200 时重下而不是错误拼接；错哈希、错包名、错签名阻止安装；授权拒绝/设置返回/安装取消；磁盘不足；对局与未保存编辑保护；实际系统安装覆盖旧包保留数据；arm64 实体机与模拟器验证。

## 官方依据

- [PackageManager.canRequestPackageInstalls](https://developer.android.com/reference/android/content/pm/PackageManager#canRequestPackageInstalls())
- [PackageInstaller.SessionParams.setRequireUserAction](https://developer.android.com/reference/android/content/pm/PackageInstaller.SessionParams#setRequireUserAction(int))
- [通过 content URI 共享文件](https://developer.android.com/training/secure-file-sharing/share-file)
- [Android 应用签名与升级](https://developer.android.com/studio/publish/app-signing#considerations)
- [Google Play In-App Updates](https://developer.android.com/guide/playcore/in-app-updates)
- [Google Play REQUEST_INSTALL_PACKAGES 政策](https://support.google.com/googleplay/android-developer/answer/12085295)
