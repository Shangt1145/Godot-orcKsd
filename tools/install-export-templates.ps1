param(
    [string]$Godot = 'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe',
    [string]$Archive = 'H:\g\downloads\Godot_v4.7-stable_mono_export_templates.tpz'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$version = (& $Godot --version | Select-Object -First 1).Trim()
if ($version -notmatch '^(\d+\.\d+(?:\.\d+)?)\.([^\.]+)\.mono\.') { throw "Not a supported .NET Godot build: $version" }
$tag = "$($Matches[1])-$($Matches[2])"
$expected = "$($Matches[1]).$($Matches[2]).mono"
$destination = Join-Path $env:APPDATA "Godot/export_templates/$expected"
$files = @('version.txt','windows_release_x86_64.exe','windows_release_x86_64_console.exe','windows_debug_x86_64.exe','windows_debug_x86_64_console.exe')
if ($files.Where({!(Test-Path -LiteralPath (Join-Path $destination $_))}).Count -eq 0) { Write-Host "TEMPLATES_OK $destination"; exit 0 }
if (!(Test-Path -LiteralPath $Archive)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Archive) -Force | Out-Null
    $url = "https://github.com/godotengine/godot-builds/releases/download/$tag/Godot_v${tag}_mono_export_templates.tpz"
    & curl.exe --ssl-revoke-best-effort -L --fail --retry 2 --connect-timeout 20 -o ($Archive+'.download') $url
    if ($LASTEXITCODE -ne 0) { throw 'Official export template download failed.' }
    Move-Item -LiteralPath ($Archive+'.download') -Destination $Archive -Force
}
$zip = [IO.Compression.ZipFile]::OpenRead($Archive)
try {
    $reader = [IO.StreamReader]::new($zip.GetEntry('templates/version.txt').Open())
    try { $actual = $reader.ReadToEnd().Trim() } finally { $reader.Dispose() }
    if ($actual -ne $expected) { throw "Template version mismatch: expected $expected, got $actual" }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in $files) {
        $entry = $zip.GetEntry('templates/'+$file)
        if (!$entry) { throw "Missing template: $file" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $destination $file), $true)
    }
}
finally { $zip.Dispose() }
Write-Host "TEMPLATES_OK $destination"
