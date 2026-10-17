param(
    [string]$BuildRoot='H:\g\orc-offline-update',
    [string]$Godot=(Join-Path $PSScriptRoot 'godot-wait.ps1')
)
$ErrorActionPreference='Stop'
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'OfflineUpdate'))
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
if($BuildRoot -eq $source -or $BuildRoot.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use a separate managed updater build workspace.'}
$marker=Join-Path $BuildRoot '.orcksd-updater-workspace'
if((Test-Path -LiteralPath $BuildRoot) -and !(Test-Path -LiteralPath $marker) -and (Get-ChildItem -LiteralPath $BuildRoot -Force|Select-Object -First 1)){throw 'Existing directory is not an updater build workspace.'}
if((Test-Path -LiteralPath $marker) -and (Get-Content $marker -Raw).Trim() -ne $source){throw 'Updater workspace belongs to another source.'}
New-Item -ItemType Directory $BuildRoot -Force|Out-Null
Set-Content -LiteralPath $marker -Value $source -Encoding utf8
& robocopy $source $BuildRoot /E /NFL /NDL /NJH /NJS /NP /XD .godot bin obj dist .git
if($LASTEXITCODE -ge 8){throw 'Updater workspace sync failed.'}
$project=Join-Path $BuildRoot 'OrC.Updater'
$target=[IO.Path]::GetFullPath((Join-Path $BuildRoot 'dist/current'))
if(!$target.StartsWith($BuildRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-Content $marker -Raw).Trim() -ne $source){throw 'Unsafe updater export directory.'}
if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Recurse -Force}
New-Item -ItemType Directory $target -Force|Out-Null
& dotnet build (Join-Path $project 'OrC.Updater.csproj') --nologo -v:q
if($LASTEXITCODE -ne 0){throw 'Updater build failed.'}
& $Godot --headless --editor --path $project --quit
if($LASTEXITCODE -ne 0){throw 'Updater resource import failed.'}
& $Godot --headless --path $project --export-release 'Windows Desktop' (Join-Path $target '更新游戏.exe')
if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath (Join-Path $target '更新游戏.pck'))){throw 'Updater export failed.'}
foreach($name in @('OrC.Updater.dll','OrC.Update.Core.dll','hostfxr.dll')){
    if(!(Get-ChildItem -LiteralPath $target -Recurse -Filter $name|Select-Object -First 1)){throw "Updater runtime missing: $name"}
}
Write-Host "UPDATER_BUILD_OK $target"
@'
OrC-KSD 离线更新

首次更新：
1. 完整解压此 ZIP，保留更新游戏.exe、更新游戏.pck 和 data_OrC.Updater_windows_x86_64 文件夹。
2. 打开“更新游戏.exe”，选择已有 KardsUi.exe 所在的游戏文件夹。
3. 选择下载的 .orcupdate 补丁，点击“更新”。先保存进度并关闭游戏窗口。
4. 完成后点击“启动游戏”。卡组、DIY 卡、导入卡图与设置会保留。

更新器会自动安装到游戏文件夹。以后只下载对应补丁，从游戏中的“离线更新”进入即可。
补丁必须匹配来源构建；同为 Beta.1 也可能是不同构建，请按更新器显示的编号选择补丁。
更新失败或中断时重新打开更新器恢复；更新成功后可以“回退上一版”。
全程离线，无需安装 Godot 或 .NET SDK。
'@ | Set-Content -LiteralPath (Join-Path $target '离线更新说明.txt') -Encoding utf8
