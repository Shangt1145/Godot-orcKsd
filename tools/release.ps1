param(
    [string]$BuildRoot = 'H:\g\kards',
    [string]$Godot = (Join-Path $PSScriptRoot 'godot-wait.ps1'),
    [string]$Preset = 'Windows Desktop',
    [string]$OutDir = 'dist\windows',
    [switch]$SkipGate,
    [switch]$TestPackage,
    [string]$EngineRoot = 'H:\Working Folder\OrC-KSD-github',
    [string]$KnownIssuesFile = 'docs/acceptance/BETA4_TEST_PACKAGE_20261023.md',
    [switch]$FullTests,
    [string]$BaselineZip='',
    [string]$BaselineVersion='',
    [string[]]$AdditionalBaselineZips=@(),
    # Zero infers previous betas, or all betas through N for a betaN.hotfix release.
    [ValidateRange(0, 1000)][int]$CompatibleThroughBeta=0,
    # 只产出「指定基线 → 本版」的单补丁（热更新用）：不发现历史基线、不生成兼容合集。
    # 历史基线跨太多版本时，pck 差异会让每个分片接近 1 GB，合集比完整包还大——那种情况不该发。
    [switch]$SingleBaseline,
    # 更新器版本独立于游戏版本：补丁校验规则变化时必须进位，否则新旧更新器同名、玩家无法区分。
    [string]$UpdaterVersion='1.2.0',
    [string]$UpdaterBuildRoot='H:\g\orc-offline-update',
    # Delivered-version ZIPs are archived so older players can still be patched. They are large
    # (200-300 MB each) and none of them ship, so they live outside the working tree by default;
    # keeping them inside made the project folder grow past 1 GB for no build-time benefit.
    [string]$BaselineArchiveDir='H:\g\baselines'
)
# Builds the player-facing package.
#
# The engine is wired in as a *source* reference (OrcEngineRoot -> another checkout), so the one
# thing that could silently go wrong here is an export that ships without Orc.Game.dll: it would
# build fine, run fine on this machine, and only fail on a player's. That is what the postflight
# below exists to catch.
$ErrorActionPreference = 'Stop'
if($AdditionalBaselineZips.Count -gt 63){throw 'At most 64 source builds are supported.'}
$AdditionalBaselineZips=@($AdditionalBaselineZips|ForEach-Object{
    $resolved=[IO.Path]::GetFullPath($_)
    if(!(Test-Path -LiteralPath $resolved)){throw "Additional baseline not found: $resolved"}
    $resolved
})
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (!(Test-Path -LiteralPath $Godot)) { throw "Godot not found: $Godot" }

# Remove-Item 在某些受管环境里被改写成「移入回收站」，大文件/大目录会失败并直接中止发布。
# 删除只是为了让下一步重新生成，因此删不掉就改名让位，绝不阻断打包。
function Clear-Path([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) { return }
    try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop; return } catch { }
    # 回收站操作可能「报错但实际已移走」——先复查再决定是否改名。
    if (!(Test-Path -LiteralPath $Path)) { return }
    $suffix = '.stale-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    try { Rename-Item -LiteralPath $Path -NewName ([IO.Path]::GetFileName($Path) + $suffix) -Force -ErrorAction Stop } catch { }
}

