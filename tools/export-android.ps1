# Android 导出封装：设好 SDK/JDK 环境变量后调 Godot，并处理"产物写完但进程不退出"的收尾。
#
#   powershell -File tools/export-android.ps1              # debug 包（默认）
#   powershell -File tools/export-android.ps1 -Release     # 需要签名配置
param(
    [switch]$Release,
    [string]$Out = 'dist\android\OrC-KSD-android.apk',
    [string]$Godot = 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64.exe',
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

# 这个脚本拿**源码目录**当项目根（--path $root），于是 Godot 会把 dist/ 里的东西当资源导入：
# 1 GB 的发行包、验证截图全进资源库；一旦其中有一张坏图，导入报错会连带整个导出失败
# （实测：dist/releases/2.0.0-alpha.5/verification/debug-android.png 被 LF→CRLF 转过，
#  Godot 报 "PNG file corrupted by ASCII conversion"，导出直接失败，且看不出跟它有关）。
# dist/ 是产物目录，永远不该进资源库——用 .gdignore 挡住，缺了就补上。
foreach ($ignored in @('dist','artifacts')) {
    $ignoredPath = Join-Path $root $ignored
    $marker = Join-Path $ignoredPath '.gdignore'
    if ((Test-Path -LiteralPath $ignoredPath) -and !(Test-Path -LiteralPath $marker)) {
        New-Item -ItemType File -Path $marker -Force | Out-Null
    }
}
& (Join-Path $PSScriptRoot 'prepare-android-platform.ps1') -ProjectRoot $root

$outPath = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
New-Item -ItemType Directory -Path (Split-Path -Parent $outPath) -Force | Out-Null
$mode = if ($Release) { '--export-release' } else { '--export-debug' }
$exportStarted = [DateTime]::UtcNow

Write-Host "导出 $mode ...（首次要拉 Gradle 依赖，之后约 3~5 分钟；APK 约 1 GB）"
# 不用 Start-Process：它一旦带 -RedirectStandardOutput 就会重建子进程环境块，
# 而这台机器的环境里同时存在 HTTPS_PROXY 和 https_proxy，重建时按大小写不敏感撞键，
# 直接抛 "Item has already been added. Key in dictionary: 'HTTPS_PROXY'"。
# .NET Process 的 RedirectStandardOutput 不走那条路径（godot-wait.ps1 一直是这么干的）。
# ArgumentList 在 .NET Core（pwsh 6+）才有；Windows PowerShell 5.1 退回 Arguments 字符串拼接。
$argumentList = @('--headless','--path',$root,$mode,'Android',$outPath)
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = $Godot
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
# Godot 输出是 UTF-8。不显式指定的话 .NET 按控制台代码页（本机 936）解码，
# 日志里的中文会变成乱码——排查失败原因时正是最需要看懂的那几行。
$utf8 = [Text.UTF8Encoding]::new($false)
$info.StandardOutputEncoding = $utf8
$info.StandardErrorEncoding = $utf8
if ($info.ArgumentList) { foreach ($argument in $argumentList) { $info.ArgumentList.Add([string]$argument) } }
else { $info.Arguments = (@($argumentList) | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { [string]$_ } }) -join ' ' }
$process = [Diagnostics.Process]::new()
$process.StartInfo = $info
if (-not $process.Start()) { throw 'Godot 进程启动失败。' }
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()

# Godot 在产物写完后常卡在 adb 收尾不退出：等到超时就直接结束进程，再看产物判定成败。
if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
    Write-Host '（Godot 收尾卡住，已结束进程；按产物判定）'
    try { $process.Kill() } catch { }
}
[IO.File]::WriteAllText($outPath + '.export.log', $stdout.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($outPath + '.export-errors.log', $stderr.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
try { $process.Dispose() } catch { }

if (!(Test-Path -LiteralPath $outPath)) { throw '导出失败：没有生成 APK（往上翻 Godot 输出找第一条 error）' }
if ((Get-Item -LiteralPath $outPath).LastWriteTimeUtc -lt $exportStarted) { throw '导出失败：磁盘上仅有此前的旧 APK。' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$apkCheck = [System.IO.Compression.ZipFile]::OpenRead($outPath)
try {
    foreach ($entry in @('AndroidManifest.xml','classes.dex')) {
        if (!$apkCheck.GetEntry($entry)) { throw "APK 缺少 $entry" }
    }
} finally { $apkCheck.Dispose() }
$size = [math]::Round((Get-Item -LiteralPath $outPath).Length / 1MB, 1)
$hash = (Get-FileHash -LiteralPath $outPath -Algorithm SHA256).Hash
Write-Host "ANDROID_EXPORT_OK $outPath ($size MB)"
Write-Host "SHA256 $hash"
