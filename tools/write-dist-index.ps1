param(
    [string]$DistRoot = (Join-Path $PSScriptRoot '../dist'),
    [string]$CurrentRelease = (Get-Content (Join-Path $PSScriptRoot '../VERSION') -Raw).Trim()
)
$ErrorActionPreference = 'Stop'
$DistRoot = [IO.Path]::GetFullPath($DistRoot)
$lines = @(
    '# 发行文件',
    '',
    "当前发行：[$CurrentRelease](releases/$CurrentRelease/)。",
    '',
    '- `releases/版本号/full/`：完整安装 ZIP。',
    '- `releases/版本号/patches/`：离线增量补丁 `.orcupdate`。',
    '- `releases/版本号/notes/`：发行说明、更新报告及历史校验记录。',
    '- `releases/版本号/verification/`：导出、升级与回归验证日志。',
    '- `releases/版本号/SHA256.txt`：相对此版本目录的发行文件校验和。',
    '- `updaters/版本号/`：独立离线更新器 ZIP，各游戏版本共用。',
    '',
    '## 版本目录',
    ''
)
$releases = Join-Path $DistRoot 'releases'
if(Test-Path -LiteralPath $releases){
    foreach($folder in Get-ChildItem -LiteralPath $releases -Directory | Sort-Object Name -Descending){
        $lines += '- ['+$folder.Name+'](releases/'+$folder.Name+'/)'
    }
}
$lines += @('', '更新时用“更新游戏.exe”选择原游戏目录与匹配来源的增量补丁。不同版本的补丁不能任意互换。', '')
[IO.File]::WriteAllLines((Join-Path $DistRoot 'README.md'),[string[]]$lines,[Text.UTF8Encoding]::new($false))