# 1) Preflight: a C# project needs the .NET ("mono") export templates, not the plain ones.
#    Without them the export dies with "Cannot export project ... due to configuration errors",
#    which says nothing about the real cause.
$sourceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
$version = (Get-Content -LiteralPath (Join-Path $sourceRoot 'VERSION') -Raw).Trim()
# Keep the previous same-version delivery even when compatibility selects an older primary source.
$previousDelivery=Join-Path $sourceRoot "dist/releases/$version/full/OrC-KSD-$version-win-x64.zip"
if(!$TestPackage -and (Test-Path -LiteralPath $previousDelivery)){
    $preservedDir=if([IO.Path]::IsPathRooted($BaselineArchiveDir)){[IO.Path]::GetFullPath($BaselineArchiveDir)}else{Join-Path $sourceRoot $BaselineArchiveDir}
    New-Item -ItemType Directory -Path $preservedDir -Force|Out-Null
    $previousHash=(Get-FileHash -LiteralPath $previousDelivery -Algorithm SHA256).Hash
    $preservedZip=Join-Path $preservedDir ($previousHash+'.zip')
    if(!(Test-Path -LiteralPath $preservedZip)){Copy-Item -LiteralPath $previousDelivery -Destination $preservedZip}
}
$baselineVersions=@{}
if(!$TestPackage){
    $throughBeta=$CompatibleThroughBeta
    if(!$throughBeta -and $version -match '^0\.0\.0beta([1-9]\d*)\.hotfix(?:\.[1-9]\d*)?$'){$throughBeta=[int]$Matches[1]}
    elseif(!$throughBeta -and $version -match '^0\.0\.0-beta\.([1-9]\d*)$'){$throughBeta=[int]$Matches[1]-1}
    if($throughBeta -gt 0 -and !$SingleBaseline){
        $archiveSearch=if([IO.Path]::IsPathRooted($BaselineArchiveDir)){$BaselineArchiveDir}else{Join-Path $sourceRoot $BaselineArchiveDir}
        $discovered=@(& (Join-Path $PSScriptRoot 'find-update-baselines.ps1') -SearchRoots @((Join-Path $sourceRoot 'dist/releases'),$archiveSearch) -ThroughBeta $throughBeta)
        foreach($known in $discovered){$baselineVersions[[IO.Path]::GetFullPath($known.Path)]=$known.Version}
        if(!$BaselineZip){$BaselineZip=$discovered[-1].Path;$BaselineVersion=$discovered[-1].Version}
        $primary=[IO.Path]::GetFullPath($BaselineZip)
        $AdditionalBaselineZips=@(@($AdditionalBaselineZips)+@($discovered.Path)|Where-Object{[IO.Path]::GetFullPath($_) -ne $primary}|Sort-Object -Unique)
        if($AdditionalBaselineZips.Count -gt 63){throw 'At most 64 source archives are supported.'}
        Write-Host "COMPATIBLE_BASELINES_FOUND through=beta.$throughBeta archives=$($discovered.Count)"
    }
}
# Preserve the previously delivered archive before publishing another build with the same display version.
if($TestPackage){
    $SkipGate = $true
    if(!(Test-Path -LiteralPath (Join-Path $sourceRoot $KnownIssuesFile))){throw 'A test package must include its known-issues document.'}
    $BaselineZip = ''; $AdditionalBaselineZips = @()
}
if(!$TestPackage -and !$BaselineZip){
    foreach($candidate in @(
        (Join-Path $sourceRoot "dist/releases/$version/full/OrC-KSD-$version-win-x64.zip"),
        (Join-Path $sourceRoot "dist/OrC-KSD-$version-win-x64.zip")
    )){if(Test-Path -LiteralPath $candidate){$BaselineZip=$candidate;break}}
}
if($BaselineZip){
    $BaselineZip=[IO.Path]::GetFullPath($BaselineZip)
    if(!(Test-Path -LiteralPath $BaselineZip)){throw 'Baseline ZIP does not exist.'}
    $baseHash=(Get-FileHash -LiteralPath $BaselineZip -Algorithm SHA256).Hash
    if(!$BaselineVersion){
        $archive=[IO.Compression.ZipFile]::OpenRead($BaselineZip)
        try{
            $entry=$archive.GetEntry('.orcksd-build.json')
            if($entry){$reader=[IO.StreamReader]::new($entry.Open());try{$BaselineVersion=($reader.ReadToEnd()|ConvertFrom-Json).Version}finally{$reader.Dispose()}}
        }finally{$archive.Dispose()}
        if(!$BaselineVersion){
            if([IO.Path]::GetFileName($BaselineZip) -match '^OrC-KSD-(.+)-win-x64\.zip$'){$BaselineVersion=$Matches[1]}else{$BaselineVersion=$version}
        }
    }
    $archiveDir=if([IO.Path]::IsPathRooted($BaselineArchiveDir)){[IO.Path]::GetFullPath($BaselineArchiveDir)}else{Join-Path $sourceRoot $BaselineArchiveDir}
    New-Item -ItemType Directory $archiveDir -Force|Out-Null
    $savedBaseline=Join-Path $archiveDir ($baseHash+'.zip')
    if(!(Test-Path -LiteralPath $savedBaseline)){Copy-Item -LiteralPath $BaselineZip -Destination $savedBaseline}
    $BaselineZip=$savedBaseline
}
& (Join-Path $PSScriptRoot 'install-export-templates.ps1') -Godot $Godot
if ($LASTEXITCODE -ne 0) { throw 'Template setup failed.' }
& (Join-Path $PSScriptRoot 'prepare.ps1') -BuildRoot $BuildRoot -SkipAssets
if (!(Test-Path -LiteralPath (Join-Path $BuildRoot 'Kards.Ui.sln'))) { throw 'Missing Kards.Ui.sln: Godot cannot export C# without a solution.' }

