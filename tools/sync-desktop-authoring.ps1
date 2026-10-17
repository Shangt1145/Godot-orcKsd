param([string]$SourceRoot='H:\Working Folder\Kards-Desktop')
$ErrorActionPreference='Stop'
$taskDestination=Join-Path $PSScriptRoot '..\proto\bridge\Kards.Ui.OrcBridge\DesktopAuthoring'
$taskPriorPath=Join-Path $taskDestination 'source-manifest.json'
$taskPrior=if(Test-Path -LiteralPath $taskPriorPath){Get-Content -LiteralPath $taskPriorPath -Raw|ConvertFrom-Json}else{$null}
# Preserve reviewed semantic fixes. A changed upstream file requires an explicit rebase,
# so synchronization cannot silently erase the native game's completed mechanics.
foreach($taskPatch in $taskPrior.patches){
    $taskEntry=$taskPrior.files|Where-Object path -eq $taskPatch.path
    $taskUpstream=Join-Path $SourceRoot ('electron/game/'+$taskPatch.path)
    $taskLocal=Join-Path $taskDestination $taskPatch.path
    if(!(Test-Path -LiteralPath $taskLocal) -or (Get-FileHash -LiteralPath $taskLocal -Algorithm SHA256).Hash -ne $taskEntry.vendoredSha256){throw "Reviewed local patch changed: $($taskPatch.path)"}
    if((Get-FileHash -LiteralPath $taskUpstream -Algorithm SHA256).Hash -ne $taskEntry.sourceSha256){throw "Upstream changed; rebase the reviewed patch before synchronization: $($taskPatch.path)"}
}
$taskFiles=@('js/engine.js','js/primitives-extra.js','js/primitives.js','js/effects.js','js/effect-primitives.js','js/compiler.js','js/effect-contract.js','js/orc.js','js/card-import.js','js/effect-inspector.js','js/effect-editor.js','js/inspector-help.js','css/inspector.css','css/inspector-polish.css','css/inspector-tutorial.css','inspector-tutorial.html')
$taskManifest=@()
foreach($taskFile in $taskFiles){
    $taskSource=Join-Path $SourceRoot ('electron/game/'+$taskFile)
    $taskTarget=Join-Path $taskDestination $taskFile
    New-Item -ItemType Directory -Force -Path (Split-Path $taskTarget -Parent) | Out-Null
    if($taskFile -notin @($taskPrior.patches.path)){Copy-Item -LiteralPath $taskSource -Destination $taskTarget -Force}
    $taskManifest+=@{path=$taskFile;sourceSha256=(Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash;vendoredSha256=(Get-FileHash -LiteralPath $taskTarget -Algorithm SHA256).Hash}
}
$taskCommit=(& git -C $SourceRoot rev-parse HEAD).Trim()
@{sourceRoot=$SourceRoot;sourceCommit=$taskCommit;sourceKind='local working tree content, pinned by per-file hashes';patches=@($taskPrior.patches);files=$taskManifest} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskDestination 'source-manifest.json') -Encoding utf8

$taskTutorial=[IO.File]::ReadAllText((Join-Path $taskDestination 'inspector-tutorial.html'))
$taskTutorial=[regex]::Match($taskTutorial,'(?s)<main>(.*?)</main>').Groups[1].Value
$taskTutorial=[regex]::Replace($taskTutorial,'(?s)<script.*?</script>|<style.*?</style>','')
$taskTutorial=[regex]::Replace($taskTutorial,'</(?:p|h1|h2|li|tr|pre)>',"`n`n")
$taskTutorial=[Net.WebUtility]::HtmlDecode([regex]::Replace($taskTutorial,'<[^>]+>',''))
[IO.File]::WriteAllText((Join-Path $taskDestination 'inspector-tutorial.txt'),$taskTutorial.Trim(),[Text.UTF8Encoding]::new($false))
