param(
    [string]$BuildRoot = 'H:\g\kards',
    [string]$Godot = 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe',
    # 引擎源码副本：房间屏要拉起 Orc.Server；构建产物缺 OrcServer/ 时按 ORC_ENGINE_ROOT 兜底解析。
    [string]$EngineRoot = 'H:\Working Folder\OrC-KSD-github',
    [switch]$Editor,
    [switch]$Test,
    [switch]$Verify,
    [switch]$VerifyDiy,
    [switch]$VerifyAuthoring,
    [switch]$VerifyEffects,
    [switch]$NetProbe,
    [switch]$NetPlay,
    [switch]$VerifyRoom,
    [switch]$VerifyNavigation,
    [switch]$VerifyPaperMotion,
    [switch]$VerifyOpening,
    [switch]$VerifyViewerReplay,
    [switch]$VerifyRoomRemote,
    [switch]$VerifyRoomJoin,
    [switch]$VerifyRoomReconnect,
    [switch]$VerifyRoomDiscovery,
    [switch]$VerifyRoomSegments,
    [switch]$VerifyRoomPing,
    # 房间名 / 固定端口 / 房间成员（含移除）。
    [switch]$VerifyRoomSettings,
    [switch]$VerifyTarget,
    [switch]$VerifyCombat,
    [switch]$VerifyDeck,
    [switch]$VerifyDecks,
    # 弃牌演出 + 弃牌堆战报（以前只能靠整轮 -Verify 跑到）。
    [switch]$VerifyDiscard,
    [switch]$VerifyAppearance,
    [switch]$CaptureRoom,
    [switch]$Capture,
    # 桌面手机模拟：按手机比例开窗 + 走触屏输入路径（鼠标按住 0.45s = 长按取消）。
    [switch]$MobileSim,
    [string]$MobileSize = '1200x540'
)
$ErrorActionPreference = 'Stop'
Write-Host '[1/5] 同步源码到运行副本 …' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'prepare.ps1') -BuildRoot $BuildRoot -SkipAssets
if (!(Test-Path -LiteralPath $Godot)) { throw 'Pass -Godot with the path to Godot 4.7 .NET.' }
if (Test-Path (Join-Path $EngineRoot 'src\Orc.Server')) { $env:ORC_ENGINE_ROOT = $EngineRoot }
Push-Location $BuildRoot
try {
    $probeOutput = & $Godot --headless --path $BuildRoot --script (Join-Path $BuildRoot 'tools\probe.gd') 2>&1
    if (($probeOutput -join "`n") -notmatch 'csharp=true') { throw 'Godot has no C# support. Use the .NET build.' }
    Write-Host '[2/5] 编译工程 …' -ForegroundColor Cyan
    Write-Host "      ORC_ENGINE_ROOT=$env:ORC_ENGINE_ROOT" -ForegroundColor DarkGray
    & dotnet build 'Kards.Ui.csproj' --nologo
    if ($LASTEXITCODE -ne 0) {
        # 运行副本的 obj/ 是增量缓存，偶尔会坚持一个源里已经不存在的错误：
        # 2026-10-27 实测——源里刚给 BattleLog 加了 Observe，副本增量编译仍报 CS1061，
        # 紧接着 -t:Rebuild 立刻通过（源与副本的 BattleLog.cs 字节一致）。
        # 全量重建一次即可自愈；真错误会原样再报一遍，不会被这条重试掩盖。
        Write-Host '      增量编译失败，改为全量重建重试一次 …' -ForegroundColor Yellow
        & dotnet build 'Kards.Ui.csproj' --nologo -t:Rebuild
    }
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    # Development runs use the engine just built, rather than an older server beside a prior export.
    $developmentServerDll = Join-Path $EngineRoot 'src\Orc.Server\bin\Debug\net8.0\Orc.Server.dll'
    if (-not $env:ORC_SERVER_DLL -and (Test-Path -LiteralPath $developmentServerDll)) { $env:ORC_SERVER_DLL = $developmentServerDll }
    # Godot's build copies project assemblies only, but the engine's scripting satellite
    # (Orc.Script) needs its Roslyn runtime closure next to the game assembly. Publish the bridge
    # once to resolve the full closure and sync whatever the Godot output is missing.
    $assemblyDir = Join-Path $BuildRoot '.godot\mono\temp\bin\Debug'
    $runtimeOut = Join-Path $env:TEMP 'kards-ui-runtime'
    Write-Host '[3/5] 准备脚本运行时依赖 …' -ForegroundColor Cyan
    & dotnet publish (Join-Path $PSScriptRoot '..\proto\bridge\Kards.Ui.OrcBridge\Kards.Ui.OrcBridge.csproj') -c Debug -o $runtimeOut --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Runtime closure publish failed.' }
    Get-ChildItem $runtimeOut -Filter '*.dll' |
        Where-Object { -not (Test-Path (Join-Path $assemblyDir $_.Name)) } |
        Copy-Item -Destination $assemblyDir -Force
    # In-process parser runtime; shipped next to the game, no Node/browser installation.
    Get-ChildItem (Join-Path $runtimeOut 'runtimes/win-x64/native') -Filter '*.dll' |
        Copy-Item -Destination $assemblyDir -Force
    if ($Test) {
        & dotnet test 'tests\Kards.Ui.Tests.csproj' --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    # Godot 的 headless 编辑器会往 stderr 打一条 "EditorSettings not instantiated yet …"（配置了
    # Android SDK 路径后必现）。$ErrorActionPreference='Stop' 会把它当成致命错误，把整个验证流程掐断，
    # 所以这里显式降级为 Continue，只看退出码。
    # 资源导入只有真正要跑 Godot 时才必要：单独 -Test 跳过，省掉几分钟的静默等待。
    $onlyTest = $Test -and @($PSBoundParameters.Keys | Where-Object {
        $_ -notin @('Test', 'BuildRoot', 'Godot', 'EngineRoot', 'MobileSize') }).Count -eq 0
    if (-not $onlyTest) {
        Write-Host '[4/5] Godot 资源导入（首次或资源变动时较慢，静默等待即可）…' -ForegroundColor Cyan
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { & $Godot --headless --editor --path $BuildRoot --quit 2>&1 | Out-Null }
        finally { $ErrorActionPreference = $previousPreference }
        if ($LASTEXITCODE -ne 0) { throw 'Godot import failed.' }
    }
    # 只跑测试就到此为止。否则会掉进下面的 else 分支，以交互方式把游戏窗口拉起来——
    # 那个进程不会自己退出，于是 `run.ps1 -Test` 永远不返回（实测踩过：静默挂 10 分钟）。
    if ($onlyTest) { Write-Host '[5/5] 只跑测试，跳过 Godot 运行。' -ForegroundColor Cyan; return }
    Write-Host '[5/5] 运行 …' -ForegroundColor Cyan
    # 同上：Godot 会把 GD.PushWarning 写到 stderr（探针会故意保存一张坏卡、故意走一遍拒绝分支），
    # $ErrorActionPreference='Stop' 会把它当成致命错误，于是"探针跑完了"被读成"脚本失败"。
    # 这里降级为 Continue，成败只看退出码——Godot 侧的 Check 失败会 Quit(1)。
    $previousRunPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    if ($VerifyEffects) { & $Godot --headless --path $BuildRoot '--' --verify-ui --verify-effects }
    elseif ($NetProbe) { & $Godot --headless --path $BuildRoot '--' --net-probe }
    elseif ($NetPlay) { & $Godot --headless --path $BuildRoot '--' --net-play }
    elseif ($VerifyOpening) { & $Godot --path $BuildRoot '--' --verify-opening }
    elseif ($VerifyPaperMotion) { & $Godot --path $BuildRoot '--' --verify-paper-motion }
    elseif ($VerifyNavigation) { & $Godot --path $BuildRoot '--' --verify-navigation }
    elseif ($VerifyViewerReplay) { & $Godot --headless --path $BuildRoot '--' --verify-viewer-replay }
    elseif ($VerifyRoom) { & $Godot --headless --path $BuildRoot '--' --verify-room }
    elseif ($VerifyRoomRemote) { & $Godot --headless --path $BuildRoot '--' --verify-room-remote }
    elseif ($VerifyRoomJoin) { & $Godot --headless --path $BuildRoot '--' --verify-room-join }
    elseif ($VerifyRoomReconnect) { & $Godot --headless --path $BuildRoot '--' --verify-room-reconnect }
    elseif ($VerifyRoomDiscovery) { & $Godot --headless --path $BuildRoot '--' --verify-room-discovery }
    elseif ($VerifyRoomSegments) { & $Godot --headless --path $BuildRoot '--' --verify-room-segments }
    elseif ($VerifyRoomPing) { & $Godot --headless --path $BuildRoot '--' --verify-room-ping }
    elseif ($VerifyRoomSettings) { & $Godot --headless --path $BuildRoot '--' --verify-room-settings }
    elseif ($VerifyTarget) { & $Godot --headless --path $BuildRoot '--' --verify-target }
    elseif ($VerifyCombat) { & $Godot --headless --path $BuildRoot '--' --verify-combat }
    elseif ($VerifyDeck) { & $Godot --headless --path $BuildRoot '--' --verify-deck }
    # 卡组构筑全流程（含断链重新绑定）。以前只能通过 -Verify 跑到，单独改一处要等整轮 UI 探针。
    elseif ($VerifyDecks) { & $Godot --headless --path $BuildRoot '--' --verify-decks }
    elseif ($VerifyDiscard) { & $Godot --headless --path $BuildRoot '--' --verify-discard }
    elseif ($VerifyAppearance) { & $Godot --path $BuildRoot '--' --verify-appearance }
    elseif ($CaptureRoom) { & $Godot --path $BuildRoot '--' --capture-room }
    elseif ($Verify) { & $Godot --headless --path $BuildRoot '--' --verify-ui }
    elseif ($VerifyDiy) { & $Godot --headless --path $BuildRoot '--' --verify-diy }
    elseif ($VerifyAuthoring) { & $Godot --headless --path $BuildRoot '--' --verify-desktop-authoring }
    elseif ($Capture) { & $Godot --path $BuildRoot '--' --capture-ui }
    elseif ($Editor) { & $Godot --editor --path $BuildRoot }
    elseif ($MobileSim) { & $Godot --path $BuildRoot '--' --mobile-sim "--mobile-size=$MobileSize" }
    else { & $Godot --path $BuildRoot }
    $ErrorActionPreference = $previousRunPreference
    if ($LASTEXITCODE -ne 0) { throw "Godot exited with code $LASTEXITCODE" }
} finally { Pop-Location }