# 2) Gate: the sync + build + unit tests + headless verify the project already trusts.
#    必须先把引擎路径钉死：本机可能同时存在 OrC-KSD（旧基线）与 OrC-KSD-github（工作副本），
#    让 MSBuild 自己去猜"同级目录"会挑错引擎，症状是一堆 CS0234 命名空间缺失。
if (Test-Path -LiteralPath (Join-Path $EngineRoot 'src\Orc.Game\Orc.Game.csproj')) {
    $env:ORC_ENGINE_ROOT = $EngineRoot
    Write-Host "ENGINE_ROOT $EngineRoot"
}
if (!$SkipGate) {
    $testArgs = @('test', (Join-Path $sourceRoot 'tests/Kards.Ui.Tests.csproj'), '--nologo')
    if (!$FullTests) { $testArgs += @('--filter', 'FullyQualifiedName~WorkshopTests|FullyQualifiedName~IdleTurnDeliveryTests|FullyQualifiedName~TurnInputGateTests|FullyQualifiedName~InspectorCompositionTests|FullyQualifiedName~InspectorBackendBaselineTests|FullyQualifiedName~DiyEditorUpdateTests|FullyQualifiedName~DeckPackTests|FullyQualifiedName~DeckConstructionTests|FullyQualifiedName~DiyCardTests|FullyQualifiedName~DiyInspectorTests|FullyQualifiedName~SourceEffectConverterTests|FullyQualifiedName~DesktopAuthoringTests|FullyQualifiedName~NativeScriptHostIsolationTests|FullyQualifiedName~OfficialCardExpansionTests|FullyQualifiedName~OfflineUpdateTests') }
    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Release unit test gate failed.' }
    & (Join-Path $PSScriptRoot 'run.ps1') -BuildRoot $BuildRoot -Godot $Godot -Verify
    if ($LASTEXITCODE -ne 0) { throw "Native UI gate failed, exit $LASTEXITCODE" }
}
# Even -SkipGate always synchronizes current sources and imports resources. The exported EXE
# verification below is mandatory; a Godot exit code of zero alone did not prove C# was exported.
& $Godot --headless --editor --path $BuildRoot --quit
if ($LASTEXITCODE -ne 0) { throw 'Resource import failed.' }

