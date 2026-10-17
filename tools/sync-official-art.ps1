param([string]$Api = 'https://herokuapi.kards.com/graphql')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $root 'proto/art/kards'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$wanted = @{}
Get-ChildItem (Join-Path $root 'proto/data/kards/packs') -Recurse -Filter '*.card.json' | ForEach-Object {
    $card = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    $wanted[$card.id] = $true
}
$images = @{}
for ($offset = 0; ;) {
    $query = 'query { cards(language: "zh", first: 2000, offset: ' + $offset + ', showSpawnables: true, showExiles: true, showReserved: true) { pageInfo { hasNextPage } edges { node { cardId image(language: "zh") } } } }'
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            $response = Invoke-RestMethod -Uri $Api -Method Post -ContentType application/json -Body (@{query=$query} | ConvertTo-Json) -TimeoutSec 30
            break
        }
        catch { if ($attempt -eq 2) { throw } }
    }
    if ($response.errors) { throw ($response.errors | ConvertTo-Json -Depth 10) }
    foreach ($edge in $response.data.cards.edges) {
        $card = $edge.node
        if ($wanted.ContainsKey($card.cardId)) { $images[$card.cardId] = ([Uri]::new([Uri]'https://www.kards.com', [string]$card.image)).AbsoluteUri }
    }
    if (!$response.data.cards.pageInfo.hasNextPage) { break }
    if ($response.data.cards.edges.Count -eq 0) { throw 'Official API returned an empty unfinished page.' }
    $offset += $response.data.cards.edges.Count
}
foreach ($id in $wanted.Keys) { if (!$images.ContainsKey($id)) { throw "No official artwork for $id" } }
$sourceRecord = Join-Path $destination 'sources.json'
$previous = if (Test-Path -LiteralPath $sourceRecord) { Get-Content -LiteralPath $sourceRecord -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
$jobs = foreach ($id in $wanted.Keys) { @{id=$id; url=$images[$id]; output=(Join-Path $destination ($id+'.avif')); refresh=($previous[$id] -ne $images[$id])} }
$jobs | ForEach-Object -Parallel {
    if ($_.refresh -or !(Test-Path -LiteralPath $_.output)) {
        for ($attempt = 0; $attempt -lt 3; $attempt++) {
            try {
                Invoke-WebRequest -Uri $_.url -OutFile ($_.output+'.download') -TimeoutSec 45 -ErrorAction Stop
                Move-Item -LiteralPath ($_.output+'.download') -Destination $_.output -Force
                break
            }
            catch { if ($attempt -eq 2) { throw } }
        }
    }
} -ThrottleLimit 6
$images | ConvertTo-Json | Set-Content -LiteralPath $sourceRecord -Encoding utf8
# Godot does not load AVIF textures. Convert locally; source URLs remain recorded for auditing.
& python (Join-Path $PSScriptRoot 'convert-official-art.py') $destination
if ($LASTEXITCODE -ne 0) { throw 'Official artwork conversion failed.' }
Write-Host "OFFICIAL_ART_OK cards=$($wanted.Count)"
