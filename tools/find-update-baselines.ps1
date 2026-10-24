param(
    [string[]]$SearchRoots = @((Join-Path $PSScriptRoot '../dist/releases'), 'H:\g\baselines'),
    [ValidateRange(1, 1000)][int]$ThroughBeta = 5
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
# The first delivered beta predates build manifests. Its identity is pinned by archive hash.
$legacy = @{ 'A9323DBB2529F5ACCC275A94969C984A40C49D3B2D12493CB2A46281A2BEF27E' = '0.0.0-beta.1' }
$seen = @{}
$baselines = @()
foreach($root in $SearchRoots){
    if(!(Test-Path -LiteralPath $root)){continue}
    foreach($file in Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.zip' | Sort-Object FullName){
        $archive = [IO.Compression.ZipFile]::OpenRead($file.FullName)
        try{
            if(!$archive.GetEntry('KardsUi.exe') -or !$archive.GetEntry('KardsUi.pck')){continue}
            $entry = $archive.GetEntry('.orcksd-build.json')
            $version = $null; $buildId = $null; $archiveHash = $null
            if($entry){
                $reader = [IO.StreamReader]::new($entry.Open())
                try{$info = $reader.ReadToEnd() | ConvertFrom-Json}finally{$reader.Dispose()}
                $version = $info.Version; $buildId = $info.BuildId
                if($buildId -notmatch '^[A-Fa-f0-9]{64}$'){throw "Invalid baseline manifest: $($file.FullName)"}
            }else{
                $archiveHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                $version = $legacy[$archiveHash]
                if(!$version){throw "Game archive has no recognized version: $($file.FullName)"}
            }
            # Hotfixes and later betas must not accidentally become older-version sources.
            if($version -notmatch '^0\.0\.0-(alpha|beta)\.([1-9]\d*)$'){continue}
            $stage = $Matches[1]; $number = [int]$Matches[2]
            if($stage -eq 'beta' -and $number -gt $ThroughBeta){continue}
            $identity = if($buildId){$buildId}else{$archiveHash}
            if($seen.ContainsKey($identity)){continue}
            $seen[$identity] = $true
            $baselines += [PSCustomObject]@{ Path = $file.FullName; Version = $version; BuildId = $buildId; ArchiveSha256 = $archiveHash }
        }finally{$archive.Dispose()}
    }
}
foreach($number in 1..$ThroughBeta){
    if(!($baselines | Where-Object Version -eq "0.0.0-beta.$number")){
        throw "Missing beta.$number baseline; refusing to claim compatibility through beta.$ThroughBeta."
    }
}
if($baselines.Count -gt 64){throw 'The updater supports at most 64 distinct source builds.'}
$baselines | Sort-Object Version,Path
