# Invoke the Windows GUI executable synchronously, without the console launcher's orphaned wait.
# ArgumentList preserves Unicode and spaces; both streams are drained while the child runs.
$ErrorActionPreference='Stop'
$executable=if($env:ORC_GODOT_EXECUTABLE){$env:ORC_GODOT_EXECUTABLE}else{'H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64.exe'}
if(!(Test-Path -LiteralPath $executable)){throw "Godot executable not found: $executable"}
$info=[Diagnostics.ProcessStartInfo]::new()
$info.FileName=$executable
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$info.RedirectStandardOutput=$true
$info.RedirectStandardError=$true
# ArgumentList 在 .NET Core（pwsh 6+）才有；Windows PowerShell 5.1 退回 Arguments 字符串拼接。
if($info.ArgumentList){foreach($argument in $args){$info.ArgumentList.Add([string]$argument)}}
else{$info.Arguments=(@($args)|ForEach-Object{if($_ -match '\s'){'"'+$_+'"'}else{[string]$_}})-join ' '}
$process=[Diagnostics.Process]::new();$process.StartInfo=$info
try{
    if(!$process.Start()){throw 'Godot process failed to start.'}
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $output=$stdout.GetAwaiter().GetResult();$errors=$stderr.GetAwaiter().GetResult()
    if($output){$output.TrimEnd() -split '\r?\n'|Write-Output}
    if($errors){$errors.TrimEnd() -split '\r?\n'|Write-Output}
    $global:LASTEXITCODE=$process.ExitCode
}finally{$process.Dispose()}
