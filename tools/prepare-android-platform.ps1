param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$platformRoot = Join-Path $ProjectRoot 'platform/android'
$javaRoot = Join-Path $ProjectRoot 'android/build/src/main/java/com/godot/game'
$manifestPath = Join-Path $ProjectRoot 'android/build/src/main/AndroidManifest.xml'
if (!(Test-Path -LiteralPath $manifestPath)) { throw '先安装 Godot Android Gradle 构建模板。' }
New-Item -ItemType Directory -Path $javaRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $platformRoot 'RoomHostService.java') -Destination (Join-Path $javaRoot 'RoomHostService.java') -Force
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$ns = 'http://schemas.android.com/apk/res/android'
foreach ($permission in @('FOREGROUND_SERVICE','FOREGROUND_SERVICE_CONNECTED_DEVICE','WAKE_LOCK','CHANGE_WIFI_MULTICAST_STATE','ACCESS_WIFI_STATE','CHANGE_NETWORK_STATE','REQUEST_INSTALL_PACKAGES')) {
    $name = 'android.permission.' + $permission
    if (!(@($manifest.manifest.'uses-permission') | Where-Object { $_ -and $_.GetAttribute('name',$ns) -eq $name })) {
        $node = $manifest.CreateElement('uses-permission'); $node.SetAttribute('name',$ns,$name)
        [void]$manifest.manifest.AppendChild($node)
    }
}
if (!(@($manifest.manifest.application.service) | Where-Object { $_ -and $_.GetAttribute('name',$ns) -eq 'com.godot.game.RoomHostService' })) {
    $node = $manifest.CreateElement('service'); $node.SetAttribute('name',$ns,'com.godot.game.RoomHostService')
    $node.SetAttribute('exported',$ns,'false'); $node.SetAttribute('foregroundServiceType',$ns,'connectedDevice')
    [void]$manifest.manifest.application.AppendChild($node)
}
$manifest.Save($manifestPath)
Write-Host 'ANDROID_PLATFORM_READY foreground-room document-import apk-update'
