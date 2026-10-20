param(
    [string]$EnginePath = 'H:\Working Folder\OrC-KSD-github',
    [string]$Remote = 'origin/main',
    [switch]$Fetch
)
# 引擎漂移报告（只读）。回答三个问题：远端又动了什么、我方是否落后、哪些源码面受影响。
# 不合并、不切换分支、不改任何文件；-Fetch 才访问网络。
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath (Join-Path $EnginePath '.git'))) { throw "不是 git 仓库：$EnginePath" }
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

function Invoke-Git([string[]]$Argv) { & git -C $EnginePath -c core.quotepath=false @Argv }
function Text([object]$Value) { (($Value | Out-String).TrimEnd()) }

$out = [System.Collections.Generic.List[string]]::new()

if ($Fetch) {
    $before = Text (Invoke-Git @('rev-parse', '--short', $Remote))
    Invoke-Git @('fetch', 'origin') | Out-Null
    $after = Text (Invoke-Git @('rev-parse', '--short', $Remote))
    $out.Add("fetch: $before -> $after")
}

$head = Text (Invoke-Git @('rev-parse', '--short', 'HEAD'))
$remoteHead = Text (Invoke-Git @('rev-parse', '--short', $Remote))
$branch = Text (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD'))
$counts = (Text (Invoke-Git @('rev-list', '--left-right', '--count', "HEAD...$Remote"))) -split '\s+'
$ahead = [int]$counts[0]; $behind = [int]$counts[1]

$out.Add(' ')
$out.Add("引擎漂移报告  $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
$out.Add("  本地 HEAD         $head  ($branch)")
$out.Add("  远端              $remoteHead  ($Remote)")
$out.Add("  本地领先 $ahead / 远端领先 $behind")

if ($behind -gt 0) {
    $out.Add(' ')
    $out.Add('远端新增提交：')
    foreach ($line in (Invoke-Git @('log', '--oneline', '--no-decorate', "HEAD..$Remote"))) { $out.Add("  $line") }
    $out.Add(' ')
    $out.Add('受影响源码面（远端新增提交触及的 src/ 文件）：')
    foreach ($line in (Invoke-Git @('diff', '--stat', "HEAD...$Remote", '--', 'src/') | Select-Object -Last 6)) { $out.Add("  $line") }
}

# 已知陷阱：.gitignore 第 30 行 `**/tools/` 在 Windows 大小写不敏感，误伤 src/Orc.Mcp/Tools，
# 导致 Orc.Mcp / Orc.Ai / Orc.Mcp.Tests 以及全量 OrcEngine.sln 构建必挂 CS0234。
$trackedTools = (Invoke-Git @('ls-tree', '-r', '--name-only', $Remote, '--', 'src/Orc.Mcp/Tools') | Measure-Object).Count
$out.Add(' ')
if ($trackedTools -eq 0) {
    $out.Add('  ! src/Orc.Mcp/Tools 仍未入库（.gitignore:30 的 **/tools/ 大小写不敏感命中）→ OrcEngine.sln 全量构建会挂 CS0234')
} else {
    $out.Add("  src/Orc.Mcp/Tools 已入库（$trackedTools 个文件）")
}
$out.Add($(if ($behind -eq 0) { '  远端已收敛，可以开始整合。' } else { '  远端仍在动，继续等待。' }))

Write-Host ($out -join [Environment]::NewLine)
exit 0
