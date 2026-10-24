# 发行一致性核对：把所有"同一个事实的多份副本"一次性对完，输出红绿表。
#
#   powershell -File tools/check-consistency.ps1            # 只核对
#   powershell -File tools/check-consistency.ps1 -WriteIndex # 核对并由机器生成 RELEASE_INDEX.md
#
# 退出码：0＝全绿（可以发版/收工）；1＝有红项（先修再走）。
# 规则定义见 docs/release/VERSIONING.md「发行规则」一节。
param(
    [switch]$WriteIndex,
    [string]$DistRoot = 'dist\releases',
    [int]$MaxPatchMb = 150,
    [int]$MaxFullMb = 1400
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$distPath = if ([IO.Path]::IsPathRooted($DistRoot)) { $DistRoot } else { Join-Path $root $DistRoot }

$red = New-Object System.Collections.ArrayList
$warn = New-Object System.Collections.ArrayList
function Ok([string]$label, [string]$detail) { Write-Host ("  [OK ] {0,-22} {1}" -f $label, $detail) }
function Bad([string]$label, [string]$detail) { Write-Host ("  [RED] {0,-22} {1}" -f $label, $detail); [void]$red.Add("$label：$detail") }
function Warn([string]$label, [string]$detail) { Write-Host ("  [WARN] {0,-21} {1}" -f $label, $detail); [void]$warn.Add("$label：$detail") }

$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
Write-Host "发行一致性核对  仓库=$root"
Write-Host "VERSION = $version"
Write-Host ''

# ── R1 发行目录清单 ────────────────────────────────────────────────
if (!(Test-Path -LiteralPath $distPath)) { throw "找不到发行目录：$distPath" }
$dirs = @(Get-ChildItem -LiteralPath $distPath -Directory | Sort-Object Name)
if ($dirs.Count -eq 0) { throw "发行目录为空：$distPath" }
$latest = $dirs[-1].Name

# ── R2 VERSION 与最新发行目录一致 ─────────────────────────────────
if ($latest -eq $version) { Ok 'VERSION=最新目录' $version }
else { Bad 'VERSION=最新目录' "VERSION=$version 但最新发行目录=$latest" }

# ── R3 该版本有 tag ───────────────────────────────────────────────
$currentTag = "v$version"
$tagCommit = (& git -C $root rev-parse -q --verify "refs/tags/$currentTag^{commit}" 2>$null)
if ($tagCommit) {
    $headCommit = (& git -C $root rev-parse HEAD 2>$null)
    if ($headCommit -and $tagCommit -eq $headCommit) { Ok "tag $currentTag" "$($tagCommit.Substring(0,7))（= HEAD）" }
    else {
        # tag 之后有新提交是正常的（文档、后续修复）；只有"tag 与当前历史分叉"才算错。
        & git -C $root merge-base --is-ancestor $tagCommit $headCommit 2>$null
        if ($LASTEXITCODE -eq 0) {
            $ahead = (& git -C $root rev-list --count "$tagCommit..$headCommit" 2>$null)
            Ok "tag $currentTag" "$($tagCommit.Substring(0,7))（HEAD 领先 $ahead 个提交）"
        }
        else { Bad "tag $currentTag" "$($tagCommit.Substring(0,7)) 与当前分支已分叉——交付来源无法回溯" }
    }
}
else { Bad "tag $currentTag" '缺失——交付过的东西在 git 里没有锚点' }

# ── R4 README 顶部"当前发行"与 VERSION 一致 ────────────────────────
# 必须按 UTF-8 读：PS5.1 的 Get-Content 默认走 ANSI，中文会被读坏导致永远匹配不上。
$readmePath = Join-Path $root 'README.md'
if (Test-Path -LiteralPath $readmePath) {
    $readmeHead = ([IO.File]::ReadAllLines($readmePath, [Text.UTF8Encoding]::new($false)) | Select-Object -First 40) -join "`n"
    if ($readmeHead -match '\*\*当前发行：([0-9A-Za-z\.\-]+)') {
        $readmeVersion = $Matches[1]
        if ($readmeVersion -eq $version) { Ok 'README 当前发行' $readmeVersion }
        else { Bad 'README 当前发行' "README=$readmeVersion 但 VERSION=$version" }
    }
    else { Warn 'README 当前发行' 'README 顶部没有"当前发行：<版本>"' }
}

# ── R5 逐版本清点结构 / 体积 / tag ────────────────────────────────
Write-Host ''
Write-Host '历史发行：'
$index = New-Object System.Collections.ArrayList
foreach ($dir in $dirs) {
    $name = $dir.Name
    $full = @(Get-ChildItem -LiteralPath $dir.FullName -Recurse -Filter '*.zip' -File -ErrorAction SilentlyContinue)
    $patches = @(Get-ChildItem -LiteralPath $dir.FullName -Recurse -Filter '*.orcupdate' -File -ErrorAction SilentlyContinue)
    $hasNotes = Test-Path -LiteralPath (Join-Path $dir.FullName 'notes')
    $hasVerify = Test-Path -LiteralPath (Join-Path $dir.FullName 'verification')
    $hasSha = Test-Path -LiteralPath (Join-Path $dir.FullName 'SHA256.txt')
    $hasTag = [bool](& git -C $root rev-parse -q --verify "refs/tags/v$name" 2>$null)

    $fullMb = 0; if ($full.Count -gt 0) { $fullMb = [math]::Round(($full | Measure-Object Length -Sum).Sum / 1MB) }
    $patchMb = 0; if ($patches.Count -gt 0) { $patchMb = [math]::Round((($patches | Measure-Object Length -Sum).Sum / 1MB)) }

    $flags = New-Object System.Collections.ArrayList
    if (!$hasTag) { [void]$flags.Add('无tag') }
    # 热修复按规则不发全量包（见 VERSIONING「热修复」）：缺 full/ 但补丁在，记作正常。
    $isHotfix = $name -match 'hotfix' -or ($name -match '-(alpha|beta)\.' -and $patches.Count -gt 0)
    if ($full.Count -eq 0) {
        if ($name -eq $latest -and !$isHotfix) { [void]$flags.Add('缺全量包') }
        elseif ($isHotfix -and $patches.Count -gt 0) { [void]$flags.Add('热修(无全量)') }
        else { [void]$flags.Add('全量已归档') }
    }
    if (!$hasSha) { [void]$flags.Add('缺SHA256') }
    if (!$hasNotes) { [void]$flags.Add('缺notes') }
    if (!$hasVerify) { [void]$flags.Add('缺verification') }
    if ($fullMb -gt $MaxFullMb) { [void]$flags.Add("全量$fullMb>${MaxFullMb}MB") }
    foreach ($p in $patches) {
        $pm = [math]::Round($p.Length / 1MB)
        if ($pm -gt $MaxPatchMb) { [void]$flags.Add("补丁$pm>${MaxPatchMb}MB") }
    }

    $state = if ($flags.Count -eq 0) { 'ok' } else { ($flags -join ',') }
    $isLatest = $name -eq $latest
    # 历史版本的"无tag/全量已归档/超大补丁"是既成事实，不阻塞发版；只有最新版的问题算红。
    # "热修(无全量)"是规则允许的形态，任何情况下都不算问题。
    $blocking = @($flags | Where-Object { $_ -ne '热修(无全量)' -and $_ -ne '全量已归档' })
    if ($isLatest -and $blocking.Count -gt 0) { foreach ($f in $blocking) { [void]$red.Add("最新版 $name：$f") } }
    elseif ($blocking.Count -gt 0) { [void]$warn.Add("$name：$state") }
    Write-Host ("  {0,-24} 全量{1,5}MB  补丁{2,4}MB  {3}" -f $name, $fullMb, $patchMb, $state)

    [void]$index.Add([pscustomobject]@{
        Version = $name; Tagged = $hasTag; FullMb = $fullMb; PatchCount = $patches.Count
        PatchMb = $patchMb; Notes = $hasNotes; Verification = $hasVerify; Sha256 = $hasSha; State = $state
    })
}

# 最新版本必须能从哪里升上来
$latestPatches = @(Get-ChildItem -LiteralPath (Join-Path $distPath $latest) -Recurse -Filter '*.orcupdate' -File -ErrorAction SilentlyContinue)
Write-Host ''
if ($latestPatches.Count -gt 0) { Ok '最新版增量补丁' "$($latestPatches.Count) 个" }
else { Bad '最新版增量补丁' '没有补丁——老玩家只能整包重装' }

# ── R6 引擎路径可解析 ────────────────────────────────────────────
$engine = $env:ORC_ENGINE_ROOT
if (!$engine) {
    $siblingA = Join-Path (Split-Path -Parent $root) 'OrC-KSD'
    $siblingB = Join-Path (Split-Path -Parent $root) 'OrC-KSD-github'
    if (Test-Path -LiteralPath (Join-Path $siblingA 'src\Orc.Game\Orc.Game.csproj')) { $engine = $siblingA }
    elseif (Test-Path -LiteralPath (Join-Path $siblingB 'src\Orc.Game\Orc.Game.csproj')) { $engine = $siblingB }
}
if ($engine -and (Test-Path -LiteralPath (Join-Path $engine 'src\Orc.Game\Orc.Game.csproj'))) { Ok '引擎路径' $engine }
else { Bad '引擎路径' '无法解析——外部贡献者将无法构建（ORC_ENGINE_ROOT / 同级目录）' }

# ── R7 工作区干净 ────────────────────────────────────────────────
$dirty = @(& git -C $root status --porcelain 2>$null)
if ($dirty.Count -eq 0) { Ok '工作区' '干净' }
else { Warn '工作区' "$($dirty.Count) 个未提交文件（收工前应为 0）" }

# ── 汇总 ────────────────────────────────────────────────────────
Write-Host ''
if ($WriteIndex) {
    $indexPath = Join-Path $root 'docs/release/RELEASE_INDEX.md'
    $lines = New-Object System.Collections.ArrayList
    [void]$lines.Add('# 发行索引（机器生成，勿手改）')
    [void]$lines.Add('')
    [void]$lines.Add('由 `tools/check-consistency.ps1 -WriteIndex` 生成。`无tag` 表示该版本交付过但没有 git 锚点，无法回溯来源。')
    [void]$lines.Add('')
    [void]$lines.Add('| 版本 | tag | 全量 | 补丁 | 说明/验证/校验和 | 状态 |')
    [void]$lines.Add('|---|---|---|---|---|---|')
    foreach ($row in ($index | Sort-Object Version -Descending)) {
        $tri = @()
        if ($row.Notes) { $tri += 'notes' }
        if ($row.Verification) { $tri += 'verify' }
        if ($row.Sha256) { $tri += 'sha256' }
        $t = if ($row.Tagged) { '有' } else { '无' }
        [void]$lines.Add("| $($row.Version) | $t | $($row.FullMb) MB | $($row.PatchCount) 个 / $($row.PatchMb) MB | $($tri -join '/') | $($row.State) |")
    }
    $lines -join "`r`n" | Out-Null
    # 无 BOM 的 UTF-8：PS5.1 的 Set-Content -Encoding utf8 会写 BOM，Markdown 首行会被当正文。
    [IO.File]::WriteAllText($indexPath, (($lines -join "`r`n") + "`r`n"), [Text.UTF8Encoding]::new($false))
    Write-Host "已生成 docs/release/RELEASE_INDEX.md"
}
if ($red.Count -eq 0) {
    Write-Host "CONSISTENCY_OK 红=0 黄=$($warn.Count)"
    exit 0
}
Write-Host "CONSISTENCY_FAIL 红=$($red.Count) 黄=$($warn.Count)"
foreach ($r in $red) { Write-Host "  - $r" }
exit 1
