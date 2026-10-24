# Android 导出封装：设好 SDK/JDK 环境变量后调 Godot，并处理"产物写完但进程不退出"的收尾。
#
#   powershell -File tools/export-android.ps1              # debug 包（默认）
#   powershell -File tools/export-android.ps1 -Release     # 需要签名配置
param(
    [switch]$Release,
    [string]$Out = 'dist\android\OrC-KSD-android.apk',
    [string]$Godot = 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe',
    [string]$Sdk = 'H:\g\tools\android-sdk',
    [string]$Jdk = 'H:\g\tools\jdk17\jdk-17.0.20.1+1',
    [int]$TimeoutMinutes = 40
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

if (!(Test-Path -LiteralPath $Godot)) { throw "找不到 Godot：$Godot" }
if (!(Test-Path -LiteralPath (Join-Path $Sdk 'platform-tools'))) { throw "找不到 Android SDK：$Sdk" }
if (!(Test-Path -LiteralPath (Join-Path $Jdk 'bin\java.exe'))) { throw "找不到 JDK：$Jdk" }

$env:ANDROID_HOME = $Sdk
$env:ANDROID_SDK_ROOT = $Sdk
$env:JAVA_HOME = $Jdk
$env:PATH = "$(Join-Path $Jdk 'bin');$env:PATH"

$outPath = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
New-Item -ItemType Directory -Path (Split-Path -Parent $outPath) -Force | Out-Null
$mode = if ($Release) { '--export-release' } else { '--export-debug' }

Write-Host "导出 $mode ...（首次要拉 Gradle 依赖，之后约 3~5 分钟；APK 约 1 GB）"
# 路径含空格，必须自己加引号：Start-Process 的 -ArgumentList 是按空格拼的，会拆坏 "--path"。
$argumentLine = '--headless --path "{0}" {1} Android "{2}"' -f $root, $mode, $outPath
$process = Start-Process -FilePath $Godot -PassThru -NoNewWindow -ArgumentList $argumentLine

# Godot 在产物写完后常卡在 adb 收尾不退出：等到超时就直接结束进程，再看产物判定成败。
if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
    Write-Host '（Godot 收尾卡住，已结束进程；按产物判定）'
    try { $process.Kill() } catch { }
}

if (!(Test-Path -LiteralPath $outPath)) { throw '导出失败：没有生成 APK（往上翻 Godot 输出找第一条 error）' }
$size = [math]::Round((Get-Item -LiteralPath $outPath).Length / 1MB, 1)
$hash = (Get-FileHash -LiteralPath $outPath -Algorithm SHA256).Hash
Write-Host "ANDROID_EXPORT_OK $outPath ($size MB)"
Write-Host "SHA256 $hash"
