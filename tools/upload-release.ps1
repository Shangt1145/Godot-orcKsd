# 把发行文件上传到 GitHub Release（资产）。规则见 docs/release/VERSIONING.md「发行规则」第 3 条。
#
#   powershell -File tools/upload-release.ps1 -Tag v0.0.0beta7.hotfix.3 -File dist\releases\0.0.0beta7.hotfix.3\full\OrC-KSD-0.0.0beta7.hotfix.3-win-x64.zip
#
# 行为：
#   - 用 git 凭据（git credential fill）取 token，不需要 gh CLI
#   - 同名资产已存在则跳过（-Force 覆盖：先删后传）
#   - 流式上传，不把 1 GB 文件读进内存
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string[]]$File,
    [string]$Repo = 'Shangt1145/Godot-orcKsd',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

# ── token ────────────────────────────────────────────────────────
$cred = "protocol=https`nhost=github.com`n`n" | & git credential fill 2>$null
$token = ($cred | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
if (!$token) { throw '取不到 GitHub 凭据（git credential fill 为空）。先确认能 git push。' }
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'orcksd-release' }

# ── 代理：沿用 HTTPS_PROXY，HttpClient 会对 https 走 CONNECT 隧道 ──
$proxyUri = $env:HTTPS_PROXY
if (!$proxyUri) { $proxyUri = $env:https_proxy }
$handler = New-Object System.Net.Http.HttpClientHandler
if ($proxyUri) {
    $handler.Proxy = New-Object System.Net.WebProxy($proxyUri)
    $handler.UseProxy = $true
}
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromHours(2)
foreach ($k in $headers.Keys) { [void]$client.DefaultRequestHeaders.TryAddWithoutValidation($k, $headers[$k]) }

function Invoke-Api([string]$Method, [string]$Url) {
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::$Method, $Url)
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (!$response.IsSuccessStatusCode) { throw "GitHub API $Method $Url 失败：$($response.StatusCode) $text" }
    return $text | ConvertFrom-Json
}

# ── 找到 release ─────────────────────────────────────────────────
$release = Invoke-Api 'Get' "https://api.github.com/repos/$Repo/releases/tags/$Tag"
Write-Host "Release $Tag  id=$($release.id)  已有资产 $($release.assets.Count) 个"

foreach ($path in $File) {
    $full = if ([IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $root $path }
    if (!(Test-Path -LiteralPath $full)) { throw "找不到文件：$full" }
    $name = [IO.Path]::GetFileName($full)
    $size = (Get-Item -LiteralPath $full).Length
    $mb = [math]::Round($size / 1MB, 1)

    $existing = @($release.assets | Where-Object { $_.name -eq $name })
    if ($existing.Count -gt 0) {
        if (!$Force) { Write-Host "跳过（已存在）：$name"; continue }
        Invoke-Api 'Delete' "https://api.github.com/repos/$Repo/releases/assets/$($existing[0].id)" | Out-Null
        Write-Host "已删除旧资产：$name"
    }

    Write-Host "上传 $name（$mb MB）…"
    $stream = [IO.File]::OpenRead($full)
    try {
        $content = New-Object System.Net.Http.StreamContent($stream)
        $content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue('application/octet-stream')
        $url = "https://uploads.github.com/repos/$Repo/releases/$($release.id)/assets?name=" + [Uri]::EscapeDataString($name)
        $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $url)
        $request.Content = $content
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (!$response.IsSuccessStatusCode) { throw "上传失败：$($response.StatusCode) $text" }
        $asset = $text | ConvertFrom-Json
        Write-Host "ASSET_OK $($asset.name)  $([math]::Round($asset.size/1MB,1)) MB"
    }
    finally { $stream.Dispose() }
}
