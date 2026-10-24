param(
    [string]$TargetDir='H:\g\kards\dist\windows',
    [ValidateRange(1,1000)][int]$ThroughBeta=5,
    [string[]]$SearchRoots=@((Join-Path $PSScriptRoot '../dist/releases'),'H:\g\baselines')
)
$ErrorActionPreference='Stop'
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$TargetDir=[IO.Path]::GetFullPath($TargetDir)
$target=Get-Content -LiteralPath (Join-Path $TargetDir '.orcksd-build.json') -Raw|ConvertFrom-Json
$version=(Get-Content -LiteralPath (Join-Path $sourceRoot 'VERSION') -Raw).Trim()
if($target.Version -ne $version){throw 'Exported target version does not match VERSION.'}
$baselines=@(& (Join-Path $PSScriptRoot 'find-update-baselines.ps1') -SearchRoots $SearchRoots -ThroughBeta $ThroughBeta)
$workspace=Join-Path $sourceRoot ('artifacts/compatible-update-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace|Out-Null
$cliProject=Join-Path $sourceRoot 'tools/OfflineUpdate/OrC.Update.Cli'
& dotnet build $cliProject --nologo -v:q
if($LASTEXITCODE -ne 0){throw 'Update CLI build failed.'}
$cli=Join-Path $cliProject 'bin/Debug/net9.0/OrC.Update.Cli.dll'
$parts=@();$sources=@();$seen=@{}
foreach($baseline in $baselines){
    $part=Join-Path $workspace ('part-'+$parts.Count+'.orcupdate')
    & dotnet $cli build $baseline.Path $TargetDir $part $baseline.Version $version
    if($LASTEXITCODE -ne 0){throw "Patch generation failed: $($baseline.Path)"}
    $info=(& dotnet $cli inspect $part)|ConvertFrom-Json
    if($LASTEXITCODE -ne 0){throw 'Generated patch inspection failed.'}
    if($info.Target.BuildId -ne $target.BuildId){throw 'Target changed during compatibility build.'}
    if(!$seen.ContainsKey($info.Source.BuildId)){
        $seen[$info.Source.BuildId]=$true;$parts+=$part
        $sources+=[PSCustomObject]@{Path=$baseline.Path;Version=$baseline.Version;BuildId=$info.Source.BuildId}
    }
}
$name="OrC-KSD-$version-compatible-through-beta$ThroughBeta-to-$($target.BuildId.Substring(0,12)).orcupdate"
$combined=Join-Path $workspace $name
& dotnet $cli combine $combined @parts
if($LASTEXITCODE -ne 0){throw 'Compatible patch combine failed.'}
$results=@()
for($i=0;$i -lt $sources.Count;$i++){
    $baseline=$sources[$i]
    $install=Join-Path $workspace ('install-'+$baseline.BuildId.Substring(0,12))
    [IO.Compression.ZipFile]::ExtractToDirectory($baseline.Path,$install)
    $sentinel=Join-Path $install '玩家自己的文件.txt'
    [IO.File]::WriteAllText($sentinel,'preserve me',[Text.UTF8Encoding]::new($false))
    $sentinelHash=(Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
    $updaterLog=Join-Path $workspace ($baseline.BuildId.Substring(0,12)+'-updater.log')
    $updater=Start-Process -FilePath (Join-Path $TargetDir '更新游戏.exe') -WorkingDirectory $TargetDir -WindowStyle Hidden -PassThru -ArgumentList @('--headless','--log-file',('"'+$updaterLog+'"'),'--','--verify-ui-update',('"'+$install+'"'),('"'+$combined+'"'),('"'+$TargetDir+'"'))
    if(!$updater.WaitForExit(60000)){$updater.Kill();throw "Updater timed out: $($baseline.BuildId)"}
    $updater.Refresh()
    if($updater.ExitCode -ne 0 -or !(Select-String -LiteralPath $updaterLog -SimpleMatch 'GODOT_UPDATER_VERIFY_OK' -Quiet)){throw "Updater verification failed: $updaterLog"}
    & dotnet $cli verify $install $TargetDir
    if($LASTEXITCODE -ne 0){throw "Exact file verification failed: $install"}
    if((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ne $sentinelHash){throw 'An unmanaged player file changed.'}
    $gameLog=Join-Path $workspace ($baseline.BuildId.Substring(0,12)+'-game.log')
    $game=Start-Process -FilePath (Join-Path $install 'KardsUi.exe') -WorkingDirectory $install -WindowStyle Hidden -PassThru -ArgumentList @('--headless','--log-file',('"'+$gameLog+'"'),'--','--verify-export')
    if(!$game.WaitForExit(60000)){$game.Kill();throw "Upgraded game timed out: $install"}
    $game.Refresh()
    if($game.ExitCode -ne 0 -or !(Select-String -LiteralPath $gameLog -SimpleMatch 'EXPORT_VERIFY_OK' -Quiet)){throw "Upgraded EXE failed: $gameLog"}
    $results+=[PSCustomObject]@{version=$baseline.Version;sourceBuild=$baseline.BuildId;targetBuild=$target.BuildId;updaterVerified=$true;exactFiles=$true;exeVerified=$true;playerFilePreserved=$true;install=$install;updaterLog=$updaterLog;gameLog=$gameLog}
    Write-Host "COMPATIBLE_SOURCE_VERIFIED $($i+1)/$($sources.Count) $($baseline.Version) $($baseline.BuildId.Substring(0,12))"
}
# Publish only after every source has passed the real updater and upgraded executable.
$delivery=Join-Path $sourceRoot "dist/releases/$version"
$patches=Join-Path $delivery 'patches';$notes=Join-Path $delivery 'notes';$verification=Join-Path $delivery 'verification'
New-Item -ItemType Directory -Path $patches,$notes,$verification -Force|Out-Null
$delivered=Join-Path $patches $name
Copy-Item -LiteralPath $combined -Destination $delivered -Force
$results|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $verification 'compatible-update-verification.json') -Encoding utf8
Get-ChildItem -LiteralPath $workspace -File -Filter '*.log'|Copy-Item -Destination $verification -Force
@{version=$version;targetBuild=$target.BuildId;throughBeta=$ThroughBeta;sources=@($sources|Select-Object Version,BuildId);sourceCount=$sources.Count;patch='patches/'+$name;patchBytes=(Get-Item -LiteralPath $delivered).Length;sha256=(Get-FileHash -LiteralPath $delivered -Algorithm SHA256).Hash;workspace=$workspace}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $notes 'compatible-update-report.json') -Encoding utf8
$deliveryPrefix=$delivery.TrimEnd('\','/')
# 同 release.ps1：[IO.Path]::GetRelativePath 在 Windows PowerShell 5.1 下不存在（.NET Framework）。
$checksumLines=Get-ChildItem -LiteralPath (Join-Path $delivery 'full'),$patches -File|ForEach-Object{$hash=Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256;$relative=$hash.Path;if($relative.StartsWith($deliveryPrefix,[StringComparison]::OrdinalIgnoreCase)){$relative=$relative.Substring($deliveryPrefix.Length).TrimStart('\','/')};$hash.Hash+'  '+$relative.Replace('\','/')}
[IO.File]::WriteAllLines((Join-Path $delivery 'SHA256.txt'),[string[]]$checksumLines,[Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'write-dist-index.ps1') -CurrentRelease $version
Write-Host "COMPATIBLE_UPDATE_OK sources=$($sources.Count) $delivered"
