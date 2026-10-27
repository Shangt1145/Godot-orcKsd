# Does the server child process die with the UI? End-to-end check.
# The in-process probe (-VerifyHostWatchdog) opens a real room and prints:
#   HOST_WATCHDOG_READY port=<p> serverPid=<child> uiPid=<ui>
# then idles 45s. This script kills only the UI (no /T, like a crash) and watches
# whether the child exits and the port can be bound again.
# Criterion is "the port can be rebound", not "the process looks gone".
# ASCII only: Windows PowerShell 5.1 reads BOM-less files as ANSI and mangles non-ASCII.

$ErrorActionPreference = 'Stop'
$root = 'H:\Working Folder\OrC-KSD.Godot'
$log  = Join-Path $root 'artifacts\watchdog-run.log'

Write-Output "[1/5] starting host-watchdog probe ..."
# No -RedirectStandardOutput: this machine's env has both HTTPS_PROXY and https_proxy and
# Start-Process rebuilds the env block case-insensitively -> "Item has already been added".
$cmdline = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$root\tools\run.ps1`" -VerifyHostWatchdog > `"$log`" 2>&1"
$runner = Start-Process -FilePath 'cmd' -ArgumentList '/c', $cmdline -PassThru -WindowStyle Hidden

$ready = $null
$deadline = (Get-Date).AddMinutes(8)
while ((Get-Date) -lt $deadline -and $null -eq $ready) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $log) {
        $line = Select-String -Path $log -Pattern 'HOST_WATCHDOG_READY' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($line) { $ready = $line.Line }
    }
}
if ($null -eq $ready) {
    Write-Output "FAIL: probe never reported HOST_WATCHDOG_READY"
    Get-Content $log -Tail 20 -ErrorAction SilentlyContinue
    Stop-Process -Id $runner.Id -Force -ErrorAction SilentlyContinue
    exit 1
}

$port   = [int][regex]::Match($ready, 'port=(\d+)').Groups[1].Value
$child  = [int][regex]::Match($ready, 'serverPid=(\d+)').Groups[1].Value
$ui     = [int][regex]::Match($ready, 'uiPid=(\d+)').Groups[1].Value
Write-Output "[2/5] ready: $ready"
Write-Output "      ui=$ui child=$child port=$port"

$childCmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$child" -ErrorAction SilentlyContinue).CommandLine
Write-Output "[3/5] child cmdline: $childCmd"
$argPid = [regex]::Match([string]$childCmd, '--parent-pid\s+(\d+)').Groups[1].Value
Write-Output "      child was told --parent-pid=$argPid (ui is $ui)"

$before = $null
try {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
    $l.Start(); $l.Stop(); $before = 'free'
} catch { $before = 'busy' }
Write-Output "      port before kill: $before"

Write-Output "[4/5] killing ONLY the UI (PID=$ui); child PID=$child left alone ..."
Stop-Process -Id $ui -Force -ErrorAction SilentlyContinue

$sw = [Diagnostics.Stopwatch]::StartNew()
$gone = $false
while ($sw.Elapsed.TotalSeconds -lt 15) {
    Start-Sleep -Milliseconds 500
    if (-not (Get-Process -Id $child -ErrorAction SilentlyContinue)) { $gone = $true; break }
}
$elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 1)

$after = 'busy'
try {
    $l2 = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
    $l2.Start(); $l2.Stop(); $after = 'free'
} catch { $after = 'busy' }

Write-Output "[5/5] childExited=$gone elapsed=${elapsed}s portAfter=$after"
Write-Output ""
if ($gone -and $after -eq 'free' -and $argPid -eq "$ui") {
    Write-Output "RESULT childExited=$gone elapsed=${elapsed}s portRebindable=$after parentPidMatches=yes"
    Write-Output 'WATCHDOG_OK'
} else {
    Write-Output "RESULT childExited=$gone elapsed=${elapsed}s portRebindable=$after parentPidMatches=$($argPid -eq "$ui")"
    Write-Output 'WATCHDOG_FAIL'
}
Stop-Process -Id $runner.Id -Force -ErrorAction SilentlyContinue
