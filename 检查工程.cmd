@echo off
rem Sync -> build -> full UI verification (about 3 minutes).
rem Last line should be UI_VERIFY_OK.
rem Keep this file ASCII-only (cmd.exe reads .cmd in the OEM codepage).
setlocal
set "ROOT=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\run.ps1" -Verify
if errorlevel 1 (echo. & echo [FAILED] & pause)
endlocal
