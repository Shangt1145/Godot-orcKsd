param(
    [string]$BuildRoot = 'H:\g\kards',
    [string]$Godot = (Join-Path $PSScriptRoot 'godot-wait.ps1'),
    [string]$Preset = 'Windows Desktop',
    [string]$OutDir = 'dist\windows',
    [switch]$SkipGate,
    [switch]$FullTests,
    [string]$BaselineZip='',
    [string]$BaselineVersion='',
    [string]$UpdaterBuildRoot='H:\g\orc-offline-update'
)
# Builds the player-facing package.
#
# The engine is wired in as a *source* reference (OrcEngineRoot -> another checkout), so the one
# thing that could silently go wrong here is an export that ships without Orc.Game.dll: it would
# build fine, run fine on this machine, and only fail on a player's. That is what the postflight
# below exists to catch.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (!(Test-Path -LiteralPath $Godot)) { throw "Godot not found: $Godot" }

# 1) Preflight: a C# project needs the .NET ("mono") export templates, not the plain ones.
#    Without them the export dies with "Cannot export project ... due to configuration errors",
#    which says nothing about the real cause.
$sourceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
$version = (Get-Content -LiteralPath (Join-Path $sourceRoot 'VERSION') -Raw).Trim()
# Preserve the previously delivered archive before publishing another build with the same display version.
if(!$BaselineZip){$candidate=Join-Path $sourceRoot "dist/OrC-KSD-$version-win-x64.zip";if(Test-Path -LiteralPath $candidate){$BaselineZip=$candidate}}
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
    $archiveDir=Join-Path $sourceRoot 'artifacts/update-baselines';New-Item -ItemType Directory $archiveDir -Force|Out-Null
    $savedBaseline=Join-Path $archiveDir ($baseHash+'.zip')
    if(!(Test-Path -LiteralPath $savedBaseline)){Copy-Item -LiteralPath $BaselineZip -Destination $savedBaseline}
    $BaselineZip=$savedBaseline
}
& (Join-Path $PSScriptRoot 'install-export-templates.ps1') -Godot $Godot
if ($LASTEXITCODE -ne 0) { throw 'Template setup failed.' }
& (Join-Path $PSScriptRoot 'prepare.ps1') -BuildRoot $BuildRoot -SkipAssets
if (!(Test-Path -LiteralPath (Join-Path $BuildRoot 'Kards.Ui.sln'))) { throw 'Missing Kards.Ui.sln: Godot cannot export C# without a solution.' }

# 2) Gate: the sync + build + unit tests + headless verify the project already trusts.
if (!$SkipGate) {
    $testArgs = @('test', (Join-Path $sourceRoot 'tests/Kards.Ui.Tests.csproj'), '--nologo')
    if (!$FullTests) { $testArgs += @('--filter', 'FullyQualifiedName~WorkshopTests|FullyQualifiedName~DeckPackTests|FullyQualifiedName~DeckConstructionTests|FullyQualifiedName~DiyCardTests|FullyQualifiedName~DiyInspectorTests|FullyQualifiedName~SourceEffectConverterTests|FullyQualifiedName~DesktopAuthoringTests|FullyQualifiedName~NativeScriptHostIsolationTests|FullyQualifiedName~OfficialCardExpansionTests|FullyQualifiedName~OfflineUpdateTests') }
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
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
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
}
$bootstrap=Join-Path (Split-Path -Parent $target) 'OrC-KSD-offline-updater-1.0.0-win-x64.zip'
if(Test-Path -LiteralPath $bootstrap){Remove-Item -LiteralPath $bootstrap -Force}
Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $updaterOutput|Select-Object -ExpandProperty FullName) -DestinationPath $bootstrap
$zip = Join-Path (Split-Path -Parent $target) "OrC-KSD-$version-win-x64.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
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
    $upgrade=Join-Path ([IO.Path]::GetTempPath()) ('OrC 离线升级 验证 '+[Guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $BaselineZip -DestinationPath $upgrade
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
$delivery = Join-Path $sourceRoot 'dist'
New-Item -ItemType Directory -Path $delivery -Force | Out-Null
Copy-Item -LiteralPath $zip -Destination $delivery -Force
Copy-Item -LiteralPath $bootstrap -Destination $delivery -Force
if($patch){Copy-Item -LiteralPath $patch -Destination $delivery -Force;Copy-Item -LiteralPath $patchLog -Destination (Join-Path $delivery 'verify-offline-update.log') -Force;Copy-Item -LiteralPath $upgradedLog -Destination (Join-Path $delivery 'verify-upgraded-game.log') -Force}
Copy-Item -LiteralPath $smokeLog -Destination (Join-Path $delivery 'verify-export.log') -Force
Copy-Item -LiteralPath $workshopLog -Destination (Join-Path $delivery 'verify-workshop.log') -Force
$deliveredZip = Join-Path $delivery ([IO.Path]::GetFileName($zip))
$deliveredFiles=@($deliveredZip,(Join-Path $delivery ([IO.Path]::GetFileName($bootstrap))))
if($patch){$deliveredFiles+=Join-Path $delivery ([IO.Path]::GetFileName($patch))}
Get-FileHash -LiteralPath $deliveredFiles -Algorithm SHA256 | Format-List | Out-File (Join-Path $delivery 'SHA256.txt') -Encoding utf8
$fullBytes=(Get-Item -LiteralPath $zip).Length;$bootstrapBytes=(Get-Item -LiteralPath $bootstrap).Length;$patchBytes=if($patch){(Get-Item -LiteralPath $patch).Length}else{0}
@{version=$version;buildId=$buildInfo.BuildId;fullPackageBytes=$fullBytes;bootstrapBytes=$bootstrapBytes;patchBytes=$patchBytes;firstUpdateDownloadBytes=($bootstrapBytes+$patchBytes);reductionPercent=if($patch){[Math]::Round(100*(1-$patchBytes/[double]$fullBytes),2)}else{$null};baselineSha256=if($BaselineZip){$baseHash}else{$null};patch=if($patch){[IO.Path]::GetFileName($patch)}else{$null}}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $delivery 'offline-update-report.json') -Encoding utf8
Write-Host "EXPORTED_PLAYER_VERIFY_OK $smokeLog"
Write-Host "RELEASE_OK $deliveredZip"
