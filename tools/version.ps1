param(
    [switch]$Get,
    [string]$Set
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$versionFile = Join-Path $root 'VERSION'
$projectFile = Join-Path $root 'project.godot'
$pattern = '^\d+\.\d+\.\d+(-alpha\.\d+|-hotfix(\.\d+)?)?$'

function Get-Version {
    if (!(Test-Path -LiteralPath $versionFile)) { throw "Missing $versionFile" }
    return (Get-Content -LiteralPath $versionFile -Raw).Trim()
}

function Set-Version([string]$value) {
    if ($value -notmatch $pattern) {
        throw "版本号格式无效：$value （应为 MAJOR.MINOR.PATCH，可加 -alpha.N 或 -hotfix[.N]）"
    }
    Set-Content -LiteralPath $versionFile -Value $value -Encoding utf8
    $lines = Get-Content -LiteralPath $projectFile
    $replaced = $false
    $output = foreach ($line in $lines) {
        if ($line -match '^config/version=') { $replaced = $true; "config/version=`"$value`"" }
        else { $line }
    }
    if (!$replaced) {
        $output = @()
        $inApplication = $false
        foreach ($line in $lines) {
            $output += $line
            if ($line -match '^\[application\]') { $inApplication = $true; continue }
            if ($inApplication) { $output += "config/version=`"$value`""; $inApplication = $false }
        }
    }
    Set-Content -LiteralPath $projectFile -Value $output -Encoding utf8
    Write-Host "Version set to $value"
}

if ($Set) { Set-Version $Set.Trim() }
else { Get-Version }
exit 0