# 3) Export.
$target = [IO.Path]::GetFullPath((Join-Path $BuildRoot $OutDir))
$buildPrefix = $BuildRoot.TrimEnd('\') + '\'
if ((!$target.StartsWith($buildPrefix, [StringComparison]::OrdinalIgnoreCase)) -or ($target -eq $BuildRoot)) {
    throw 'OutDir must be a child directory of BuildRoot.'
}
Write-Host "Generated export directory: $target"
if (Test-Path -LiteralPath $target) { Clear-Path $target }
New-Item -ItemType Directory -Path $target -Force | Out-Null
$exe = Join-Path $target 'KardsUi.exe'
& $Godot --headless --path $BuildRoot --export-release $Preset $exe
if ($LASTEXITCODE -ne 0) { throw "Export failed, exit $LASTEXITCODE" }
if (!(Test-Path -LiteralPath $exe)) { throw "Export reported success but wrote no exe." }
if (!(Test-Path -LiteralPath (Join-Path $target 'KardsUi.pck'))) { throw 'Missing game resource pack.' }

# 4) Postflight: the package must carry the engine assembly itself.
#    Loose files (embed_pck/embed_build_outputs off) show up in the tree; an embedded pack hides
#    them, so fall back to a chunked byte scan rather than trusting the layout.
function Test-CarriesAssembly([string]$Dir, [string]$Name) {
    if (Get-ChildItem -LiteralPath $Dir -Recurse -File -Filter $Name | Select-Object -First 1) { return $true }
    $encoding = [Text.Encoding]::GetEncoding(28591)
    $chunk = 4MB
    foreach ($file in Get-ChildItem -LiteralPath $Dir -Recurse -File) {
        $buffer = New-Object byte[] ($chunk + $Name.Length)
        $carry = 0
        $stream = [IO.File]::OpenRead($file.FullName)
        try {
            while (($read = $stream.Read($buffer, $carry, $chunk)) -gt 0) {
                $total = $carry + $read
                if ($encoding.GetString($buffer, 0, $total).IndexOf($Name, [StringComparison]::Ordinal) -ge 0) {
                    return $true
                }
                $carry = [Math]::Min($Name.Length - 1, $total)
                [Array]::Copy($buffer, $total - $carry, $buffer, 0, $carry)
            }
        }
        finally { $stream.Dispose() }
    }
    return $false
}
if (!(Test-CarriesAssembly $target 'Orc.Game.dll')) {
    throw "The export does not carry Orc.Game.dll. The engine is a source reference, so the build " +
        "can succeed here and still ship a package that crashes on machines without it."
}
Write-Host "postflight: Orc.Game.dll present"
foreach ($dependency in @('Kards.Ui.dll','ClearScriptV8.win-x64.dll','ClearScript.V8.ICUData.dll','hostfxr.dll')) {
    if (!(Get-ChildItem -LiteralPath $target -Recurse -File -Filter $dependency | Select-Object -First 1)) { throw "Missing exported runtime: $dependency" }
}

# Ship the server and a private .NET runtime; hosting must not require the developer checkout or SDK.
$serverOutput = Join-Path $target 'OrcServer'
& dotnet publish (Join-Path $EngineRoot 'src/Orc.Server/Orc.Server.csproj') -c Release -o $serverOutput --self-contained false --nologo
if($LASTEXITCODE -ne 0){throw 'Server publish failed.'}
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$coreRuntime = Get-ChildItem (Join-Path $dotnetRoot 'shared/Microsoft.NETCore.App') -Directory | Where-Object Name -match '^8\.' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$hostRuntime = Get-ChildItem (Join-Path $dotnetRoot 'host/fxr') -Directory | Where-Object Name -match '^8\.' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if(!$coreRuntime -or !$hostRuntime){throw 'A .NET 8 runtime and hostfxr are required to assemble the private server runtime.'}
$privateRuntime = Join-Path $target 'OrcRuntime'
New-Item -ItemType Directory -Path $privateRuntime,(Join-Path $privateRuntime 'host/fxr'),(Join-Path $privateRuntime 'shared/Microsoft.NETCore.App') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'dotnet.exe'),(Join-Path $dotnetRoot 'LICENSE.txt'),(Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination $privateRuntime
Copy-Item -LiteralPath $coreRuntime.FullName -Destination (Join-Path $privateRuntime 'shared/Microsoft.NETCore.App') -Recurse
Copy-Item -LiteralPath $hostRuntime.FullName -Destination (Join-Path $privateRuntime 'host/fxr') -Recurse
if($TestPackage){
    Set-Content -LiteralPath (Join-Path $target 'TEST_BUILD.txt') -Value 'Known-issues test package. See 已知问题-测试包.md. Not a release acceptance pass.' -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $sourceRoot $KnownIssuesFile) -Destination (Join-Path $target '已知问题-测试包.md')
}

