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

# .NET exports put the same architecture-independent crypto JAR under each ABI.
# Feed each identical JAR to D8 once, retaining all ABI-specific native libraries.
$gradlePath = Join-Path $ProjectRoot 'android/build/build.gradle'
$gradle = [IO.File]::ReadAllText($gradlePath)
if ($gradle.Contains('// ORC_ANDROID_JAR_DEDUP') -and !$gradle.Contains('// ORC_ANDROID_JAR_DEDUP_V2')) {
    $start = $gradle.IndexOf('// ORC_ANDROID_JAR_DEDUP')
    $end = $gradle.IndexOf('dependencies {', $start)
    $gradle = $gradle.Substring(0, $start) + $gradle.Substring($end)
    foreach ($variant in @('debug','release')) {
        $gradle = $gradle.Replace("$($variant)Implementation files(orcUniqueJars('libs/$variant'))", "$($variant)Implementation fileTree(dir: 'libs/$variant', include: ['**/*.jar', '*.aar'])")
        $gradle = $gradle.Replace("$($variant)Implementation fileTree(dir: 'libs/$variant', include: ['*.aar'])", '')
    }
}
if (!$gradle.Contains('// ORC_ANDROID_JAR_DEDUP_V2')) {
    $helper = @'
// ORC_ANDROID_JAR_DEDUP_V2
def orcJarContent = { jar ->
    def archive = new java.util.zip.ZipFile(jar)
    try {
        archive.entries().toList().findAll { entry ->
            !entry.directory && !(entry.name ==~ /(?i)META-INF\/.*\.(RSA|DSA|EC|SF)/)
        }.sort { it.name }.collect { entry ->
            entry.name + ':' + java.security.MessageDigest.getInstance('SHA-256').digest(archive.getInputStream(entry).bytes).encodeHex().toString()
        }.join('\n')
    } finally { archive.close() }
}
def orcUniqueJars = { directory ->
    fileTree(dir: directory, include: ['**/*.jar']).files.groupBy { it.name }.collect { name, copies ->
        // SDK signatures differ by RID; all class/resource payloads must still match.
        def hashes = copies.collect { orcJarContent(it) }.unique()
        if (hashes.size() != 1) throw new GradleException("Different Android JARs share a filename: " + name)
        copies.sort { it.path }.first()
    }
}
def orcAndroidBuildDir = System.getenv('ORC_ANDROID_BUILD_DIR')
if (orcAndroidBuildDir) layout.buildDirectory.set(file(orcAndroidBuildDir))

'@
    foreach ($variant in @('debug','release')) {
        $old = "$($variant)Implementation fileTree(dir: 'libs/$variant', include: ['**/*.jar', '*.aar'])"
        if (!$gradle.Contains($old)) { throw "Unrecognized Android Gradle dependency template: $variant" }
        $replacement = "$($variant)Implementation files(orcUniqueJars('libs/$variant'))`n        $($variant)Implementation fileTree(dir: 'libs/$variant', include: ['*.aar'])"
        $gradle = $gradle.Replace($old, $replacement)
    }
    $gradle = $gradle.Replace('dependencies {', $helper + "`n" + 'dependencies {')
    [IO.File]::WriteAllText($gradlePath, $gradle, [Text.UTF8Encoding]::new($false))
}
Write-Host 'ANDROID_PLATFORM_READY foreground-room document-import apk-update'
