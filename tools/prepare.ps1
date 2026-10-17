param(
    [string]$BuildRoot = 'H:\g\kards',
    [string]$ReferenceRoot = 'H:\Working Folder\Kards-Desktop\dist\win-unpacked\resources\app',
    [switch]$SkipAssets
)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$targetRoot = [IO.Path]::GetFullPath($BuildRoot)
if ($sourceRoot -eq $targetRoot) { throw 'BuildRoot must differ from the source workspace.' }
if ($targetRoot -match 'Working Folder') { throw 'Use a short build directory outside Working Folder.' }
New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
$marker = Join-Path $targetRoot '.kards-ui-workspace'
if ((Test-Path -LiteralPath $marker) -and (Get-Content -LiteralPath $marker -Raw).Trim() -ne $sourceRoot) {
    throw 'BuildRoot belongs to another source workspace.'
}
if (!(Test-Path -LiteralPath $marker) -and (Test-Path -LiteralPath (Join-Path $targetRoot 'project.godot'))) {
    throw 'Existing unmanaged Godot project; choose a different BuildRoot.'
}
Set-Content -LiteralPath $marker -Value $sourceRoot -Encoding utf8
if (!$SkipAssets) {
    $artRoot = Join-Path $sourceRoot 'proto\art'
    $sfxRoot = Join-Path $sourceRoot 'proto\sfx'
    $dataRoot = Join-Path $sourceRoot 'proto\data\nations'
    New-Item -ItemType Directory -Path $artRoot,$sfxRoot,$dataRoot -Force | Out-Null
    foreach ($folder in @('USG','UN','av76','deran','星盟','牌！Q!!!','自定义')) {
        $artSource = Join-Path $ReferenceRoot $folder
        if (!(Test-Path -LiteralPath $artSource)) { throw "Missing art source: $artSource" }
        Copy-Item -LiteralPath $artSource -Destination $artRoot -Recurse -Force
    }
    Get-ChildItem -LiteralPath $ReferenceRoot -Filter '*.png' | Copy-Item -Destination $artRoot -Force
    Get-ChildItem -LiteralPath (Join-Path $ReferenceRoot 'game\data\nations') -Filter '*.json' |
        Copy-Item -Destination $dataRoot -Force
    Get-ChildItem -LiteralPath (Join-Path $ReferenceRoot 'game\assets\sfx') -Filter '*.mp3' |
        Copy-Item -Destination $sfxRoot -Force
    $manifest = [ordered]@{
        referenceRoot = $ReferenceRoot
        cardImages = @(Get-ChildItem -LiteralPath $artRoot -Recurse -Filter '*.png').Count
        soundFiles = @(Get-ChildItem -LiteralPath $sfxRoot -Filter '*.mp3').Count
        nationFiles = @(Get-ChildItem -LiteralPath $dataRoot -Filter '*.json').Count
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $sourceRoot 'proto\data\asset-manifest.json') -Encoding utf8
}
# Non-destructive synchronization. Generated output never copies back into source.
& robocopy $sourceRoot $targetRoot /E /NFL /NDL /NJH /NJS /NP /XD .git .godot bin obj artifacts /XF '*.user' '.kards-ui-workspace'
if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $LASTEXITCODE" }
# Package data is generated content. Remove obsolete copies left by the old directory
# layout so exports contain exactly the source card packs, without touching other assets.
$packSource = [IO.Path]::GetFullPath((Join-Path $sourceRoot 'proto/data/kards'))
$packTarget = [IO.Path]::GetFullPath((Join-Path $targetRoot 'proto/data/kards'))
if (!$packTarget.StartsWith($targetRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package sync escaped build workspace.' }
if ((Get-Content -LiteralPath $marker -Raw).Trim() -ne $sourceRoot) { throw 'Package cleanup requires the matching managed workspace marker.' }
if ((Test-Path -LiteralPath $packSource) -and (Test-Path -LiteralPath $packTarget)) {
    $packPrefix = $packTarget.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Get-ChildItem -LiteralPath $packTarget -File -Recurse | ForEach-Object {
        $packFile = [IO.Path]::GetFullPath($_.FullName)
        if (!$packFile.StartsWith($packPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package file escaped build workspace.' }
        # Windows PowerShell 5.1 uses .NET Framework, which has no Path.GetRelativePath.
        $relative = $packFile.Substring($packPrefix.Length)
        if (!(Test-Path -LiteralPath (Join-Path $packSource $relative))) { Remove-Item -LiteralPath $_.FullName -Force }
    }
    Get-ChildItem -LiteralPath $packTarget -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
        if (!(Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $_.FullName -Force }
    }
}
# Retired authoring web tools must not survive the non-destructive source sync.
foreach ($obsoleteRelative in @(
    'proto/bridge/Kards.Ui.OrcBridge/DesktopInspectorSession.cs',
    'proto/bridge/Kards.Ui.OrcBridge/DesktopAuthoring/host.cjs',
    'proto/bridge/Kards.Ui.OrcBridge/DesktopAuthoring/index.html',
    'proto/bridge/Kards.Ui.OrcBridge/DesktopAuthoring/workbench.js',
    'tools/check-desktop-authoring.cjs',
    'tools/AuthoringPreview/Program.cs',
    'tools/AuthoringPreview/AuthoringPreview.csproj'
)) {
    $obsoleteAuthoringFile = [IO.Path]::GetFullPath((Join-Path $targetRoot $obsoleteRelative))
    if (!$obsoleteAuthoringFile.StartsWith($targetRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected obsolete source path.' }
    if (!(Test-Path -LiteralPath (Join-Path $sourceRoot $obsoleteRelative)) -and (Test-Path -LiteralPath $obsoleteAuthoringFile)) {
        Remove-Item -LiteralPath $obsoleteAuthoringFile
    }
}
Write-Host "Prepared $targetRoot from $sourceRoot"
exit 0