# 5) Package.
& (Join-Path $PSScriptRoot 'build-updater.ps1') -BuildRoot $UpdaterBuildRoot -Godot $Godot
if($LASTEXITCODE -ne 0){throw 'Offline updater build failed.'}
$updaterOutput=Join-Path $UpdaterBuildRoot 'dist/current'
Get-ChildItem -LiteralPath $updaterOutput | Copy-Item -Destination $target -Recurse -Force
$updateCli=Join-Path $sourceRoot 'tools/OfflineUpdate/OrC.Update.Cli'
& dotnet build $updateCli --nologo -v:q
if($LASTEXITCODE -ne 0){throw 'Update tools build failed.'}
$updateDll=Join-Path $updateCli 'bin/Debug/net9.0/OrC.Update.Cli.dll'
& dotnet $updateDll stamp $target $version
if($LASTEXITCODE -ne 0){throw 'Build manifest generation failed.'}
$buildInfo=Get-Content -LiteralPath (Join-Path $target '.orcksd-build.json') -Raw|ConvertFrom-Json
$patch=$null
if($BaselineZip){
    $patch=Join-Path (Split-Path -Parent $target) "OrC-KSD-$version-$($buildInfo.BuildId.Substring(0,12)).orcupdate"
    & dotnet $updateDll build $BaselineZip $target $patch $BaselineVersion $version
    if($LASTEXITCODE -ne 0){throw 'Incremental patch generation failed.'}
    $archive=[IO.Compression.ZipFile]::OpenRead($patch)
    try{$reader=[IO.StreamReader]::new($archive.GetEntry('patch.json').Open());try{$patchInfo=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}}finally{$archive.Dispose()}
    $namedPatch=Join-Path (Split-Path -Parent $target) "OrC-KSD-$version-from-$($patchInfo.Source.BuildId.Substring(0,12))-to-$($buildInfo.BuildId.Substring(0,12)).orcupdate"
    Move-Item -LiteralPath $patch -Destination $namedPatch -Force;$patch=$namedPatch
    if($AdditionalBaselineZips.Count){
        $parts=@($patch);$sourceIds=@($patchInfo.Source.BuildId)
        foreach($additional in $AdditionalBaselineZips){
            $additionalVersion=if($baselineVersions.ContainsKey($additional)){$baselineVersions[$additional]}else{$BaselineVersion}
            $archive=[IO.Compression.ZipFile]::OpenRead($additional)
            try{
                $entry=$archive.GetEntry('.orcksd-build.json')
                if($entry){$reader=[IO.StreamReader]::new($entry.Open());try{$additionalVersion=($reader.ReadToEnd()|ConvertFrom-Json).Version}finally{$reader.Dispose()}}
            }finally{$archive.Dispose()}
            $part=Join-Path (Split-Path -Parent $target) ('baseline-'+[Guid]::NewGuid().ToString('N')+'.orcupdate')
            & dotnet $updateDll build $additional $target $part $additionalVersion $version
            if($LASTEXITCODE -ne 0){throw 'Additional baseline patch generation failed.'}
            $info=(& dotnet $updateDll inspect $part)|ConvertFrom-Json
            if($sourceIds -notcontains $info.Source.BuildId){$parts+=$part;$sourceIds+=$info.Source.BuildId}
        }
        $compatible=Join-Path (Split-Path -Parent $target) "OrC-KSD-$version-compatible-to-$($buildInfo.BuildId.Substring(0,12)).orcupdate"
        & dotnet $updateDll combine $compatible @parts
        if($LASTEXITCODE -ne 0){throw 'Compatible patch generation failed.'}
        $patch=$compatible
    }
}
$bootstrap=Join-Path (Split-Path -Parent $target) "OrC-KSD-offline-updater-$UpdaterVersion-win-x64.zip"
if(Test-Path -LiteralPath $bootstrap){Clear-Path $bootstrap}
Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $updaterOutput|Select-Object -ExpandProperty FullName) -DestinationPath $bootstrap
$packageSuffix = if($TestPackage){'-test'}else{''}
$zip = Join-Path (Split-Path -Parent $target) "OrC-KSD-$version$packageSuffix-win-x64.zip"
if (Test-Path -LiteralPath $zip) { Clear-Path $zip }
$runtime = Get-ChildItem -LiteralPath $target -Directory -Filter 'data_*' | Select-Object -First 1
if (!$runtime) { throw 'Missing self-contained .NET runtime directory.' }
Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $target -Force|Select-Object -ExpandProperty FullName) -DestinationPath $zip
# Unzip to a new directory and start that player with no project/SDK arguments. Verify card data,
# every shipped package definition/effect, textures, in-process parser and real native matches.
$smoke = Join-Path ([IO.Path]::GetTempPath()) ('orcksd-export-'+[Guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $zip -DestinationPath $smoke
$smokeLog = Join-Path $smoke 'verify-export.log'
$process = Start-Process -FilePath (Join-Path $smoke 'KardsUi.exe') -WorkingDirectory $smoke -WindowStyle Hidden -PassThru `
    -ArgumentList @('--headless','--log-file',('"'+$smokeLog+'"'),'--','--verify-export')
if (!$process.WaitForExit(120000)) { $process.Kill(); throw "Exported game verification timed out. See $smokeLog" }
$process.Refresh()
if ($process.ExitCode -ne 0 -or !(Select-String -LiteralPath $smokeLog -SimpleMatch 'EXPORT_VERIFY_OK' -Quiet)) {
    throw "Exported game verification failed. See $smokeLog"
}
$workshopLog = Join-Path $smoke 'verify-workshop.log'
$workshopProcess = Start-Process -FilePath (Join-Path $smoke 'KardsUi.exe') -WorkingDirectory $smoke -WindowStyle Hidden -PassThru -ArgumentList @('--headless','--log-file',('"'+$workshopLog+'"'),'--','--verify-workshop')
if (!$workshopProcess.WaitForExit(120000)) { $workshopProcess.Kill(); throw 'Exported workshop verification timed out.' }
$workshopProcess.Refresh()
if ($workshopProcess.ExitCode -ne 0 -or !(Select-String -LiteralPath $workshopLog -SimpleMatch 'WORKSHOP_VERIFY_OK' -Quiet)) { throw "Exported workshop verification failed. See $workshopLog" }
$patchLog=$null
if($patch){
  foreach($upgradeBaseline in (@($BaselineZip)+$AdditionalBaselineZips)){
    $upgrade=Join-Path ([IO.Path]::GetTempPath()) ('OrC 离线升级 验证 '+[Guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $upgradeBaseline -DestinationPath $upgrade
    $sentinel=Join-Path $upgrade '玩家自己的文件.txt';Set-Content -LiteralPath $sentinel -Value 'preserve me' -Encoding utf8
    $sentinelHash=(Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
    $patchLog=Join-Path $upgrade 'verify-offline-update.log'
    $updateExe=Join-Path $target '更新游戏.exe'
    $updater=Start-Process -FilePath $updateExe -WorkingDirectory $target -WindowStyle Hidden -PassThru -ArgumentList @('--headless','--log-file',('"'+$patchLog+'"'),'--','--verify-ui-update',('"'+$upgrade+'"'),('"'+$patch+'"'),('"'+$target+'"'))
    if(!$updater.WaitForExit(180000)){ $updater.Kill();throw 'Actual offline updater timed out.' }
    $updater.Refresh()
    if($updater.ExitCode -ne 0 -or !(Select-String -LiteralPath $patchLog -SimpleMatch 'GODOT_UPDATER_VERIFY_OK' -Quiet)){throw "Actual offline updater failed: $patchLog"}
    & dotnet $updateDll verify $upgrade $target
    if($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ne $sentinelHash){throw 'Offline update changed an unmanaged file or mismatched the complete build.'}
    $upgradedLog=Join-Path $upgrade 'verify-upgraded-game.log'
    $upgraded=Start-Process -FilePath (Join-Path $upgrade 'KardsUi.exe') -WorkingDirectory $upgrade -WindowStyle Hidden -PassThru -ArgumentList @('--headless','--log-file',('"'+$upgradedLog+'"'),'--','--verify-export')
    if(!$upgraded.WaitForExit(120000)){$upgraded.Kill();throw 'Upgraded game verification timed out.'}
    $upgraded.Refresh()
    if($upgraded.ExitCode -ne 0 -or !(Select-String -LiteralPath $upgradedLog -SimpleMatch 'EXPORT_VERIFY_OK' -Quiet)){throw "Upgraded EXE failed: $upgradedLog"}
    Write-Host "OFFLINE_UPGRADE_EXE_VERIFY_OK $upgrade"
  }
}
$distRoot = Join-Path $sourceRoot 'dist'
$delivery = Join-Path $distRoot "releases/$version$packageSuffix"
$fullDelivery = Join-Path $delivery 'full'
$patchDelivery = Join-Path $delivery 'patches'
$notesDelivery = Join-Path $delivery 'notes'
$verificationDelivery = Join-Path $delivery 'verification'
$updaterDelivery = Join-Path $distRoot "updaters/$UpdaterVersion"
New-Item -ItemType Directory -Path $fullDelivery,$patchDelivery,$notesDelivery,$verificationDelivery,$updaterDelivery -Force | Out-Null
Copy-Item -LiteralPath $zip -Destination $fullDelivery -Force
Copy-Item -LiteralPath $bootstrap -Destination $updaterDelivery -Force
if($patch){Copy-Item -LiteralPath $patch -Destination $patchDelivery -Force;Copy-Item -LiteralPath $patchLog -Destination (Join-Path $verificationDelivery 'verify-offline-update.log') -Force;Copy-Item -LiteralPath $upgradedLog -Destination (Join-Path $verificationDelivery 'verify-upgraded-game.log') -Force}
Copy-Item -LiteralPath $smokeLog -Destination (Join-Path $verificationDelivery 'verify-export.log') -Force
Copy-Item -LiteralPath $workshopLog -Destination (Join-Path $verificationDelivery 'verify-workshop.log') -Force
$deliveredZip = Join-Path $fullDelivery ([IO.Path]::GetFileName($zip))
$deliveredFiles=@($deliveredZip,(Join-Path $updaterDelivery ([IO.Path]::GetFileName($bootstrap))))
if($patch){$deliveredFiles+=Join-Path $patchDelivery ([IO.Path]::GetFileName($patch))}
# [IO.Path]::GetRelativePath 是 .NET Core 的 API，Windows PowerShell 5.1（.NET Framework）没有它——
# 这行曾在发版最后一步炸掉，前面 9 分钟的导出全白做。这里用 Substring 手算相对路径，5.1/7 都成立。
$deliveryPrefix=$delivery.TrimEnd('\','/')
$checksumLines=$deliveredFiles|ForEach-Object{
    $hash=Get-FileHash -LiteralPath $_ -Algorithm SHA256
    # 用 Uri.MakeRelativeUri 而不是 Substring：更新器在 delivery 之外（dist/updaters/…），
    # 手算会退化成绝对路径，而校验和必须相对本发行目录。两种位置都能得到正确相对路径。
    $relative=([Uri]($deliveryPrefix+'/')).MakeRelativeUri([Uri]$hash.Path).ToString().Replace('\','/')
    $hash.Hash+'  '+$relative
}
[IO.File]::WriteAllLines((Join-Path $delivery 'SHA256.txt'),[string[]]$checksumLines,[Text.UTF8Encoding]::new($false))
$fullBytes=(Get-Item -LiteralPath $zip).Length;$bootstrapBytes=(Get-Item -LiteralPath $bootstrap).Length;$patchBytes=if($patch){(Get-Item -LiteralPath $patch).Length}else{0}
@{version=$version;buildId=$buildInfo.BuildId;fullPackageBytes=$fullBytes;bootstrapBytes=$bootstrapBytes;patchBytes=$patchBytes;firstUpdateDownloadBytes=($bootstrapBytes+$patchBytes);reductionPercent=if($patch){[Math]::Round(100*(1-$patchBytes/[double]$fullBytes),2)}else{$null};baselineSha256=if($BaselineZip){$baseHash}else{$null};patch=if($patch){'patches/'+[IO.Path]::GetFileName($patch)}else{$null}}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $notesDelivery 'offline-update-report.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'write-dist-index.ps1') -DistRoot $distRoot -CurrentRelease "$version$packageSuffix"
Write-Host "EXPORTED_PLAYER_VERIFY_OK $smokeLog"
Write-Host "$(if($TestPackage){'TEST_PACKAGE_OK'}else{'RELEASE_OK'}) $deliveredZip"
