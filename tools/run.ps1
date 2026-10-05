param(
    [string]$BuildRoot = 'H:\g\kards',
    [string]$Godot = 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe',
    [switch]$Editor,
    [switch]$Test,
    [switch]$Verify,
    [switch]$VerifyEffects,
    [switch]$Capture
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'prepare.ps1') -BuildRoot $BuildRoot -SkipAssets
if (!(Test-Path -LiteralPath $Godot)) { throw 'Pass -Godot with the path to Godot 4.7 .NET.' }
Push-Location $BuildRoot
try {
    $probeOutput = & $Godot --headless --path $BuildRoot --script (Join-Path $BuildRoot 'tools\probe.gd') 2>&1
    if (($probeOutput -join "`n") -notmatch 'csharp=true') { throw 'Godot has no C# support. Use the .NET build.' }
    & dotnet build 'Kards.Ui.csproj' --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Test) {
        & dotnet test 'tests\Kards.Ui.Tests.csproj' --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    & $Godot --headless --editor --path $BuildRoot --quit
    if ($LASTEXITCODE -ne 0) { throw 'Godot import failed.' }
    if ($VerifyEffects) { & $Godot --headless --path $BuildRoot -- --verify-ui --verify-effects }
    elseif ($Verify) { & $Godot --headless --path $BuildRoot -- --verify-ui }
    elseif ($Capture) { & $Godot --path $BuildRoot -- --capture-ui }
    elseif ($Editor) { & $Godot --editor --path $BuildRoot }
    else { & $Godot --path $BuildRoot }
    if ($LASTEXITCODE -ne 0) { throw "Godot exited with code $LASTEXITCODE" }
} finally { Pop-Location }
