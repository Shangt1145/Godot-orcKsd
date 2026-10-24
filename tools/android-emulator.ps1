<#
.SYNOPSIS
    Android 模拟器测试入口：装加速驱动、起模拟器、装 APK、看日志。

.DESCRIPTION
    模拟器组件装在 G:\android-emulator（H 盘容量不够），AVD 数据在 G:\android-avd。
    x86_64 镜像必须开硬件加速，否则纯软件模拟慢到无法测试；驱动（AEHD）首次安装需要管理员。

    常用：
      tools\android-emulator.ps1                     # 起模拟器（已加速）+ 装最新 APK 并打开
      tools\android-emulator.ps1 -Driver             # 只装/修复加速驱动（会弹 UAC）
      tools\android-emulator.ps1 -Log                # 起来后挂着看 logcat
      tools\android-emulator.ps1 -Boot               # 只起模拟器，不装 APK
      tools\android-emulator.ps1 -Shutdown           # 关掉模拟器

.PARAMETER Apk
    要装进模拟器的 APK。缺省找 dist\android-emu\OrC-KSD-x86_64-debug.apk。

.PARAMETER Package
    启动的包名，缺省 com.orcksd.game。
#>
param(
    [string]$Apk = '',
    [string]$Package = 'com.orcksd.game',
    [string]$Avd = 'orcksd35',
    [string]$Sdk = 'G:\android-emulator',
    [string]$AvdHome = 'G:\android-avd',
    [string]$Adb = 'H:\g\tools\android-sdk\platform-tools\adb.exe',
    [switch]$Driver,
    [switch]$Boot,
    [switch]$Log,
    [switch]$Headless,
    [switch]$Shutdown
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Elevated {
    # 仅用于装驱动：驱动服务必须由管理员创建，普通会话拿不到权限。
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Driver"
    $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $argLine -Verb RunAs -Wait -PassThru
    return $p.ExitCode
}

function Assert-Sdk {
    if (!(Test-Path -LiteralPath (Join-Path $Sdk 'emulator\emulator.exe'))) {
        throw "找不到模拟器：$Sdk\emulator\emulator.exe。先装组件：sdkmanager --sdk_root=$Sdk --install emulator 'system-images;android-35;google_apis;x86_64'"
    }
    if (!(Test-Path -LiteralPath $Adb)) { throw "找不到 adb：$Adb" }
}

function Get-AccelState {
    $emulator = Join-Path $Sdk 'emulator\emulator.exe'
    $raw = & $emulator -accel-check 2>&1 | Out-String
    if ($raw -match 'is installed and usable|WHPX.*is installed|Hypervisor\.Framework') { return @{ Ok = $true; Text = $raw.Trim() } }
    return @{ Ok = $false; Text = $raw.Trim() }
}

function Install-Driver {
    $dir = Join-Path $Sdk 'extras\google\Android_Emulator_Hypervisor_Driver'
    if (!(Test-Path -LiteralPath $dir)) {
        throw "缺驱动包：$dir。先执行 sdkmanager --sdk_root=$Sdk --install 'extras;google;Android_Emulator_Hypervisor_Driver'"
    }
    if (!(Test-Admin)) {
        Write-Host '需要管理员权限安装加速驱动，正在请求提权（会弹 UAC，请点“是”）...'
        exit (Invoke-Elevated)
    }
    Push-Location $dir
    try {
        # silent_install.bat 内部走 sc create/start；在这里用 pnputil 装同一份 inf 更稳。
        & pnputil.exe /add-driver (Join-Path $dir 'aehd.Inf') /install | Out-Host
        & silent_install.bat | Out-Host
    }
    finally { Pop-Location }
    $state = Get-AccelState
    if ($state.Ok) { Write-Host '加速驱动已就绪。' } else { Write-Host "驱动装完但仍未生效：`n$($state.Text)"; exit 1 }
}

function Stop-Emulator {
    $devices = & $Adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '^emulator-\d+\s+' }
    if (!$devices) { Write-Host '没有正在运行的模拟器。'; return }
    & $Adb emu kill | Out-Null
    Start-Sleep -Seconds 3
    Write-Host '模拟器已关闭。'
}

