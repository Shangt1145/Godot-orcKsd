param(
    [string]$BuildRoot = 'H:\g\kards',
    [string]$Godot = 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe',
    [string]$Preset = 'Windows Desktop',
    [string]$OutDir = 'dist\windows',
    [switch]$SkipGate
)
# Builds the player-facing package.
#
# The engine is wired in as a *source* reference (OrcEngineRoot -> another checkout), so the one
# thing that could silently go wrong here is an export that ships without Orc.Game.dll: it would
# build fine, run fine on this machine, and only fail on a player's. That is what the postflight
# below exists to catch.
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Godot)) { throw "Godot not found: $Godot" }

# 1) Preflight: a C# project needs the .NET ("mono") export templates, not the plain ones.
#    Without them the export dies with "Cannot export project ... due to configuration errors",
#    which says nothing about the real cause.
$templateRoot = Join-Path $env:APPDATA 'Godot\export_templates'
$mono = if (Test-Path -LiteralPath $templateRoot) {
    @(Get-ChildItem -LiteralPath $templateRoot -Directory -Filter '*.mono')
} else { @() }
if ($mono.Count -eq 0) {
    throw @"
.NET export templates are missing.

  looked in : $templateRoot
  Godot wants: <version>.mono\windows_release_x86_64.exe

Install from the editor: Editor > Manage Export Templates > Download.
The plain (non-mono) templates do not work -- this project's scripts are C#.
"@
}
Write-Host "templates: $($mono[0].Name)"

# 2) Gate: the sync + build + unit tests + headless verify the project already trusts.
if (!$SkipGate) {
    & (Join-Path $PSScriptRoot 'run.ps1') -Test -Verify
    if ($LASTEXITCODE -ne 0) { throw "Gate failed (run.ps1 -Test -Verify), exit $LASTEXITCODE" }
}

# 3) Export.
$target = Join-Path $BuildRoot $OutDir
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
New-Item -ItemType Directory -Path $target -Force | Out-Null
$exe = Join-Path $target 'KardsUi.exe'
& $Godot --headless --path $BuildRoot --export-release $Preset $exe
if ($LASTEXITCODE -ne 0) { throw "Export failed, exit $LASTEXITCODE" }
if (!(Test-Path -LiteralPath $exe)) { throw "Export reported success but wrote no exe." }

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

# 5) Package.
$zip = Join-Path (Split-Path -Parent $target) 'KardsUi-win-x64.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $target '*') -DestinationPath $zip
Write-Host "RELEASE_OK $zip"
