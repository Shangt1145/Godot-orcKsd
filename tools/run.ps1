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
    # Godot's build copies project assemblies only, but the engine's scripting satellite
    # (Orc.Script) needs its Roslyn runtime closure next to the game assembly. Publish the bridge
    # once to resolve the full closure and sync whatever the Godot output is missing.
    $assemblyDir = Join-Path $BuildRoot '.godot\mono\temp\bin\Debug'
    $runtimeOut = Join-Path $env:TEMP 'kards-ui-runtime'
    & dotnet publish (Join-Path $PSScriptRoot '..\proto\bridge\Kards.Ui.OrcBridge\Kards.Ui.OrcBridge.csproj') -c Debug -o $runtimeOut --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Runtime closure publish failed.' }
    Get-ChildItem $runtimeOut -Filter '*.dll' |
        Where-Object { -not (Test-Path (Join-Path $assemblyDir $_.Name)) } |
        Copy-Item -Destination $assemblyDir -Force
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
