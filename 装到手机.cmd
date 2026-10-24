@echo off
rem Install to a connected Android phone and launch it (adb install -r -d).
rem Add  -Log  to the last command to tail logcat.
rem Keep this file ASCII-only (cmd.exe reads .cmd in the OEM codepage).
setlocal
set "ROOT=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\install-android.ps1"
if errorlevel 1 (echo. & echo [FAILED] & pause)
endlocal
