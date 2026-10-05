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
Write-Host "Prepared $targetRoot from $sourceRoot"
exit 0
