param(
    [switch]$Get,
    [string]$Set
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$versionFile = Join-Path $root 'VERSION'
$projectFile = Join-Path $root 'project.godot'
$pattern = '^(\d+\.\d+\.\d+(-(alpha|beta)\.\d+|-hotfix(\.\d+)?)?|0\.0\.0beta[1-9]\d*\.hotfix(\.[1-9]\d*)?)$'

function Get-Version {
    if (!(Test-Path -LiteralPath $versionFile)) { throw "Missing $versionFile" }
    return (Get-Content -LiteralPath $versionFile -Raw).Trim()
}

function Set-Version([string]$value) {
    if ($value -notmatch $pattern) {
        throw "版本号格式无效：$value （应为 MAJOR.MINOR.PATCH，可加 -alpha.N / -beta.N / -hotfix[.N]；预发布热修复可用 0.0.0betaN.hotfix[.N]）"
    }
    # 预发布不占号：alpha/beta 期间三位数字必须恒为 0.0.0，首个正式版才是 0.1.0。
    if ($value -match '-(alpha|beta)\.\d+$' -and $value -notmatch '^0\.0\.0-') {
        throw "alpha/beta 不占用版本号：三位数字必须恒为 0.0.0（例如 0.0.0-beta.1）"
    }
    if ($value -match '-hotfix' -and $value -match '^0\.0\.0') {
        throw "hotfix 用于已发布版本：0.0.0 尚未发布，不能有 hotfix"
    }
    # PS5.1 的 -Encoding utf8 会写 BOM，Godot 读到 BOM 会把文件解析坏（历史事故：
    # project.godot 的中文项目名被写崩）。统一用无 BOM 的 UTF-8 写。
    [IO.File]::WriteAllText($versionFile, $value, [Text.UTF8Encoding]::new($false))
    # 必须按 UTF-8 读：PS5.1 的 Get-Content 默认走 ANSI，中文项目名会先被读坏再写回去（历史事故）。
    $lines = [IO.File]::ReadAllLines($projectFile, [Text.UTF8Encoding]::new($false))
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
    [IO.File]::WriteAllLines($projectFile, [string[]]$output, [Text.UTF8Encoding]::new($false))
    Write-Host "Version set to $value"
}

if ($Set) { Set-Version $Set.Trim() }
else { Get-Version }
exit 0
