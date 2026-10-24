# 一键把 Android 包装到真机并看日志。
#
#   powershell -File tools/install-android.ps1
#   powershell -File tools/install-android.ps1 -Log        # 装完挂着看 logcat
#
# 比手动拷 1 GB APK 快的地方：走 USB/adb，安装完直接拉起，出问题当场有日志。
# 前提：手机开 USB 调试、`adb devices` 能看到设备。
param(
    [string]$Apk = 'dist\android\OrC-KSD-android.apk',
    [string]$Sdk = 'H:\g\tools\android-sdk',
    [string]$Package = 'com.orcksd.game',
    [switch]$Log
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$apkPath = if ([IO.Path]::IsPathRooted($Apk)) { $Apk } else { Join-Path $root $Apk }
$adb = Join-Path $Sdk 'platform-tools\adb.exe'

if (!(Test-Path -LiteralPath $adb)) { throw "找不到 adb：$adb（用 -Sdk 指定 Android SDK 根目录）" }
if (!(Test-Path -LiteralPath $apkPath)) { throw "找不到 APK：$apkPath（先跑一次 Android 导出）" }

$devices = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\sdevice$' }
if (-not $devices) {
    & $adb devices
    throw '没有已授权的设备：请开手机的 USB 调试，插上后允许这台电脑调试。'
}

$size = [math]::Round((Get-Item -LiteralPath $apkPath).Length / 1MB, 1)
Write-Host "安装 $([IO.Path]::GetFileName($apkPath))（$size MB）→ $($devices.Count) 台设备"
# -r 覆盖安装，保留卡组/自制卡/user:// 数据；首次装会慢（1 GB 包）。
& $adb install -r -d $apkPath
if ($LASTEXITCODE -ne 0) { throw "adb install 失败（退出码 $LASTEXITCODE）。常见原因：包签名与已装版本不同，先 adb uninstall $Package" }

& $adb shell monkey -p $Package -c android.intent.category.LAUNCHER 1 | Out-Null
Write-Host '已启动。'

if ($Log) {
    Write-Host '--- logcat（Ctrl+C 退出；Godot 的日志在 godot 标签下）---'
    & $adb logcat -c
    & $adb logcat godot:V GodotEngine:V AndroidRuntime:E *:S
}