function Start-Emulator {
    $running = & $Adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '^emulator-\d+\s+device' }
    if ($running) { Write-Host '模拟器已在运行，直接复用。'; return }
    $env:ANDROID_SDK_ROOT = $Sdk
    $env:ANDROID_HOME = $Sdk
    $env:ANDROID_AVD_HOME = $AvdHome
    $emulator = Join-Path $Sdk 'emulator\emulator.exe'
    $emuArgs = @('-avd', $Avd, '-no-snapshot', '-no-audio', '-no-boot-anim',
        '-gpu', 'swiftshader_indirect', '-memory', '3072')
    if ($Headless) { $emuArgs += '-no-window' }
    Write-Host "启动模拟器 $Avd ..."
    Start-Process -FilePath $emulator -ArgumentList $emuArgs -WorkingDirectory (Join-Path $Sdk 'emulator') | Out-Null
}

function Wait-Boot([int]$TimeoutSeconds = 240) {
    Write-Host "等待 Android 启动（最多 $TimeoutSeconds 秒）..." -NoNewline
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $booted = (& $Adb shell getprop sys.boot_completed 2>$null | Out-String).Trim()
        if ($booted -eq '1') { Write-Host ' 已就绪。'; & $Adb shell input keyevent 82 | Out-Null; return $true }
        Write-Host '.' -NoNewline
        Start-Sleep -Seconds 3
    }
    Write-Host ''
    Write-Host '启动超时。若日志里出现 “x86_64 emulation may not work without hardware acceleration”，跑 -Driver 装加速驱动。'
    return $false
}

function Install-Apk([string]$Path) {
    if (!$Path) {
        $Path = Join-Path $sourceRoot 'dist\android-emu\OrC-KSD-x86_64-debug.apk'
    }
    if (!(Test-Path -LiteralPath $Path)) {
        throw ("没有 APK：$Path`n" +
            '先导出 x86_64 调试包：把 export_presets.cfg 的 architectures/x86_64 设为 true，' +
            '再执行 tools\export-android.ps1 -Out G:\orc-dist\android-emu\OrC-KSD-x86_64-debug.apk')
    }
    $mb = [math]::Round((Get-Item -LiteralPath $Path).Length / 1MB, 1)
    Write-Host "安装 $([IO.Path]::GetFileName($Path))（$mb MB）..."
    & $Adb install -r -d $Path | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "adb install 失败（退出码 $LASTEXITCODE）。签名不一致时先：adb uninstall $Package" }
    & $Adb shell monkey -p $Package -c android.intent.category.LAUNCHER 1 | Out-Null
    Write-Host "已启动 $Package。"
}

function Show-Log {
    & $Adb logcat -c
    Write-Host '--- logcat（Ctrl+C 退出；Godot 日志在 godot / GodotEngine 标签下）---'
    & $Adb logcat godot:V GodotEngine:V AndroidRuntime:E ActivityManager:I *:S
}

Assert-Sdk

if ($Shutdown) { Stop-Emulator; return }
if ($Driver) { Install-Driver; return }

$state = Get-AccelState
if (!$state.Ok) {
    Write-Host "模拟器没有硬件加速，x86_64 镜像会慢到无法测试。"
    Write-Host "修复：$($MyInvocation.MyCommand.Name) -Driver （需要管理员，会弹 UAC）"
    Write-Host "当前状态：$($state.Text)"
    exit 2
}

Start-Emulator
if (!(Wait-Boot)) { exit 3 }
if ($Boot) { Write-Host '完成（只起了模拟器，未装 APK）。'; return }

Install-Apk $Apk
if ($Log) { Show-Log }
